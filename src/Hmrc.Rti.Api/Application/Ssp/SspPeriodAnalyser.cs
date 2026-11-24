// ---------------------------------------------------------------------------
// Turns raw sickness spells into what SSP is actually payable on:
//
//   1. Spells that touch or overlap are one continuous spell of sickness.
//   2. A spell of MinimumPiwDays or more calendar days is a period of
//      incapacity for work (PIW). Shorter spells are not, and never earn SSP.
//   3. PIWs no more than LinkingGapDays apart are "linked" and treated as one
//      chain, which ends once it would run past LinkedPeriodMaxYears.
//   4. Waiting days are the first WaitingDays qualifying days of a whole chain,
//      not of each PIW. A worker who falls sick again within eight weeks does
//      not serve them twice.
//
// Short spells are returned rather than dropped, so a caller can say why a
// day was not paid instead of it silently vanishing.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application.Ssp;

/// <summary>A period of incapacity for work: a continuous spell of at least the minimum length.</summary>
/// <param name="QualifyingDays">Qualifying days that fall inside the period. Can be zero.</param>
public sealed record PeriodOfIncapacity(DateOnly Start, DateOnly End, int QualifyingDays)
{
    public int CalendarDays => End.DayNumber - Start.DayNumber + 1;
}

/// <summary>A chain of linked periods of incapacity, assessed under one set of rules.</summary>
/// <param name="Periods">The periods in date order. Never empty.</param>
/// <param name="Rules">The rules in force on the chain's first day of sickness.</param>
/// <param name="WaitingDates">Qualifying dates served as waiting days; no SSP is paid for them.</param>
/// <param name="PayableDates">Qualifying dates after the waiting days, on which SSP is payable.</param>
public sealed record LinkedSickness(
    IReadOnlyList<PeriodOfIncapacity> Periods,
    SspRules Rules,
    IReadOnlyList<DateOnly> WaitingDates,
    IReadOnlyList<DateOnly> PayableDates)
{
    public DateOnly Start => Periods[0].Start;

    public DateOnly End => Periods[^1].End;
}

/// <summary>The result of analysing a worker's sickness history.</summary>
/// <param name="Chains">Linked chains of periods of incapacity, in date order.</param>
/// <param name="ShortSpells">Spells too short to be a period of incapacity.</param>
public sealed record SspPeriodAnalysis(
    IReadOnlyList<LinkedSickness> Chains,
    IReadOnlyList<SicknessPeriod> ShortSpells);

public static class SspPeriodAnalyser
{
    /// <summary>Analyse a sickness history. Input order does not matter.</summary>
    /// <param name="sickness">Spells of sickness, which may be unsorted, adjacent or overlapping.</param>
    /// <param name="pattern">The worker's qualifying days.</param>
    /// <param name="rulesFor">
    /// Chooses the rules for a first day of sickness. Defaults to
    /// <see cref="SspRules.ForDate"/>, which refuses dates before the earliest
    /// regime held.
    /// </param>
    public static SspPeriodAnalysis Analyse(
        IEnumerable<SicknessPeriod> sickness,
        QualifyingDayPattern pattern,
        Func<DateOnly, SspRules>? rulesFor = null)
    {
        ArgumentNullException.ThrowIfNull(sickness);
        ArgumentNullException.ThrowIfNull(pattern);
        rulesFor ??= SspRules.ForDate;

        var shortSpells = new List<SicknessPeriod>();
        var chains = new List<LinkedSickness>();
        List<PeriodOfIncapacity>? current = null;
        SspRules? currentRules = null;

        foreach (var spell in Merge(sickness))
        {
            var spellRules = rulesFor(spell.Start);

            if (spell.CalendarDays < spellRules.MinimumPiwDays)
            {
                shortSpells.Add(spell);
                continue;
            }

            var piw = new PeriodOfIncapacity(
                spell.Start, spell.End, pattern.CountBetween(spell.Start, spell.End));

            if (current is not null && IsLinked(current, currentRules!, piw))
            {
                current.Add(piw);
                continue;
            }

            if (current is not null)
            {
                chains.Add(BuildChain(current, currentRules!, pattern));
            }

            current = new List<PeriodOfIncapacity> { piw };
            currentRules = spellRules;
        }

        if (current is not null)
        {
            chains.Add(BuildChain(current, currentRules!, pattern));
        }

        return new SspPeriodAnalysis(chains, shortSpells);
    }

    /// <summary>Sort, then join spells that overlap or run on from one day to the next.</summary>
    private static List<SicknessPeriod> Merge(IEnumerable<SicknessPeriod> sickness)
    {
        var merged = new List<SicknessPeriod>();

        foreach (var spell in sickness.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            if (merged.Count > 0 && spell.Start <= merged[^1].End.AddDays(1))
            {
                var last = merged[^1];
                merged[^1] = new SicknessPeriod(last.Start, spell.End > last.End ? spell.End : last.End);
            }
            else
            {
                merged.Add(spell);
            }
        }

        return merged;
    }

    /// <summary>
    /// A PIW links to the chain when the gap is within the limit and it begins
    /// before the chain's maximum length is reached. The gap counts the days
    /// strictly between the two periods, so a gap of exactly the limit links
    /// and one day more does not.
    /// </summary>
    private static bool IsLinked(
        List<PeriodOfIncapacity> chain, SspRules rules, PeriodOfIncapacity next)
    {
        var gapDays = next.Start.DayNumber - chain[^1].End.DayNumber - 1;

        return gapDays <= rules.LinkingGapDays
               && next.Start < chain[0].Start.AddYears(rules.LinkedPeriodMaxYears);
    }

    private static LinkedSickness BuildChain(
        List<PeriodOfIncapacity> periods, SspRules rules, QualifyingDayPattern pattern)
    {
        var qualifying = periods
            .SelectMany(p => pattern.QualifyingDatesBetween(p.Start, p.End))
            .ToList();

        return new LinkedSickness(
            periods,
            rules,
            WaitingDates: qualifying.Take(rules.WaitingDays).ToList(),
            PayableDates: qualifying.Skip(rules.WaitingDays).ToList());
    }
}
