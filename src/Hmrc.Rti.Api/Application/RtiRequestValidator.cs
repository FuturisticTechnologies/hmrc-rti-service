// ---------------------------------------------------------------------------
// Request-shape checks, run before anything is fetched, built or saved.
//
// Two tiers of validation, on purpose:
//   - here: a request that could never describe a real filing, whatever the
//     payroll data says (negative money, sub-penny amounts, a tax year that
//     has not started). These answer 400 with per-field errors and leave no
//     trace, because there is nothing to audit.
//   - RtiEnvelopeBuilder.Validate*: a well-formed request whose content HMRC
//     would reject. Those are saved as Failed with the payload that failed,
//     so the audit trail shows what was attempted, and answer 422.
// Employer reference formats stay in the second tier for that reason.
// ---------------------------------------------------------------------------
using Payroll.Platform;

namespace Hmrc.Rti.Api.Application;

public static class RtiRequestValidator
{
    public static void EnsureValid(CreateFpsRequest request)
    {
        var errors = new Dictionary<string, List<string>>();

        // Guid.Empty is what an omitted payRunId binds to. Without this the
        // payroll service is asked for pay run 0000... and the caller gets a
        // 404 about a pay run they never named.
        if (request.PayRunId == Guid.Empty)
        {
            Add(errors, "payRunId", "A pay run id is required.");
        }

        CheckReferencesPresent(errors, request.EmployerPayeReference, request.AccountsOfficeReference);
        CheckMaternityPay(errors, request.MaternityPay);
        Throw("FPS request", errors);
    }

    public static void EnsureValid(CreateEpsRequest request, DateOnly ukToday)
    {
        var errors = new Dictionary<string, List<string>>();

        CheckReferencesPresent(errors, request.EmployerPayeReference, request.AccountsOfficeReference);

        var currentTaxYear = TaxYearOf(ukToday);
        if (request.TaxYear > currentTaxYear)
        {
            Add(errors, "taxYear",
                $"Tax year {RtiEnvelopeBuilder.FormatTaxYear(request.TaxYear)} has not started; " +
                $"the current tax year is {RtiEnvelopeBuilder.FormatTaxYear(currentTaxYear)}.");
        }

        CheckAmount(errors, "totalPayeTax", request.TotalPayeTax);
        CheckAmount(errors, "totalEmployeeNi", request.TotalEmployeeNi);
        CheckAmount(errors, "totalEmployerNi", request.TotalEmployerNi);
        CheckAmount(errors, "smpRecovered", request.SmpRecovered);
        CheckAmount(errors, "smpNicCompensation", request.SmpNicCompensation);

        // Compensation is a percentage of SMP that is itself recovered in full
        // or at 92%, so it can never be the larger of the two.
        if (request.SmpNicCompensation > request.SmpRecovered)
        {
            Add(errors, "smpNicCompensation",
                "NIC compensation cannot exceed the SMP recovered; it is a fraction of the SMP paid.");
        }

        // A no-payment declaration tells HMRC nothing is owed for the period.
        // Sending it with liabilities attached leaves HMRC to guess which half
        // the employer meant.
        if (request.NoPaymentForPeriod
            && (request.TotalPayeTax != 0 || request.TotalEmployeeNi != 0 || request.TotalEmployerNi != 0))
        {
            Add(errors, "noPaymentForPeriod",
                "A no-payment declaration cannot carry PAYE or NI totals; set them to 0 or clear the flag.");
        }

        Throw("EPS request", errors);
    }

    /// <summary>
    /// The UK tax year a date falls in, by its starting year: 5 April 2027 is
    /// still 2026 (2026-27); 6 April 2027 is 2027.
    /// </summary>
    public static int TaxYearOf(DateOnly date) =>
        date < new DateOnly(date.Year, 4, 6) ? date.Year - 1 : date.Year;

    /// <summary>
    /// Today's date in the UK. The tax year turns at midnight UK time, which is
    /// an hour ahead of UTC in April; on a host without the time zone database
    /// this falls back to UTC, which only matters for that one hour.
    /// </summary>
    public static DateOnly UkToday(DateTimeOffset now)
    {
        var local = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var uk)
            ? TimeZoneInfo.ConvertTime(now, uk)
            : now.ToUniversalTime();

        return DateOnly.FromDateTime(local.DateTime);
    }

    private static void CheckReferencesPresent(
        Dictionary<string, List<string>> errors, string? payeReference, string? accountsOfficeReference)
    {
        // An explicit JSON null binds straight over the record's string.Empty
        // default, and the service's .Trim() then fails as a 500. Missing is a
        // shape problem; a present-but-malformed reference is the envelope
        // validator's to judge and record.
        if (payeReference is null)
        {
            Add(errors, "employerPayeReference", "An employer PAYE reference is required.");
        }

        if (accountsOfficeReference is null)
        {
            Add(errors, "accountsOfficeReference", "An accounts office reference is required.");
        }
    }

    /// <summary>
    /// Shape only: whether the employee qualifies is for the SMP rules to judge
    /// and record. Dates bind to 0001-01-01 when omitted, which is not a date
    /// anyone meant, so that is treated as missing.
    /// </summary>
    private static void CheckMaternityPay(
        Dictionary<string, List<string>> errors, IReadOnlyList<SmpEmployeeRequest>? entries)
    {
        if (entries is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            var key = $"maternityPay[{i}]";
            var entry = entries[i];

            if (entry is null)
            {
                Add(errors, key, "An entry is required.");
                continue;
            }

            var number = entry.EmployeeNumber?.Trim() ?? string.Empty;
            if (number.Length == 0)
            {
                Add(errors, $"{key}.employeeNumber", "An employee number is required.");
            }
            else if (!seen.Add(number))
            {
                Add(errors, $"{key}.employeeNumber", $"Employee {number} appears more than once.");
            }

            RequireDate(errors, $"{key}.expectedDueDate", entry.ExpectedDueDate);
            RequireDate(errors, $"{key}.employmentStartDate", entry.EmploymentStartDate);
            RequireDate(errors, $"{key}.leaveStartDate", entry.LeaveStartDate);
            RequireDate(errors, $"{key}.noticeGivenDate", entry.NoticeGivenDate);

            CheckAmount(errors, $"{key}.averageWeeklyEarnings", entry.AverageWeeklyEarnings);

            if (entry.ContractualWeeklyPay is { } contractual)
            {
                if (contractual.Count > Smp.SmpRates.MaxPaidWeeks)
                {
                    Add(errors, $"{key}.contractualWeeklyPay",
                        $"At most {Smp.SmpRates.MaxPaidWeeks} weekly amounts (was {contractual.Count}).");
                }

                for (var week = 0; week < contractual.Count; week++)
                {
                    CheckAmount(errors, $"{key}.contractualWeeklyPay[{week}]", contractual[week]);
                }
            }
        }
    }

    private static void RequireDate(Dictionary<string, List<string>> errors, string field, DateOnly value)
    {
        if (value == default)
        {
            Add(errors, field, "A date is required.");
        }
    }

    private static void CheckAmount(Dictionary<string, List<string>> errors, string field, decimal value)
    {
        // An EPS declares what is owed. Recoveries have their own fields on the
        // HMRC schema, so a negative total here is a sign error, not a refund.
        if (value < 0)
        {
            Add(errors, field, $"Must not be negative (was {value}).");
        }

        // Money is filed in pounds and pence. Rounding a sub-penny value on the
        // caller's behalf would file a figure they never sent.
        if (decimal.Round(value, 2) != value)
        {
            Add(errors, field, $"Must have at most 2 decimal places (was {value}).");
        }
    }

    private static void Add(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var messages))
        {
            errors[field] = messages = [];
        }

        messages.Add(message);
    }

    private static void Throw(string what, Dictionary<string, List<string>> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        throw ApiException.BadRequest(
            $"{what} is invalid: " + string.Join(" ", errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))),
            errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }
}
