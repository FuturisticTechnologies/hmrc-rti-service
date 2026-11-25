// ---------------------------------------------------------------------------
// Statutory Maternity Pay eligibility.
//
// Pure rules over dates and a figure the payroll service supplies; no database,
// no HTTP. Every failed test is reported, not just the first, so a payroll admin
// can fix everything in one pass -- the same shape as LateReportingOutcome.
//
// Weeks run Sunday to Saturday, as they do for SMP. All week arithmetic is
// anchored on the Expected Week of Childbirth (EWC), the week containing the
// due date.
//
// Not covered yet (later SNOV-141 commits or out of scope): the SMP start date
// and its 11-week earliest limit, premature births (which move the qualifying
// week), and deriving average weekly earnings from pay history. AWE is supplied
// by the payroll service, which owns that data.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application.Smp;

public sealed record SmpEligibilityInput
{
    /// <summary>Expected due date. Any day; the EWC is the Sunday-Saturday week containing it.</summary>
    public DateOnly ExpectedDueDate { get; init; }

    public DateOnly EmploymentStartDate { get; init; }

    /// <summary>Null while still employed.</summary>
    public DateOnly? EmploymentEndDate { get; init; }

    /// <summary>The date the employee asked for SMP to start.</summary>
    public DateOnly LeaveStartDate { get; init; }

    /// <summary>When the employee gave notice of the leave start.</summary>
    public DateOnly NoticeGivenDate { get; init; }

    /// <summary>True where 28 days' notice was not possible and it was given as soon as reasonably practicable.</summary>
    public bool NoticeAsSoonAsReasonablyPracticable { get; init; }

    /// <summary>Whether proof of pregnancy (MATB1) has been received.</summary>
    public bool HasEvidenceOfPregnancy { get; init; }

    /// <summary>
    /// Set when the pregnancy ended without a birth that qualifies. A birth, and
    /// a stillbirth after 24 weeks, are not endings for this purpose: leave null.
    /// </summary>
    public DateOnly? PregnancyEndedWithoutBirthDate { get; init; }

    /// <summary>Average weekly earnings over the relevant period, as calculated by payroll.</summary>
    public decimal AverageWeeklyEarnings { get; init; }
}

/// <summary>Outcome of the SMP eligibility tests.</summary>
/// <param name="QualifyingWeekStart">First day (Sunday) of the 15th week before the EWC.</param>
/// <param name="LowerEarningsLimit">The limit AWE was tested against.</param>
/// <param name="Errors">Why the employee does not qualify; empty when eligible.</param>
public sealed record SmpEligibilityOutcome(
    DateOnly QualifyingWeekStart,
    decimal LowerEarningsLimit,
    IReadOnlyList<string> Errors)
{
    public bool IsEligible => Errors.Count == 0;
}

public static class SmpEligibility
{
    private const int QualifyingWeeksBeforeEwc = 15;
    private const int ContinuousEmploymentWeeks = 26;
    private const int PregnancyWeeksBeforeEwc = 11;
    private const int NoticeDays = 28;

    /// <summary>The Sunday that starts the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    /// <exception cref="InvalidOperationException">
    /// No SMP rates are configured for the tax year of the qualifying week. That
    /// is a configuration gap, not an ineligible employee, so it is not reported
    /// as an eligibility failure.
    /// </exception>
    public static SmpEligibilityOutcome Evaluate(SmpEligibilityInput input)
    {
        var ewcStart = WeekStart(input.ExpectedDueDate);
        var qualifyingWeekStart = ewcStart.AddDays(-QualifyingWeeksBeforeEwc * 7);
        var rates = SmpRates.ForDate(qualifyingWeekStart);
        var errors = new List<string>();

        // 26 weeks of continuous employment *ending with* the qualifying week:
        // the qualifying week plus the 25 before it. Starting any later falls short.
        var latestEmploymentStart = qualifyingWeekStart.AddDays(-(ContinuousEmploymentWeeks - 1) * 7);
        if (input.EmploymentStartDate > latestEmploymentStart)
        {
            errors.Add(
                $"Employment started {input.EmploymentStartDate:yyyy-MM-dd}; it must have started on or before " +
                $"{latestEmploymentStart:yyyy-MM-dd} to give {ContinuousEmploymentWeeks} weeks' continuous " +
                $"employment into the qualifying week beginning {qualifyingWeekStart:yyyy-MM-dd}.");
        }

        if (input.EmploymentEndDate is { } ended && ended < qualifyingWeekStart)
        {
            errors.Add(
                $"Employment ended {ended:yyyy-MM-dd}, before the qualifying week beginning " +
                $"{qualifyingWeekStart:yyyy-MM-dd}.");
        }

        if (input.AverageWeeklyEarnings < rates.LowerEarningsLimit)
        {
            errors.Add(
                $"Average weekly earnings {input.AverageWeeklyEarnings:0.00} are below the lower earnings " +
                $"limit of {rates.LowerEarningsLimit:0.00}.");
        }

        var pregnancyWeekStart = ewcStart.AddDays(-PregnancyWeeksBeforeEwc * 7);
        if (input.PregnancyEndedWithoutBirthDate is { } endedOn && endedOn < pregnancyWeekStart)
        {
            errors.Add(
                $"The pregnancy ended {endedOn:yyyy-MM-dd}, before the {PregnancyWeeksBeforeEwc}th week " +
                $"before the expected week of childbirth ({pregnancyWeekStart:yyyy-MM-dd}).");
        }

        var noticeDays = input.LeaveStartDate.DayNumber - input.NoticeGivenDate.DayNumber;
        if (noticeDays < NoticeDays && !input.NoticeAsSoonAsReasonablyPracticable)
        {
            errors.Add(
                $"Notice was given {noticeDays} day(s) before the leave start of " +
                $"{input.LeaveStartDate:yyyy-MM-dd}; {NoticeDays} days are required unless it was given " +
                "as soon as reasonably practicable.");
        }

        if (!input.HasEvidenceOfPregnancy)
        {
            errors.Add("Evidence of pregnancy (MATB1) has not been received.");
        }

        return new SmpEligibilityOutcome(qualifyingWeekStart, rates.LowerEarningsLimit, errors);
    }
}
