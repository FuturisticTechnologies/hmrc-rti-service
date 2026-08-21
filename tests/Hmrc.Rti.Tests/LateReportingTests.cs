using Hmrc.Rti.Api.Application;
using Xunit;

namespace Hmrc.Rti.Tests;

public class LateReportingTests
{
    private static readonly DateOnly PaymentDate = new(2026, 8, 31);

    // ---------------------------------------------------------------- on time

    [Fact]
    public void FilingOnThePaymentDateIsNotLate()
    {
        var outcome = LateReporting.Evaluate(PaymentDate, PaymentDate, LateReportingReason.None);

        Assert.False(outcome.IsLate);
        Assert.Equal(0, outcome.DaysLate);
        Assert.False(outcome.ReasonRequired);
        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void FilingBeforeThePaymentDateIsNotLate()
    {
        var outcome = LateReporting.Evaluate(
            PaymentDate, PaymentDate.AddDays(-5), LateReportingReason.None);

        Assert.False(outcome.IsLate);
        Assert.Equal(0, outcome.DaysLate);
        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void EarlyFilingDoesNotReportNegativeDaysLate()
    {
        var outcome = LateReporting.Evaluate(
            PaymentDate, PaymentDate.AddDays(-10), LateReportingReason.None);

        Assert.Equal(0, outcome.DaysLate);
    }

    // ------------------------------------------------------------------- late

    [Fact]
    public void LateFilingWithoutAReasonIsRejected()
    {
        var outcome = LateReporting.Evaluate(
            PaymentDate, PaymentDate.AddDays(1), LateReportingReason.None);

        Assert.True(outcome.IsLate);
        Assert.Equal(1, outcome.DaysLate);
        Assert.True(outcome.ReasonRequired);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Errors, e => e.Contains("late-reporting reason"));
    }

    [Fact]
    public void LateFilingWithAReasonIsAccepted()
    {
        var outcome = LateReporting.Evaluate(
            PaymentDate, PaymentDate.AddDays(3), LateReportingReason.ReasonableExcuse);

        Assert.True(outcome.IsLate);
        Assert.Equal(3, outcome.DaysLate);
        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void DaysLateIsReportedAccurately()
    {
        var outcome = LateReporting.Evaluate(
            PaymentDate, PaymentDate.AddDays(14), LateReportingReason.ReasonableExcuse);

        Assert.Equal(14, outcome.DaysLate);
    }

    [Fact]
    public void AReasonOnAnOnTimeSubmissionIsRejected()
    {
        // Not harmless: HMRC reads it as an admission of lateness.
        var outcome = LateReporting.Evaluate(
            PaymentDate, PaymentDate, LateReportingReason.ReasonableExcuse);

        Assert.False(outcome.IsLate);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Errors, e => e.Contains("on or before"));
    }

    // ------------------------------------------------------------------- codes

    [Theory]
    [InlineData(LateReportingReason.NotionalPaymentExpat, "A")]
    [InlineData(LateReportingReason.NotionalPaymentSecurity, "B")]
    [InlineData(LateReportingReason.NotionalPaymentOther, "C")]
    [InlineData(LateReportingReason.Class1NicsButP11D, "D")]
    [InlineData(LateReportingReason.MicroEmployerRelaxation, "E")]
    [InlineData(LateReportingReason.ImpracticalToReportOnTheDay, "F")]
    [InlineData(LateReportingReason.ReasonableExcuse, "G")]
    [InlineData(LateReportingReason.CorrectionToEarlierSubmission, "H")]
    public void EachReasonMapsToItsHmrcCode(LateReportingReason reason, string expected)
    {
        Assert.Equal(expected, reason.ToHmrcCode());
    }

    [Fact]
    public void NoneHasNoWireCode()
    {
        Assert.Null(LateReportingReason.None.ToHmrcCode());
    }

    [Theory]
    [InlineData("A", LateReportingReason.NotionalPaymentExpat)]
    [InlineData("h", LateReportingReason.CorrectionToEarlierSubmission)]
    [InlineData(" G ", LateReportingReason.ReasonableExcuse)]
    public void CodesParseCaseInsensitivelyAndTrimmed(string code, LateReportingReason expected)
    {
        Assert.True(LateReporting.TryParseHmrcCode(code, out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCodesParseAsNone(string? code)
    {
        Assert.True(LateReporting.TryParseHmrcCode(code, out var reason));
        Assert.Equal(LateReportingReason.None, reason);
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("AA")]
    [InlineData("1")]
    public void UnknownCodesAreRejected(string code)
    {
        Assert.False(LateReporting.TryParseHmrcCode(code, out var reason));
        Assert.Equal(LateReportingReason.None, reason);
    }

    [Fact]
    public void EveryReasonRoundTripsThroughItsCode()
    {
        foreach (LateReportingReason reason in Enum.GetValues<LateReportingReason>())
        {
            var code = reason.ToHmrcCode();
            Assert.True(LateReporting.TryParseHmrcCode(code, out var parsed) || code is null);
            if (code is not null)
            {
                Assert.Equal(reason, parsed);
            }
        }
    }

    // ---------------------------------------------------------------- penalty

    [Theory]
    [InlineData(LateReportingReason.NotionalPaymentExpat)]
    [InlineData(LateReportingReason.MicroEmployerRelaxation)]
    [InlineData(LateReportingReason.ImpracticalToReportOnTheDay)]
    [InlineData(LateReportingReason.CorrectionToEarlierSubmission)]
    public void AcceptedCircumstancesDoNotAttractAPenalty(LateReportingReason reason)
    {
        Assert.False(LateReporting.MayAttractPenalty(reason));
    }

    [Fact]
    public void ReasonableExcuseIsNotPromisedToBeAccepted()
    {
        // HMRC decides. Reporting "no penalty" would be telling the client
        // something we do not know.
        Assert.True(LateReporting.MayAttractPenalty(LateReportingReason.ReasonableExcuse));
    }

    [Fact]
    public void LateWithNoReasonMayAttractAPenalty()
    {
        Assert.True(LateReporting.MayAttractPenalty(LateReportingReason.None));
    }
}
