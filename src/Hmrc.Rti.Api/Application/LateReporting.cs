// ---------------------------------------------------------------------------
// FPS late-reporting reason codes.
//
// HMRC requires an FPS to be filed on or before the date the employee is paid.
// When it is not, the submission must carry a reason code explaining why, and
// an FPS that is late without one attracts a penalty even though the figures
// are correct.
//
// The codes are HMRC's, not ours. They are reproduced here rather than being
// passed through as free-text strings so that an invalid code is rejected
// locally, before transmission, alongside every other envelope validation --
// a rejection round trip to HMRC for a typo is a slow way to learn something
// we already know.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application;

/// <summary>HMRC FPS late-reporting reason codes.</summary>
public enum LateReportingReason
{
    /// <summary>No reason supplied. Valid only when the FPS is on time.</summary>
    None = 0,

    /// <summary>A - Notional payment: payment to expat by third party or overseas employer.</summary>
    NotionalPaymentExpat,

    /// <summary>B - Notional payment: employment related security.</summary>
    NotionalPaymentSecurity,

    /// <summary>C - Notional payment: other.</summary>
    NotionalPaymentOther,

    /// <summary>D - Payment subject to Class 1 NICs but P11D/P9D for tax.</summary>
    Class1NicsButP11D,

    /// <summary>E - Micro employer using temporary "on or before" relaxation.</summary>
    MicroEmployerRelaxation,

    /// <summary>F - Impractical to report work done on the day.</summary>
    ImpracticalToReportOnTheDay,

    /// <summary>G - Reasonable excuse for late reporting.</summary>
    ReasonableExcuse,

    /// <summary>H - Correction to an earlier submission.</summary>
    CorrectionToEarlierSubmission,
}

/// <summary>Outcome of checking an FPS against the on-or-before rule.</summary>
/// <param name="IsLate">Whether the submission is after the payment date.</param>
/// <param name="DaysLate">Whole days late; zero when on time.</param>
/// <param name="ReasonRequired">Whether HMRC requires a reason code.</param>
/// <param name="Errors">Why the submission cannot be transmitted as it stands.</param>
public sealed record LateReportingOutcome(
    bool IsLate,
    int DaysLate,
    bool ReasonRequired,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class LateReporting
{
    /// <summary>
    /// The single-character code HMRC expects on the wire. Kept as an explicit
    /// mapping rather than deriving it from the enum position, so reordering
    /// the enum can never silently change what is transmitted.
    /// </summary>
    public static string? ToHmrcCode(this LateReportingReason reason) => reason switch
    {
        LateReportingReason.None => null,
        LateReportingReason.NotionalPaymentExpat => "A",
        LateReportingReason.NotionalPaymentSecurity => "B",
        LateReportingReason.NotionalPaymentOther => "C",
        LateReportingReason.Class1NicsButP11D => "D",
        LateReportingReason.MicroEmployerRelaxation => "E",
        LateReportingReason.ImpracticalToReportOnTheDay => "F",
        LateReportingReason.ReasonableExcuse => "G",
        LateReportingReason.CorrectionToEarlierSubmission => "H",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason code"),
    };

    /// <summary>Parse an HMRC code. Case-insensitive; null or blank is <see cref="LateReportingReason.None"/>.</summary>
    public static bool TryParseHmrcCode(string? code, out LateReportingReason reason)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            reason = LateReportingReason.None;
            return true;
        }

        reason = code.Trim().ToUpperInvariant() switch
        {
            "A" => LateReportingReason.NotionalPaymentExpat,
            "B" => LateReportingReason.NotionalPaymentSecurity,
            "C" => LateReportingReason.NotionalPaymentOther,
            "D" => LateReportingReason.Class1NicsButP11D,
            "E" => LateReportingReason.MicroEmployerRelaxation,
            "F" => LateReportingReason.ImpracticalToReportOnTheDay,
            "G" => LateReportingReason.ReasonableExcuse,
            "H" => LateReportingReason.CorrectionToEarlierSubmission,
            _ => LateReportingReason.None,
        };

        return reason != LateReportingReason.None;
    }

    /// <summary>
    /// Apply HMRC's on-or-before rule.
    /// </summary>
    /// <remarks>
    /// Filing early is always fine. Filing on the payment date is fine. Filing
    /// after it requires a reason. Supplying a reason when the FPS is on time
    /// is also an error -- it is not harmless, because HMRC reads it as an
    /// admission of lateness that did not happen.
    /// </remarks>
    public static LateReportingOutcome Evaluate(
        DateOnly paymentDate,
        DateOnly submissionDate,
        LateReportingReason reason)
    {
        var daysLate = submissionDate.DayNumber - paymentDate.DayNumber;
        var isLate = daysLate > 0;
        var errors = new List<string>();

        if (isLate && reason == LateReportingReason.None)
        {
            errors.Add(
                $"FPS is {daysLate} day(s) after the payment date ({paymentDate:yyyy-MM-dd}) " +
                "and requires a late-reporting reason code.");
        }

        if (!isLate && reason != LateReportingReason.None)
        {
            errors.Add(
                "A late-reporting reason was supplied but the FPS is on or before the payment date.");
        }

        return new LateReportingOutcome(
            IsLate: isLate,
            DaysLate: isLate ? daysLate : 0,
            ReasonRequired: isLate,
            Errors: errors);
    }

    /// <summary>
    /// Whether a late FPS with this reason is expected to attract a penalty.
    /// </summary>
    /// <remarks>
    /// Codes A-F describe circumstances HMRC accepts as ordinary, so they do
    /// not draw a penalty. G is a claim of reasonable excuse, which HMRC may or
    /// may not accept -- reporting it as "no penalty" would be telling the
    /// client something we do not know. H is a correction, not a late payment.
    /// </remarks>
    public static bool MayAttractPenalty(LateReportingReason reason) => reason switch
    {
        LateReportingReason.None => true,
        LateReportingReason.ReasonableExcuse => true,
        _ => false,
    };
}
