using Musync.Network;
using Sharpcaster.Models.Media;
using Xunit;

namespace Musync.Tests;

public sealed class CastMediaStatusScopeTests
{
    [Fact]
    public void ReloadedMediaRejectsAnOldSessionsError()
    {
        var feedback = new CastMediaFeedback();
        var load = feedback.BeginLoad();
        feedback.AcceptLoad(load, new() { MediaSessionId = 101, PlayerState = PlayerStateType.Buffering });
        load = feedback.BeginLoad();
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Paused });
        feedback.Observe(new() { MediaSessionId = 101, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Null(feedback.Error);
    }

    [Fact]
    public void ReloadWindowIgnoresOldSessionStatus()
    {
        var feedback = new CastMediaFeedback();
        var load = feedback.BeginLoad();
        feedback.AcceptLoad(load, new() { MediaSessionId = 101, PlayerState = PlayerStateType.Playing });
        load = feedback.BeginLoad();
        // A broadcast can arrive while the new LOAD response is still pending.
        feedback.Observe(new() { MediaSessionId = 101, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Null(feedback.Error);
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Buffering });
        Assert.Null(feedback.Error);
    }

    [Fact]
    public void AnUnrelatedSessionsStatusCannotReplaceTheCurrentFailure()
    {
        var feedback = new CastMediaFeedback();
        var load = feedback.BeginLoad();
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Playing });
        feedback.Fail("Current stream could not load its next segment.");
        feedback.Observe(new() { MediaSessionId = 101, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Equal("Current stream could not load its next segment.", feedback.Error);
    }

    [Theory]
    [InlineData(102)]
    [InlineData(900)]
    public void ActiveSessionErrorsRemainStickyUntilAnotherLoadIsAccepted(int session)
    {
        var feedback = new CastMediaFeedback();
        var load = feedback.BeginLoad();
        feedback.AcceptLoad(load, new() { MediaSessionId = session, PlayerState = PlayerStateType.Paused });
        feedback.Observe(new() { MediaSessionId = session, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Contains("audio stream failed", feedback.Error);
        feedback.Observe(new() { MediaSessionId = session, PlayerState = PlayerStateType.Playing });
        Assert.NotNull(feedback.Error);
        load = feedback.BeginLoad();
        feedback.AcceptLoad(load, new() { MediaSessionId = session + 1, PlayerState = PlayerStateType.Buffering });
        feedback.Observe(new() { MediaSessionId = session, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Null(feedback.Error);
        feedback.Observe(new() { MediaSessionId = session + 1, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.NotNull(feedback.Error);
    }

    [Fact]
    public void AFailedLoadResponseIsNotIgnoredAsAnUnboundBroadcast()
    {
        var feedback = new CastMediaFeedback();
        var load = feedback.BeginLoad();
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Contains("audio stream failed", feedback.Error);
    }

    [Fact]
    public void ALoadRequestFailureSurvivesTheAcceptedStatus()
    {
        var feedback = new CastMediaFeedback();
        var load = feedback.BeginLoad(); feedback.Fail("LOAD_FAILED");
        feedback.Observe(new() { MediaSessionId = 102, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Buffering });
        Assert.Equal("LOAD_FAILED", feedback.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentFailureBroadcastBeforeLoadResponseIsRetainedRegardlessOfOldStatusOrder(bool oldFirst)
    {
        var feedback = new CastMediaFeedback(); var load = feedback.BeginLoad();
        var old = new MediaStatus { MediaSessionId = 101, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" };
        var current = new MediaStatus { MediaSessionId = 102, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" };
        feedback.Observe(oldFirst ? old : current); feedback.Observe(oldFirst ? current : old);
        Assert.Null(feedback.Error); // Neither broadcast identifies our LOAD yet.
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Buffering });
        Assert.Contains("audio stream failed", feedback.Error);
    }

    [Fact]
    public void StopCannotBeRearmedByADelayedLoadResponse()
    {
        var feedback = new CastMediaFeedback();
        var load = feedback.BeginLoad();
        feedback.Observe(new() { MediaSessionId = 102, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        feedback.ExpectStop();
        feedback.AcceptLoad(load, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        feedback.Observe(new() { MediaSessionId = 102, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Null(feedback.Error);
    }

    [Fact]
    public void APreviousLoadResponseCannotRebindANewerAcceptedSession()
    {
        var feedback = new CastMediaFeedback(); var oldLoad = feedback.BeginLoad();
        var currentLoad = feedback.BeginLoad();
        feedback.Observe(new() { MediaSessionId = 102, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        feedback.AcceptLoad(oldLoad, new() { MediaSessionId = 101, PlayerState = PlayerStateType.Buffering });
        Assert.Null(feedback.Error);
        feedback.AcceptLoad(currentLoad, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Paused });
        Assert.NotNull(feedback.Error); // The old reply did not consume the new session's pending error.
        currentLoad = feedback.BeginLoad();
        feedback.AcceptLoad(currentLoad, new() { MediaSessionId = 103, PlayerState = PlayerStateType.Paused });
        feedback.AcceptLoad(oldLoad, new() { MediaSessionId = 101, PlayerState = PlayerStateType.Buffering });
        feedback.Observe(new() { MediaSessionId = 101, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Null(feedback.Error);
        feedback.Observe(new() { MediaSessionId = 103, PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.NotNull(feedback.Error);
    }

    [Fact]
    public void DecodedWireMessagesKeepTheirPlaybackSessionBoundary()
    {
        var context = Sharpcaster.Extensions.SharpcasteSerializationContext.Default;
        var accepted = System.Text.Json.JsonSerializer.Deserialize("{\"mediaSessionId\":102,\"playerState\":\"PAUSED\"}", context.MediaStatus)!;
        var old = System.Text.Json.JsonSerializer.Deserialize("{\"mediaSessionId\":101,\"playerState\":\"IDLE\",\"idleReason\":\"ERROR\"}", context.MediaStatus)!;
        var current = System.Text.Json.JsonSerializer.Deserialize("{\"mediaSessionId\":102,\"playerState\":\"IDLE\",\"idleReason\":\"ERROR\"}", context.MediaStatus)!;
        var feedback = new CastMediaFeedback(); var load = feedback.BeginLoad(); feedback.AcceptLoad(load, accepted);
        feedback.Observe(old); Assert.Null(feedback.Error);
        feedback.Observe(current); Assert.Contains("audio stream failed", feedback.Error);
    }

    [Fact]
    public void ARejectedOldLoadReplyCannotFailItsReplacement()
    {
        var feedback = new CastMediaFeedback(); var oldLoad = feedback.BeginLoad();
        var currentLoad = feedback.BeginLoad();
        feedback.Fail(oldLoad, "Old LOAD returned no status.");
        feedback.AcceptLoad(currentLoad, new() { MediaSessionId = 102, PlayerState = PlayerStateType.Paused });
        Assert.Null(feedback.Error);
        feedback.Fail(currentLoad, "Current LOAD failed.");
        Assert.Equal("Current LOAD failed.", feedback.Error);
    }
}
