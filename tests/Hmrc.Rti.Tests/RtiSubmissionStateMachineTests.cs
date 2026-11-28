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

    // --- Full 7×7 pair coverage -------------------------------------------

    private static readonly Dictionary<RtiSubmissionStatus, RtiSubmissionStatus[]> Expected = new()
    {
        [RtiSubmissionStatus.Draft] = [RtiSubmissionStatus.Validated, RtiSubmissionStatus.Failed],
        [RtiSubmissionStatus.Validated] = [RtiSubmissionStatus.Queued, RtiSubmissionStatus.Failed],
        [RtiSubmissionStatus.Queued] = [RtiSubmissionStatus.Transmitting, RtiSubmissionStatus.Failed],
        [RtiSubmissionStatus.Transmitting] =
            [RtiSubmissionStatus.Accepted, RtiSubmissionStatus.Rejected, RtiSubmissionStatus.Failed],
        [RtiSubmissionStatus.Rejected] = [RtiSubmissionStatus.Queued],
        [RtiSubmissionStatus.Failed] = [RtiSubmissionStatus.Queued],
        [RtiSubmissionStatus.Accepted] = [],
    };

    public static TheoryData<RtiSubmissionStatus, RtiSubmissionStatus> AllPairs()
    {
        var data = new TheoryData<RtiSubmissionStatus, RtiSubmissionStatus>();
        foreach (var from in Enum.GetValues<RtiSubmissionStatus>())
        foreach (var to in Enum.GetValues<RtiSubmissionStatus>())
            data.Add(from, to);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_pair_matches_the_declared_table(RtiSubmissionStatus from, RtiSubmissionStatus to)
    {
        var legal = Expected[from].Contains(to);

        Assert.Equal(legal, RtiSubmissionStateMachine.CanTransition(from, to));

        if (legal)
        {
            RtiSubmissionStateMachine.EnsureCanTransition(from, to); // must not throw
        }
        else
        {
            var ex = Assert.Throws<IllegalTransitionException>(
                () => RtiSubmissionStateMachine.EnsureCanTransition(from, to));
            Assert.Equal(from, ex.From);
            Assert.Equal(to, ex.To);
        }
    }

    // --- Undefined enum value behaviour -----------------------------------

    [Fact]
    public void An_undefined_status_raises_rather_than_falling_through()
    {
        var bogus = (RtiSubmissionStatus)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => RtiSubmissionStateMachine.NextStates(bogus));
        Assert.Throws<ArgumentOutOfRangeException>(() => RtiSubmissionStateMachine.IsTerminal(bogus));
        Assert.Throws<ArgumentOutOfRangeException>(() => RtiSubmissionStateMachine.IsRetryable(bogus));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RtiSubmissionStateMachine.CanTransition(bogus, RtiSubmissionStatus.Draft));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RtiSubmissionStateMachine.CanTransition(RtiSubmissionStatus.Draft, bogus));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RtiSubmissionStateMachine.EnsureCanTransition(RtiSubmissionStatus.Draft, bogus));
    }

    // --- TransitionTo on the domain model ---------------------------------

    [Fact]
    public void TransitionTo_assigns_status_and_appends_exactly_one_event()
    {
        var actor = Guid.NewGuid();
        var submission = new RtiSubmission { TenantId = Guid.NewGuid() };

        var evt = submission.TransitionTo(
            RtiSubmissionStatus.Validated, "validated", "Local validation passed.", actor);

        Assert.Equal(RtiSubmissionStatus.Validated, submission.Status);
        var recorded = Assert.Single(submission.Events);
        Assert.Same(evt, recorded);
        Assert.Equal(RtiSubmissionStatus.Draft, recorded.FromStatus);
        Assert.Equal(RtiSubmissionStatus.Validated, recorded.ToStatus);
        Assert.Equal("validated", recorded.EventType);
        Assert.Equal(submission.Id, recorded.SubmissionId);
        Assert.Equal(submission.TenantId, recorded.TenantId);
        Assert.Equal(actor, recorded.ActorUserId);
    }

    [Fact]
    public void An_illegal_TransitionTo_changes_nothing()
    {
        var submission = new RtiSubmission();
        var before = submission.UpdatedAt;

        Assert.Throws<IllegalTransitionException>(() =>
            submission.TransitionTo(RtiSubmissionStatus.Accepted, "accepted", "skipped the queue"));

        Assert.Equal(RtiSubmissionStatus.Draft, submission.Status);
        Assert.Empty(submission.Events);
        Assert.Equal(before, submission.UpdatedAt);
    }

    [Fact]
    public void An_accepted_submission_cannot_be_moved_by_TransitionTo()
    {
        var submission = new RtiSubmission();
        // Walk through to Accepted.
        foreach (var step in new[]
        {
            RtiSubmissionStatus.Validated, RtiSubmissionStatus.Queued,
            RtiSubmissionStatus.Transmitting, RtiSubmissionStatus.Accepted,
        })
        {
            submission.TransitionTo(step, step.ToString().ToLowerInvariant(), "step");
        }

        var eventCount = submission.Events.Count;

        foreach (var target in Enum.GetValues<RtiSubmissionStatus>())
        {
            Assert.Throws<IllegalTransitionException>(
                () => submission.TransitionTo(target, "mutate", "must not happen"));
        }

        Assert.Equal(RtiSubmissionStatus.Accepted, submission.Status);
        Assert.Equal(eventCount, submission.Events.Count);
    }

    [Fact]
    public void An_empty_actor_id_is_recorded_as_null()
    {
        var submission = new RtiSubmission();
        var evt = submission.TransitionTo(
            RtiSubmissionStatus.Validated, "validated", "ok", Guid.Empty);
        Assert.Null(evt.ActorUserId);
    }

    [Fact]
    public void AppendEvent_records_a_non_transition_event_with_no_status_fields()
    {
        var submission = new RtiSubmission { TenantId = Guid.NewGuid() };
        var actor = Guid.NewGuid();

        var evt = submission.AppendEvent("created", "FPS built from pay run xyz.", actor);

        Assert.Equal(RtiSubmissionStatus.Draft, submission.Status); // unchanged
        Assert.Single(submission.Events);
        Assert.Same(evt, submission.Events[0]);
        Assert.Null(evt.FromStatus);
        Assert.Null(evt.ToStatus);
        Assert.Equal("created", evt.EventType);
        Assert.Equal(actor, evt.ActorUserId);
        Assert.Equal(submission.Id, evt.SubmissionId);
        Assert.Equal(submission.TenantId, evt.TenantId);
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
