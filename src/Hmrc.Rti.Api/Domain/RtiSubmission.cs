// ---------------------------------------------------------------------------
// RTI submission domain.
//
// Covers SOW milestones M6 (FPS) and M7 (EPS, error handling, reconciliation).
// A submission is an immutable-by-intent record of "we told HMRC this"; every
// status change appends an event rather than overwriting history, matching the
// append-only ledger approach delivered in M3.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Domain;

public enum RtiSubmissionType
{
    /// <summary>Full Payment Submission - one per pay run.</summary>
    Fps = 1,

    /// <summary>Employer Payment Summary - per tax period.</summary>
    Eps = 2,
}

public enum RtiSubmissionStatus
{
    /// <summary>Built from payroll data, not yet checked.</summary>
    Draft = 0,

    /// <summary>Passed local validation, safe to queue.</summary>
    Validated = 1,

    /// <summary>Accepted into the transmission queue.</summary>
    Queued = 2,

    /// <summary>In flight to HMRC.</summary>
    Transmitting = 3,

    /// <summary>HMRC accepted it. Terminal.</summary>
    Accepted = 4,

    /// <summary>HMRC rejected it on business grounds. Correctable, then retryable.</summary>
    Rejected = 5,

    /// <summary>Transport or local failure. Retryable.</summary>
    Failed = 6,
}

/// <summary>Raised when a status change is not in the legal transition table.</summary>
public sealed class IllegalTransitionException(
    RtiSubmissionStatus from, RtiSubmissionStatus to, IReadOnlyList<RtiSubmissionStatus> allowed)
    : InvalidOperationException(
        $"Illegal transition {from} -> {to}. Allowed: " +
        (allowed.Count == 0 ? "none (terminal)" : string.Join(", ", allowed)) + ".")
{
    public RtiSubmissionStatus From { get; } = from;
    public RtiSubmissionStatus To { get; } = to;
}

/// <summary>
/// Legal status transitions. Anything not listed here is refused - an unmapped
/// transition raises rather than being allowed through, on the same reasoning as
/// the NI category matrix: a wrong-but-plausible state is worse than an error.
/// Every status has its own arm and there is no default, so a status added to the
/// enum without a decision here raises instead of silently behaving as terminal.
/// </summary>
public static class RtiSubmissionStateMachine
{
    private static readonly RtiSubmissionStatus[] None = [];

    public static IReadOnlyList<RtiSubmissionStatus> NextStates(RtiSubmissionStatus from) => from switch
    {
        RtiSubmissionStatus.Draft =>
            [RtiSubmissionStatus.Validated, RtiSubmissionStatus.Failed],
        RtiSubmissionStatus.Validated =>
            [RtiSubmissionStatus.Queued, RtiSubmissionStatus.Failed],
        RtiSubmissionStatus.Queued =>
            [RtiSubmissionStatus.Transmitting, RtiSubmissionStatus.Failed],
        RtiSubmissionStatus.Transmitting =>
            [RtiSubmissionStatus.Accepted, RtiSubmissionStatus.Rejected, RtiSubmissionStatus.Failed],
        // Rejected and Failed are both recoverable: correct, then re-queue.
        RtiSubmissionStatus.Rejected => [RtiSubmissionStatus.Queued],
        RtiSubmissionStatus.Failed => [RtiSubmissionStatus.Queued],
        // Accepted is terminal. An accepted submission is amended by sending a
        // further FPS, never by mutating the one HMRC already holds.
        RtiSubmissionStatus.Accepted => None,
        _ => throw new ArgumentOutOfRangeException(
            nameof(from), from, "Status has no entry in the RTI state machine."),
    };

    public static bool CanTransition(RtiSubmissionStatus from, RtiSubmissionStatus to)
    {
        ValidateDefined(to);
        return NextStates(from).Contains(to);
    }

    /// <summary>Raises <see cref="IllegalTransitionException"/> unless the move is legal.</summary>
    public static void EnsureCanTransition(RtiSubmissionStatus from, RtiSubmissionStatus to)
    {
        ValidateDefined(to);
        var allowed = NextStates(from);
        if (!allowed.Contains(to))
        {
            throw new IllegalTransitionException(from, to, allowed);
        }
    }

    public static bool IsTerminal(RtiSubmissionStatus status) => status switch
    {
        RtiSubmissionStatus.Accepted => true,
        RtiSubmissionStatus.Draft or RtiSubmissionStatus.Validated or RtiSubmissionStatus.Queued
            or RtiSubmissionStatus.Transmitting or RtiSubmissionStatus.Rejected
            or RtiSubmissionStatus.Failed => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Status has no entry in the RTI state machine."),
    };

    public static bool IsRetryable(RtiSubmissionStatus status) => status switch
    {
        RtiSubmissionStatus.Failed or RtiSubmissionStatus.Rejected => true,
        RtiSubmissionStatus.Draft or RtiSubmissionStatus.Validated or RtiSubmissionStatus.Queued
            or RtiSubmissionStatus.Transmitting or RtiSubmissionStatus.Accepted => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Status has no entry in the RTI state machine."),
    };

    private static void ValidateDefined(RtiSubmissionStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Status has no entry in the RTI state machine.");
        }
    }
}

public sealed class RtiSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant scope. Enforced by RLS on rti.rti_submissions.</summary>
    public Guid TenantId { get; set; }

    public RtiSubmissionType SubmissionType { get; set; }

    /// <summary>
    /// Assigned only by <see cref="TransitionTo"/>. The setter is private to
    /// prevent any code path from bypassing the state machine; EF Core maps it
    /// via its built-in private-setter support.
    /// </summary>
    public RtiSubmissionStatus Status { get; private set; } = RtiSubmissionStatus.Draft;

    /// <summary>UK tax year the submission belongs to, e.g. 2026 for 2026/27.</summary>
    public int TaxYear { get; set; }

    /// <summary>Tax period number, 1-53. Period 1 starts 6 April.</summary>
    public int PeriodNumber { get; set; }

    /// <summary>Source pay run in the payroll service. Null for a standalone EPS.</summary>
    public Guid? PayRunId { get; set; }

    public string EmployerPayeReference { get; set; } = string.Empty;
    public string AccountsOfficeReference { get; set; } = string.Empty;

    /// <summary>Our correlation id, echoed in logs and to HMRC.</summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The built envelope, stored so the exact payload sent is reproducible.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>SHA-256 of Payload - detects any post-hoc alteration.</summary>
    public string PayloadHash { get; set; } = string.Empty;

    public int EmployeeCount { get; set; }
    public decimal TotalPayeTax { get; set; }
    public decimal TotalEmployeeNi { get; set; }
    public decimal TotalEmployerNi { get; set; }

    /// <summary>Identifier HMRC returns on acceptance.</summary>
    public string? HmrcSubmissionId { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>True when this went to the real HMRC endpoint rather than the sandbox.</summary>
    public bool IsLive { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }

    public List<RtiSubmissionEvent> Events { get; set; } = [];

    /// <summary>
    /// Appends a free-form audit event that is not associated with a status
    /// change, for example the initial "created" record. Use
    /// <see cref="TransitionTo"/> for any change that moves the status.
    /// </summary>
    public RtiSubmissionEvent AppendEvent(string eventType, string detail, Guid? actorUserId = null)
    {
        var evt = new RtiSubmissionEvent
        {
            SubmissionId = Id,
            TenantId = TenantId,
            EventType = eventType,
            Detail = detail.Length <= 2000 ? detail : detail[..2000],
            ActorUserId = actorUserId == Guid.Empty ? null : actorUserId,
        };
        Events.Add(evt);
        return evt;
    }

    /// <summary>
    /// Moves the submission to a new status. Validates the move, assigns the
    /// status and appends the audit event in one step, so the two cannot drift
    /// apart. Raises <see cref="IllegalTransitionException"/> and changes nothing
    /// when the move is not legal.
    /// </summary>
    public RtiSubmissionEvent TransitionTo(
        RtiSubmissionStatus to, string eventType, string detail, Guid? actorUserId = null)
    {
        var from = Status;
        RtiSubmissionStateMachine.EnsureCanTransition(from, to);

        var now = DateTimeOffset.UtcNow;
        Status = to;
        UpdatedAt = now;

        var evt = new RtiSubmissionEvent
        {
            SubmissionId = Id,
            TenantId = TenantId,
            EventType = eventType,
            FromStatus = from,
            ToStatus = to,
            Detail = detail.Length <= 2000 ? detail : detail[..2000],
            ActorUserId = actorUserId == Guid.Empty ? null : actorUserId,
            OccurredAt = now,
        };
        Events.Add(evt);
        return evt;
    }
}

/// <summary>
/// Append-only audit of everything that happened to a submission. Insert and
/// select only - the migration withholds UPDATE and DELETE from the app role.
/// </summary>
public sealed class RtiSubmissionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubmissionId { get; set; }
    public Guid TenantId { get; set; }

    public string EventType { get; set; } = string.Empty;
    public RtiSubmissionStatus? FromStatus { get; set; }
    public RtiSubmissionStatus? ToStatus { get; set; }

    /// <summary>Human-readable explanation, safe to show a payroll admin.</summary>
    public string Detail { get; set; } = string.Empty;

    public Guid? ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
