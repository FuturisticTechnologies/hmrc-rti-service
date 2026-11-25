// ---------------------------------------------------------------------------
// Statutory Sick Pay rules, versioned by the date they take effect.
//
// SSP law changes. From 6 April 2026 the waiting days and the lower earnings
// limit (LEL) eligibility test were removed, and workers earning less than the
// flat rate are paid a percentage of their average weekly earnings instead. A
// sickness that began before that date must still be assessed under the old
// rules, so the rules are selected by the first day of sickness rather than
// being a single set of constants.
//
// The figures below are held as data, not scattered through the calculation,
// so a new tax year is one new entry here. VERIFY every figure against gov.uk
// before release: they were entered from the published rates, not derived.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application.Ssp;

/// <summary>One regime of SSP rules, applying to sickness beginning on or after <see cref="EffectiveFrom"/>.</summary>
/// <param name="EffectiveFrom">First day of sickness the rules apply to.</param>
/// <param name="WeeklyRate">Flat weekly SSP rate in pounds.</param>
/// <param name="LowerEarningsLimit">
/// Average weekly earnings must reach this to be eligible. Null when the regime
/// has no earnings test.
/// </param>
/// <param name="WaitingDays">Qualifying days at the start of a period for which SSP is not paid.</param>
/// <param name="EarningsPercentageCap">
/// When set, weekly SSP is the lower of the flat rate and this fraction of
/// average weekly earnings (0.80 = 80%). Null means the flat rate is always paid.
/// </param>
/// <param name="MaxWeeks">Maximum entitlement, in weeks, within one period of linked sickness.</param>
/// <param name="MinimumPiwDays">Consecutive calendar days of incapacity that form a period of incapacity for work.</param>
/// <param name="LinkingGapDays">Two periods separated by this many calendar days or fewer are linked.</param>
/// <param name="LinkedPeriodMaxYears">A chain of linked periods ends after this many years.</param>
public sealed record SspRules(
    DateOnly EffectiveFrom,
    decimal WeeklyRate,
    decimal? LowerEarningsLimit,
    int WaitingDays,
    decimal? EarningsPercentageCap,
    decimal MaxWeeks = 28m,
    int MinimumPiwDays = 4,
    int LinkingGapDays = 56,
    int LinkedPeriodMaxYears = 3)
{
    /// <summary>Whether the regime refuses SSP to workers under the lower earnings limit.</summary>
    public bool HasEarningsTest => LowerEarningsLimit.HasValue;

    /// <summary>Every known regime, oldest first.</summary>
    public static IReadOnlyList<SspRules> All { get; } =
    [
        // 2025/26: flat rate, LEL test, three waiting days.
        new SspRules(
            EffectiveFrom: new DateOnly(2025, 4, 6),
            WeeklyRate: 118.75m,
            LowerEarningsLimit: 125.00m,
            WaitingDays: 3,
            EarningsPercentageCap: null),

        // From 6 April 2026: no waiting days, no LEL test, and the lower of the
        // flat rate and 80% of average weekly earnings.
        new SspRules(
            EffectiveFrom: new DateOnly(2026, 4, 6),
            WeeklyRate: 123.25m,
            LowerEarningsLimit: null,
            WaitingDays: 0,
            EarningsPercentageCap: 0.80m),
    ];

    /// <summary>
    /// The rules for sickness that begins on <paramref name="firstDayOfSickness"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The date is before the earliest regime held. Guessing at older rules would
    /// produce a plausible but wrong payment, so this fails instead.
    /// </exception>
    public static SspRules ForDate(DateOnly firstDayOfSickness)
    {
        SspRules? match = null;
        foreach (var rules in All)
        {
            if (rules.EffectiveFrom <= firstDayOfSickness)
            {
                match = rules;
            }
        }

        return match ?? throw new ArgumentOutOfRangeException(
            nameof(firstDayOfSickness),
            firstDayOfSickness,
            $"No SSP rules are held for sickness beginning before {All[0].EffectiveFrom:yyyy-MM-dd}.");
    }
}
