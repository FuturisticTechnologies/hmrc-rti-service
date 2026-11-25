using System.Text.Json;
using Hmrc.Rti.Api.Application;
using Hmrc.Rti.Api.Application.Smp;
using Hmrc.Rti.Api.Domain;
using Hmrc.Rti.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.Platform;
using Xunit;

namespace Hmrc.Rti.Tests;

/// <summary>
/// The acceptance criterion: the SMP rate change falls on the correct week and
/// is visible as evidence on the pay run -- in the FPS payload and in the audit
/// trail -- and a pay run whose maternity pay cannot be supported is not filed.
/// </summary>
public class SmpFpsEvidenceTests
{
    private const string Paye = "123/AB456";
    private const string AccountsOffice = "123PA00012345";

    // SMP starts Sunday 1 June 2025. Week 7, the first standard-rate week,
    // starts Sunday 13 July 2025.
    private static readonly DateOnly SmpStart = new(2025, 6, 1);
    private static readonly DateOnly RateChangeStart = new(2025, 7, 13);

    private readonly DbContextOptions<RtiDbContext> _options =
        new DbContextOptionsBuilder<RtiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly TenantContext _tenant =
        new() { TenantId = Guid.NewGuid(), UserId = Guid.NewGuid(), IsResolved = true };

    // Due Wednesday 13 August 2025: EWC starts 10 August, qualifying week starts
    // 27 April 2025 (LEL 125), and SMP may start from 25 May. Everything passes.
    private static SmpEmployeeRequest Employee(string number = "001") => new()
    {
        EmployeeNumber = number,
        ExpectedDueDate = new DateOnly(2025, 8, 13),
        EmploymentStartDate = new DateOnly(2023, 1, 1),
        LeaveStartDate = SmpStart,
        NoticeGivenDate = new DateOnly(2025, 3, 1),
        HasEvidenceOfPregnancy = true,
        AverageWeeklyEarnings = 300m,
    };

    private static PayPeriodDto Period(DateOnly start, DateOnly end) => new()
    {
        TaxYear = 2025,
        PeriodNumber = 4,
        PayFrequency = "monthly",
        PeriodStart = start,
        PeriodEnd = end,
        PaymentDate = end.AddDays(-5),
    };

    // July 2025 pay period: contains week 6 (6-12 Jul) through week 10.
    private static readonly PayPeriodDto JulyPeriod = Period(new DateOnly(2025, 7, 6), new DateOnly(2025, 8, 5));

    private PayRunDto PayRun() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenant.TenantId,
        Status = "committed",
        EmployeeCount = 2,
        GrossEarnings = 5000.00m,
        TotalPayeTax = 800.00m,
        TotalEmployeeNi = 300.00m,
        TotalEmployerNi = 420.00m,
        EmployerPayrollCost = 5420.00m,
        NetPay = 3900.00m,
        CalculationVersion = "m5.1",
        PayPeriod = JulyPeriod,
        Lines =
        [
            new PayRunLineDto
            {
                EmployeeId = Guid.NewGuid(), EmployeeNumber = "001", EmployeeName = "A Worker",
                GrossPay = 3000.00m, TaxablePay = 2000.00m, NiablePay = 2500.00m,
                PayeTax = 500.00m, TaxCode = "1257L", TaxBasis = "cumulative",
                NiCategory = "A", EmployeeNi = 180.00m, EmployerNi = 250.00m, NetPay = 2320.00m,
            },
            new PayRunLineDto
            {
                EmployeeId = Guid.NewGuid(), EmployeeNumber = "002", EmployeeName = "B Worker",
                GrossPay = 2000.00m, TaxablePay = 1300.00m, NiablePay = 1600.00m,
                PayeTax = 300.00m, TaxCode = "BR", TaxBasis = "non_cumulative",
                NiCategory = "M", EmployeeNi = 120.00m, EmployerNi = 170.00m, NetPay = 1580.00m,
            },
        ],
    };

    private SubmissionService Service(PayRunDto payRun) =>
        new(new RtiDbContext(_options, _tenant), new FixedPayrollApi(payRun), new NoHmrc(), _tenant,
            NullLogger<SubmissionService>.Instance);

    private CreateFpsRequest Request(PayRunDto payRun, params SmpEmployeeRequest[] maternity) => new()
    {
        PayRunId = payRun.Id,
        EmployerPayeReference = Paye,
        AccountsOfficeReference = AccountsOffice,
        MaternityPay = maternity.Length == 0 ? null : maternity,
    };

    // ------------------------------------------------- evidence on the pay run

    [Fact]
    public async Task The_rate_change_is_in_the_fps_payload_on_week_seven()
    {
        var payRun = PayRun();
        var created = await Service(payRun).CreateFpsAsync(Request(payRun, Employee()), "token", default);

        Assert.Equal(nameof(RtiSubmissionStatus.Validated), created.Status);

        using var doc = JsonDocument.Parse(await Service(payRun).GetPayloadAsync(created.Id, default));
        var smp = doc.RootElement.GetProperty("employees")[0].GetProperty("statutoryMaternityPay");

        Assert.True(smp.GetProperty("eligible").GetBoolean());

        var change = smp.GetProperty("rateChange");
        Assert.Equal(7, change.GetProperty("weekNumber").GetInt32());
        Assert.Equal(RateChangeStart.ToString("yyyy-MM-dd"), change.GetProperty("startDate").GetString());
        Assert.Equal("in_period", change.GetProperty("timing").GetString());
        Assert.Equal(270.00m, change.GetProperty("higherAmount").GetDecimal());
        Assert.Equal(187.18m, change.GetProperty("standardAmount").GetDecimal());
    }

    [Fact]
    public async Task The_payload_lists_the_weeks_in_the_pay_period_with_their_bands()
    {
        var payRun = PayRun();
        var created = await Service(payRun).CreateFpsAsync(Request(payRun, Employee()), "token", default);

        using var doc = JsonDocument.Parse(await Service(payRun).GetPayloadAsync(created.Id, default));
        var weeks = doc.RootElement.GetProperty("employees")[0]
            .GetProperty("statutoryMaternityPay").GetProperty("weeks");

        Assert.Equal(5, weeks.GetArrayLength()); // weeks 6-10

        Assert.Equal(6, weeks[0].GetProperty("weekNumber").GetInt32());
        Assert.Equal("Higher", weeks[0].GetProperty("band").GetString());
        Assert.Equal(270.00m, weeks[0].GetProperty("statutoryAmount").GetDecimal());
        Assert.False(weeks[0].GetProperty("isRateChange").GetBoolean());

        Assert.Equal(7, weeks[1].GetProperty("weekNumber").GetInt32());
        Assert.Equal("Standard", weeks[1].GetProperty("band").GetString());
        Assert.Equal(187.18m, weeks[1].GetProperty("statutoryAmount").GetDecimal());
        Assert.True(weeks[1].GetProperty("isRateChange").GetBoolean());
    }

    [Fact]
    public async Task The_rate_change_is_recorded_in_the_audit_trail()
    {
        var payRun = PayRun();
        var created = await Service(payRun).CreateFpsAsync(Request(payRun, Employee()), "token", default);

        var events = await Service(payRun).GetEventsAsync(created.Id, default);

        var smp = Assert.Single(events, e => e.EventType == "smp_assessed");
        Assert.Contains("employee 001", smp.Detail);
        Assert.Contains("week 7", smp.Detail);
        Assert.Contains("2025-07-13", smp.Detail);
        Assert.Contains("within this pay period", smp.Detail);
    }

    [Fact]
    public async Task Contractual_top_ups_show_in_the_evidence_apart_from_statutory_pay()
    {
        var payRun = PayRun();
        var employee = Employee() with { ContractualWeeklyPay = [300m, 300m, 300m, 300m, 300m, 300m] };
        var created = await Service(payRun).CreateFpsAsync(Request(payRun, employee), "token", default);

        using var doc = JsonDocument.Parse(await Service(payRun).GetPayloadAsync(created.Id, default));
        var week6 = doc.RootElement.GetProperty("employees")[0]
            .GetProperty("statutoryMaternityPay").GetProperty("weeks")[0];

        Assert.Equal(270.00m, week6.GetProperty("statutoryAmount").GetDecimal());
        Assert.Equal(30.00m, week6.GetProperty("topUp").GetDecimal());
        Assert.Equal(300.00m, week6.GetProperty("totalPay").GetDecimal());
    }

    [Fact]
    public async Task Employees_without_maternity_pay_are_filed_exactly_as_before()
    {
        var payRun = PayRun();
        var created = await Service(payRun).CreateFpsAsync(Request(payRun, Employee("001")), "token", default);

        using var doc = JsonDocument.Parse(await Service(payRun).GetPayloadAsync(created.Id, default));
        var employees = doc.RootElement.GetProperty("employees");

        Assert.True(employees[0].TryGetProperty("statutoryMaternityPay", out _));
        Assert.False(employees[1].TryGetProperty("statutoryMaternityPay", out _));
    }

    [Fact]
    public async Task A_pay_run_with_no_maternity_pay_carries_no_smp_evidence_at_all()
    {
        var payRun = PayRun();
        var created = await Service(payRun).CreateFpsAsync(Request(payRun), "token", default);

        var payload = await Service(payRun).GetPayloadAsync(created.Id, default);

        Assert.DoesNotContain("statutoryMaternityPay", payload);
        Assert.DoesNotContain(await Service(payRun).GetEventsAsync(created.Id, default),
            e => e.EventType == "smp_assessed");
    }

    // ------------------------------------------------------- refused, recorded

    [Fact]
    public async Task An_ineligible_employee_fails_local_validation_with_the_reason_and_the_evidence()
    {
        var payRun = PayRun();
        var employee = Employee() with { HasEvidenceOfPregnancy = false };

        var error = await Assert.ThrowsAsync<ApiException>(
            () => Service(payRun).CreateFpsAsync(Request(payRun, employee), "token", default));

        Assert.Equal(422, error.StatusCode);
        Assert.Contains("MATB1", error.Message);

        // Kept as Failed, with the evidence that explains why.
        var stored = Assert.Single(await Service(payRun).ListAsync(null, null, null, default));
        Assert.Equal(nameof(RtiSubmissionStatus.Failed), stored.Status);

        using var doc = JsonDocument.Parse(await Service(payRun).GetPayloadAsync(stored.Id, default));
        var smp = doc.RootElement.GetProperty("employees")[0].GetProperty("statutoryMaternityPay");
        Assert.False(smp.GetProperty("eligible").GetBoolean());
        Assert.Contains("MATB1", smp.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task An_unconfigured_tax_year_blocks_the_filing_rather_than_guessing_a_rate()
    {
        var payRun = PayRun();
        var employee = Employee() with { ExpectedDueDate = new DateOnly(2027, 6, 16) };

        var error = await Assert.ThrowsAsync<ApiException>(
            () => Service(payRun).CreateFpsAsync(Request(payRun, employee), "token", default));

        Assert.Equal(422, error.StatusCode);
        Assert.Contains("No SMP rates are configured", error.Message);
    }

    [Fact]
    public async Task Maternity_pay_for_an_employee_not_on_the_pay_run_blocks_the_filing()
    {
        var payRun = PayRun();

        var error = await Assert.ThrowsAsync<ApiException>(
            () => Service(payRun).CreateFpsAsync(Request(payRun, Employee("999")), "token", default));

        Assert.Equal(422, error.StatusCode);
        Assert.Contains("employee 999", error.Message);
        Assert.Contains("not on this pay run", error.Message);
    }

    // ------------------------------------------------------------ the assessor

    [Theory]
    [InlineData("2025-06-01", "2025-06-30", "after_period")]   // weeks 1-5: change still ahead
    [InlineData("2025-07-06", "2025-08-05", "in_period")]      // contains 13 Jul
    [InlineData("2025-08-06", "2025-09-05", "before_period")]  // change already happened
    public void The_rate_change_is_placed_relative_to_the_pay_period(string start, string end, string expected)
    {
        var evidence = SmpPayRunAssessor.Assess(
            Employee(), Period(DateOnly.Parse(start), DateOnly.Parse(end)));

        Assert.Empty(evidence.Errors);
        Assert.Equal(7, evidence.RateChange!.WeekNumber);
        Assert.Equal(RateChangeStart, evidence.RateChange.StartDate);
        Assert.Equal(expected, evidence.RateChange.Timing);
    }

    [Fact]
    public void A_pay_period_before_the_leave_starts_cannot_carry_smp()
    {
        var evidence = SmpPayRunAssessor.Assess(
            Employee(), Period(new DateOnly(2025, 4, 1), new DateOnly(2025, 4, 30)));

        Assert.True(evidence.Eligible);
        Assert.Contains(evidence.Errors, e => e.Contains("does not overlap"));
        Assert.Empty(evidence.Weeks);
        Assert.Null(evidence.RateChange);
    }

    [Fact]
    public void A_pay_period_after_week_thirty_nine_cannot_carry_smp()
    {
        // SMP ends Sunday 1 March 2026.
        var evidence = SmpPayRunAssessor.Assess(
            Employee(), Period(new DateOnly(2026, 3, 6), new DateOnly(2026, 4, 5)));

        Assert.Contains(evidence.Errors, e => e.Contains("does not overlap"));
        Assert.Equal(new DateOnly(2026, 3, 1), evidence.SmpEndDate);
    }

    [Fact]
    public void A_pay_period_that_straddles_the_end_of_the_leave_still_carries_the_last_week()
    {
        var evidence = SmpPayRunAssessor.Assess(
            Employee(), Period(new DateOnly(2026, 2, 6), new DateOnly(2026, 3, 5)));

        Assert.Empty(evidence.Errors);
        Assert.Equal(39, evidence.Weeks[^1].WeekNumber);
    }

    [Fact]
    public void Ineligible_evidence_carries_every_failed_test()
    {
        var evidence = SmpPayRunAssessor.Assess(
            Employee() with { HasEvidenceOfPregnancy = false, AverageWeeklyEarnings = 50m }, JulyPeriod);

        Assert.False(evidence.Eligible);
        Assert.Equal(2, evidence.Errors.Count);
        Assert.Equal(125m, evidence.LowerEarningsLimit);
        Assert.Equal(new DateOnly(2025, 4, 27), evidence.QualifyingWeekStart);
        Assert.Empty(evidence.Weeks);
    }

    [Fact]
    public void The_audit_line_for_a_period_after_the_change_says_the_rate_changes_later()
    {
        var evidence = SmpPayRunAssessor.Assess(
            Employee(), Period(new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30)));

        var text = SmpPayRunAssessor.Describe(evidence);

        Assert.Contains("weeks 1-5 of 39", text);
        Assert.Contains("changes later", text);
    }

    [Fact]
    public void Entries_are_matched_to_pay_run_lines_by_trimmed_employee_number()
    {
        var payRun = PayRun();

        var assessment = SmpPayRunAssessor.AssessAll([Employee(" 001 ")], payRun);

        Assert.Empty(assessment.Errors);
        Assert.Contains("001", assessment.Evidence.Keys);
    }

    [Fact]
    public void No_requests_means_no_assessment()
    {
        Assert.Same(SmpAssessment.None, SmpPayRunAssessor.AssessAll(null, PayRun()));
        Assert.Same(SmpAssessment.None, SmpPayRunAssessor.AssessAll([], PayRun()));
    }

    // ----------------------------------------------------------------- doubles

    private sealed class FixedPayrollApi(PayRunDto payRun) : IPayrollApiClient
    {
        public Task<PayRunDto> GetPayRunAsync(
            Guid payRunId, string bearerToken, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(payRun);
    }

    private sealed class NoHmrc : IHmrcClient
    {
        public Task<HmrcSubmissionResult> SubmitAsync(RtiSubmission submission, CancellationToken ct) =>
            throw new NotSupportedException("These tests build FPS submissions; none is transmitted.");
    }
}
