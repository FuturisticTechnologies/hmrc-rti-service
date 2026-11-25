using Hmrc.Rti.Api.Application;
using Hmrc.Rti.Api.Application.Smp;
using Payroll.Platform;
using Xunit;

namespace Hmrc.Rti.Tests;

/// <summary>
/// The request-shape tier: what is refused with 400 before anything is built
/// or saved, and - just as important - what is left for the envelope validator
/// to record as a failed submission.
/// </summary>
public class RtiRequestValidatorTests
{
    private const string Paye = "123/AB456";
    private const string AccountsOffice = "123PA00012345";

    // Mid-2026-27, so 2026 is the current tax year.
    private static readonly DateOnly Today = new(2026, 9, 24);

    [Fact]
    public void A_well_formed_eps_request_passes()
    {
        RtiRequestValidator.EnsureValid(ValidEps(), Today);
    }

    [Fact]
    public void A_zero_eps_with_a_no_payment_declaration_passes()
    {
        var request = ValidEps() with
        {
            TotalPayeTax = 0m, TotalEmployeeNi = 0m, TotalEmployerNi = 0m, NoPaymentForPeriod = true,
        };

        RtiRequestValidator.EnsureValid(request, Today);
    }

    [Theory]
    [InlineData(-0.01, "Must not be negative")]
    [InlineData(10.005, "at most 2 decimal places")]
    public void An_eps_amount_that_cannot_be_filed_is_a_field_error(double amount, string expected)
    {
        var request = ValidEps() with { TotalEmployerNi = (decimal)amount };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));

        var messages = Assert.Single(errors).Value;
        Assert.Contains(messages, m => m.Contains(expected));
        Assert.Equal("totalEmployerNi", Assert.Single(errors).Key);
    }

    [Fact]
    public void Every_bad_amount_is_reported_not_just_the_first()
    {
        var request = ValidEps() with { TotalPayeTax = -1m, TotalEmployeeNi = -2m, TotalEmployerNi = 0.001m };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));

        Assert.Equal(["totalEmployeeNi", "totalEmployerNi", "totalPayeTax"], errors.Keys.Order());
    }

    [Fact]
    public void An_eps_with_smp_recovery_and_compensation_passes()
    {
        var request = ValidEps() with { SmpRecovered = 1000.00m, SmpNicCompensation = 30.00m };

        RtiRequestValidator.EnsureValid(request, Today);
    }

    [Theory]
    [InlineData(-0.01, "Must not be negative")]
    [InlineData(10.005, "at most 2 decimal places")]
    public void An_smp_recovery_amount_that_cannot_be_filed_is_a_field_error(double amount, string expected)
    {
        var request = ValidEps() with { SmpRecovered = (decimal)amount };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));

        Assert.Contains(errors["smpRecovered"], m => m.Contains(expected));
    }

    [Fact]
    public void Smp_compensation_cannot_exceed_the_smp_recovered()
    {
        var request = ValidEps() with { SmpRecovered = 10.00m, SmpNicCompensation = 10.01m };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));

        Assert.Contains("cannot exceed", Assert.Single(errors["smpNicCompensation"]));
    }

    [Fact]
    public void Smp_compensation_without_any_recovery_is_refused()
    {
        var request = ValidEps() with { SmpNicCompensation = 5.00m };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));

        Assert.Contains("smpNicCompensation", errors.Keys);
    }

    [Fact]
    public void A_no_payment_declaration_may_still_recover_smp()
    {
        // Recovery is not a liability: an employer can owe nothing and still reclaim.
        var request = ValidEps() with
        {
            TotalPayeTax = 0m, TotalEmployeeNi = 0m, TotalEmployerNi = 0m, NoPaymentForPeriod = true,
            SmpRecovered = 500.00m,
        };

        RtiRequestValidator.EnsureValid(request, Today);
    }

    [Fact]
    public void A_no_payment_declaration_with_liabilities_is_contradictory()
    {
        var request = ValidEps() with { NoPaymentForPeriod = true };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));

        Assert.Contains("noPaymentForPeriod", errors.Keys);
    }

    [Theory]
    [InlineData(2026, true)]  // current
    [InlineData(2025, true)]  // previous - late or corrective filings are legitimate
    [InlineData(2027, false)] // has not started
    public void An_eps_tax_year_may_not_be_in_the_future(int taxYear, bool allowed)
    {
        var request = ValidEps() with { TaxYear = taxYear };

        if (allowed)
        {
            RtiRequestValidator.EnsureValid(request, Today);
            return;
        }

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));
        Assert.Contains("2027-28", Assert.Single(errors["taxYear"]));
    }

    [Fact]
    public void A_null_reference_is_missing_rather_than_a_500()
    {
        var request = ValidEps() with { EmployerPayeReference = null!, AccountsOfficeReference = null! };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request, Today));

        Assert.Contains("employerPayeReference", errors.Keys);
        Assert.Contains("accountsOfficeReference", errors.Keys);
    }

    [Fact]
    public void A_malformed_reference_is_left_for_the_envelope_validator_to_record()
    {
        // Refusing it here would drop the Failed submission the audit trail
        // relies on - see SubmissionServiceTests.
        var request = ValidEps() with { EmployerPayeReference = "not-a-reference" };

        RtiRequestValidator.EnsureValid(request, Today);
    }

    [Fact]
    public void An_fps_request_needs_a_pay_run_id()
    {
        var request = new CreateFpsRequest { EmployerPayeReference = Paye, AccountsOfficeReference = AccountsOffice };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(request));

        Assert.Equal("payRunId", Assert.Single(errors).Key);
    }

    [Fact]
    public void A_well_formed_fps_request_passes()
    {
        RtiRequestValidator.EnsureValid(new CreateFpsRequest
        {
            PayRunId = Guid.NewGuid(), EmployerPayeReference = Paye, AccountsOfficeReference = AccountsOffice,
        });
    }

    // ---------------------------------------------------------- maternity pay

    private static SmpEmployeeRequest Maternity(string number = "001") => new()
    {
        EmployeeNumber = number,
        ExpectedDueDate = new DateOnly(2025, 8, 13),
        EmploymentStartDate = new DateOnly(2023, 1, 1),
        LeaveStartDate = new DateOnly(2025, 6, 1),
        NoticeGivenDate = new DateOnly(2025, 3, 1),
        HasEvidenceOfPregnancy = true,
        AverageWeeklyEarnings = 300m,
    };

    private static CreateFpsRequest FpsWith(params SmpEmployeeRequest[] maternity) => new()
    {
        PayRunId = Guid.NewGuid(),
        EmployerPayeReference = Paye,
        AccountsOfficeReference = AccountsOffice,
        MaternityPay = maternity,
    };

    [Fact]
    public void An_fps_request_with_well_formed_maternity_pay_passes()
    {
        RtiRequestValidator.EnsureValid(FpsWith(Maternity("001"), Maternity("002")));
    }

    [Fact]
    public void An_ineligible_employee_is_not_a_shape_error()
    {
        // Whether she qualifies is for the SMP rules to judge and record, so the
        // failed submission is kept for the audit trail rather than refused here.
        RtiRequestValidator.EnsureValid(FpsWith(Maternity() with { HasEvidenceOfPregnancy = false }));
    }

    [Fact]
    public void A_maternity_entry_needs_an_employee_number()
    {
        var errors = Refused(() => RtiRequestValidator.EnsureValid(FpsWith(Maternity(" "))));

        Assert.Contains("maternityPay[0].employeeNumber", errors.Keys);
    }

    [Fact]
    public void An_employee_cannot_appear_twice()
    {
        var errors = Refused(() => RtiRequestValidator.EnsureValid(FpsWith(Maternity("001"), Maternity(" 001 "))));

        Assert.Contains("appears more than once", Assert.Single(errors["maternityPay[1].employeeNumber"]));
    }

    [Fact]
    public void Omitted_maternity_dates_are_missing_rather_than_year_one()
    {
        var entry = new SmpEmployeeRequest { EmployeeNumber = "001", AverageWeeklyEarnings = 300m };

        var errors = Refused(() => RtiRequestValidator.EnsureValid(FpsWith(entry)));

        Assert.Equal(4, errors.Count);
        foreach (var field in new[] { "expectedDueDate", "employmentStartDate", "leaveStartDate", "noticeGivenDate" })
        {
            Assert.Contains($"maternityPay[0].{field}", errors.Keys);
        }
    }

    [Theory]
    [InlineData(-0.01, "Must not be negative")]
    [InlineData(10.005, "at most 2 decimal places")]
    public void Maternity_earnings_that_cannot_be_money_are_a_field_error(double amount, string expected)
    {
        var errors = Refused(() => RtiRequestValidator.EnsureValid(
            FpsWith(Maternity() with { AverageWeeklyEarnings = (decimal)amount })));

        Assert.Contains(errors["maternityPay[0].averageWeeklyEarnings"], m => m.Contains(expected));
    }

    [Fact]
    public void A_negative_contractual_amount_names_its_week()
    {
        var errors = Refused(() => RtiRequestValidator.EnsureValid(
            FpsWith(Maternity() with { ContractualWeeklyPay = [300m, -1m] })));

        Assert.Contains("maternityPay[0].contractualWeeklyPay[1]", errors.Keys);
        Assert.DoesNotContain("maternityPay[0].contractualWeeklyPay[0]", errors.Keys);
    }

    [Fact]
    public void More_than_thirty_nine_contractual_weeks_is_refused()
    {
        var errors = Refused(() => RtiRequestValidator.EnsureValid(
            FpsWith(Maternity() with { ContractualWeeklyPay = Enumerable.Repeat(100m, 40).ToArray() })));

        Assert.Contains("At most 39", Assert.Single(errors["maternityPay[0].contractualWeeklyPay"]));
    }

    [Theory]
    [InlineData("2027-04-05", 2026)] // last day of 2026-27
    [InlineData("2027-04-06", 2027)] // first day of 2027-28
    [InlineData("2027-01-01", 2026)]
    public void The_tax_year_turns_on_6_april(string date, int expected)
    {
        Assert.Equal(expected, RtiRequestValidator.TaxYearOf(DateOnly.Parse(date)));
    }

    [Fact]
    public void The_tax_year_turns_at_uk_midnight_not_utc_midnight()
    {
        // 23:30 UTC on 5 April is 00:30 BST on 6 April. Skipped where the host
        // has no time zone database, which the validator falls back from.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out _))
        {
            return;
        }

        var today = RtiRequestValidator.UkToday(new DateTimeOffset(2027, 4, 5, 23, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2027, 4, 6), today);
    }

    private static IReadOnlyDictionary<string, string[]> Refused(Action validate)
    {
        var error = Assert.Throws<ApiException>(validate);
        Assert.Equal(400, error.StatusCode);
        Assert.NotNull(error.Errors);
        return error.Errors;
    }

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
}
