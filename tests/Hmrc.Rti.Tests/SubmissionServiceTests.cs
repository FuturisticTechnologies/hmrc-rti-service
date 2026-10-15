using Hmrc.Rti.Api.Application;
using Hmrc.Rti.Api.Domain;
using Hmrc.Rti.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.Platform;
using Xunit;

namespace Hmrc.Rti.Tests;

/// <summary>
/// The submission lifecycle through SubmissionService. Each call gets a fresh
/// context over the same in-memory store, so what a test sees afterwards is
/// what was saved - the state the next request would find.
/// </summary>
public class SubmissionServiceTests
{
    private const string Paye = "123/AB456";
    private const string AccountsOffice = "123PA00012345";

    private readonly DbContextOptions<RtiDbContext> _options =
        new DbContextOptionsBuilder<RtiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly TenantContext _tenant =
        new() { TenantId = Guid.NewGuid(), UserId = Guid.NewGuid(), IsResolved = true };

    [Fact]
    public async Task A_failed_token_request_leaves_the_submission_retryable()
    {
        // What HmrcClient throws when HMRC refuses the OAuth token request -
        // after the submission has already been saved as Transmitting.
        var hmrc = new ThrowingHmrc(ApiException.BadGateway("HMRC token request failed with 503."));
        var created = await Service(hmrc).CreateEpsAsync(ValidEps(), default);

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(hmrc).TransmitAsync(created.Id, default));
        Assert.Equal(502, error.StatusCode);

        var after = await Service(hmrc).GetAsync(created.Id, default);
        Assert.Equal(nameof(RtiSubmissionStatus.Failed), after.Status);
        Assert.Equal(1, after.Attempts);
        Assert.Contains("token request failed", after.LastError);

        // The way out is open again: retry re-queues it for another attempt.
        var retried = await Service(hmrc).RetryAsync(created.Id, default);
        Assert.Equal(nameof(RtiSubmissionStatus.Queued), retried.Status);
    }

    [Fact]
    public async Task A_submission_that_failed_local_validation_cannot_be_requeued()
    {
        // Kept as Failed for the audit trail, with the payload that failed.
        // Retry used to move it to Queued, and transmit would then have sent
        // that payload to HMRC without anything checking it again.
        var hmrc = new ThrowingHmrc(new InvalidOperationException("HMRC must not be called."));
        var invalid = ValidEps() with { EmployerPayeReference = "not-a-reference" };

        await Assert.ThrowsAsync<ApiException>(() => Service(hmrc).CreateEpsAsync(invalid, default));
        var stored = Assert.Single(await Service(hmrc).ListAsync(null, null, null, default));
        Assert.Equal(nameof(RtiSubmissionStatus.Failed), stored.Status);

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(hmrc).RetryAsync(stored.Id, default));
        Assert.Equal(409, error.StatusCode);

        var after = await Service(hmrc).GetAsync(stored.Id, default);
        Assert.Equal(nameof(RtiSubmissionStatus.Failed), after.Status);
    }

    [Fact]
    public async Task A_malformed_eps_request_is_refused_before_anything_is_saved()
    {
        // Unlike a bad reference, a negative liability is not an attempted
        // filing worth auditing - it is a request no payroll could produce.
        var hmrc = new ThrowingHmrc(new InvalidOperationException("HMRC must not be called."));
        var malformed = ValidEps() with { TotalPayeTax = -800.00m };

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(hmrc).CreateEpsAsync(malformed, default));

        Assert.Equal(400, error.StatusCode);
        Assert.NotNull(error.Errors);
        Assert.Contains("totalPayeTax", error.Errors.Keys);
        Assert.Empty(await Service(hmrc).ListAsync(null, null, null, default));
    }

    [Fact]
    public async Task An_fps_request_without_a_pay_run_is_refused_before_the_payroll_service_is_asked()
    {
        // NoPayrollApi throws if called, so reaching it would surface as a
        // NotSupportedException rather than the 400 asserted here.
        var hmrc = new ThrowingHmrc(new InvalidOperationException("HMRC must not be called."));
        var request = new CreateFpsRequest { EmployerPayeReference = Paye, AccountsOfficeReference = AccountsOffice };

        var error = await Assert.ThrowsAsync<ApiException>(
            () => Service(hmrc).CreateFpsAsync(request, "token", default));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("payRunId", error.Errors!.Keys);
    }

    private SubmissionService Service(IHmrcClient hmrc) =>
        new(new RtiDbContext(_options, _tenant), new NoPayrollApi(), hmrc, _tenant,
            NullLogger<SubmissionService>.Instance);

    private static CreateEpsRequest ValidEps() => new()
    {
        TaxYear = 2026,
        PeriodNumber = 5,
        EmployerPayeReference = Paye,
        AccountsOfficeReference = AccountsOffice,
        TotalPayeTax = 800.00m,
        TotalEmployeeNi = 300.00m,
        TotalEmployerNi = 420.00m,
    };

    private sealed class ThrowingHmrc(Exception error) : IHmrcClient
    {
        public Task<HmrcSubmissionResult> SubmitAsync(RtiSubmission submission, CancellationToken ct) =>
            Task.FromException<HmrcSubmissionResult>(error);
    }

    private sealed class NoPayrollApi : IPayrollApiClient
    {
        public Task<PayRunDto> GetPayRunAsync(
            Guid payRunId, string bearerToken, Guid tenantId, CancellationToken ct) =>
            throw new NotSupportedException("An EPS is not built from a pay run.");
    }
}
