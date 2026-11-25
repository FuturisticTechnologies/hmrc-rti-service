using Hmrc.Rti.Api.Application.Ssp;
using Xunit;

namespace Hmrc.Rti.Tests;

public class SspRulesTests
{
    [Fact]
    public void SicknessBeforeApril2026UsesTheOldRules()
    {
        var rules = SspRules.ForDate(new DateOnly(2026, 4, 5));

        Assert.Equal(3, rules.WaitingDays);
        Assert.True(rules.HasEarningsTest);
        Assert.Equal(125.00m, rules.LowerEarningsLimit);
        Assert.Null(rules.EarningsPercentageCap);
    }

    [Fact]
    public void SicknessFromApril2026UsesTheNewRules()
    {
        var rules = SspRules.ForDate(new DateOnly(2026, 4, 6));

        Assert.Equal(0, rules.WaitingDays);
        Assert.False(rules.HasEarningsTest);
        Assert.Equal(0.80m, rules.EarningsPercentageCap);
    }

    [Fact]
    public void LaterDatesKeepUsingTheLatestKnownRules()
    {
        Assert.Same(SspRules.ForDate(new DateOnly(2026, 4, 6)), SspRules.ForDate(new DateOnly(2030, 1, 1)));
    }

    [Fact]
    public void ADateBeforeTheEarliestRulesIsRefusedRatherThanGuessed()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => SspRules.ForDate(new DateOnly(2025, 4, 5)));

        Assert.Contains("2025-04-06", ex.Message);
    }

    [Fact]
    public void RegimesAreListedOldestFirstSoForDatePicksTheLatestApplicable()
    {
        var dates = SspRules.All.Select(r => r.EffectiveFrom).ToList();

        Assert.Equal(dates.OrderBy(d => d), dates);
    }

    [Fact]
    public void StatutoryConstantsHaveTheirUsualValues()
    {
        var rules = SspRules.ForDate(new DateOnly(2026, 5, 1));

        Assert.Equal(28m, rules.MaxWeeks);
        Assert.Equal(4, rules.MinimumPiwDays);
        Assert.Equal(56, rules.LinkingGapDays);
        Assert.Equal(3, rules.LinkedPeriodMaxYears);
    }
}
