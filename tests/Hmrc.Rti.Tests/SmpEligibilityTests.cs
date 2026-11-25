using Hmrc.Rti.Api.Application.Smp;
using Xunit;

namespace Hmrc.Rti.Tests;

public class SmpEligibilityTests
{
    // Due Wednesday 17 June 2026 -> EWC starts Sunday 14 June 2026.
    // Qualifying week (15th before) starts Sunday 1 March 2026, in tax year 2025-26.
    private static readonly DateOnly DueDate = new(2026, 6, 17);
    private static readonly DateOnly QualifyingWeekStart = new(2026, 3, 1);

    // 26 weeks ending with the qualifying week => must start on or before this Sunday.
    private static readonly DateOnly LatestEmploymentStart = new(2025, 9, 7);

    private static readonly DateOnly LeaveStart = new(2026, 5, 3);

    private static SmpEligibilityInput Eligible() => new()
    {
        ExpectedDueDate = DueDate,
        EmploymentStartDate = new DateOnly(2024, 1, 1),
        LeaveStartDate = LeaveStart,
        NoticeGivenDate = LeaveStart.AddDays(-63),
        HasEvidenceOfPregnancy = true,
        AverageWeeklyEarnings = 300m,
    };

    // ------------------------------------------------------------ week anchors

    [Theory]
    [InlineData(2026, 6, 14, 2026, 6, 14)] // Sunday is its own week start
    [InlineData(2026, 6, 17, 2026, 6, 14)] // Wednesday
    [InlineData(2026, 6, 20, 2026, 6, 14)] // Saturday is the last day of the week
    [InlineData(2026, 6, 21, 2026, 6, 21)] // next Sunday starts a new week
    public void WeeksRunSundayToSaturday(int y, int m, int d, int ey, int em, int ed)
    {
        Assert.Equal(new DateOnly(ey, em, ed), SmpEligibility.WeekStart(new DateOnly(y, m, d)));
    }

    [Fact]
    public void QualifyingWeekIsTheFifteenthWeekBeforeTheEwc()
    {
        var outcome = SmpEligibility.Evaluate(Eligible());

        Assert.Equal(QualifyingWeekStart, outcome.QualifyingWeekStart);
        Assert.Equal(DayOfWeek.Sunday, outcome.QualifyingWeekStart.DayOfWeek);
    }

    [Fact]
    public void QualifyingWeekDoesNotMoveAcrossTheDaysOfTheEwc()
    {
        // Sunday and Saturday of the same EWC give the same qualifying week.
        var sunday = SmpEligibility.Evaluate(Eligible() with { ExpectedDueDate = new DateOnly(2026, 6, 14) });
        var saturday = SmpEligibility.Evaluate(Eligible() with { ExpectedDueDate = new DateOnly(2026, 6, 20) });

        Assert.Equal(sunday.QualifyingWeekStart, saturday.QualifyingWeekStart);
    }

    // ------------------------------------------------------------------ happy

    [Fact]
    public void AnEmployeeWhoMeetsEveryTestIsEligible()
    {
        var outcome = SmpEligibility.Evaluate(Eligible());

        Assert.True(outcome.IsEligible);
        Assert.Empty(outcome.Errors);
    }

    // ------------------------------------------------- continuous employment

    [Fact]
    public void EmploymentStartingOnTheLatestQualifyingDateIsEnough()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { EmploymentStartDate = LatestEmploymentStart });

        Assert.True(outcome.IsEligible);
    }

    [Fact]
    public void EmploymentStartingOneDayTooLateIsNotEnough()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { EmploymentStartDate = LatestEmploymentStart.AddDays(1) });

        Assert.False(outcome.IsEligible);
        Assert.Contains(outcome.Errors, e => e.Contains("continuous employment"));
    }

    [Fact]
    public void EmploymentEndingBeforeTheQualifyingWeekIsNotEligible()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { EmploymentEndDate = QualifyingWeekStart.AddDays(-1) });

        Assert.False(outcome.IsEligible);
        Assert.Contains(outcome.Errors, e => e.Contains("before the qualifying week"));
    }

    [Fact]
    public void EmploymentEndingInTheQualifyingWeekIsStillEligible()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { EmploymentEndDate = QualifyingWeekStart });

        Assert.True(outcome.IsEligible);
    }

    [Fact]
    public void EmploymentEndingAfterTheQualifyingWeekIsStillEligible()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { EmploymentEndDate = QualifyingWeekStart.AddDays(60) });

        Assert.True(outcome.IsEligible);
    }

    // ---------------------------------------------------------------- earnings

    [Fact]
    public void EarningsExactlyAtTheLowerEarningsLimitQualify()
    {
        var outcome = SmpEligibility.Evaluate(Eligible() with { AverageWeeklyEarnings = 125m });

        Assert.True(outcome.IsEligible);
        Assert.Equal(125m, outcome.LowerEarningsLimit);
    }

    [Fact]
    public void EarningsAPennyBelowTheLowerEarningsLimitDoNotQualify()
    {
        var outcome = SmpEligibility.Evaluate(Eligible() with { AverageWeeklyEarnings = 124.99m });

        Assert.False(outcome.IsEligible);
        Assert.Contains(outcome.Errors, e => e.Contains("lower earnings limit"));
    }

    [Fact]
    public void TheLowerEarningsLimitIsTheOneInForceInTheQualifyingWeek()
    {
        // Qualifying week 30 Mar 2025 is still 2024-25 (LEL 123); 6 Apr 2025 is 2025-26 (LEL 125).
        var in202425 = Eligible() with { ExpectedDueDate = new DateOnly(2025, 7, 16), AverageWeeklyEarnings = 124m,
            EmploymentStartDate = new DateOnly(2023, 1, 1), LeaveStartDate = new DateOnly(2025, 5, 4),
            NoticeGivenDate = new DateOnly(2025, 3, 1) };
        var in202526 = in202425 with { ExpectedDueDate = new DateOnly(2025, 7, 23) };

        var before = SmpEligibility.Evaluate(in202425);
        var after = SmpEligibility.Evaluate(in202526);

        Assert.Equal(new DateOnly(2025, 3, 30), before.QualifyingWeekStart);
        Assert.Equal(123m, before.LowerEarningsLimit);
        Assert.True(before.IsEligible);

        Assert.Equal(new DateOnly(2025, 4, 6), after.QualifyingWeekStart);
        Assert.Equal(125m, after.LowerEarningsLimit);
        Assert.False(after.IsEligible);
    }

    // --------------------------------------------------------------- pregnancy

    [Fact]
    public void PregnancyEndingBeforeTheEleventhWeekIsNotEligible()
    {
        // 11th week before the EWC starts Sunday 29 March 2026.
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { PregnancyEndedWithoutBirthDate = new DateOnly(2026, 3, 28) });

        Assert.False(outcome.IsEligible);
        Assert.Contains(outcome.Errors, e => e.Contains("pregnancy ended"));
    }

    [Fact]
    public void PregnancyEndingInTheEleventhWeekIsStillEligible()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { PregnancyEndedWithoutBirthDate = new DateOnly(2026, 3, 29) });

        Assert.True(outcome.IsEligible);
    }

    // ------------------------------------------------------------------ notice

    [Fact]
    public void TwentyEightDaysNoticeIsEnough()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { NoticeGivenDate = LeaveStart.AddDays(-28) });

        Assert.True(outcome.IsEligible);
    }

    [Fact]
    public void TwentySevenDaysNoticeIsNotEnough()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { NoticeGivenDate = LeaveStart.AddDays(-27) });

        Assert.False(outcome.IsEligible);
        Assert.Contains(outcome.Errors, e => e.Contains("Notice was given 27 day(s)"));
    }

    [Fact]
    public void ShortNoticeIsAcceptedWhenGivenAsSoonAsReasonablyPracticable()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with
            {
                NoticeGivenDate = LeaveStart.AddDays(-3),
                NoticeAsSoonAsReasonablyPracticable = true,
            });

        Assert.True(outcome.IsEligible);
    }

    [Fact]
    public void NoticeGivenAfterTheLeaveStartIsNotEnough()
    {
        var outcome = SmpEligibility.Evaluate(
            Eligible() with { NoticeGivenDate = LeaveStart.AddDays(2) });

        Assert.False(outcome.IsEligible);
    }

    // ------------------------------------------------------------- earliest start

    [Fact]
    public void SmpCannotStartBeforeTheEleventhWeekBeforeTheEwc()
    {
        // The 11th week before the EWC starts Sunday 29 March 2026.
        var outcome = SmpEligibility.Evaluate(Eligible() with
        {
            LeaveStartDate = new DateOnly(2026, 3, 28),
            NoticeGivenDate = new DateOnly(2026, 1, 1),
        });

        Assert.False(outcome.IsEligible);
        Assert.Contains(outcome.Errors, e => e.Contains("cannot start before"));
    }

    [Fact]
    public void SmpMayStartOnTheFirstDayOfTheEleventhWeek()
    {
        var outcome = SmpEligibility.Evaluate(Eligible() with
        {
            LeaveStartDate = new DateOnly(2026, 3, 29),
            NoticeGivenDate = new DateOnly(2026, 1, 1),
        });

        Assert.True(outcome.IsEligible);
    }

    // ---------------------------------------------------------------- evidence

    [Fact]
    public void MissingEvidenceOfPregnancyIsNotEligible()
    {
        var outcome = SmpEligibility.Evaluate(Eligible() with { HasEvidenceOfPregnancy = false });

        Assert.False(outcome.IsEligible);
        Assert.Contains(outcome.Errors, e => e.Contains("MATB1"));
    }

    // --------------------------------------------------------------- reporting

    [Fact]
    public void EveryFailedTestIsReportedNotJustTheFirst()
    {
        var outcome = SmpEligibility.Evaluate(Eligible() with
        {
            EmploymentStartDate = LatestEmploymentStart.AddDays(30),
            AverageWeeklyEarnings = 50m,
            NoticeGivenDate = LeaveStart.AddDays(-5),
            HasEvidenceOfPregnancy = false,
        });

        Assert.Equal(4, outcome.Errors.Count);
    }

    // ------------------------------------------------------------------- rates

    [Fact]
    public void AnUnconfiguredTaxYearRaisesRatherThanGuessing()
    {
        // Qualifying week in 2029-30: no rates configured.
        var input = Eligible() with { ExpectedDueDate = new DateOnly(2030, 6, 12) };

        Assert.Throws<InvalidOperationException>(() => SmpEligibility.Evaluate(input));
    }

    [Theory]
    [InlineData(2024, 184.03, 123)]
    [InlineData(2025, 187.18, 125)]
    public void ConfiguredRatesMatchThePublishedFigures(int taxYear, double standard, double lel)
    {
        var rates = SmpRates.ForTaxYear(taxYear);

        Assert.Equal((decimal)standard, rates.StandardWeeklyRate);
        Assert.Equal((decimal)lel, rates.LowerEarningsLimit);
    }

    [Fact]
    public void TryGetReportsAMissingYearWithoutThrowing()
    {
        Assert.False(SmpRates.TryGet(1999, out var rates));
        Assert.Null(rates);
    }

    [Fact]
    public void RatesAreLookedUpByTheTaxYearOfTheDate()
    {
        Assert.Equal(2024, SmpRates.ForDate(new DateOnly(2025, 4, 5)).TaxYear);
        Assert.Equal(2025, SmpRates.ForDate(new DateOnly(2025, 4, 6)).TaxYear);
    }

    [Fact]
    public void BandConstantsAreHmrcs()
    {
        Assert.Equal(6, SmpRates.HigherRateWeeks);
        Assert.Equal(39, SmpRates.MaxPaidWeeks);
        Assert.Equal(0.90m, SmpRates.HigherRateFraction);
    }
}
