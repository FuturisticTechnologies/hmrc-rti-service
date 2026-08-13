// ---------------------------------------------------------------------------
// Builds the RTI envelopes and validates them before anything is transmitted.
//
// Validation is local and deliberately strict: HMRC rejections are slow, opaque
// and land on the employer, so anything we can catch here we catch here.
// ---------------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hmrc.Rti.Api.Domain;
using Hmrc.Rti.Api.Infrastructure;

namespace Hmrc.Rti.Api.Application;

public sealed record ValidationOutcome(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationOutcome Valid { get; } = new(true, []);
    public static ValidationOutcome Invalid(params string[] errors) => new(false, errors);
}

public static partial class RtiEnvelopeBuilder
{
    // HMRC employer PAYE reference: 3 numeric office digits, slash, then the
    // employer reference, e.g. 123/AB456.
    [GeneratedRegex(@"^\d{3}/[A-Z0-9]{1,10}$", RegexOptions.IgnoreCase)]
    private static partial Regex PayeReferencePattern();

    // Accounts office reference, e.g. 123PA00012345.
    [GeneratedRegex(@"^\d{3}P[A-Z]\d{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex AccountsOfficeReferencePattern();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Full Payment Submission - what each employee was paid and what was
    /// deducted. Filed on or before the payment date.
    /// </summary>
    public static string BuildFps(
        PayRunDto payRun, string payeReference, string accountsOfficeReference, string correlationId)
    {
        var envelope = new
        {
            submissionType = "FPS",
            correlationId,
            employer = new
            {
                payeReference,
                accountsOfficeReference,
            },
            taxYear = FormatTaxYear(payRun.PayPeriod.TaxYear),
            period = new
            {
                number = payRun.PayPeriod.PeriodNumber,
                frequency = payRun.PayPeriod.PayFrequency,
                start = payRun.PayPeriod.PeriodStart.ToString("yyyy-MM-dd"),
                end = payRun.PayPeriod.PeriodEnd.ToString("yyyy-MM-dd"),
                paymentDate = payRun.PayPeriod.PaymentDate.ToString("yyyy-MM-dd"),
            },
            payRun = new
            {
                id = payRun.Id,
                calculationVersion = payRun.CalculationVersion,
                // Ties the filing to the immutable ledger entry it was built from.
                ledgerEntryHash = payRun.LedgerEntryHash,
            },
            totals = new
            {
                employeeCount = payRun.EmployeeCount,
                grossEarnings = Money(payRun.GrossEarnings),
                payeTax = Money(payRun.TotalPayeTax),
                employeeNi = Money(payRun.TotalEmployeeNi),
                employerNi = Money(payRun.TotalEmployerNi),
                employerPayrollCost = Money(payRun.EmployerPayrollCost),
                netPay = Money(payRun.NetPay),
            },
            employees = payRun.Lines.Select(line => new
            {
                payrollId = line.EmployeeNumber,
                name = line.EmployeeName,
                pay = new
                {
                    gross = Money(line.GrossPay),
                    taxable = Money(line.TaxablePay),
                    niable = Money(line.NiablePay),
                    net = Money(line.NetPay),
                },
                paye = new
                {
                    taxCode = line.TaxCode,
                    basis = line.TaxBasis,
                    taxDeducted = Money(line.PayeTax),
                    taxDueToDate = Money(line.TaxDueToDate),
                    taxPaidToDate = Money(line.TaxPaidToDate),
                },
                nationalInsurance = new
                {
                    category = line.NiCategory,
                    employee = Money(line.EmployeeNi),
                    employer = Money(line.EmployerNi),
                    employeeToDate = Money(line.EmployeeNiDueToDate),
                    employerToDate = Money(line.EmployerNiDueToDate),
                },
            }).ToArray(),
        };

        return JsonSerializer.Serialize(envelope, Json);
    }

    /// <summary>
    /// Employer Payment Summary - what the employer owes HMRC for the period,
    /// including recoveries and any no-payment declaration.
    /// </summary>
    public static string BuildEps(
        int taxYear,
        int periodNumber,
        string payeReference,
        string accountsOfficeReference,
        string correlationId,
        decimal totalEmployeeNi,
        decimal totalEmployerNi,
        decimal totalPayeTax,
        bool noPaymentForPeriod)
    {
        var envelope = new
        {
            submissionType = "EPS",
            correlationId,
            employer = new { payeReference, accountsOfficeReference },
            taxYear = FormatTaxYear(taxYear),
            period = new { number = periodNumber },
            declaration = new
            {
                noPaymentForPeriod,
                payeTax = Money(totalPayeTax),
                employeeNi = Money(totalEmployeeNi),
                employerNi = Money(totalEmployerNi),
                totalLiability = Money(totalPayeTax + totalEmployeeNi + totalEmployerNi),
            },
        };

        return JsonSerializer.Serialize(envelope, Json);
    }

    public static ValidationOutcome ValidateFps(
        PayRunDto payRun, string payeReference, string accountsOfficeReference)
    {
        var errors = new List<string>();

        errors.AddRange(ValidateEmployerReferences(payeReference, accountsOfficeReference));

        // An uncommitted pay run can still change. Filing one would mean telling
        // HMRC something the ledger does not yet agree with.
        if (!payRun.Status.Equals("committed", StringComparison.OrdinalIgnoreCase)
            && !payRun.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Pay run status is '{payRun.Status}'; only a committed pay run can be filed.");
        }

        if (payRun.Lines.Count == 0)
        {
            errors.Add("Pay run has no employee lines.");
        }

        if (payRun.EmployeeCount != payRun.Lines.Count)
        {
            errors.Add(
                $"Employee count {payRun.EmployeeCount} does not match {payRun.Lines.Count} employee lines.");
        }

        if (payRun.PayPeriod.PeriodNumber is < 1 or > 53)
        {
            errors.Add($"Period number {payRun.PayPeriod.PeriodNumber} is outside the legal range 1-53.");
        }

        if (payRun.PayPeriod.PeriodEnd < payRun.PayPeriod.PeriodStart)
        {
            errors.Add("Period end falls before period start.");
        }

        // Totals must tie to the sum of the lines. If they do not, one of the two
        // is wrong and we must not pick a side on the employer's behalf.
        AssertTotal(errors, "PAYE tax", payRun.TotalPayeTax, payRun.Lines.Sum(l => l.PayeTax));
        AssertTotal(errors, "employee NI", payRun.TotalEmployeeNi, payRun.Lines.Sum(l => l.EmployeeNi));
        AssertTotal(errors, "employer NI", payRun.TotalEmployerNi, payRun.Lines.Sum(l => l.EmployerNi));
        AssertTotal(errors, "gross earnings", payRun.GrossEarnings, payRun.Lines.Sum(l => l.GrossPay));

        foreach (var line in payRun.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.EmployeeNumber))
            {
                errors.Add($"Employee {line.EmployeeId} has no payroll id.");
            }

            if (string.IsNullOrWhiteSpace(line.TaxCode))
            {
                errors.Add($"Employee {line.EmployeeNumber} has no tax code.");
            }

            if (string.IsNullOrWhiteSpace(line.NiCategory))
            {
                errors.Add($"Employee {line.EmployeeNumber} has no NI category.");
            }
        }

        return errors.Count == 0 ? ValidationOutcome.Valid : new ValidationOutcome(false, errors);
    }

    public static ValidationOutcome ValidateEps(
        int taxYear, int periodNumber, string payeReference, string accountsOfficeReference)
    {
        var errors = ValidateEmployerReferences(payeReference, accountsOfficeReference).ToList();

        if (periodNumber is < 1 or > 53)
        {
            errors.Add($"Period number {periodNumber} is outside the legal range 1-53.");
        }

        if (taxYear is < 2000 or > 2100)
        {
            errors.Add($"Tax year {taxYear} is outside the supported range.");
        }

        return errors.Count == 0 ? ValidationOutcome.Valid : new ValidationOutcome(false, errors);
    }

    /// <summary>SHA-256 of the payload, so later alteration is detectable.</summary>
    public static string Hash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    /// <summary>2026 becomes "2026-27", the form HMRC expects.</summary>
    public static string FormatTaxYear(int startYear) => $"{startYear}-{(startYear + 1) % 100:D2}";

    private static IEnumerable<string> ValidateEmployerReferences(
        string payeReference, string accountsOfficeReference)
    {
        if (!PayeReferencePattern().IsMatch(payeReference ?? string.Empty))
        {
            yield return $"Employer PAYE reference '{payeReference}' is not in the form 123/AB456.";
        }

        if (!AccountsOfficeReferencePattern().IsMatch(accountsOfficeReference ?? string.Empty))
        {
            yield return
                $"Accounts office reference '{accountsOfficeReference}' is not in the form 123PA00012345.";
        }
    }

    private static void AssertTotal(List<string> errors, string label, decimal stated, decimal summed)
    {
        // Exact comparison: these are decimals, not floats, and payroll totals
        // are expected to tie to the penny.
        if (stated != summed)
        {
            errors.Add($"Stated {label} total {stated:0.00} does not equal the line sum {summed:0.00}.");
        }
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
