using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class PlaybackPreparationTests
{
    private sealed class Backend : IAudioBackend
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AudioHub? Hub;
        public double Position => 0;
        public double Duration => 30;
        public bool IsFinished => false;
        public string? DefaultDeviceId => null;
        public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public IAudioOutput CreateOutput(SpeakerDevice d) => throw new NotSupportedException();
        public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        { Hub = hub; Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => PlayFileAsync(id, hub, token);
        public void Pause(bool paused) { }
        public void Seek(double seconds) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Output(string id, bool pcm = true) : IAudioOutput
    {
        public SpeakerDevice Device { get; } = new(id, id, pcm ? SpeakerTransport.Windows : SpeakerTransport.Chromecast);
        public readonly TaskCompletionSource Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReceivesPcm => pcm;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error => null;
        public int Releases, Disposals;
        public TaskCompletionSource? ResumeReady;
        public TaskCompletionSource? DisposeReady;
        public Task StartAsync(CancellationToken token) => Prepared.Task.WaitAsync(token);
        public Task BeginPlaybackAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Releases++; return Task.CompletedTask; }
        public Task PrepareResumeAsync(CancellationToken token) => ResumeReady?.Task.WaitAsync(token) ?? Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync() { Disposals++; if (DisposeReady is not null) await DisposeReady.Task; }
    }
    [Fact]
    public async Task AllReceiversMustBeReadyBeforeAnyReleaseOrSourcePublication()
    {
        var backend = new Backend(); var pc = new Output("pc"); var cast = new Output("cast", false); pc.Prepared.SetResult();
        PlaybackSource? received = null;
        await using var player = new PlaybackCoordinator(backend, (d, source) => { received = source; return d.Id == "pc" ? pc : cast; });
        var start = player.StartAsync("song.flac", false, [(pc.Device, new()), (cast.Device, new())], 100, 12);
        Assert.True(player.IsPreparing); Assert.False(player.IsPlaying); Assert.Equal(1, player.ReadyOutputs); Assert.Equal(2, player.PreparingOutputs);
        Assert.True(player.IsOutputPrepared("pc")); Assert.False(player.IsOutputPrepared("cast"));
        Assert.False(backend.Started.Task.IsCompleted); Assert.Equal(0, pc.Releases + cast.Releases);
        Assert.Equal(new PlaybackSource("song.flac", false, 12), received);
        cast.Prepared.SetResult(); await start.WaitAsync(TimeSpan.FromSeconds(2)); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(player.IsPreparing); Assert.True(player.IsPlaying); Assert.Equal(1, cast.Releases); Assert.Equal(1, pc.Releases);
        backend.Hub!.Publish([.3f, .3f]); Assert.True(pc.Buffer.BufferedFrames > 0); Assert.Equal(0, cast.Buffer.BufferedFrames);
    }
    [Fact]
    public async Task StopInterruptsPendingPreparationWithoutStartingMusic()
    {
        var backend = new Backend(); var output = new Output("cast", false);
        await using var player = new PlaybackCoordinator(backend, _ => output);
        var start = player.StartAsync("song.flac", false, [(output.Device, new())], 100);
        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(backend.Started.Task.IsCompleted); Assert.False(player.IsPreparing); Assert.Empty(player.Outputs); Assert.Equal(1, output.Disposals);
        Assert.False(player.IsOutputPrepared("cast")); Assert.Empty(player.OutputErrors);
    }
    [Fact]
    public async Task StopDuringPreviousSongCleanupCancelsTheQueuedNewPreparation()
    {
        var previous = new Output("pc"); previous.Prepared.SetResult();
        var next = new Output("pc"); var creations = 0;
        await using var player = new PlaybackCoordinator(new Backend(), _ => ++creations == 1 ? previous : next);
        await player.StartAsync("previous.flac", false, [(previous.Device, new())], 100);
        previous.DisposeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = player.StartAsync("next.flac", false, [(next.Device, new())], 100);
        for (var i = 0; i < 100 && previous.Disposals == 0; i++) await Task.Delay(5);
        Assert.Equal(1, previous.Disposals);
        var stop = player.StopAsync(); previous.DisposeReady.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, creations); Assert.False(player.IsPlaying); Assert.Empty(player.Outputs);
    }
    [Fact]
    public async Task PausedStartLoadsReceiversWithoutReleasingThem()
    {
        var output = new Output("cast", false); output.Prepared.SetResult();
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("song.flac", false, [(output.Device, new())], 100, paused: true);
        Assert.Equal(0, output.Releases); Assert.False(player.IsPlaying); Assert.False(player.IsPreparing);
        await player.PauseAsync(false); Assert.Equal(1, output.Releases); Assert.True(player.IsPlaying);
    }
    [Fact]
    public async Task ResumeHoldsPreparedCastUntilTheOtherTransportCompletesItsHandshake()
    {
        var pc = new Output("pc"); var cast = new Output("cast", false); pc.Prepared.SetResult(); cast.Prepared.SetResult();
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == "pc" ? pc : cast);
        await player.StartAsync("song.flac", false, [(pc.Device, new()), (cast.Device, new())], 100);
        await player.PauseAsync(true); pc.ResumeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = player.PauseAsync(false);
        Assert.True(player.IsPreparing); Assert.False(player.IsPlaying); Assert.Equal(1, cast.Releases);
        pc.ResumeReady.SetResult(); await resume.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, cast.Releases); Assert.False(player.IsPreparing); Assert.True(player.IsPlaying);
    }
    [Fact]
    public async Task FailedPreparationKeepsTheHealthyReceiverAndExposesItsReason()
    {
        var pc = new Output("pc"); var cast = new Output("cast", false); pc.Prepared.SetResult(); cast.Prepared.SetException(new IOException("Preload rejected"));
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == "pc" ? pc : cast);
        await player.StartAsync("song.flac", false, [(pc.Device, new()), (cast.Device, new())], 100);
        Assert.True(player.IsPlaying); Assert.Equal(1, pc.Releases); Assert.Equal(0, cast.Releases); Assert.Single(player.Outputs);
        Assert.Equal("Preload rejected", player.OutputErrors["cast"]); Assert.Equal(1, cast.Disposals);
    }
    [Fact]
    public async Task FailedReceiverReportsItsReasonBeforeOtherReceiversFinishPreparing()
    {
        var backend = new Backend(); var pc = new Output("pc"); var cast = new Output("cast", false); var slow = new Output("slow", false);
        pc.Prepared.SetResult(); cast.Prepared.SetException(new IOException("Receiver unavailable"));
        await using var player = new PlaybackCoordinator(backend, d => d.Id == "pc" ? pc : d.Id == "cast" ? cast : slow);
        var start = player.StartAsync("song.flac", false, [(pc.Device, new()), (cast.Device, new()), (slow.Device, new())], 100);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!player.OutputErrors.ContainsKey("cast")) await Task.Delay(10, timeout.Token);
            Assert.Equal("Receiver unavailable", player.OutputErrors["cast"]);
            Assert.True(player.IsOutputPrepared("pc")); Assert.False(player.IsOutputPrepared("cast")); Assert.False(player.IsOutputPrepared("slow"));
            Assert.True(player.IsPreparing); Assert.False(backend.Started.Task.IsCompleted); Assert.Equal(0, pc.Releases + cast.Releases + slow.Releases);
            slow.Prepared.SetResult(); await start.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, player.ReadyOutputs); Assert.True(player.IsPlaying); Assert.Equal(1, pc.Releases); Assert.Equal(1, slow.Releases);
            await player.StopAsync(); Assert.Equal(0, player.ReadyOutputs); Assert.False(player.IsOutputPrepared("pc"));
        }
        finally { slow.Prepared.TrySetResult(); await start; }
    }
}
