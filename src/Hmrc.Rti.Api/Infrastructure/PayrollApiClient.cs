// ---------------------------------------------------------------------------
// Read-only client for the Python payroll API.
//
// The RTI service never recalculates payroll. It reads a committed pay run and
// reports those figures to HMRC verbatim - if RTI disagreed with the ledger, the
// ledger would no longer be the record of what was filed. The caller's own
// bearer token is forwarded so tenant scoping and RBAC are enforced once, by the
// service that owns that data.
// ---------------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Payroll.Platform;

namespace Hmrc.Rti.Api.Infrastructure;

public sealed class PayrollApiOptions
{
    public const string SectionName = "PayrollApi";

    public string BaseUrl { get; set; } = "http://localhost:8000";
    public int TimeoutSeconds { get; set; } = 30;
}

public interface IPayrollApiClient
{
    Task<PayRunDto> GetPayRunAsync(Guid payRunId, string bearerToken, Guid tenantId, CancellationToken ct);
}

public sealed class PayrollApiClient(HttpClient http, ILogger<PayrollApiClient> logger) : IPayrollApiClient
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        // The payroll API is FastAPI + Pydantic, so the wire format is snake_case.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<PayRunDto> GetPayRunAsync(
        Guid payRunId, string bearerToken, Guid tenantId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/pay-runs/{payRunId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.TryAddWithoutValidation("X-Tenant-ID", tenantId.ToString());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "payroll_api.unreachable {PayRunId}", payRunId);
            throw ApiException.BadGateway("The payroll API is unreachable.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Also the answer when the pay run belongs to another tenant -
                // row-level security makes it invisible rather than forbidden.
                throw ApiException.NotFound($"Pay run {payRunId} was not found.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ApiException((int)response.StatusCode,
                    "The payroll API rejected the forwarded credentials.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw ApiException.BadGateway(
                    $"The payroll API returned {(int)response.StatusCode} for pay run {payRunId}.");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var payRun = JsonSerializer.Deserialize<PayRunDto>(body, Json)
                         ?? throw ApiException.BadGateway("The payroll API returned an empty pay run.");

            return payRun;
        }
    }
}

// --- Wire DTOs, mirroring app/schemas/payroll.py -----------------------------

public sealed record PayRunDto
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid PayPeriodId { get; init; }
    public string Status { get; init; } = string.Empty;
    public int EmployeeCount { get; init; }
    public decimal GrossEarnings { get; init; }
    public decimal TotalPayeTax { get; init; }
    public decimal TotalEmployeeNi { get; init; }
    public decimal TotalEmployerNi { get; init; }
    public decimal EmployerPayrollCost { get; init; }
    public decimal TotalDeductions { get; init; }
    public decimal NetPay { get; init; }
    public string Currency { get; init; } = "GBP";
    public string CalculationVersion { get; init; } = string.Empty;
    public string? LedgerEntryHash { get; init; }
    public PayPeriodDto PayPeriod { get; init; } = new();
    public List<PayRunLineDto> Lines { get; init; } = [];
}

public sealed record PayPeriodDto
{
    public Guid Id { get; init; }
    public int TaxYear { get; init; }
    public int PeriodNumber { get; init; }
    public string PayFrequency { get; init; } = "monthly";
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public DateOnly PaymentDate { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed record PayRunLineDto
{
    public Guid Id { get; init; }
    public Guid EmployeeId { get; init; }
    public string EmployeeNumber { get; init; } = string.Empty;
    public string EmployeeName { get; init; } = string.Empty;
    public decimal GrossPay { get; init; }
    public decimal TaxablePay { get; init; }
    public decimal PayeTax { get; init; }
    public string TaxCode { get; init; } = string.Empty;
    public string TaxBasis { get; init; } = string.Empty;
    public decimal TaxDueToDate { get; init; }
    public decimal TaxPaidToDate { get; init; }
    public string NiCategory { get; init; } = string.Empty;
    public decimal NiablePay { get; init; }
    public decimal EmployeeNi { get; init; }
    public decimal EmployerNi { get; init; }
    public decimal EmployeeNiDueToDate { get; init; }
    public decimal EmployerNiDueToDate { get; init; }
    public decimal TotalDeductions { get; init; }
    public decimal NetPay { get; init; }
}
