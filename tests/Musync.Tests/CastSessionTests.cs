using Musync.Network;
using Sharpcaster.Extensions;
using Sharpcaster.Messages.Media;
using Sharpcaster.Models.Media;
using System.Text.Json;
using Xunit;

namespace Musync.Tests;

public sealed class CastSessionTests
{
    [Fact]
    public void SerializedLoadTellsTheReceiverThatHlsSegmentsContainMp3()
    {
        const string url = "http://192.0.2.1:5000/s/test/index.m3u8";
        var request = new LoadMessage { Media = CastSession.CreateMedia(url), AutoPlay = true };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, SharpcasteSerializationContext.Default.LoadMessage));
        var media = json.RootElement.GetProperty("media");
        Assert.Equal(url, media.GetProperty("contentId").GetString());
        Assert.Equal(url, media.GetProperty("contentUrl").GetString());
        Assert.Equal("application/vnd.apple.mpegurl", media.GetProperty("contentType").GetString());
        Assert.Equal("MP3", media.GetProperty("hlsSegmentFormat").GetString());
        Assert.Equal("LIVE", media.GetProperty("streamType").GetString());
        Assert.Equal(-1, media.GetProperty("duration").GetDouble());
        Assert.True(json.RootElement.GetProperty("autoplay").GetBoolean());
    }

    [Fact]
    public void RuntimeReceiverFailureRemainsVisibleAfterALoadWasAccepted()
    {
        var feedback = new CastMediaFeedback();
        feedback.BeginLoad();
        feedback.Observe(new() { PlayerState = PlayerStateType.Buffering });
        Assert.Null(feedback.Error);
        feedback.Observe(new() { PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Contains("audio stream failed", feedback.Error);
        feedback.Observe(new() { PlayerState = PlayerStateType.Buffering });
        Assert.NotNull(feedback.Error);
    }

    [Fact]
    public void ExpectedPauseStopAndDisconnectDoNotCreateMediaErrors()
    {
        var feedback = new CastMediaFeedback();
        feedback.BeginLoad(); feedback.ExpectStop();
        feedback.Fail("LOAD_FAILED");
        feedback.Observe(new() { PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" });
        Assert.Null(feedback.Error);
    }

    [Fact]
    public void ANewLoadClearsAnEarlierFailureButRetainsNewRejections()
    {
        var feedback = new CastMediaFeedback();
        feedback.BeginLoad(); feedback.Fail("LOAD_FAILED");
        Assert.Equal("LOAD_FAILED", feedback.Error);
        feedback.ExpectStop(); feedback.BeginLoad();
        Assert.Null(feedback.Error);
        feedback.Fail("INVALID_REQUEST");
        Assert.Equal("INVALID_REQUEST", feedback.Error);
    }
}
