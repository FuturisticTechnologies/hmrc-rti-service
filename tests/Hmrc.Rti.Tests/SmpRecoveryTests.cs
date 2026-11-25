using Hmrc.Rti.Api.Application.Smp;
using Xunit;

namespace Hmrc.Rti.Tests;

public class SmpRecoveryTests
{
    // ---------------------------------------------------------------- standard

    [Fact]
    public void StandardEmployersRecoverNinetyTwoPercent()
    {
        var amounts = SmpRecovery.Calculate(1000m, SmpEmployerRelief.Standard);

        Assert.Equal(920.00m, amounts.Recoverable);
        Assert.Equal(0m, amounts.NicCompensation);
        Assert.Equal(920.00m, amounts.TotalRecoverable);
        Assert.Equal(80.00m, amounts.NetCostToEmployer);
    }

    [Fact]
    public void StandardRecoveryRoundsToThePenny()
    {
        // 92% of 187.18 is 172.2056.
        var amounts = SmpRecovery.Calculate(187.18m, SmpEmployerRelief.Standard);

        Assert.Equal(172.21m, amounts.Recoverable);
        Assert.Equal(14.97m, amounts.NetCostToEmployer);
    }

    // ---------------------------------------------------------- small employer

    [Fact]
    public void SmallEmployersRecoverAllOfItPlusThreePercentCompensation()
    {
        var amounts = SmpRecovery.Calculate(1000m, SmpEmployerRelief.SmallEmployer);

        Assert.Equal(1000.00m, amounts.Recoverable);
        Assert.Equal(30.00m, amounts.NicCompensation);
        Assert.Equal(1030.00m, amounts.TotalRecoverable);
    }

    [Fact]
    public void SmallEmployerReliefCanExceedTheCostOfTheSmp()
    {
        // 3% of 187.18 is 5.6154.
        var amounts = SmpRecovery.Calculate(187.18m, SmpEmployerRelief.SmallEmployer);

        Assert.Equal(187.18m, amounts.Recoverable);
        Assert.Equal(5.62m, amounts.NicCompensation);
        Assert.Equal(192.80m, amounts.TotalRecoverable);
        Assert.Equal(-5.62m, amounts.NetCostToEmployer);
    }

    [Theory]
    [InlineData("45000", true)]
    [InlineData("44999.99", true)]
    [InlineData("45000.01", false)]
    [InlineData("0", true)]
    public void SmallEmployerThresholdIsFortyFiveThousandInclusive(string nics, bool small)
    {
        Assert.Equal(small, SmpRecovery.QualifiesForSmallEmployerRelief(decimal.Parse(nics)));
    }

    // ------------------------------------------------------------------- edges

    [Theory]
    [InlineData(SmpEmployerRelief.Standard)]
    [InlineData(SmpEmployerRelief.SmallEmployer)]
    public void NothingPaidMeansNothingRecovered(SmpEmployerRelief relief)
    {
        var amounts = SmpRecovery.Calculate(0m, relief);

        Assert.Equal(0m, amounts.TotalRecoverable);
        Assert.Equal(0m, amounts.NetCostToEmployer);
    }

    [Fact]
    public void NegativeSmpIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SmpRecovery.Calculate(-0.01m, SmpEmployerRelief.Standard));
    }

    [Fact]
    public void AnUnknownReliefIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SmpRecovery.Calculate(100m, (SmpEmployerRelief)99));
    }

    [Fact]
    public void RecoveryFractionsAreHmrcs()
    {
        Assert.Equal(0.92m, SmpRecovery.StandardRecoveryFraction);
        Assert.Equal(1.00m, SmpRecovery.SmallEmployerRecoveryFraction);
        Assert.Equal(0.03m, SmpRecovery.SmallEmployerCompensationFraction);
    }

    // --------------------------------------------------------------- from weeks

    private static SmpSchedule Schedule(IReadOnlyList<decimal>? contractual = null) =>
        SmpCalculator.Calculate(new SmpCalculationInput
        {
            SmpStartDate = new DateOnly(2025, 6, 1),
            AverageWeeklyEarnings = 300m,
            ContractualWeeklyPay = contractual ?? [],
        });

    [Fact]
    public void RecoveryOverAScheduleUsesTheStatutoryTotal()
    {
        // 6 x 270.00 + 33 x 187.18 = 7796.94
        var standard = SmpRecovery.Calculate(Schedule().Weeks, SmpEmployerRelief.Standard);
        var small = SmpRecovery.Calculate(Schedule().Weeks, SmpEmployerRelief.SmallEmployer);

        Assert.Equal(7796.94m, standard.SmpPaid);
        Assert.Equal(7173.18m, standard.Recoverable); // 7173.1848

        Assert.Equal(7796.94m, small.Recoverable);
        Assert.Equal(233.91m, small.NicCompensation); // 233.9082
    }

    [Fact]
    public void ContractualTopUpsAreNeverRecoverable()
    {
        var withoutTopUp = Schedule();
        var withTopUp = Schedule([400m, 400m, 400m, 400m, 400m, 400m]);

        Assert.True(withTopUp.TotalTopUp > 0);

        var a = SmpRecovery.Calculate(withoutTopUp.Weeks, SmpEmployerRelief.Standard);
        var b = SmpRecovery.Calculate(withTopUp.Weeks, SmpEmployerRelief.Standard);

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecoveryCanBeTakenOverASubsetOfWeeks()
    {
        // The first six weeks only: 6 x 270.00 = 1620.00 at 92%.
        var amounts = SmpRecovery.Calculate(Schedule().Weeks.Take(6), SmpEmployerRelief.Standard);

        Assert.Equal(1620.00m, amounts.SmpPaid);
        Assert.Equal(1490.40m, amounts.Recoverable);
    }
}
