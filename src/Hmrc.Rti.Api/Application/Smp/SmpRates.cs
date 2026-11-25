// ---------------------------------------------------------------------------
// Statutory Maternity Pay rates, by tax year.
//
// Rates are data, not logic: HMRC changes them every April, so they live in one
// table and nothing else in the SMP code carries a pound figure. A tax year that
// is not in the table raises instead of falling back to the nearest year -- an
// employee told they are ineligible, or paid at last year's rate, because
// nobody added a row is a worse outcome than a loud failure.
//
// TODO(SNOV-141): add 2026-27 once the figures are confirmed against the
// gov.uk "Statutory pay rates" page. Deliberately not guessed.
// ---------------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;

namespace Hmrc.Rti.Api.Application.Smp;

/// <summary>The SMP figures that change with the tax year.</summary>
/// <param name="TaxYear">Starting year, e.g. 2025 for 2025-26.</param>
/// <param name="StandardWeeklyRate">Flat weekly rate paid after the higher-rate weeks.</param>
/// <param name="LowerEarningsLimit">Average weekly earnings an employee must reach to qualify.</param>
public sealed record SmpRateSet(int TaxYear, decimal StandardWeeklyRate, decimal LowerEarningsLimit);

public static class SmpRates
{
    /// <summary>Weeks paid at the higher rate (90% of average weekly earnings).</summary>
    public const int HigherRateWeeks = 6;

    /// <summary>Total weeks SMP is payable for.</summary>
    public const int MaxPaidWeeks = 39;

    /// <summary>The higher rate, as a fraction of average weekly earnings.</summary>
    public const decimal HigherRateFraction = 0.90m;

    private static readonly Dictionary<int, SmpRateSet> Table = new SmpRateSet[]
    {
        new(2024, StandardWeeklyRate: 184.03m, LowerEarningsLimit: 123m),
        new(2025, StandardWeeklyRate: 187.18m, LowerEarningsLimit: 125m),
    }.ToDictionary(r => r.TaxYear);

    public static bool TryGet(int taxYear, [NotNullWhen(true)] out SmpRateSet? rates) =>
        Table.TryGetValue(taxYear, out rates);

    /// <exception cref="InvalidOperationException">No rates are configured for the year.</exception>
    public static SmpRateSet ForTaxYear(int taxYear) =>
        Table.TryGetValue(taxYear, out var rates)
            ? rates
            : throw new InvalidOperationException(
                $"No SMP rates are configured for tax year {RtiEnvelopeBuilder.FormatTaxYear(taxYear)}.");

    /// <summary>The rates in force on a given date.</summary>
    public static SmpRateSet ForDate(DateOnly date) =>
        ForTaxYear(RtiRequestValidator.TaxYearOf(date));
}
