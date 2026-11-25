// ---------------------------------------------------------------------------
// Statutory Maternity Pay evidence for a pay run.
//
// The payroll API sends no maternity data, so the caller supplies it with the
// FPS request. This assesses eligibility, builds the 39-week schedule, and
// reduces it to the weeks that fall inside the pay run's period. That slice is
// what goes into the FPS payload and the audit trail, so the week the rate
// changes from the higher band to the standard band is on the record for the
// pay run it occurs in -- not something to be re-derived from the amounts.
//
// Anything that stops the pay run being filed (ineligible, no rates for the
// year, SMP not running in this period) comes back as errors on the evidence
// and in the assessment, and the submission is recorded as Failed with the
// evidence attached. A pay run is never filed with maternity pay quietly
// dropped or guessed.
// ---------------------------------------------------------------------------
using Hmrc.Rti.Api.Infrastructure;

namespace Hmrc.Rti.Api.Application.Smp;

/// <summary>The maternity facts for one employee on a pay run.</summary>
public sealed record SmpEmployeeRequest
{
    /// <summary>Matches the pay run line's payroll id.</summary>
    public string EmployeeNumber { get; init; } = string.Empty;

    public DateOnly ExpectedDueDate { get; init; }
    public DateOnly EmploymentStartDate { get; init; }
    public DateOnly? EmploymentEndDate { get; init; }

    /// <summary>The SMP start date.</summary>
    public DateOnly LeaveStartDate { get; init; }

    public DateOnly NoticeGivenDate { get; init; }
    public bool NoticeAsSoonAsReasonablyPracticable { get; init; }
    public bool HasEvidenceOfPregnancy { get; init; }
    public DateOnly? PregnancyEndedWithoutBirthDate { get; init; }
    public decimal AverageWeeklyEarnings { get; init; }

    /// <summary>Contractual maternity pay per week, inclusive of SMP; entry 0 is week 1.</summary>
    public IReadOnlyList<decimal>? ContractualWeeklyPay { get; init; }
}

public sealed record SmpWeekEvidence(
    int WeekNumber,
    DateOnly StartDate,
    DateOnly EndDate,
    string Band,
    decimal StatutoryAmount,
    decimal TopUp,
    decimal TotalPay,
    bool IsRateChange);

/// <param name="WeekNumber">The first standard-band week (7).</param>
/// <param name="Timing">
/// Where the change falls relative to this pay run's period: before_period,
/// in_period or after_period.
/// </param>
/// <param name="HigherAmount">Weekly SMP before the change.</param>
/// <param name="StandardAmount">Weekly SMP from the change.</param>
public sealed record SmpRateChangeEvidence(
    int WeekNumber,
    DateOnly StartDate,
    string Timing,
    decimal HigherAmount,
    decimal StandardAmount);

/// <param name="Eligible">Whether the eligibility tests passed.</param>
/// <param name="Errors">Everything that blocks filing; empty when the evidence is clean.</param>
/// <param name="Weeks">The SMP weeks overlapping the pay period. Empty if not assessed.</param>
public sealed record SmpPayRunEvidence(
    string EmployeeNumber,
    bool Eligible,
    IReadOnlyList<string> Errors,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly? QualifyingWeekStart,
    decimal? LowerEarningsLimit,
    decimal AverageWeeklyEarnings,
    DateOnly? SmpStartDate,
    DateOnly? SmpEndDate,
    IReadOnlyList<SmpWeekEvidence> Weeks,
    SmpRateChangeEvidence? RateChange);

public sealed record SmpAssessment(
    IReadOnlyDictionary<string, SmpPayRunEvidence> Evidence,
    IReadOnlyList<string> Errors)
{
    public static SmpAssessment None { get; } =
        new(new Dictionary<string, SmpPayRunEvidence>(), []);
}

public static class SmpPayRunAssessor
{
    public static SmpAssessment AssessAll(IReadOnlyList<SmpEmployeeRequest>? requests, PayRunDto payRun)
    {
        if (requests is null || requests.Count == 0)
        {
            return SmpAssessment.None;
        }

        var onPayRun = payRun.Lines.Select(l => l.EmployeeNumber).ToHashSet(StringComparer.Ordinal);
        var evidence = new Dictionary<string, SmpPayRunEvidence>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var request in requests)
        {
            var number = request.EmployeeNumber.Trim();

            if (!onPayRun.Contains(number))
            {
                errors.Add($"Maternity pay was supplied for employee {number}, who is not on this pay run.");
                continue;
            }

            var result = Assess(request with { EmployeeNumber = number }, payRun.PayPeriod);
            evidence[number] = result;
            errors.AddRange(result.Errors.Select(e => $"Maternity pay for employee {number}: {e}"));
        }

        return new SmpAssessment(evidence, errors);
    }

    public static SmpPayRunEvidence Assess(SmpEmployeeRequest request, PayPeriodDto period)
    {
        SmpEligibilityOutcome eligibility;
        try
        {
            eligibility = SmpEligibility.Evaluate(new SmpEligibilityInput
            {
                ExpectedDueDate = request.ExpectedDueDate,
                EmploymentStartDate = request.EmploymentStartDate,
                EmploymentEndDate = request.EmploymentEndDate,
                LeaveStartDate = request.LeaveStartDate,
                NoticeGivenDate = request.NoticeGivenDate,
                NoticeAsSoonAsReasonablyPracticable = request.NoticeAsSoonAsReasonablyPracticable,
                HasEvidenceOfPregnancy = request.HasEvidenceOfPregnancy,
                PregnancyEndedWithoutBirthDate = request.PregnancyEndedWithoutBirthDate,
                AverageWeeklyEarnings = request.AverageWeeklyEarnings,
            });
        }
        catch (InvalidOperationException ex)
        {
            // No rates for the year: a configuration gap, reported as blocking.
            return Blocked(request, period, eligible: false, null, null, [ex.Message]);
        }

        if (!eligibility.IsEligible)
        {
            return Blocked(
                request, period, eligible: false,
                eligibility.QualifyingWeekStart, eligibility.LowerEarningsLimit, eligibility.Errors);
        }

        SmpSchedule schedule;
        try
        {
            schedule = SmpCalculator.Calculate(new SmpCalculationInput
            {
                SmpStartDate = request.LeaveStartDate,
                AverageWeeklyEarnings = request.AverageWeeklyEarnings,
                ContractualWeeklyPay = request.ContractualWeeklyPay ?? [],
            });
        }
        catch (InvalidOperationException ex)
        {
            return Blocked(
                request, period, eligible: true,
                eligibility.QualifyingWeekStart, eligibility.LowerEarningsLimit, [ex.Message]);
        }

        var inPeriod = schedule.Weeks
            .Where(w => w.EndDate >= period.PeriodStart && w.StartDate <= period.PeriodEnd)
            .ToList();

        if (inPeriod.Count == 0)
        {
            return Blocked(
                request, period, eligible: true,
                eligibility.QualifyingWeekStart, eligibility.LowerEarningsLimit,
                [$"SMP runs {schedule.Weeks[0].StartDate:yyyy-MM-dd} to {schedule.Weeks[^1].EndDate:yyyy-MM-dd}, " +
                 $"which does not overlap the pay period {period.PeriodStart:yyyy-MM-dd} to {period.PeriodEnd:yyyy-MM-dd}."],
                schedule);
        }

        var change = schedule.RateChangeWeek;
        var timing = change.StartDate < period.PeriodStart ? "before_period"
            : change.StartDate > period.PeriodEnd ? "after_period"
            : "in_period";

        return new SmpPayRunEvidence(
            EmployeeNumber: request.EmployeeNumber,
            Eligible: true,
            Errors: [],
            PeriodStart: period.PeriodStart,
            PeriodEnd: period.PeriodEnd,
            QualifyingWeekStart: eligibility.QualifyingWeekStart,
            LowerEarningsLimit: eligibility.LowerEarningsLimit,
            AverageWeeklyEarnings: request.AverageWeeklyEarnings,
            SmpStartDate: schedule.Weeks[0].StartDate,
            SmpEndDate: schedule.Weeks[^1].EndDate,
            Weeks: inPeriod.Select(w => new SmpWeekEvidence(
                w.WeekNumber, w.StartDate, w.EndDate, w.Band.ToString(),
                w.StatutoryAmount, w.TopUp, w.TotalPay, w.IsRateChange)).ToArray(),
            RateChange: new SmpRateChangeEvidence(
                change.WeekNumber,
                change.StartDate,
                timing,
                HigherAmount: schedule.Weeks[change.WeekNumber - 2].StatutoryAmount,
                StandardAmount: change.StatutoryAmount));
    }

    /// <summary>One line for the submission's audit trail.</summary>
    public static string Describe(SmpPayRunEvidence e)
    {
        if (e.Errors.Count > 0)
        {
            return $"SMP for employee {e.EmployeeNumber} blocked the filing: {string.Join(" ", e.Errors)}";
        }

        var first = e.Weeks[0].WeekNumber;
        var last = e.Weeks[^1].WeekNumber;
        var change = e.RateChange!;

        var changeText = change.Timing switch
        {
            "in_period" =>
                $"the rate changes in week {change.WeekNumber} (from {change.StartDate:yyyy-MM-dd}), " +
                $"{change.HigherAmount:0.00} to {change.StandardAmount:0.00} a week, within this pay period",
            "before_period" =>
                $"the rate changed earlier, in week {change.WeekNumber} (from {change.StartDate:yyyy-MM-dd})",
            _ =>
                $"the rate changes later, in week {change.WeekNumber} (from {change.StartDate:yyyy-MM-dd})",
        };

        return $"SMP for employee {e.EmployeeNumber}: weeks {first}-{last} of {SmpRates.MaxPaidWeeks}; {changeText}.";
    }

    private static SmpPayRunEvidence Blocked(
        SmpEmployeeRequest request,
        PayPeriodDto period,
        bool eligible,
        DateOnly? qualifyingWeekStart,
        decimal? lowerEarningsLimit,
        IReadOnlyList<string> errors,
        SmpSchedule? schedule = null) =>
        new(
            EmployeeNumber: request.EmployeeNumber,
            Eligible: eligible,
            Errors: errors,
            PeriodStart: period.PeriodStart,
            PeriodEnd: period.PeriodEnd,
            QualifyingWeekStart: qualifyingWeekStart,
            LowerEarningsLimit: lowerEarningsLimit,
            AverageWeeklyEarnings: request.AverageWeeklyEarnings,
            SmpStartDate: schedule?.Weeks[0].StartDate,
            SmpEndDate: schedule?.Weeks[^1].EndDate,
            Weeks: [],
            RateChange: null);
}
