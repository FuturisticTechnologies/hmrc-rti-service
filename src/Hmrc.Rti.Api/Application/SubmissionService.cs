// ---------------------------------------------------------------------------
// Submission orchestration.
//
// Every status change goes through RtiSubmission.TransitionTo(), which validates
// the move and appends the audit event in one step. Status has a private setter,
// so there is no code path that bypasses the state machine.
// ---------------------------------------------------------------------------
using Hmrc.Rti.Api.Domain;
using Hmrc.Rti.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Payroll.Platform;

namespace Hmrc.Rti.Api.Application;

// --- Requests / responses ---------------------------------------------------

public sealed record CreateFpsRequest
{
    public Guid PayRunId { get; init; }
    public string EmployerPayeReference { get; init; } = string.Empty;
    public string AccountsOfficeReference { get; init; } = string.Empty;
}

public sealed record CreateEpsRequest
{
    public int TaxYear { get; init; }
    public int PeriodNumber { get; init; }
    public string EmployerPayeReference { get; init; } = string.Empty;
    public string AccountsOfficeReference { get; init; } = string.Empty;
    public decimal TotalPayeTax { get; init; }
    public decimal TotalEmployeeNi { get; init; }
    public decimal TotalEmployerNi { get; init; }
    public bool NoPaymentForPeriod { get; init; }
}

public sealed record SubmissionResponse
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string SubmissionType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string TaxYear { get; init; } = string.Empty;
    public int PeriodNumber { get; init; }
    public Guid? PayRunId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string PayloadHash { get; init; } = string.Empty;
    public int EmployeeCount { get; init; }
    public decimal TotalPayeTax { get; init; }
    public decimal TotalEmployeeNi { get; init; }
    public decimal TotalEmployerNi { get; init; }
    public string? HmrcSubmissionId { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public bool IsLive { get; init; }
    public IReadOnlyList<string> NextStates { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }

    public static SubmissionResponse From(RtiSubmission s) => new()
    {
        Id = s.Id,
        TenantId = s.TenantId,
        SubmissionType = s.SubmissionType.ToString().ToUpperInvariant(),
        Status = s.Status.ToString(),
        TaxYear = RtiEnvelopeBuilder.FormatTaxYear(s.TaxYear),
        PeriodNumber = s.PeriodNumber,
        PayRunId = s.PayRunId,
        CorrelationId = s.CorrelationId,
        PayloadHash = s.PayloadHash,
        EmployeeCount = s.EmployeeCount,
        TotalPayeTax = s.TotalPayeTax,
        TotalEmployeeNi = s.TotalEmployeeNi,
        TotalEmployerNi = s.TotalEmployerNi,
        HmrcSubmissionId = s.HmrcSubmissionId,
        Attempts = s.Attempts,
        LastError = s.LastError,
        IsLive = s.IsLive,
        NextStates = RtiSubmissionStateMachine.NextStates(s.Status).Select(x => x.ToString()).ToArray(),
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        SubmittedAt = s.SubmittedAt,
    };
}

public sealed record SubmissionEventResponse(
    Guid Id, string EventType, string? FromStatus, string? ToStatus,
    string Detail, Guid? ActorUserId, DateTimeOffset OccurredAt);

// --- Service ----------------------------------------------------------------

public sealed class SubmissionService(
    RtiDbContext db,
    IPayrollApiClient payrollApi,
    IHmrcClient hmrc,
    TenantContext tenant,
    ILogger<SubmissionService> logger)
{
    private const int MaxAttempts = 5;

    public async Task<SubmissionResponse> CreateFpsAsync(
        CreateFpsRequest request, string bearerToken, CancellationToken ct)
    {
        RtiRequestValidator.EnsureValid(request);

        var payRun = await payrollApi.GetPayRunAsync(request.PayRunId, bearerToken, tenant.TenantId, ct);

        // Belt and braces. RLS should already have made another tenant's pay run
        // invisible, but an explicit check costs nothing and fails loudly.
        if (payRun.TenantId != tenant.TenantId)
        {
            throw ApiException.NotFound($"Pay run {request.PayRunId} was not found.");
        }

        var duplicate = await db.Submissions.AnyAsync(
            s => s.PayRunId == request.PayRunId
                 && s.SubmissionType == RtiSubmissionType.Fps
                 && s.Status == RtiSubmissionStatus.Accepted, ct);

        if (duplicate)
        {
            throw ApiException.Conflict(
                $"Pay run {request.PayRunId} already has an accepted FPS. " +
                "File a further FPS to amend it rather than resubmitting this one.");
        }

        var submission = new RtiSubmission
        {
            TenantId = tenant.TenantId,
            SubmissionType = RtiSubmissionType.Fps,
            TaxYear = payRun.PayPeriod.TaxYear,
            PeriodNumber = payRun.PayPeriod.PeriodNumber,
            PayRunId = payRun.Id,
            EmployerPayeReference = request.EmployerPayeReference.Trim(),
            AccountsOfficeReference = request.AccountsOfficeReference.Trim(),
            EmployeeCount = payRun.EmployeeCount,
            TotalPayeTax = payRun.TotalPayeTax,
            TotalEmployeeNi = payRun.TotalEmployeeNi,
            TotalEmployerNi = payRun.TotalEmployerNi,
            CreatedByUserId = tenant.UserId,
        };

        submission.Payload = RtiEnvelopeBuilder.BuildFps(
            payRun, submission.EmployerPayeReference,
            submission.AccountsOfficeReference, submission.CorrelationId);
        submission.PayloadHash = RtiEnvelopeBuilder.Hash(submission.Payload);

        db.Submissions.Add(submission);
        db.SubmissionEvents.Add(submission.AppendEvent(
            "created",
            $"FPS built from pay run {payRun.Id} ({payRun.EmployeeCount} employees).",
            tenant.UserId));

        var validation = RtiEnvelopeBuilder.ValidateFps(
            payRun, submission.EmployerPayeReference, submission.AccountsOfficeReference);

        ApplyValidation(submission, validation);
        await db.SaveChangesAsync(ct);

        if (!validation.IsValid)
        {
            throw ApiException.Unprocessable(
                "FPS failed local validation: " + string.Join(" ", validation.Errors));
        }

        logger.LogInformation("rti.fps_created {SubmissionId} {PayRunId}", submission.Id, payRun.Id);
        return SubmissionResponse.From(submission);
    }

    public async Task<SubmissionResponse> CreateEpsAsync(CreateEpsRequest request, CancellationToken ct)
    {
        RtiRequestValidator.EnsureValid(request, RtiRequestValidator.UkToday(DateTimeOffset.UtcNow));

        var submission = new RtiSubmission
        {
            TenantId = tenant.TenantId,
            SubmissionType = RtiSubmissionType.Eps,
            TaxYear = request.TaxYear,
            PeriodNumber = request.PeriodNumber,
            EmployerPayeReference = request.EmployerPayeReference.Trim(),
            AccountsOfficeReference = request.AccountsOfficeReference.Trim(),
            TotalPayeTax = request.TotalPayeTax,
            TotalEmployeeNi = request.TotalEmployeeNi,
            TotalEmployerNi = request.TotalEmployerNi,
            CreatedByUserId = tenant.UserId,
        };

        submission.Payload = RtiEnvelopeBuilder.BuildEps(
            request.TaxYear, request.PeriodNumber,
            submission.EmployerPayeReference, submission.AccountsOfficeReference,
            submission.CorrelationId,
            request.TotalEmployeeNi, request.TotalEmployerNi, request.TotalPayeTax,
            request.NoPaymentForPeriod);
        submission.PayloadHash = RtiEnvelopeBuilder.Hash(submission.Payload);

        db.Submissions.Add(submission);
        db.SubmissionEvents.Add(submission.AppendEvent(
            "created",
            $"EPS built for {RtiEnvelopeBuilder.FormatTaxYear(request.TaxYear)} period {request.PeriodNumber}.",
            tenant.UserId));

        var validation = RtiEnvelopeBuilder.ValidateEps(
            request.TaxYear, request.PeriodNumber,
            submission.EmployerPayeReference, submission.AccountsOfficeReference);

        ApplyValidation(submission, validation);
        await db.SaveChangesAsync(ct);

        if (!validation.IsValid)
        {
            throw ApiException.Unprocessable(
                "EPS failed local validation: " + string.Join(" ", validation.Errors));
        }

        logger.LogInformation("rti.eps_created {SubmissionId}", submission.Id);
        return SubmissionResponse.From(submission);
    }

    public async Task<SubmissionResponse> TransmitAsync(Guid id, CancellationToken ct)
    {
        var submission = await LoadAsync(id, ct);

        if (submission.Status == RtiSubmissionStatus.Validated)
        {
            db.SubmissionEvents.Add(submission.TransitionTo(
                RtiSubmissionStatus.Queued, "queued", "Queued for transmission.", tenant.UserId));
        }

        if (submission.Status != RtiSubmissionStatus.Queued)
        {
            throw ApiException.Conflict(
                $"A submission in status '{submission.Status}' cannot be transmitted. " +
                $"Allowed next states: {FormatNext(submission)}.");
        }

        if (submission.Attempts >= MaxAttempts)
        {
            throw ApiException.Conflict(
                $"Submission has reached the maximum of {MaxAttempts} attempts and needs manual review.");
        }

        db.SubmissionEvents.Add(submission.TransitionTo(
            RtiSubmissionStatus.Transmitting, "transmitting",
            $"Attempt {submission.Attempts + 1} of {MaxAttempts}.", tenant.UserId));
        submission.Attempts += 1;
        await db.SaveChangesAsync(ct);

        HmrcSubmissionResult result;
        try
        {
            result = await hmrc.SubmitAsync(submission, ct);
        }
        catch (Exception ex)
        {
            // No outcome came back - the token request failed, or the client
            // refused before sending. Left in Transmitting, neither transmit nor
            // retry would ever touch this submission again, so record a failed
            // attempt. Not the request's token: the caller may have gone away.
            submission.LastError = Truncate(ex.Message, 2000);
            submission.NextAttemptAt = DateTimeOffset.UtcNow.Add(BackoffFor(submission.Attempts));
            db.SubmissionEvents.Add(submission.TransitionTo(
                RtiSubmissionStatus.Failed, "failed", ex.Message, tenant.UserId));
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        submission.IsLive = result.WasLive;

        switch (result.Status)
        {
            case RtiSubmissionStatus.Accepted:
                submission.HmrcSubmissionId = result.HmrcSubmissionId;
                submission.SubmittedAt = DateTimeOffset.UtcNow;
                submission.LastError = null;
                submission.NextAttemptAt = null;
                db.SubmissionEvents.Add(submission.TransitionTo(
                    RtiSubmissionStatus.Accepted, "accepted", result.Detail, tenant.UserId));
                break;

            case RtiSubmissionStatus.Rejected:
                submission.LastError = result.Detail;
                // A business rejection will fail identically if replayed unchanged,
                // so no automatic retry is scheduled.
                submission.NextAttemptAt = null;
                db.SubmissionEvents.Add(submission.TransitionTo(
                    RtiSubmissionStatus.Rejected, "rejected", result.Detail, tenant.UserId));
                break;

            case RtiSubmissionStatus.Failed:
                submission.LastError = result.Detail;
                submission.NextAttemptAt = DateTimeOffset.UtcNow.Add(BackoffFor(submission.Attempts));
                db.SubmissionEvents.Add(submission.TransitionTo(
                    RtiSubmissionStatus.Failed, "failed", result.Detail, tenant.UserId));
                break;

            default:
                // HmrcClient returned a status (e.g. Draft, Queued) that makes no
                // sense from HMRC. This is a programming error, not a network event,
                // so raise rather than silently treating it as a failure.
                throw new InvalidOperationException(
                    $"HmrcClient returned unexpected status '{result.Status}' " +
                    $"for submission {submission.Id}.");
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("rti.transmitted {SubmissionId} {Status} {Attempts}",
            submission.Id, submission.Status, submission.Attempts);

        return SubmissionResponse.From(submission);
    }

    public async Task<SubmissionResponse> RetryAsync(Guid id, CancellationToken ct)
    {
        var submission = await LoadAsync(id, ct);

        if (!RtiSubmissionStateMachine.IsRetryable(submission.Status))
        {
            throw ApiException.Conflict(
                $"A submission in status '{submission.Status}' cannot be retried.");
        }

        // Retry is for a transmission that went wrong. A submission that never
        // passed local validation still holds the payload that failed it, and
        // re-queueing would send that to HMRC with nothing checking it again.
        var validated = await db.SubmissionEvents.AnyAsync(
            e => e.SubmissionId == id && e.ToStatus == RtiSubmissionStatus.Validated, ct);

        if (!validated)
        {
            throw ApiException.Conflict(
                "This submission failed local validation, so retrying it would transmit the same " +
                "invalid payload. Correct the source data and create a new submission.");
        }

        if (submission.Attempts >= MaxAttempts)
        {
            throw ApiException.Conflict(
                $"Submission has reached the maximum of {MaxAttempts} attempts and needs manual review.");
        }

        db.SubmissionEvents.Add(submission.TransitionTo(
            RtiSubmissionStatus.Queued, "requeued",
            $"Requeued after {submission.Status.ToString().ToLowerInvariant()}.", tenant.UserId));
        submission.NextAttemptAt = null;
        await db.SaveChangesAsync(ct);

        return SubmissionResponse.From(submission);
    }

    public async Task<IReadOnlyList<SubmissionResponse>> ListAsync(
        int? taxYear, int? periodNumber, RtiSubmissionStatus? status, CancellationToken ct)
    {
        var query = db.Submissions.AsNoTracking().AsQueryable();

        if (taxYear.HasValue)
        {
            query = query.Where(s => s.TaxYear == taxYear.Value);
        }

        if (periodNumber.HasValue)
        {
            query = query.Where(s => s.PeriodNumber == periodNumber.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(s => s.Status == status.Value);
        }

        var rows = await query.OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(ct);
        return rows.Select(SubmissionResponse.From).ToArray();
    }

    public async Task<SubmissionResponse> GetAsync(Guid id, CancellationToken ct) =>
        SubmissionResponse.From(await LoadAsync(id, ct));

    public async Task<string> GetPayloadAsync(Guid id, CancellationToken ct) =>
        (await LoadAsync(id, ct)).Payload;

    public async Task<IReadOnlyList<SubmissionEventResponse>> GetEventsAsync(
        Guid id, CancellationToken ct)
    {
        _ = await LoadAsync(id, ct);

        var events = await db.SubmissionEvents.AsNoTracking()
            .Where(e => e.SubmissionId == id)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);

        return events.Select(e => new SubmissionEventResponse(
            e.Id, e.EventType, e.FromStatus?.ToString(), e.ToStatus?.ToString(),
            e.Detail, e.ActorUserId, e.OccurredAt)).ToArray();
    }

    // --- internals ----------------------------------------------------------

    private async Task<RtiSubmission> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Submissions.FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw ApiException.NotFound($"Submission {id} was not found.");

    private void ApplyValidation(RtiSubmission submission, ValidationOutcome validation)
    {
        if (validation.IsValid)
        {
            db.SubmissionEvents.Add(submission.TransitionTo(
                RtiSubmissionStatus.Validated, "validated", "Local validation passed.", tenant.UserId));
            return;
        }

        var errors = string.Join(" ", validation.Errors);
        db.SubmissionEvents.Add(submission.TransitionTo(
            RtiSubmissionStatus.Failed, "validation_failed", errors, tenant.UserId));
        submission.LastError = errors;
    }

    private static string FormatNext(RtiSubmission submission)
    {
        var next = RtiSubmissionStateMachine.NextStates(submission.Status);
        return next.Count == 0 ? "none (terminal)" : string.Join(", ", next);
    }

    /// <summary>Exponential backoff, capped: 1, 2, 4, 8, 16 minutes.</summary>
    /// <remarks>
    /// The attempt is clamped before the subtraction, not after. Clamping
    /// <c>attempt - 1</c> overflows for a sufficiently negative attempt, which
    /// wraps to int.MaxValue and yields the longest delay instead of the
    /// shortest - the opposite of what a defensive floor is for.
    /// </remarks>
    internal static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMinutes(Math.Pow(2, Math.Clamp(attempt, 1, MaxAttempts) - 1));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
