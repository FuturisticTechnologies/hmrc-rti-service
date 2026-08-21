// ---------------------------------------------------------------------------
// Classification of HMRC rejection codes.
//
// The state machine already knows that Rejected and Failed are both re-queueable.
// What it does not know is *why*, and that difference is the whole operational
// question: a transport timeout should be retried automatically, an expired
// credential should page someone, and a schema failure should never be retried
// at all because the same envelope will be rejected identically every time.
//
// Retrying a permanent rejection is not merely useless. HMRC rate-limits, and a
// tight retry loop against a submission that cannot succeed puts the *next*
// employer's filing at risk.
//
// Codes are HMRC's. Anything unrecognised is treated as permanent and surfaced
// to an operator, because silently retrying an unknown failure is how a filing
// deadline gets missed quietly.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application;

/// <summary>What should happen to a submission after HMRC rejects it.</summary>
public enum RejectionDisposition
{
    /// <summary>Transient. Retry automatically with backoff.</summary>
    Retryable,

    /// <summary>The envelope is wrong. Correct it and resubmit; never auto-retry.</summary>
    FixEnvelope,

    /// <summary>Credentials or enrolment are wrong. Needs an operator.</summary>
    FixConfiguration,

    /// <summary>Already accepted. Not an error; reconcile rather than resubmit.</summary>
    AlreadyAccepted,

    /// <summary>Unrecognised. Treated as permanent and escalated.</summary>
    Unknown,
}

/// <param name="Code">The HMRC code, uppercased.</param>
/// <param name="Disposition">What to do about it.</param>
/// <param name="Summary">What it means, in one line.</param>
/// <param name="OperatorAction">What a human should actually do.</param>
public sealed record RejectionClassification(
    string Code,
    RejectionDisposition Disposition,
    string Summary,
    string OperatorAction)
{
    /// <summary>Whether the dispatcher may re-queue this without a human.</summary>
    public bool IsAutoRetryable => Disposition == RejectionDisposition.Retryable;

    /// <summary>Whether this needs to reach a person rather than a log line.</summary>
    public bool RequiresOperator =>
        Disposition is RejectionDisposition.FixConfiguration or RejectionDisposition.Unknown;
}

public static class RejectionCatalog
{
    private static readonly Dictionary<string, RejectionClassification> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // -------------------------------------------------- transient
            ["SERVER_ERROR"] = new(
                "SERVER_ERROR", RejectionDisposition.Retryable,
                "HMRC returned an internal error.",
                "None. The dispatcher retries with backoff."),
            ["SERVICE_UNAVAILABLE"] = new(
                "SERVICE_UNAVAILABLE", RejectionDisposition.Retryable,
                "HMRC service is unavailable, often a scheduled window.",
                "None unless it persists past the filing deadline."),
            ["GATEWAY_TIMEOUT"] = new(
                "GATEWAY_TIMEOUT", RejectionDisposition.Retryable,
                "HMRC did not respond in time.",
                "None. Verify the submission was not accepted before resubmitting."),
            ["RATE_LIMIT_EXCEEDED"] = new(
                "RATE_LIMIT_EXCEEDED", RejectionDisposition.Retryable,
                "Too many requests.",
                "None. Backoff handles it; investigate if it recurs at low volume."),

            // ------------------------------------------- envelope is wrong
            ["SCHEMA_VALIDATION_FAILED"] = new(
                "SCHEMA_VALIDATION_FAILED", RejectionDisposition.FixEnvelope,
                "The FPS or EPS failed HMRC schema validation.",
                "Local validation should have caught this. Raise a defect."),
            ["INVALID_REQUEST"] = new(
                "INVALID_REQUEST", RejectionDisposition.FixEnvelope,
                "The request body was malformed or a field was invalid.",
                "Inspect the stored envelope; correct and resubmit."),
            ["INVALID_TAX_YEAR"] = new(
                "INVALID_TAX_YEAR", RejectionDisposition.FixEnvelope,
                "The tax year is not open for submission.",
                "Check the pay period tax year against the filing window."),
            ["INVALID_PAYE_REFERENCE"] = new(
                "INVALID_PAYE_REFERENCE", RejectionDisposition.FixEnvelope,
                "The PAYE reference is not valid or does not match the enrolment.",
                "Correct the tenant PAYE and accounts office references."),

            // ------------------------------------- credentials / enrolment
            ["1046"] = new(
                "1046", RejectionDisposition.FixConfiguration,
                "Authentication failure - the classic Transaction Engine code.",
                "Check the sender ID, password and PAYE reference. Do not retry."),
            ["INVALID_CREDENTIALS"] = new(
                "INVALID_CREDENTIALS", RejectionDisposition.FixConfiguration,
                "The access token was rejected.",
                "Re-authorise the HMRC application. Check the client secret."),
            ["CLIENT_OR_AGENT_NOT_AUTHORISED"] = new(
                "CLIENT_OR_AGENT_NOT_AUTHORISED", RejectionDisposition.FixConfiguration,
                "Not authorised to file for this employer.",
                "Confirm the agent authorisation covers this PAYE scheme."),

            // ------------------------------------------------ already done
            ["DUPLICATE_SUBMISSION"] = new(
                "DUPLICATE_SUBMISSION", RejectionDisposition.AlreadyAccepted,
                "An identical submission was already accepted.",
                "Do not resubmit. Reconcile against the earlier correlation ID."),
        };

    /// <summary>Every code the catalog recognises.</summary>
    public static IReadOnlyCollection<string> KnownCodes => Known.Keys;

    /// <summary>
    /// Classify an HMRC rejection code. Unrecognised and blank codes are
    /// deliberately <see cref="RejectionDisposition.Unknown"/> rather than
    /// retryable - an unknown failure that quietly retries is how a filing
    /// deadline gets missed without anybody noticing.
    /// </summary>
    public static RejectionClassification Classify(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new RejectionClassification(
                string.Empty, RejectionDisposition.Unknown,
                "HMRC rejected the submission without a code.",
                "Inspect the stored response body and escalate.");
        }

        var trimmed = code.Trim();
        if (Known.TryGetValue(trimmed, out var known))
        {
            return known;
        }

        return new RejectionClassification(
            trimmed.ToUpperInvariant(), RejectionDisposition.Unknown,
            $"Unrecognised HMRC rejection code: {trimmed}",
            "Escalate. Do not retry until the code is understood and catalogued.");
    }

    /// <summary>Whether the dispatcher may re-queue this rejection unattended.</summary>
    public static bool ShouldAutoRetry(string? code) => Classify(code).IsAutoRetryable;
}
