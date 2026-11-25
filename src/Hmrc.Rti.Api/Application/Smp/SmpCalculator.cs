// ---------------------------------------------------------------------------
// Statutory Maternity Pay calculation: the week-by-week schedule.
//
// SMP is paid for 39 weeks. Weeks 1-6 are paid at 90% of average weekly
// earnings (the higher rate); weeks 7-39 at the LOWER of the standard weekly
// rate and 90% of average weekly earnings. The schedule marks week 7 as the
// rate change, so the point where pay drops is visible evidence rather than
// something a reader has to infer from the amounts.
//
// SMP weeks run in 7-day blocks from the SMP start date -- not Sunday to
// Sunday, which is how the eligibility weeks run.
//
// Contractual maternity pay: the employer's contractual amount is INCLUSIVE of
// SMP, and can never leave the employee with less than SMP. So per week the
// employee receives max(SMP, contractual); the difference is a top-up the
// employer funds entirely, and only the SMP part is ever recoverable.
//
// This does not check eligibility: assess with SmpEligibility first. The
// standard rate is looked up per week, so a leave that spans April picks up
// the new rate from the week it takes effect.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application.Smp;

public enum SmpRateBand
{
    /// <summary>Weeks 1-6: 90% of average weekly earnings.</summary>
    Higher = 1,

    /// <summary>Weeks 7-39: the lower of the standard rate and 90% of average weekly earnings.</summary>
    Standard = 2,
}

public sealed record SmpCalculationInput
{
    /// <summary>First day SMP is paid from.</summary>
    public DateOnly SmpStartDate { get; init; }

    public decimal AverageWeeklyEarnings { get; init; }

    /// <summary>
    /// Total contractual maternity pay per week, inclusive of SMP. Entry 0 is
    /// week 1. Weeks beyond the list have no contractual entitlement.
    /// </summary>
    public IReadOnlyList<decimal> ContractualWeeklyPay { get; init; } = [];
}

/// <param name="WeekNumber">1-39.</param>
/// <param name="StatutoryAmount">The SMP for the week.</param>
/// <param name="ContractualAmount">Contractual pay for the week, inclusive of SMP; zero if none.</param>
/// <param name="TopUp">Employer-funded amount above SMP; never negative.</param>
/// <param name="IsRateChange">True on the first week of the standard rate band.</param>
public sealed record SmpWeek(
    int WeekNumber,
    DateOnly StartDate,
    DateOnly EndDate,
    SmpRateBand Band,
    decimal StatutoryAmount,
    decimal ContractualAmount,
    decimal TopUp,
    bool IsRateChange)
{
    /// <summary>What the employee receives: SMP plus any contractual top-up.</summary>
    public decimal TotalPay => StatutoryAmount + TopUp;
}

public sealed record SmpSchedule(IReadOnlyList<SmpWeek> Weeks)
{
    public decimal TotalStatutory => Weeks.Sum(w => w.StatutoryAmount);
    public decimal TotalTopUp => Weeks.Sum(w => w.TopUp);
    public decimal TotalPay => Weeks.Sum(w => w.TotalPay);

    /// <summary>The week the pay moves from the higher to the standard band.</summary>
    public SmpWeek RateChangeWeek => Weeks.Single(w => w.IsRateChange);

    /// <summary>The SMP week a date falls in, or null if it is before the leave or after week 39.</summary>
    public SmpWeek? WeekContaining(DateOnly date) =>
        Weeks.FirstOrDefault(w => date >= w.StartDate && date <= w.EndDate);
}

public static class SmpCalculator
{
    /// <exception cref="ArgumentOutOfRangeException">Negative earnings.</exception>
    /// <exception cref="ArgumentException">A negative contractual amount.</exception>
    /// <exception cref="InvalidOperationException">No standard rate is configured for a week's date.</exception>
    public static SmpSchedule Calculate(SmpCalculationInput input)
    {
        if (input.AverageWeeklyEarnings < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), input.AverageWeeklyEarnings, "Average weekly earnings must not be negative.");
        }

        if (input.ContractualWeeklyPay.Any(c => c < 0))
        {
            throw new ArgumentException("Contractual weekly pay must not be negative.", nameof(input));
        }

        var higherRate = Money(input.AverageWeeklyEarnings * SmpRates.HigherRateFraction);
        var weeks = new List<SmpWeek>(SmpRates.MaxPaidWeeks);

        for (var number = 1; number <= SmpRates.MaxPaidWeeks; number++)
        {
            var start = input.SmpStartDate.AddDays((number - 1) * 7);
            var inHigherBand = number <= SmpRates.HigherRateWeeks;

            var statutory = inHigherBand
                ? higherRate
                : Math.Min(SmpRates.StandardRateOn(start), higherRate);

            var contractual = number <= input.ContractualWeeklyPay.Count
                ? Money(input.ContractualWeeklyPay[number - 1])
                : 0m;

            weeks.Add(new SmpWeek(
                WeekNumber: number,
                StartDate: start,
                EndDate: start.AddDays(6),
                Band: inHigherBand ? SmpRateBand.Higher : SmpRateBand.Standard,
                StatutoryAmount: statutory,
                ContractualAmount: contractual,
                TopUp: Math.Max(0m, contractual - statutory),
                IsRateChange: number == SmpRates.HigherRateWeeks + 1));
        }

        return new SmpSchedule(weeks);
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
