using Hmrc.Rti.Api.Application;
using Xunit;

namespace Hmrc.Rti.Tests;

public class RejectionCatalogTests
{
    [Theory]
    [InlineData("SERVER_ERROR")]
    [InlineData("SERVICE_UNAVAILABLE")]
    [InlineData("GATEWAY_TIMEOUT")]
    [InlineData("RATE_LIMIT_EXCEEDED")]
    public void TransientCodesAreAutoRetryable(string code)
    {
        var classification = RejectionCatalog.Classify(code);

        Assert.Equal(RejectionDisposition.Retryable, classification.Disposition);
        Assert.True(classification.IsAutoRetryable);
        Assert.False(classification.RequiresOperator);
    }

    [Theory]
    [InlineData("SCHEMA_VALIDATION_FAILED")]
    [InlineData("INVALID_REQUEST")]
    [InlineData("INVALID_TAX_YEAR")]
    [InlineData("INVALID_PAYE_REFERENCE")]
    public void EnvelopeFaultsAreNeverAutoRetried(string code)
    {
        var classification = RejectionCatalog.Classify(code);

        Assert.Equal(RejectionDisposition.FixEnvelope, classification.Disposition);
        Assert.False(classification.IsAutoRetryable);
    }

    [Theory]
    [InlineData("1046")]
    [InlineData("INVALID_CREDENTIALS")]
    [InlineData("CLIENT_OR_AGENT_NOT_AUTHORISED")]
    public void CredentialFaultsReachAnOperator(string code)
    {
        var classification = RejectionCatalog.Classify(code);

        Assert.Equal(RejectionDisposition.FixConfiguration, classification.Disposition);
        Assert.False(classification.IsAutoRetryable);
        Assert.True(classification.RequiresOperator);
    }

    [Fact]
    public void DuplicateSubmissionIsNotTreatedAsAFailure()
    {
        var classification = RejectionCatalog.Classify("DUPLICATE_SUBMISSION");

        Assert.Equal(RejectionDisposition.AlreadyAccepted, classification.Disposition);
        Assert.False(classification.IsAutoRetryable);
        Assert.Contains("Do not resubmit", classification.OperatorAction);
    }

    [Fact]
    public void UnknownCodesArePermanentNotRetryable()
    {
        // The important default. Retrying an unknown failure is how a filing
        // deadline gets missed quietly.
        var classification = RejectionCatalog.Classify("SOMETHING_NEW");

        Assert.Equal(RejectionDisposition.Unknown, classification.Disposition);
        Assert.False(classification.IsAutoRetryable);
        Assert.True(classification.RequiresOperator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingCodesAreEscalatedNotRetried(string? code)
    {
        var classification = RejectionCatalog.Classify(code);

        Assert.Equal(RejectionDisposition.Unknown, classification.Disposition);
        Assert.False(classification.IsAutoRetryable);
        Assert.True(classification.RequiresOperator);
    }

    [Fact]
    public void CodeLookupIsCaseInsensitive()
    {
        Assert.Equal(
            RejectionDisposition.Retryable,
            RejectionCatalog.Classify("server_error").Disposition);
    }

    [Fact]
    public void SurroundingWhitespaceIsTolerated()
    {
        Assert.Equal(
            RejectionDisposition.FixConfiguration,
            RejectionCatalog.Classify("  1046  ").Disposition);
    }

    [Fact]
    public void ShouldAutoRetryAgreesWithTheClassification()
    {
        Assert.True(RejectionCatalog.ShouldAutoRetry("SERVER_ERROR"));
        Assert.False(RejectionCatalog.ShouldAutoRetry("SCHEMA_VALIDATION_FAILED"));
        Assert.False(RejectionCatalog.ShouldAutoRetry("UNRECOGNISED"));
    }

    [Fact]
    public void EveryCatalogedCodeCarriesOperatorGuidance()
    {
        foreach (var code in RejectionCatalog.KnownCodes)
        {
            var classification = RejectionCatalog.Classify(code);
            Assert.False(string.IsNullOrWhiteSpace(classification.Summary));
            Assert.False(string.IsNullOrWhiteSpace(classification.OperatorAction));
            Assert.NotEqual(RejectionDisposition.Unknown, classification.Disposition);
        }
    }

    [Fact]
    public void OnlyTransientCodesAreAutoRetryable()
    {
        foreach (var code in RejectionCatalog.KnownCodes)
        {
            var classification = RejectionCatalog.Classify(code);
            Assert.Equal(
                classification.Disposition == RejectionDisposition.Retryable,
                classification.IsAutoRetryable);
        }
    }
}
