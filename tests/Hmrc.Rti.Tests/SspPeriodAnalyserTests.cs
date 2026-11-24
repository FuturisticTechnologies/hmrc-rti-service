using Hmrc.Rti.Api.Application.Ssp;
using Xunit;

namespace Hmrc.Rti.Tests;

public class SspPeriodAnalyserTests
{
    // Monday under the 2025/26 rules: three waiting days, flat rate.
    private static readonly DateOnly OldMonday = new(2025, 9, 1);

    // Monday under the rules from 6 April 2026: no waiting days.
    private static readonly DateOnly NewMonday = new(2026, 8, 3);

    private static SicknessPeriod Spell(DateOnly start, int calendarDays) =>
        new(start, start.AddDays(calendarDays - 1));

    private static SspPeriodAnalysis Analyse(params SicknessPeriod[] spells) =>
        SspPeriodAnalyser.Analyse(spells, QualifyingDayPattern.Weekdays);

    // ------------------------------------------------- period of incapacity

    [Fact]
    public void NoSicknessGivesNothing()
    {
        var result = Analyse();

        Assert.Empty(result.Chains);
        Assert.Empty(result.ShortSpells);
    }

    [Fact]
    public void AThreeDaySpellIsNotAPeriodOfIncapacityAndIsReported()
    {
        var spell = Spell(OldMonday, 3);

        var result = Analyse(spell);

        Assert.Empty(result.Chains);
        Assert.Equal(spell, Assert.Single(result.ShortSpells));
    }

    [Fact]
    public void AFourDaySpellIsAPeriodOfIncapacity()
    {
        var result = Analyse(Spell(OldMonday, 4));

        var chain = Assert.Single(result.Chains);
        var piw = Assert.Single(chain.Periods);
        Assert.Equal(4, piw.CalendarDays);
        Assert.Equal(4, piw.QualifyingDays);
        Assert.Empty(result.ShortSpells);
    }

    [Fact]
    public void NonQualifyingDaysCountTowardsTheFourDaysButNotTheQualifyingDays()
    {
        // Saturday to Tuesday: four calendar days, two of them weekdays.
        var result = Analyse(Spell(OldMonday.AddDays(5), 4));

        var piw = Assert.Single(Assert.Single(result.Chains).Periods);
        Assert.Equal(2, piw.QualifyingDays);
    }

    [Fact]
    public void AdjacentSpellsAreOneContinuousSpell()
    {
        // Monday-Tuesday then Wednesday-Thursday: neither is a PIW alone.
        var result = Analyse(Spell(OldMonday, 2), Spell(OldMonday.AddDays(2), 2));

        var piw = Assert.Single(Assert.Single(result.Chains).Periods);
        Assert.Equal(OldMonday, piw.Start);
        Assert.Equal(OldMonday.AddDays(3), piw.End);
        Assert.Empty(result.ShortSpells);
    }

    [Fact]
    public void OverlappingSpellsAreMergedWithoutDoubleCounting()
    {
        var result = Analyse(Spell(OldMonday, 5), Spell(OldMonday.AddDays(3), 5));

        var piw = Assert.Single(Assert.Single(result.Chains).Periods);
        Assert.Equal(OldMonday.AddDays(7), piw.End);
        Assert.Equal(8, piw.CalendarDays);
    }

    [Fact]
    public void InputOrderDoesNotMatter()
    {
        var first = Spell(OldMonday, 4);
        var second = Spell(OldMonday.AddDays(14), 4);

        var forward = Analyse(first, second);
        var reversed = Analyse(second, first);

        Assert.Equal(forward.Chains[0].Periods, reversed.Chains[0].Periods);
    }

    // ------------------------------------------------------------ waiting days

    [Fact]
    public void TheFirstThreeQualifyingDaysAreWaitingDaysUnderTheOldRules()
    {
        // Monday to Friday.
        var chain = Assert.Single(Analyse(Spell(OldMonday, 5)).Chains);

        Assert.Equal(
            new[] { OldMonday, OldMonday.AddDays(1), OldMonday.AddDays(2) },
            chain.WaitingDates);
        Assert.Equal(
            new[] { OldMonday.AddDays(3), OldMonday.AddDays(4) },
            chain.PayableDates);
    }

    [Fact]
    public void WeekendDaysAreNeverWaitingOrPayableDays()
    {
        // Friday to the following Tuesday: Fri, Mon and Tue are qualifying.
        var chain = Assert.Single(Analyse(Spell(OldMonday.AddDays(4), 5)).Chains);

        Assert.Equal(3, chain.WaitingDates.Count);
        Assert.Empty(chain.PayableDates);
    }

    [Fact]
    public void ThereAreNoWaitingDaysFromApril2026()
    {
        var chain = Assert.Single(Analyse(Spell(NewMonday, 5)).Chains);

        Assert.Empty(chain.WaitingDates);
        Assert.Equal(5, chain.PayableDates.Count);
    }

    [Fact]
    public void WaitingDaysAreServedOncePerChainAndCarryAcrossLinkedPeriods()
    {
        // One qualifying day a week (Monday), so each four-day PIW holds one.
        var mondaysOnly = new QualifyingDayPattern([DayOfWeek.Monday]);
        var spells = new[]
        {
            Spell(OldMonday, 4),                // Monday 1 Sep
            Spell(OldMonday.AddDays(14), 4),    // Monday 15 Sep
            Spell(OldMonday.AddDays(28), 4),    // Monday 29 Sep
            Spell(OldMonday.AddDays(42), 4),    // Monday 13 Oct
        };

        var chain = Assert.Single(SspPeriodAnalyser.Analyse(spells, mondaysOnly).Chains);

        Assert.Equal(4, chain.Periods.Count);
        Assert.Equal(
            new[] { OldMonday, OldMonday.AddDays(14), OldMonday.AddDays(28) },
            chain.WaitingDates);
        Assert.Equal(new[] { OldMonday.AddDays(42) }, chain.PayableDates);
    }

    // ---------------------------------------------------------- linked periods

    [Fact]
    public void PeriodsExactlyEightWeeksApartAreLinked()
    {
        var first = Spell(OldMonday, 4);
        // 56 days strictly between the end of the first and the start of the second.
        var second = Spell(first.End.AddDays(57), 4);

        var result = Analyse(first, second);

        Assert.Equal(2, Assert.Single(result.Chains).Periods.Count);
    }

    [Fact]
    public void PeriodsOneDayMoreThanEightWeeksApartAreNotLinked()
    {
        var first = Spell(OldMonday, 4);
        var second = Spell(first.End.AddDays(58), 4);

        var result = Analyse(first, second);

        Assert.Equal(2, result.Chains.Count);
    }

    [Fact]
    public void ASeparateChainServesItsOwnWaitingDays()
    {
        var first = Spell(OldMonday, 5);
        var second = Spell(first.End.AddDays(58), 5);

        var result = Analyse(first, second);

        Assert.All(result.Chains, c => Assert.Equal(3, c.WaitingDates.Count));
    }

    [Fact]
    public void AShortSpellBetweenPeriodsDoesNotBridgeTheGap()
    {
        var first = Spell(OldMonday, 4);
        var bridge = Spell(first.End.AddDays(40), 2);    // too short to be a PIW
        var second = Spell(first.End.AddDays(80), 4);    // 79 days after the first ended

        var result = Analyse(first, bridge, second);

        Assert.Equal(2, result.Chains.Count);
        Assert.Equal(bridge, Assert.Single(result.ShortSpells));
    }

    [Fact]
    public void AChainEndsAtItsMaximumLengthEvenWhenTheGapIsSmall()
    {
        // Cap the chain at one year so the test needs only a few periods.
        SspRules OneYearCap(DateOnly d) => SspRules.ForDate(d) with { LinkedPeriodMaxYears = 1 };

        // Four-day PIWs every 53 days: each gap is 49 days, always within the limit.
        var spells = Enumerable.Range(0, 9)
            .Select(i => Spell(OldMonday.AddDays(i * 53), 4))
            .ToList();

        var result = SspPeriodAnalyser.Analyse(spells, QualifyingDayPattern.Weekdays, OneYearCap);

        // Offsets 0..318 start inside the first year (365 days); 371 and 424 do not.
        Assert.Equal(2, result.Chains.Count);
        Assert.Equal(7, result.Chains[0].Periods.Count);
        Assert.Equal(2, result.Chains[1].Periods.Count);
    }

    // ------------------------------------------------------------------- rules

    [Fact]
    public void ARefusedDateBeforeTheEarliestRulesPropagates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Analyse(Spell(new DateOnly(2025, 1, 6), 4)));
    }

    [Fact]
    public void TheChainRecordsTheRulesInForceOnItsFirstDay()
    {
        var old = Assert.Single(Analyse(Spell(OldMonday, 4)).Chains);
        var current = Assert.Single(Analyse(Spell(NewMonday, 4)).Chains);

        Assert.Equal(3, old.Rules.WaitingDays);
        Assert.Equal(0, current.Rules.WaitingDays);
    }
}
