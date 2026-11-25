using Hmrc.Rti.Api.Application.Smp;
using Xunit;

namespace Hmrc.Rti.Tests;

public class SmpCalculatorTests
{
    // A Sunday, with all 39 weeks inside tax year 2025-26 (the last starts 22 Feb 2026).
    private static readonly DateOnly Start = new(2025, 6, 1);

    private static SmpCalculationInput Input(decimal awe = 300m) => new()
    {
        SmpStartDate = Start,
        AverageWeeklyEarnings = awe,
    };

    // ---------------------------------------------------------------- schedule

    [Fact]
    public void ScheduleCoversThirtyNineWeeksInSevenDayBlocks()
    {
        var schedule = SmpCalculator.Calculate(Input());

        Assert.Equal(39, schedule.Weeks.Count);
        Assert.Equal(Enumerable.Range(1, 39), schedule.Weeks.Select(w => w.WeekNumber));
        Assert.Equal(Start, schedule.Weeks[0].StartDate);
        Assert.Equal(Start.AddDays(6), schedule.Weeks[0].EndDate);
        Assert.Equal(Start.AddDays(7), schedule.Weeks[1].StartDate);
        Assert.Equal(Start.AddDays(38 * 7), schedule.Weeks[38].StartDate);
    }

    [Fact]
    public void ScheduleWeeksFollowTheSmpStartDateNotTheCalendarWeek()
    {
        var wednesday = new DateOnly(2025, 6, 4);
        var schedule = SmpCalculator.Calculate(Input() with { SmpStartDate = wednesday });

        Assert.Equal(wednesday.AddDays(7), schedule.Weeks[1].StartDate);
        Assert.Equal(DayOfWeek.Wednesday, schedule.Weeks[1].StartDate.DayOfWeek);
    }

    // --------------------------------------------------------------- rate band

    [Fact]
    public void FirstSixWeeksAreTheHigherBandAndTheRestStandard()
    {
        var schedule = SmpCalculator.Calculate(Input());

        Assert.All(schedule.Weeks.Take(6), w => Assert.Equal(SmpRateBand.Higher, w.Band));
        Assert.All(schedule.Weeks.Skip(6), w => Assert.Equal(SmpRateBand.Standard, w.Band));
    }

    [Fact]
    public void TheRateChangeIsFlaggedOnWeekSevenAndNowhereElse()
    {
        var schedule = SmpCalculator.Calculate(Input());

        var flagged = schedule.Weeks.Where(w => w.IsRateChange).ToList();

        Assert.Single(flagged);
        Assert.Equal(7, flagged[0].WeekNumber);
        Assert.Equal(Start.AddDays(42), flagged[0].StartDate);
        Assert.Same(flagged[0], schedule.RateChangeWeek);
    }

    [Fact]
    public void RateChangeIsStillWeekSevenWhenTheAmountDoesNotChange()
    {
        // 90% of 150 is 135, below the standard rate: the amount is flat across
        // the change, but the band still changes on week 7.
        var schedule = SmpCalculator.Calculate(Input(awe: 150m));

        Assert.Equal(135.00m, schedule.Weeks[5].StatutoryAmount);
        Assert.Equal(135.00m, schedule.Weeks[6].StatutoryAmount);
        Assert.Equal(7, schedule.RateChangeWeek.WeekNumber);
    }

    // ----------------------------------------------------------------- amounts

    [Fact]
    public void HigherRateIsNinetyPercentOfAverageWeeklyEarnings()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m));

        Assert.All(schedule.Weeks.Take(6), w => Assert.Equal(270.00m, w.StatutoryAmount));
    }

    [Fact]
    public void StandardWeeksPayTheStandardRateWhenNinetyPercentIsHigher()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m));

        Assert.All(schedule.Weeks.Skip(6), w => Assert.Equal(187.18m, w.StatutoryAmount));
    }

    [Fact]
    public void StandardWeeksPayNinetyPercentWhenThatIsLower()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 200m));

        Assert.All(schedule.Weeks.Skip(6), w => Assert.Equal(180.00m, w.StatutoryAmount));
    }

    [Fact]
    public void WhenNinetyPercentEqualsTheStandardRateEitherGivesTheSameAmount()
    {
        // 90% of 207.98 is 187.182, rounding to exactly the standard rate.
        var schedule = SmpCalculator.Calculate(Input(awe: 207.98m));

        Assert.Equal(187.18m, schedule.Weeks[5].StatutoryAmount);
        Assert.Equal(187.18m, schedule.Weeks[6].StatutoryAmount);
    }

    [Fact]
    public void AmountsRoundToThePennyAwayFromZero()
    {
        // 90% of 333.33 is 299.997.
        var schedule = SmpCalculator.Calculate(Input(awe: 333.33m));

        Assert.Equal(300.00m, schedule.Weeks[0].StatutoryAmount);
    }

    [Fact]
    public void TotalsAddUpAcrossTheWholeLeave()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m));

        // 6 x 270.00 + 33 x 187.18
        Assert.Equal(7796.94m, schedule.TotalStatutory);
        Assert.Equal(0m, schedule.TotalTopUp);
        Assert.Equal(7796.94m, schedule.TotalPay);
    }

    // ----------------------------------------------------------- April uprating

    [Fact]
    public void StandardRateChangesFromTheWeekTheNewRateTakesEffect()
    {
        // Week 19 starts Sunday 6 April 2025, the first day of the 2025-26 rate.
        var schedule = SmpCalculator.Calculate(Input(awe: 300m) with { SmpStartDate = new DateOnly(2024, 12, 1) });

        Assert.Equal(new DateOnly(2025, 3, 30), schedule.Weeks[17].StartDate);
        Assert.Equal(184.03m, schedule.Weeks[17].StatutoryAmount);

        Assert.Equal(new DateOnly(2025, 4, 6), schedule.Weeks[18].StartDate);
        Assert.Equal(187.18m, schedule.Weeks[18].StatutoryAmount);

        Assert.Equal(184.03m, schedule.Weeks[6].StatutoryAmount);
        Assert.Equal(187.18m, schedule.Weeks[38].StatutoryAmount);
    }

    [Fact]
    public void HigherRateWeeksAreNotAffectedByTheStandardRate()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m) with { SmpStartDate = new DateOnly(2025, 3, 2) });

        Assert.All(schedule.Weeks.Take(6), w => Assert.Equal(270.00m, w.StatutoryAmount));
    }

    [Theory]
    [InlineData(2025, 4, 5, "184.03")]
    [InlineData(2025, 4, 6, "187.18")]
    [InlineData(2025, 4, 13, "187.18")]
    public void StandardRateIsLookedUpByDate(int y, int m, int d, string expected)
    {
        Assert.Equal(decimal.Parse(expected), SmpRates.StandardRateOn(new DateOnly(y, m, d)));
    }

    [Fact]
    public void DaysBeforeTheNewRateTakesEffectUseThePreviousYearsRate()
    {
        // 2024-25's rate starts Sunday 7 April 2024, so Saturday the 6th is still
        // 2023-24, which is not configured: raises rather than guesses.
        Assert.Throws<InvalidOperationException>(() => SmpRates.StandardRateOn(new DateOnly(2024, 4, 6)));
        Assert.Equal(184.03m, SmpRates.StandardRateOn(new DateOnly(2024, 4, 7)));
    }

    [Fact]
    public void AWeekInAnUnconfiguredTaxYearRaises()
    {
        var input = Input() with { SmpStartDate = new DateOnly(2030, 1, 6) };

        Assert.Throws<InvalidOperationException>(() => SmpCalculator.Calculate(input));
    }

    // ----------------------------------------------------------- contractual pay

    [Fact]
    public void ContractualPayAboveSmpIsPaidAsSmpPlusATopUp()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m) with
        {
            ContractualWeeklyPay = [300m, 300m, 300m, 300m, 300m, 300m],
        });

        var week1 = schedule.Weeks[0];
        Assert.Equal(270.00m, week1.StatutoryAmount);
        Assert.Equal(300.00m, week1.ContractualAmount);
        Assert.Equal(30.00m, week1.TopUp);
        Assert.Equal(300.00m, week1.TotalPay);
    }

    [Fact]
    public void ContractualPayBelowSmpNeverReducesPay()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m) with
        {
            ContractualWeeklyPay = [300m, 300m, 300m, 300m, 300m, 300m, 100m],
        });

        var week7 = schedule.Weeks[6];
        Assert.Equal(187.18m, week7.StatutoryAmount);
        Assert.Equal(100.00m, week7.ContractualAmount);
        Assert.Equal(0m, week7.TopUp);
        Assert.Equal(187.18m, week7.TotalPay);
    }

    [Fact]
    public void ContractualPayEqualToSmpIsNotATopUp()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m) with { ContractualWeeklyPay = [270m] });

        Assert.Equal(0m, schedule.Weeks[0].TopUp);
        Assert.Equal(270.00m, schedule.Weeks[0].TotalPay);
    }

    [Fact]
    public void WeeksBeyondTheContractualListGetSmpOnly()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m) with { ContractualWeeklyPay = [300m] });

        Assert.Equal(0m, schedule.Weeks[1].ContractualAmount);
        Assert.Equal(0m, schedule.Weeks[1].TopUp);
        Assert.Equal(270.00m, schedule.Weeks[1].TotalPay);
    }

    [Fact]
    public void TopUpIsTrackedSeparatelyFromStatutoryPay()
    {
        var schedule = SmpCalculator.Calculate(Input(awe: 300m) with
        {
            ContractualWeeklyPay = [300m, 300m],
        });

        // Two weeks of 30.00 above SMP; the statutory total is unchanged by it.
        Assert.Equal(60.00m, schedule.TotalTopUp);
        Assert.Equal(7796.94m, schedule.TotalStatutory);
        Assert.Equal(7856.94m, schedule.TotalPay);
    }

    // ---------------------------------------------------------- finding a week

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(42, 7)]
    [InlineData(272, 39)]
    public void ADateMapsToItsSmpWeek(int daysAfterStart, int expectedWeek)
    {
        var week = SmpCalculator.Calculate(Input()).WeekContaining(Start.AddDays(daysAfterStart));

        Assert.NotNull(week);
        Assert.Equal(expectedWeek, week!.WeekNumber);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(273)]
    public void ADateOutsideTheLeaveHasNoWeek(int daysAfterStart)
    {
        Assert.Null(SmpCalculator.Calculate(Input()).WeekContaining(Start.AddDays(daysAfterStart)));
    }

    // -------------------------------------------------------------- bad input

    [Fact]
    public void NegativeEarningsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SmpCalculator.Calculate(Input(awe: -1m)));
    }

    [Fact]
    public void NegativeContractualPayIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            SmpCalculator.Calculate(Input() with { ContractualWeeklyPay = [100m, -5m] }));
    }
}
