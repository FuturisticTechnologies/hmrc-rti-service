using System.Text.Json;
using Hmrc.Rti.Api.Application;
using Hmrc.Rti.Api.Infrastructure;
using Xunit;

namespace Hmrc.Rti.Tests;

public class RtiEnvelopeBuilderTests
{
    private const string Paye = "123/AB456";
    private const string AccountsOffice = "123PA00012345";

    // A minimal but internally consistent pay run: totals tie to the line sums.
    private static PayRunDto ValidPayRun() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Status = "committed",
        EmployeeCount = 2,
        GrossEarnings = 5000.00m,
        TotalPayeTax = 800.00m,
        TotalEmployeeNi = 300.00m,
        TotalEmployerNi = 420.00m,
        EmployerPayrollCost = 5420.00m,
        NetPay = 3900.00m,
        CalculationVersion = "m5.1",
        LedgerEntryHash = "abc123",
        PayPeriod = new PayPeriodDto
        {
            TaxYear = 2026,
            PeriodNumber = 5,
            PayFrequency = "monthly",
            PeriodStart = new DateOnly(2026, 8, 6),
            PeriodEnd = new DateOnly(2026, 9, 5),
            PaymentDate = new DateOnly(2026, 8, 31),
        },
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

    [Theory]
    [InlineData(2026, "2026-27")]
    [InlineData(2025, "2025-26")]
    [InlineData(2099, "2099-00")]
    public void Formats_tax_year_the_way_HMRC_expects(int startYear, string expected) =>
        Assert.Equal(expected, RtiEnvelopeBuilder.FormatTaxYear(startYear));

    [Fact]
    public void Accepts_a_consistent_committed_pay_run()
    {
        var result = RtiEnvelopeBuilder.ValidateFps(ValidPayRun(), Paye, AccountsOffice);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Rejects_an_uncommitted_pay_run()
    {
        // Filing a draft would tell HMRC something the ledger has not agreed to yet.
        var payRun = ValidPayRun() with { Status = "draft" };
        var result = RtiEnvelopeBuilder.ValidateFps(payRun, Paye, AccountsOffice);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("committed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_totals_that_do_not_tie_to_the_line_sums()
    {
        // One penny out. If the stated total and the lines disagree, one of them is
        // wrong and we must not pick a side on the employer's behalf.
        var payRun = ValidPayRun() with { TotalPayeTax = 800.01m };
        var result = RtiEnvelopeBuilder.ValidateFps(payRun, Paye, AccountsOffice);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("PAYE tax", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_employee_count_that_disagrees_with_the_lines()
    {
        var payRun = ValidPayRun() with { EmployeeCount = 3 };
        var result = RtiEnvelopeBuilder.ValidateFps(payRun, Paye, AccountsOffice);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Employee count", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("12/AB456")]      // office number too short
    [InlineData("1234/AB456")]    // office number too long
    [InlineData("123-AB456")]     // wrong separator
    [InlineData("")]
    public void Rejects_malformed_employer_paye_references(string paye)
    {
        var result = RtiEnvelopeBuilder.ValidateFps(ValidPayRun(), paye, AccountsOffice);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("PAYE reference", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("123PA0001234")]   // one digit short
    [InlineData("123XA00012345")]  // missing the P
    [InlineData("")]
    public void Rejects_malformed_accounts_office_references(string accountsOffice)
    {
        var result = RtiEnvelopeBuilder.ValidateFps(ValidPayRun(), Paye, accountsOffice);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Accounts office", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_line_with_no_ni_category()
    {
        var payRun = ValidPayRun();
        var broken = payRun with
        {
            Lines = [payRun.Lines[0] with { NiCategory = "" }, payRun.Lines[1]],
        };

        var result = RtiEnvelopeBuilder.ValidateFps(broken, Paye, AccountsOffice);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("NI category", StringComparison.Ordinal));
    }

    [Fact]
    public void Fps_envelope_carries_the_figures_and_the_ledger_link()
    {
        var payRun = ValidPayRun();
        var json = RtiEnvelopeBuilder.BuildFps(payRun, Paye, AccountsOffice, "corr-1");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("FPS", root.GetProperty("submissionType").GetString());
        Assert.Equal("2026-27", root.GetProperty("taxYear").GetString());
        Assert.Equal(5, root.GetProperty("period").GetProperty("number").GetInt32());
        Assert.Equal("2026-08-31", root.GetProperty("period").GetProperty("paymentDate").GetString());

        // The filing is tied back to the immutable ledger entry it was built from.
        Assert.Equal("abc123", root.GetProperty("payRun").GetProperty("ledgerEntryHash").GetString());

        var totals = root.GetProperty("totals");
        Assert.Equal(800.00m, totals.GetProperty("payeTax").GetDecimal());
        Assert.Equal(420.00m, totals.GetProperty("employerNi").GetDecimal());

        var employees = root.GetProperty("employees");
        Assert.Equal(2, employees.GetArrayLength());
        Assert.Equal("1257L", employees[0].GetProperty("paye").GetProperty("taxCode").GetString());
        Assert.Equal("M", employees[1].GetProperty("nationalInsurance").GetProperty("category").GetString());
    }

    [Fact]
    public void Eps_envelope_totals_the_period_liability()
    {
        var json = RtiEnvelopeBuilder.BuildEps(
            2026, 5, Paye, AccountsOffice, "corr-2",
            totalEmployeeNi: 300.00m, totalEmployerNi: 420.00m, totalPayeTax: 800.00m,
            noPaymentForPeriod: false);

        using var doc = JsonDocument.Parse(json);
        var declaration = doc.RootElement.GetProperty("declaration");

        Assert.False(declaration.GetProperty("noPaymentForPeriod").GetBoolean());
        Assert.Equal(1520.00m, declaration.GetProperty("totalLiability").GetDecimal());
    }

    [Fact]
    public void Eps_envelope_reports_smp_recovery_and_compensation_beside_gross_liability()
    {
        var json = RtiEnvelopeBuilder.BuildEps(
            2026, 5, Paye, AccountsOffice, "corr-3",
            totalEmployeeNi: 300.00m, totalEmployerNi: 420.00m, totalPayeTax: 800.00m,
            noPaymentForPeriod: false,
            smpRecovered: 1000.00m, smpNicCompensation: 30.00m);

        using var doc = JsonDocument.Parse(json);
        var declaration = doc.RootElement.GetProperty("declaration");
        var recovery = declaration.GetProperty("statutoryPayRecovery");

        Assert.Equal(1000.00m, recovery.GetProperty("smpRecovered").GetDecimal());
        Assert.Equal(30.00m, recovery.GetProperty("nicCompensationOnSmp").GetDecimal());
        Assert.Equal(1030.00m, recovery.GetProperty("totalRecovered").GetDecimal());

        // Not netted off: the declared liability is unchanged by recovery.
        Assert.Equal(1520.00m, declaration.GetProperty("totalLiability").GetDecimal());
    }

    [Fact]
    public void Eps_envelope_without_smp_reports_zero_recovery()
    {
        var json = RtiEnvelopeBuilder.BuildEps(
            2026, 5, Paye, AccountsOffice, "corr-4",
            totalEmployeeNi: 300.00m, totalEmployerNi: 420.00m, totalPayeTax: 800.00m,
            noPaymentForPeriod: false);

        using var doc = JsonDocument.Parse(json);
        var recovery = doc.RootElement.GetProperty("declaration").GetProperty("statutoryPayRecovery");

        Assert.Equal(0m, recovery.GetProperty("smpRecovered").GetDecimal());
        Assert.Equal(0m, recovery.GetProperty("totalRecovered").GetDecimal());
    }

    [Fact]
    public void Hash_is_stable_and_detects_alteration()
    {
        var payRun = ValidPayRun();
        var a = RtiEnvelopeBuilder.BuildFps(payRun, Paye, AccountsOffice, "corr-fixed");
        var b = RtiEnvelopeBuilder.BuildFps(payRun, Paye, AccountsOffice, "corr-fixed");

        Assert.Equal(RtiEnvelopeBuilder.Hash(a), RtiEnvelopeBuilder.Hash(b));
        Assert.NotEqual(RtiEnvelopeBuilder.Hash(a), RtiEnvelopeBuilder.Hash(a + " "));
    }
}
