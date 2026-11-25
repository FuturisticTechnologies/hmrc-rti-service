// ---------------------------------------------------------------------------
// Statutory Maternity Pay recovery and NIC compensation, for the EPS.
//
// An employer that pays SMP reclaims part of it from HMRC by deducting it from
// what it pays over:
//
//   - standard employers recover 92% of the SMP paid;
//   - small employers (Class 1 NICs of 45,000 or less in the qualifying tax
//     year) recover 100% of the SMP paid, plus 3% of it as compensation for
//     the employer NICs paid on it -- 103% in all.
//
// Only the STATUTORY amount is ever recoverable. A contractual top-up above
// SMP is the employer's own cost, so these functions take the SMP figure, never
// the total pay: passing total pay would claim back money HMRC never owed.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application.Smp;

public enum SmpEmployerRelief
{
    /// <summary>Recovers 92% of SMP paid.</summary>
    Standard = 1,

    /// <summary>Small employers' relief: recovers 100% of SMP plus 3% NIC compensation.</summary>
    SmallEmployer = 2,
}

/// <param name="SmpPaid">The statutory maternity pay actually paid.</param>
/// <param name="Recoverable">The part of it reclaimed from HMRC.</param>
/// <param name="NicCompensation">Extra compensation for employer NICs; zero for standard employers.</param>
public sealed record SmpRecoveryAmounts(decimal SmpPaid, decimal Recoverable, decimal NicCompensation)
{
    /// <summary>Everything the employer deducts from its payment to HMRC.</summary>
    public decimal TotalRecoverable => Recoverable + NicCompensation;

    /// <summary>What the SMP costs the employer after recovery. Negative when relief exceeds the cost.</summary>
    public decimal NetCostToEmployer => SmpPaid - TotalRecoverable;
}

public static class SmpRecovery
{
    public const decimal StandardRecoveryFraction = 0.92m;
    public const decimal SmallEmployerRecoveryFraction = 1.00m;
    public const decimal SmallEmployerCompensationFraction = 0.03m;

    /// <summary>Class 1 NICs, in the qualifying tax year, at or below which an employer is "small".</summary>
    public const decimal SmallEmployerNicThreshold = 45_000m;

    public static bool QualifiesForSmallEmployerRelief(decimal qualifyingYearClass1Nics) =>
        qualifyingYearClass1Nics <= SmallEmployerNicThreshold;

    /// <exception cref="ArgumentOutOfRangeException">Negative SMP, or an unknown relief.</exception>
    public static SmpRecoveryAmounts Calculate(decimal smpPaid, SmpEmployerRelief relief)
    {
        if (smpPaid < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(smpPaid), smpPaid, "SMP paid must not be negative.");
        }

        return relief switch
        {
            SmpEmployerRelief.Standard => new SmpRecoveryAmounts(
                smpPaid, Money(smpPaid * StandardRecoveryFraction), 0m),

            SmpEmployerRelief.SmallEmployer => new SmpRecoveryAmounts(
                smpPaid,
                Money(smpPaid * SmallEmployerRecoveryFraction),
                Money(smpPaid * SmallEmployerCompensationFraction)),

            _ => throw new ArgumentOutOfRangeException(nameof(relief), relief, "Unknown employer relief."),
        };
    }

    /// <summary>Recovery on the statutory part of the given weeks; contractual top-ups are ignored.</summary>
    public static SmpRecoveryAmounts Calculate(IEnumerable<SmpWeek> weeks, SmpEmployerRelief relief) =>
        Calculate(weeks.Sum(w => w.StatutoryAmount), relief);

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
