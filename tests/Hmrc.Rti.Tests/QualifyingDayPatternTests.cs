using Hmrc.Rti.Api.Application.Ssp;
using Xunit;

namespace Hmrc.Rti.Tests;

public class QualifyingDayPatternTests
{
    // 2026-08-03 is a Monday.
    private static readonly DateOnly Monday = new(2026, 8, 3);

    [Fact]
    public void WeekdaysHasFiveDaysAndExcludesTheWeekend()
    {
        var pattern = QualifyingDayPattern.Weekdays;

        Assert.Equal(5, pattern.DaysPerWeek);
        Assert.True(pattern.IsQualifying(Monday));
        Assert.False(pattern.IsQualifying(Monday.AddDays(5))); // Saturday
        Assert.False(pattern.IsQualifying(Monday.AddDays(6))); // Sunday
    }

    [Fact]
    public void AnEmptyPatternIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new QualifyingDayPattern([]));
    }

    [Fact]
    public void DuplicateDaysAreCountedOnce()
    {
        var pattern = new QualifyingDayPattern([DayOfWeek.Monday, DayOfWeek.Monday, DayOfWeek.Friday]);

        Assert.Equal(2, pattern.DaysPerWeek);
    }

    [Fact]
    public void CountBetweenIncludesBothEnds()
    {
        // Monday to Friday of one week.
        Assert.Equal(5, QualifyingDayPattern.Weekdays.CountBetween(Monday, Monday.AddDays(4)));
        Assert.Equal(1, QualifyingDayPattern.Weekdays.CountBetween(Monday, Monday));
    }

    [Fact]
    public void WeekendsAreNotCounted()
    {
        // Monday to the Sunday after next: ten weekdays.
        Assert.Equal(10, QualifyingDayPattern.Weekdays.CountBetween(Monday, Monday.AddDays(13)));
    }

    [Fact]
    public void ASingleNonQualifyingDayCountsZero()
    {
        Assert.Equal(0, QualifyingDayPattern.Weekdays.CountBetween(Monday.AddDays(5), Monday.AddDays(5)));
    }

    [Fact]
    public void ACustomPatternCountsOnlyItsOwnDays()
    {
        var partTime = new QualifyingDayPattern([DayOfWeek.Tuesday, DayOfWeek.Thursday]);

        Assert.Equal(2, partTime.DaysPerWeek);
        Assert.Equal(2, partTime.CountBetween(Monday, Monday.AddDays(6)));
        Assert.Equal(4, partTime.CountBetween(Monday, Monday.AddDays(13)));
    }

    [Fact]
    public void QualifyingDatesAreReturnedInOrder()
    {
        var dates = new QualifyingDayPattern([DayOfWeek.Wednesday, DayOfWeek.Monday])
            .QualifyingDatesBetween(Monday, Monday.AddDays(9))
            .ToList();

        Assert.Equal(
            new[] { Monday, Monday.AddDays(2), Monday.AddDays(7), Monday.AddDays(9) },
            dates);
    }

    [Fact]
    public void ReversedRangeIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => QualifyingDayPattern.Weekdays.CountBetween(Monday.AddDays(1), Monday));
    }

    [Fact]
    public void PatternsWithTheSameDaysAreEqualWhateverTheOrder()
    {
        var a = new QualifyingDayPattern([DayOfWeek.Monday, DayOfWeek.Friday]);
        var b = new QualifyingDayPattern([DayOfWeek.Friday, DayOfWeek.Monday]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, QualifyingDayPattern.Weekdays);
    }

    [Fact]
    public void SicknessPeriodCountsCalendarDaysInclusively()
    {
        Assert.Equal(1, new SicknessPeriod(Monday, Monday).CalendarDays);
        Assert.Equal(4, new SicknessPeriod(Monday, Monday.AddDays(3)).CalendarDays);
    }

    [Fact]
    public void SicknessPeriodEndingBeforeItStartsIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new SicknessPeriod(Monday, Monday.AddDays(-1)));
    }
}
