using Musync.Network;
using Xunit;

namespace Musync.Tests;

public sealed class CastRequestOwnershipTests
{
    private sealed class SessionLease : IAsyncDisposable
    {
        public CastSession Player { get; } = new();
        public async ValueTask DisposeAsync() => await Player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task ConnectAsync(CastSession session, FakeCastReceiver receiver)
    {
        await session.ConnectAsync(receiver.Device).WaitAsync(TimeSpan.FromSeconds(4));
        await session.LaunchAsync().WaitAsync(TimeSpan.FromSeconds(4));
    }
    private static async Task<FakeCastReceiver.Request> LoadAsync(CastSession session, FakeCastReceiver receiver, long id)
    {
        var pending = session.LoadAsync("http://127.0.0.1/example/index.m3u8");
        var request = await receiver.NextAsync("LOAD");
        await receiver.ReplyStatusAsync(request, id, "BUFFERING");
        await pending.WaitAsync(TimeSpan.FromSeconds(3)); return request;
    }
    private static async Task<FakeCastReceiver.Request> PrepareAsync(CastSession session, FakeCastReceiver receiver, long id = 102)
    {
        var pending = session.PrepareFileAsync("http://127.0.0.1/music.mp3", 120, 0, CancellationToken.None);
        var request = await receiver.NextAsync("LOAD");
        await receiver.ReplyStatusAsync(request, id); await pending.WaitAsync(TimeSpan.FromSeconds(3)); return request;
    }
    private static Task ControlAsync(CastSession session, string type) => type switch
    {
        "PLAY" => session.PlayPreparedAsync(), "PAUSE" => session.PausePreparedAsync(),
        "SEEK" => session.SeekPreparedAsync(24), "STOP" => session.StopMediaAsync(), _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    [Theory]
    [InlineData("LOAD_FAILED")]
    [InlineData("INVALID_REQUEST")]
    [InlineData("ERROR")]
    public async Task LateRequestErrorsCannotPoisonTheAcceptedReplacement(string type)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver);
        var old = await LoadAsync(session, receiver, 101); await LoadAsync(session, receiver, 102);
        await receiver.SendErrorAsync(old, type);
        // Ordered response dispatch proves the earlier error was consumed.
        Assert.Equal(.5, await session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(session.Error);
    }
    [Fact]
    public async Task DelayedRejectedLoadCannotFailANewerAcceptedLoad()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver);
        var first = session.LoadAsync("http://127.0.0.1/old/index.m3u8"); var old = await receiver.NextAsync("LOAD");
        try
        {
            await LoadAsync(session, receiver, 102);
            await receiver.SendErrorAsync(old, "LOAD_FAILED");
            await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Null(session.Error);
        }
        finally { await session.DisconnectAsync(); try { await first; } catch (IOException) { } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("LOAD_FAILED")]
    [InlineData("INVALID_REQUEST")]
    public async Task LateRequestErrorDuringReloadDoesNotRejectTheNewLoad(string type)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); var old = await LoadAsync(session, receiver, 101);
        var pending = session.LoadAsync("http://127.0.0.1/new/index.m3u8"); var current = await receiver.NextAsync("LOAD");
        await receiver.SendErrorAsync(old, type); await receiver.ReplyStatusAsync(current, 102, "BUFFERING");
        await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Null(session.Error);
    }

    [Theory]
    [InlineData("PLAY")]
    [InlineData("PAUSE")]
    [InlineData("SEEK")]
    [InlineData("STOP")]
    public async Task ControlsUseTheAcceptedSessionAfterAnOldMediaBroadcast(string type)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); var load = await PrepareAsync(session, receiver);
        await receiver.BroadcastAsync(load, FakeCastReceiver.Status(101, "PLAYING", 99));
        await session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var pending = ControlAsync(session, type); var request = await receiver.NextAsync(type);
        await receiver.ReplyStatusAsync(request, 102, type == "STOP" ? "IDLE" : "PAUSED");
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(102, request.Payload.GetProperty("mediaSessionId").GetInt64());
        Assert.Equal("loopback-transport", request.Envelope.DestinationId);
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task PositionUsesItsOwnedEntryWhenAStatusReplyContainsSeveralSessions()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); await PrepareAsync(session, receiver);
        receiver.StatusReplyOverride = [FakeCastReceiver.Status(101, "PLAYING", 99), FakeCastReceiver.Status(102, "PAUSED", 12)];
        Assert.Equal(12, await session.ReadPositionAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(session.Error);
    }

    [Theory]
    [InlineData("PLAY", "INVALID_REQUEST")]
    [InlineData("PAUSE", "INVALID_REQUEST")]
    [InlineData("SEEK", "INVALID_REQUEST")]
    [InlineData("SEEK", "ERROR")]
    public async Task ARejectedControlFailsItsOperationWithoutPoisoningPlaybackOrRetry(string type, string errorType)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); await PrepareAsync(session, receiver);
        var pending = ControlAsync(session, type); var request = await receiver.NextAsync(type);
        await receiver.SendErrorAsync(request, errorType);
        var error = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains(errorType == "ERROR" ? "201" : "INVALID_COMMAND", error.Message); Assert.Null(session.Error);
        pending = ControlAsync(session, type); request = await receiver.NextAsync(type);
        await receiver.ReplyStatusAsync(request, 102); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(12, await session.ReadPositionAsync().WaitAsync(TimeSpan.FromSeconds(3))); Assert.Null(session.Error);
    }

    [Theory]
    [InlineData("LOAD_FAILED")]
    [InlineData("INVALID_REQUEST")]
    [InlineData("ERROR")]
    public async Task TheCurrentLoadRejectionRemainsVisibleAndANewLoadCanRecover(string type)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver);
        var pending = session.LoadAsync("http://127.0.0.1/music/index.m3u8"); var request = await receiver.NextAsync("LOAD");
        await receiver.SendErrorAsync(request, type);
        var error = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(error.Message, session.Error); Assert.False(session.ConnectionLost);
        await LoadAsync(session, receiver, 103); Assert.Null(session.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingCompletesThroughANewStatusRequestWithoutWaitingForASecondOriginalReply(bool live)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver);
        var pending = live ? session.LoadAsync("http://127.0.0.1/music/index.m3u8")
            : session.PrepareFileAsync("http://127.0.0.1/music.mp3", 120, 0, CancellationToken.None);
        var request = await receiver.NextAsync("LOAD"); await receiver.ReplyLoadingAsync(request, 102);
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(live, request.Payload.GetProperty("autoplay").GetBoolean());
        Assert.Equal(12, await session.ReadPositionAsync().WaitAsync(TimeSpan.FromSeconds(3))); Assert.Null(session.Error);
    }

    [Fact]
    public async Task MatchingRuntimeMediaErrorsStillFailTheActivePlayback()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); var request = await PrepareAsync(session, receiver);
        await receiver.BroadcastAsync(request, new { mediaSessionId = 102, playerState = "IDLE", idleReason = "ERROR" });
        await session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("audio stream failed", session.Error); Assert.False(session.ConnectionLost);
    }

    [Theory]
    [InlineData("CANCELLED", "stopped on the receiver")]
    [InlineData("INTERRUPTED", "replaced by another source")]
    public async Task ExternalStopOfTheOwnedPlaybackRemainsVisibleAndReleasesReceiverControls(string reason, string detail)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); var request = await PrepareAsync(session, receiver);
        await receiver.BroadcastAsync(request, new { mediaSessionId = 102, playerState = "IDLE", idleReason = reason });
        await session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(detail, session.Error); Assert.False(session.ConnectionLost); Assert.True(session.ControlLost);
        var volumeWrites = receiver.VolumeWrites; var mediaCommands = receiver.MediaCommands;
        await Assert.ThrowsAsync<IOException>(() => session.SetVolumeAsync(.25));
        await Assert.ThrowsAsync<IOException>(() => session.PlayPreparedAsync());
        await session.StopMediaAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(volumeWrites, receiver.VolumeWrites); Assert.Equal(mediaCommands, receiver.MediaCommands);
        Assert.Contains(detail, session.Error);
        await PrepareAsync(session, receiver, 103); Assert.Null(session.Error); Assert.False(session.ControlLost);
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("INTERRUPTED")]
    public async Task UnrelatedTerminalBroadcastDoesNotRelinquishTheAcceptedPlayback(string reason)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); var request = await PrepareAsync(session, receiver);
        await receiver.BroadcastAsync(request, new { mediaSessionId = 101, playerState = "IDLE", idleReason = reason });
        await receiver.BroadcastAsync(request, new { mediaSessionId = 999, playerState = "IDLE", idleReason = reason });
        await session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(session.Error); Assert.False(session.ControlLost);
        var pending = session.PlayPreparedAsync(); var play = await receiver.NextAsync("PLAY");
        Assert.Equal(102, play.Payload.GetProperty("mediaSessionId").GetInt64());
        await receiver.ReplyStatusAsync(play, 102, "PLAYING"); await pending.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("INTERRUPTED")]
    public async Task ExternalStopBeforeLoadReplyFailsOnlyItsMatchingPreparation(string reason)
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver);
        var pending = session.PrepareFileAsync("http://127.0.0.1/music.mp3", 120, 0, default);
        var request = await receiver.NextAsync("LOAD");
        await receiver.BroadcastAsync(request, new { mediaSessionId = 102, playerState = "IDLE", idleReason = reason });
        await receiver.ReplyStatusAsync(request, 102);
        var error = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(session.Error, error.Message); Assert.True(session.ControlLost);
        await session.StopMediaAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, receiver.MediaCommands);
    }

    [Fact]
    public async Task ExpectedStopReplyAndBroadcastDoNotReportExternalInterruption()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); var load = await PrepareAsync(session, receiver);
        var pending = session.StopMediaAsync(); var stop = await receiver.NextAsync("STOP");
        await receiver.BroadcastAsync(load, new { mediaSessionId = 102, playerState = "IDLE", idleReason = "CANCELLED" });
        await receiver.ReplyStatusAsync(stop, 102, "IDLE"); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(session.Error); Assert.False(session.ControlLost);
    }

    [Fact]
    public async Task AStoppedPendingLoadCannotRearmItsControlsWhenItsStatusArrivesLater()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver);
        var pending = session.LoadAsync("http://127.0.0.1/music/index.m3u8"); var request = await receiver.NextAsync("LOAD");
        await session.StopMediaAsync(); await receiver.ReplyStatusAsync(request, 102, "BUFFERING");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PlayPreparedAsync()); Assert.Null(session.Error);
    }

    [Fact]
    public async Task FilePreparationSeeksAndHoldsTheOwnedMusicAtItsRequestedPosition()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver);
        var pending = session.PrepareFileAsync("http://127.0.0.1/music.mp3", 120, 14, CancellationToken.None);
        var load = await receiver.NextAsync("LOAD"); await receiver.ReplyStatusAsync(load, 102);
        var seek = await receiver.NextAsync("SEEK"); await receiver.ReplyStatusAsync(seek, 102, "BUFFERING", 14);
        var pause = await receiver.NextAsync("PAUSE"); await receiver.ReplyStatusAsync(pause, 102, "PAUSED", 14);
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(14, seek.Payload.GetProperty("currentTime").GetDouble());
        Assert.Equal(102, seek.Payload.GetProperty("mediaSessionId").GetInt64());
        Assert.Equal(102, pause.Payload.GetProperty("mediaSessionId").GetInt64());
        Assert.Equal(3, new[] { load.Id, seek.Id, pause.Id }.Distinct().Count());
        Assert.Equal(14, await session.ReadPositionAsync().WaitAsync(TimeSpan.FromSeconds(3))); Assert.Null(session.Error);
    }

    [Fact]
    public async Task AReceiverApplicationBroadcastCannotRedirectTheOwnedMediaControls()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); var load = await PrepareAsync(session, receiver);
        await receiver.BroadcastForeignApplicationAsync(load);
        await session.ReadPositionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var pending = session.PlayPreparedAsync(); var play = await receiver.NextAsync("PLAY");
        await receiver.ReplyStatusAsync(play, 102, "PLAYING"); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("loopback-transport", play.Envelope.DestinationId); Assert.Null(session.Error);
    }

    [Fact]
    public async Task CancelingLoadingKeepsCancellationDistinctAndStopsTheBoundPreparedMusic()
    {
        await using var receiver = new FakeCastReceiver(); await using var lease = new SessionLease(); var session = lease.Player;
        await ConnectAsync(session, receiver); using var cancel = new CancellationTokenSource();
        receiver.StatusReplyOverride = [new { mediaSessionId = 102, playerState = "IDLE", extendedStatus = new { playerState = "LOADING" } }];
        var pending = session.PrepareFileAsync("http://127.0.0.1/music.mp3", 120, 0, cancel.Token);
        var load = await receiver.NextAsync("LOAD"); await receiver.ReplyLoadingAsync(load, 102);
        await receiver.MediaPolled.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3))); Assert.Null(session.Error);
        pending = session.StopMediaAsync(); var stop = await receiver.NextAsync("STOP");
        await receiver.ReplyStatusAsync(stop, 102, "IDLE"); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(102, stop.Payload.GetProperty("mediaSessionId").GetInt64()); Assert.Null(session.Error);
    }
}
