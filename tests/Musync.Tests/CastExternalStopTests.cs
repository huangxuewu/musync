using Musync.Network;
using Sharpcaster.Models.Media;
using Xunit;

namespace Musync.Tests;

public sealed class CastExternalStopTests
{
    private static MediaStatus Terminal(long session, string reason) => new()
    { MediaSessionId = session, PlayerState = PlayerStateType.Idle, IdleReason = reason };

    [Theory]
    [InlineData("CANCELLED", false)]
    [InlineData("CANCELLED", true)]
    [InlineData("INTERRUPTED", false)]
    [InlineData("INTERRUPTED", true)]
    public void OnlyTheAcceptedSessionsEarlyTerminalStatusReleasesControl(string reason, bool oldFirst)
    {
        var feedback = new CastMediaFeedback(); var load = feedback.BeginLoad("receiver");
        feedback.Observe(Terminal(oldFirst ? 101 : 102, reason));
        feedback.Observe(Terminal(oldFirst ? 102 : 101, reason));
        Assert.Null(feedback.Error); Assert.False(feedback.ControlLost);
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Paused });
        Assert.NotNull(feedback.Error); Assert.True(feedback.ControlLost); Assert.Null(feedback.Binding); Assert.Null(feedback.StopBinding());
        load = feedback.BeginLoad("receiver"); feedback.AcceptLoad(load, new() { MediaSessionId = 103, PlayerState = PlayerStateType.Paused });
        feedback.Observe(Terminal(102, reason));
        Assert.Null(feedback.Error); Assert.False(feedback.ControlLost); Assert.Equal(103, feedback.Binding?.Session);
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("INTERRUPTED")]
    [InlineData("ERROR")]
    public void ExpectedStopKeepsLaterTerminalStatusQuiet(string reason)
    {
        var feedback = new CastMediaFeedback(); var load = feedback.BeginLoad("receiver");
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Playing });
        Assert.NotNull(feedback.StopBinding()); feedback.Observe(Terminal(102, reason));
        Assert.Null(feedback.Error); Assert.False(feedback.ControlLost); Assert.Null(feedback.Binding);
    }

    [Theory]
    [InlineData("FINISHED", PlayerStateType.Idle)]
    [InlineData("UNKNOWN", PlayerStateType.Idle)]
    [InlineData(null, PlayerStateType.Idle)]
    [InlineData("CANCELLED", PlayerStateType.Paused)]
    [InlineData("INTERRUPTED", PlayerStateType.Buffering)]
    public void CompletionAndNonterminalStatesDoNotRelinquishControl(string? reason, PlayerStateType state)
    {
        var feedback = new CastMediaFeedback(); var load = feedback.BeginLoad("receiver");
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Playing });
        // The wire protocol omits idleReason for a newly idle receiver.
        feedback.Observe(new() { MediaSessionId = 102, PlayerState = state, IdleReason = reason! });
        Assert.Null(feedback.Error); Assert.False(feedback.ControlLost); Assert.Equal(102, feedback.Binding?.Session);
    }

    [Fact]
    public void ANewConnectionClearsTheOldOwnershipLossWithoutAcceptingItsLateLoadResponse()
    {
        var feedback = new CastMediaFeedback(); var load = feedback.BeginLoad("old-receiver");
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Playing });
        feedback.Observe(Terminal(102, "INTERRUPTED")); Assert.True(feedback.ControlLost);
        feedback.BeginConnection(); Assert.Null(feedback.Error); Assert.False(feedback.ControlLost);
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Paused });
        Assert.Null(feedback.Binding); Assert.False(feedback.IsCurrent(load));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExistingStreamFailureDoesNotHideOwnershipLossOrLoseItsOriginalDetail(bool beforeReply)
    {
        var feedback = new CastMediaFeedback(); var load = feedback.BeginLoad("receiver");
        if (!beforeReply) feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Playing });
        feedback.Observe(Terminal(102, "ERROR")); feedback.Observe(Terminal(102, "INTERRUPTED"));
        if (beforeReply) feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Paused });
        Assert.Contains("audio stream failed", feedback.Error); Assert.True(feedback.ControlLost); Assert.Null(feedback.StopBinding());
    }
}
