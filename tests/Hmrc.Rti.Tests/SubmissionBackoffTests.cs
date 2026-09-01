using Hmrc.Rti.Api.Application;
using Xunit;

namespace Hmrc.Rti.Tests;

/// <summary>
/// The retry cadence for transient transmission failures.
///
/// This is worth testing directly rather than through the service, because the
/// consequence of getting it wrong is invisible locally. HMRC rate-limits, so a
/// curve that grows too slowly turns one employer's failing submission into a
/// throttled endpoint for every other employer filing at the same time. A curve
/// that grows too fast pushes a recoverable failure past a filing deadline.
///
/// The documented curve is 1, 2, 4, 8, 16 minutes, capped at 16.
/// </summary>
public class SubmissionBackoffTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    public void Follows_the_documented_curve(int attempt, int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            SubmissionService.BackoffFor(attempt));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(50)]
    [InlineData(int.MaxValue)]
    public void Never_exceeds_the_cap(int attempt)
    {
        // The attempt ceiling should stop us before here, but the cap is what
        // guarantees an overflow in the caller cannot schedule a retry years out.
        Assert.Equal(TimeSpan.FromMinutes(16), SubmissionService.BackoffFor(attempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Treats_a_non_positive_attempt_as_the_first(int attempt)
    {
        // Defensive: a caller that passes a zero-based or unset attempt should
        // get the shortest delay, never a negative TimeSpan that would schedule
        // the retry in the past and spin.
        Assert.Equal(TimeSpan.FromMinutes(1), SubmissionService.BackoffFor(attempt));
    }

    [Fact]
    public void Is_never_negative_or_zero()
    {
        for (var attempt = -5; attempt <= 20; attempt++)
        {
            Assert.True(
                SubmissionService.BackoffFor(attempt) > TimeSpan.Zero,
                $"attempt {attempt} produced a non-positive delay");
        }
    }

    [Fact]
    public void Never_decreases_as_attempts_rise()
    {
        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var delay = SubmissionService.BackoffFor(attempt);
            Assert.True(delay >= previous,
                $"attempt {attempt} backed off less than attempt {attempt - 1}");
            previous = delay;
        }
    }

    [Fact]
    public void Total_wait_across_the_attempt_ceiling_stays_inside_the_hour()
    {
        // 1 + 2 + 4 + 8 + 16 = 31 minutes. The point of the assertion is the
        // upper bound: a submission either succeeds or reaches an operator well
        // within the same working hour, rather than retrying quietly all day.
        var total = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            total += SubmissionService.BackoffFor(attempt);
        }

        Assert.Equal(TimeSpan.FromMinutes(31), total);
        Assert.True(total < TimeSpan.FromHours(1));
    }
}
