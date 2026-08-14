using Hmrc.Rti.Api.Domain;
using Xunit;

namespace Hmrc.Rti.Tests;

public class RtiSubmissionStateMachineTests
{
    [Theory]
    [InlineData(RtiSubmissionStatus.Draft, RtiSubmissionStatus.Validated)]
    [InlineData(RtiSubmissionStatus.Draft, RtiSubmissionStatus.Failed)]
    [InlineData(RtiSubmissionStatus.Validated, RtiSubmissionStatus.Queued)]
    [InlineData(RtiSubmissionStatus.Queued, RtiSubmissionStatus.Transmitting)]
    [InlineData(RtiSubmissionStatus.Transmitting, RtiSubmissionStatus.Accepted)]
    [InlineData(RtiSubmissionStatus.Transmitting, RtiSubmissionStatus.Rejected)]
    [InlineData(RtiSubmissionStatus.Transmitting, RtiSubmissionStatus.Failed)]
    [InlineData(RtiSubmissionStatus.Failed, RtiSubmissionStatus.Queued)]
    [InlineData(RtiSubmissionStatus.Rejected, RtiSubmissionStatus.Queued)]
    public void Allows_legal_transitions(RtiSubmissionStatus from, RtiSubmissionStatus to) =>
        Assert.True(RtiSubmissionStateMachine.CanTransition(from, to));

    [Theory]
    // Cannot skip validation and go straight to the wire.
    [InlineData(RtiSubmissionStatus.Draft, RtiSubmissionStatus.Queued)]
    [InlineData(RtiSubmissionStatus.Draft, RtiSubmissionStatus.Accepted)]
    [InlineData(RtiSubmissionStatus.Validated, RtiSubmissionStatus.Accepted)]
    // Cannot transmit without first being queued.
    [InlineData(RtiSubmissionStatus.Validated, RtiSubmissionStatus.Transmitting)]
    // A rejection is not a retry in itself - it must be re-queued.
    [InlineData(RtiSubmissionStatus.Rejected, RtiSubmissionStatus.Transmitting)]
    [InlineData(RtiSubmissionStatus.Failed, RtiSubmissionStatus.Accepted)]
    public void Refuses_illegal_transitions(RtiSubmissionStatus from, RtiSubmissionStatus to) =>
        Assert.False(RtiSubmissionStateMachine.CanTransition(from, to));

    [Fact]
    public void Accepted_is_terminal()
    {
        Assert.True(RtiSubmissionStateMachine.IsTerminal(RtiSubmissionStatus.Accepted));
        Assert.Empty(RtiSubmissionStateMachine.NextStates(RtiSubmissionStatus.Accepted));

        // An accepted filing is amended by sending a further FPS, never by
        // mutating the one HMRC already holds.
        foreach (var target in Enum.GetValues<RtiSubmissionStatus>())
        {
            Assert.False(RtiSubmissionStateMachine.CanTransition(RtiSubmissionStatus.Accepted, target));
        }
    }

    [Fact]
    public void Only_failed_and_rejected_are_retryable()
    {
        foreach (var status in Enum.GetValues<RtiSubmissionStatus>())
        {
            var expected = status is RtiSubmissionStatus.Failed or RtiSubmissionStatus.Rejected;
            Assert.Equal(expected, RtiSubmissionStateMachine.IsRetryable(status));
        }
    }

    [Fact]
    public void Every_status_has_an_explicit_entry()
    {
        // No status may fall through to a default. An unmapped state would silently
        // behave as terminal, which is exactly the wrong-but-plausible failure the
        // NI category matrix was built to avoid.
        foreach (var status in Enum.GetValues<RtiSubmissionStatus>())
        {
            var reachable = RtiSubmissionStateMachine.NextStates(status);
            var isTerminal = status == RtiSubmissionStatus.Accepted;
            Assert.True(isTerminal || reachable.Count > 0,
                $"Status {status} has no declared transitions and is not terminal.");
        }
    }
}
