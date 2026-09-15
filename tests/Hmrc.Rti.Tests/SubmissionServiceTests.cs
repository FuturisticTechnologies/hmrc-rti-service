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
