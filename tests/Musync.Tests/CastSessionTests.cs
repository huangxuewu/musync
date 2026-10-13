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
    public async Task SessionDisposalCancelsPendingClientRequestsWithoutAReceiver()
    {
        // SharpCaster queues a status request even when its transport is
        // absent. This exercises its real pending-request cancellation without
        // opening a network connection or sending audio to a receiver.
        var session = new CastSession();
        var pending = session.ReadVolumeAsync();
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.True(pending.IsCompleted);
        }
        finally
        {
            await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void ConnectionLossIsStickyUntilANewConnectionBegins()
    {
        var connection = new CastConnectionFeedback();
        connection.BeginConnect(); connection.Connected(); connection.ObserveDisconnect();
        Assert.True(connection.ConnectionLost); Assert.Contains("connection lost", connection.Error);
        connection.ExpectDisconnect(); connection.ObserveDisconnect();
        Assert.True(connection.ConnectionLost);
        connection.BeginConnect(); Assert.False(connection.ConnectionLost); Assert.Null(connection.Error);
        connection.Connected(); Assert.False(connection.ConnectionLost);
    }

    [Fact]
    public void ExpectedDisconnectAndMediaRejectionDoNotMarkTheTransportLost()
    {
        var connection = new CastConnectionFeedback();
        connection.ObserveDisconnect(); Assert.False(connection.ConnectionLost);
        connection.BeginConnect(); connection.Connected();
        var media = new CastMediaFeedback(); media.BeginLoad(); media.Fail("LOAD_FAILED");
        Assert.NotNull(media.Error); Assert.False(connection.ConnectionLost);
        connection.ExpectDisconnect(); connection.ObserveDisconnect();
        Assert.Null(connection.Error); Assert.False(connection.ConnectionLost);
    }

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
