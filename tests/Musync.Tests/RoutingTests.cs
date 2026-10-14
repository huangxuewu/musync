using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class RoutingTests
{
    private sealed class Backend : IAudioBackend
    {
        public string? Source;
        public bool Paused;
        public bool FailSource;
        public Task? SourceFailureSignal;
        public AudioHub? Hub;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public string? DefaultDeviceId => "source";
        public IAudioOutput CreateOutput(SpeakerDevice device) => new Output(device);
        public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        {
            Source = path; Hub = hub; Started.TrySetResult();
            try
            {
                if (FailSource) throw new IOException("Source decode failed");
                if (SourceFailureSignal is not null)
                {
                    await SourceFailureSignal.WaitAsync(token);
                    throw new IOException("Source decode failed");
                }
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { Stopped.TrySetResult(); }
        }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => PlayFileAsync(id, hub, token);
        public void Pause(bool paused) { Paused = paused; }
        public void Seek(double seconds) { }
        public double Position => 0;
        public double Duration => 30;
        public bool IsFinished => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Output(SpeakerDevice device, bool fail = false, Task? prepared = null) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error { get; set; }
        public bool Disposed;
        public Task? ResumePrepared;
        public bool FailPause;
        public bool FailDispose;
        public Task? ControlUpdate;
        public Task UpdateControlsAsync() => ControlUpdate ?? Task.CompletedTask;
        public Task SetPausedAsync(bool paused) => FailPause ? Task.FromException(new IOException("Pause rejected")) : paused ? Task.CompletedTask : ResumePrepared ?? Task.CompletedTask;
        public Task StartAsync(CancellationToken token) => fail ? Task.FromException(new IOException("Rejected")) : prepared ?? Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return FailDispose ? ValueTask.FromException(new IOException("Cleanup failed")) : ValueTask.CompletedTask; }
    }
    [Fact]
    public async Task PauseRejectsLateSourceFramesUntilResumeIsPrepared()
    {
        var backend = new Backend();
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.AirPlay));
        await using var player = new PlaybackCoordinator(backend, _ => output);
        await player.StartAsync("music.wav", false, [(output.Device, new())], 100);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Hub!.Publish([.1f, .1f]);
        await player.PauseAsync(true);
        backend.Hub.Publish([.2f, .2f]); Assert.Equal(1, output.Buffer.BufferedFrames);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.ResumePrepared = prepared.Task;
        var resume = player.PauseAsync(false);
        backend.Hub.Publish([.3f, .3f]); Assert.Equal(1, output.Buffer.BufferedFrames);
        var samples = new float[2]; Assert.Equal(0, output.Buffer.Read(samples));
        prepared.SetResult(); await resume;
        backend.Hub.Publish([.4f, .4f]); Assert.Equal(2, output.Buffer.BufferedFrames);
        output.Buffer.Read(samples); Assert.Equal(new[] { .1f, .1f }, samples);
        output.Buffer.Read(samples); Assert.Equal(new[] { .4f, .4f }, samples);
    }
    [Fact]
    public async Task LiveCapturePauseDiscardsOldAudioAndResumesCurrentSound()
    {
        var backend = new Backend(); var output = new Output(new("speaker", "Speaker", SpeakerTransport.Windows));
        await using var player = new PlaybackCoordinator(backend, _ => output);
        await player.StartAsync("source", true, [(output.Device, new())], 100);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Hub!.Publish([.1f, .1f]); await player.PauseAsync(true);
        Assert.Equal(0, output.Buffer.BufferedFrames);
        backend.Hub.Publish([.2f, .2f]); Assert.Equal(0, output.Buffer.BufferedFrames);
        await player.PauseAsync(false); backend.Hub.Publish([.3f, .3f]);
        var read = new float[2]; output.Buffer.Read(read); Assert.Equal(new[] { .3f, .3f }, read);
    }
    [Fact]
    public async Task StopClearsTheMusicRetainedByPauseEvenIfOutputCleanupDoesNot()
    {
        var backend = new Backend(); var output = new Output(new("speaker", "Speaker", SpeakerTransport.Windows));
        await using var player = new PlaybackCoordinator(backend, _ => output);
        await player.StartAsync("music.wav", false, [(output.Device, new())], 100);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Hub!.Publish([.1f, .1f]); await player.PauseAsync(true);
        Assert.Equal(1, output.Buffer.BufferedFrames);
        await player.StopAsync(); Assert.Equal(0, output.Buffer.BufferedFrames); Assert.True(output.Disposed);
    }
    [Fact]
    public async Task CaptureNeverRoutesBackToCapturedEndpoint()
    {
        var backend = new Backend(); await using var player = new PlaybackCoordinator(backend, d => new Output(d));
        await player.StartAsync("source", true, [(new("source", "Source", SpeakerTransport.Windows), new()),
            (new("sink", "Sink", SpeakerTransport.Windows), new() { Volume = 50 })], 80);
        Assert.Single(player.Outputs); Assert.DoesNotContain("source", player.Outputs.Keys);
        Assert.Equal(.4f, player.Outputs["sink"].Control.Gain, 3);
    }
    [Fact]
    public async Task FailedReceiverDoesNotDiscardWorkingReceiver()
    {
        var rejected = new Output(new("bad", "Bad", SpeakerTransport.AirPlay), true);
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == "bad" ? rejected : new Output(d));
        await player.StartAsync("music.wav", false, [(rejected.Device, new()), (new("good", "Good", SpeakerTransport.Windows), new())], 100);
        Assert.True(rejected.Disposed); Assert.Single(player.Outputs); Assert.True(player.IsPlaying);
        Assert.Equal("Rejected", player.OutputErrors["bad"]);
    }
    [Fact]
    public async Task EmptyRoutingFailsAndLeavesStoppedState()
    {
        await using var player = new PlaybackCoordinator(new Backend(), d => new Output(d));
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.StartAsync("music.wav", false, [], 100));
        Assert.False(player.IsPlaying); Assert.Empty(player.Outputs);
    }
    [Fact]
    public async Task LiveGainRetainsRelativeSpeakerLevels()
    {
        await using var player = new PlaybackCoordinator(new Backend(), d => new Output(d));
        var preferences = new SpeakerPreferences { Volume = 25, DelayMs = 250 };
        await player.StartAsync("music.wav", false, [(new("a", "A", SpeakerTransport.Windows), preferences)], 80);
        Assert.Equal(.2f, player.Outputs["a"].Control.Gain, 3);
        preferences.Muted = true; player.Update("a", preferences, 100);
        Assert.Equal(0, player.Outputs["a"].Control.Gain); Assert.Equal(250, player.Outputs["a"].Control.DelayMs);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateControlFailureDoesNotOverwriteStoppedOrReplacementPlayback(bool replace)
    {
        var oldOutput = new Output(new("speaker", "Speaker", SpeakerTransport.Chromecast));
        var replacement = new Output(oldOutput.Device);
        var nextOutput = oldOutput;
        await using var player = new PlaybackCoordinator(new Backend(), _ => nextOutput);
        await player.StartAsync("first.wav", false, [(oldOutput.Device, new())], 100);
        var pending = new TaskCompletionSource();
        oldOutput.ControlUpdate = pending.Task;
        await Task.Run(() => player.Update(oldOutput.Device.Id, new() { Volume = 30 }, 100));
        if (replace)
        {
            nextOutput = replacement;
            await player.StartAsync("second.wav", false, [(replacement.Device, new())], 100);
        }
        else await player.StopAsync();
        await Task.Run(() => pending.SetException(new IOException("Old connection failed")));
        Assert.Null(player.Error);
        Assert.Equal(replace, player.IsPlaying);
    }
    [Fact]
    public async Task StopCancellationOfControlUpdateDoesNotBecomePlaybackError()
    {
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.Chromecast));
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("music.wav", false, [(output.Device, new())], 100);
        var pending = new TaskCompletionSource(); output.ControlUpdate = pending.Task;
        await Task.Run(() => player.Update(output.Device.Id, new() { Volume = 30 }, 100));
        await player.StopAsync();
        await Task.Run(() => pending.SetCanceled());
        Assert.Null(player.Error); Assert.False(player.IsPlaying);
    }
    [Fact]
    public async Task CurrentControlFailureStillReportsErrorAndAllowsRetry()
    {
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.Chromecast));
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("music.wav", false, [(output.Device, new())], 100);
        output.ControlUpdate = Task.FromException(new IOException("Volume rejected"));
        player.Update(output.Device.Id, new() { Volume = 30 }, 100);
        Assert.Equal("Speaker: Volume rejected", player.Error);
        Assert.True(player.IsPlaying); Assert.Same(output, player.Outputs[output.Device.Id]);
        output.ControlUpdate = Task.CompletedTask;
        player.Update(output.Device.Id, new() { Volume = 40 }, 100);
        Assert.Equal(.4f, output.Control.Gain, 3);
        Assert.Null(player.Error);
    }
    [Fact]
    public async Task ControlRecoveryKeepsOtherReceiversUnresolvedFailures()
    {
        var first = new Output(new("first", "First", SpeakerTransport.Chromecast));
        var second = new Output(new("second", "Second", SpeakerTransport.AirPlay));
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == first.Device.Id ? first : second);
        await player.StartAsync("music.wav", false, [(first.Device, new()), (second.Device, new())], 100);
        first.ControlUpdate = Task.FromException(new IOException("Volume rejected"));
        second.ControlUpdate = Task.FromException(new IOException("Delay rejected"));
        player.Update(first.Device.Id, new(), 100); player.Update(second.Device.Id, new(), 100);
        Assert.Equal("Second: Delay rejected", player.Error);
        second.ControlUpdate = Task.CompletedTask; player.Update(second.Device.Id, new(), 100);
        Assert.Equal("First: Volume rejected", player.Error);
        first.ControlUpdate = Task.CompletedTask; player.Update(first.Device.Id, new(), 100);
        Assert.Null(player.Error); Assert.True(player.IsPlaying);
    }
    [Fact]
    public async Task ControlRecoveryPreservesAReceiverConnectionFailure()
    {
        var good = new Output(new("good", "Good", SpeakerTransport.Windows));
        var bad = new Output(new("bad", "Bad", SpeakerTransport.Chromecast));
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == good.Device.Id ? good : bad);
        await player.StartAsync("music.wav", false, [(good.Device, new()), (bad.Device, new())], 100);
        good.ControlUpdate = Task.FromException(new IOException("Volume rejected"));
        player.Update(good.Device.Id, new(), 100);
        bad.Error = "Connection lost"; await player.CheckOutputsAsync();
        good.ControlUpdate = Task.CompletedTask; player.Update(good.Device.Id, new(), 100);
        Assert.Equal("Bad: Connection lost", player.Error);
        Assert.Equal("Connection lost", player.OutputErrors[bad.Device.Id]);
        Assert.True(player.IsPlaying); Assert.True(bad.Disposed);
    }
    [Fact]
    public async Task ReceiverControlResultsDoNotReplaceOrClearFatalSourceErrors()
    {
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.Chromecast));
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = new PlaybackCoordinator(new Backend { SourceFailureSignal = fail.Task }, _ => output);
        await player.StartAsync("broken.wav", false, [(output.Device, new())], 100);
        fail.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (player.Error is null) await Task.Delay(10, deadline.Token);
        Assert.Equal("Source decode failed", player.Error);
        output.ControlUpdate = Task.FromException(new IOException("Volume rejected"));
        player.Update(output.Device.Id, new(), 100);
        Assert.Equal("Source decode failed", player.Error);
        output.ControlUpdate = Task.CompletedTask; player.Update(output.Device.Id, new(), 100);
        Assert.Equal("Source decode failed", player.Error);
        await player.CheckOutputsAsync(); Assert.False(player.IsPlaying);
    }
    [Fact]
    public async Task FirstMusicFrameWaitsForSlowReceiverPreparation()
    {
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend();
        await using var player = new PlaybackCoordinator(backend, d => new Output(d, prepared: prepared.Task));
        var start = player.StartAsync("music.wav", false, [(new("a", "A", SpeakerTransport.AirPlay), new())], 100);
        Assert.Null(backend.Source);
        prepared.SetResult(); await start;
        for (var attempt = 0; attempt < 20 && backend.Source is null; attempt++) await Task.Delay(10);
        Assert.Equal("music.wav", backend.Source);
    }
    [Fact]
    public async Task ResumeKeepsSourceFrozenUntilReceiverIsReady()
    {
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend();
        var output = new Output(new("cast", "Cast", SpeakerTransport.Chromecast)) { ResumePrepared = prepared.Task };
        await using var player = new PlaybackCoordinator(backend, _ => output);
        await player.StartAsync("music.wav", false, [(output.Device, new())], 100);
        await player.PauseAsync(true);
        var resume = player.PauseAsync(false);
        Assert.True(backend.Paused); Assert.False(player.IsPlaying); Assert.False(resume.IsCompleted);
        prepared.SetResult(); await resume;
        Assert.False(backend.Paused); Assert.True(player.IsPlaying);
    }
    [Fact]
    public async Task PauseFailureKeepsOtherReceiverAndExposesDeviceError()
    {
        var bad = new Output(new("bad", "Bad", SpeakerTransport.Chromecast)) { FailPause = true };
        var good = new Output(new("good", "Good", SpeakerTransport.Windows));
        var backend = new Backend();
        await using var player = new PlaybackCoordinator(backend, d => d.Id == "bad" ? bad : good);
        await player.StartAsync("music.wav", false, [(bad.Device, new()), (good.Device, new())], 100);
        await player.PauseAsync(true);
        Assert.True(backend.Paused); Assert.True(bad.Disposed); Assert.Single(player.Outputs);
        Assert.Equal("Pause rejected", player.OutputErrors["bad"]);
        await player.PauseAsync(false);
        Assert.True(player.IsPlaying); Assert.False(backend.Paused);
    }
    [Fact]
    public async Task CleanupFailureStillDisposesOtherReceiversAndAllowsNewSession()
    {
        var bad = new Output(new("bad", "Bad", SpeakerTransport.AirPlay)) { FailDispose = true };
        var good = new Output(new("good", "Good", SpeakerTransport.Windows));
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == "bad" ? bad : good);
        await player.StartAsync("music.wav", false, [(bad.Device, new()), (good.Device, new())], 100);
        await player.StopAsync();
        Assert.True(bad.Disposed); Assert.True(good.Disposed); Assert.Empty(player.Outputs); Assert.False(player.IsPlaying);
        Assert.Equal("Cleanup failed", player.OutputErrors["bad"]);
        await player.StartAsync("music.wav", false, [(good.Device, new())], 100);
        Assert.True(player.IsPlaying); Assert.Null(player.Error);
    }
    [Fact]
    public async Task AllPauseFailuresLeaveSessionStopped()
    {
        var output = new Output(new("bad", "Bad", SpeakerTransport.Chromecast)) { FailPause = true };
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("music.wav", false, [(output.Device, new())], 100);
        await Assert.ThrowsAsync<IOException>(() => player.PauseAsync(true));
        Assert.False(player.IsPlaying); Assert.Empty(player.Outputs); Assert.True(output.Disposed);
    }
    [Fact]
    public async Task ReroutingPausedMusicDoesNotRestartPlayback()
    {
        var backend = new Backend();
        await using var player = new PlaybackCoordinator(backend, d => new Output(d));
        await player.StartAsync("music.wav", false, [(new("first", "First", SpeakerTransport.Windows), new())], 100);
        await player.PauseAsync(true);
        await player.StartAsync("music.wav", false, [(new("second", "Second", SpeakerTransport.Windows), new())], 100, 12, paused: true);
        Assert.True(backend.Paused); Assert.False(player.IsPlaying); Assert.True(player.Outputs.ContainsKey("second"));
        await player.PauseAsync(false);
        Assert.False(backend.Paused); Assert.True(player.IsPlaying);
    }
    [Fact]
    public async Task EndpointConstructionFailureKeepsWorkingSpeaker()
    {
        var missing = new SpeakerDevice("missing", "Unplugged endpoint", SpeakerTransport.Windows);
        var good = new Output(new("good", "Good", SpeakerTransport.Windows));
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == "missing"
            ? throw new IOException("Endpoint unavailable") : good);
        await player.StartAsync("music.wav", false, [(missing, new()), (good.Device, new())], 100);
        Assert.True(player.IsPlaying); Assert.Single(player.Outputs); Assert.False(good.Disposed);
        Assert.Equal("Endpoint unavailable", player.OutputErrors["missing"]);
    }
    [Fact]
    public async Task RuntimeReceiverFailureDetachesOnlyItsBuffer()
    {
        var failed = new Output(new("failed", "Failed", SpeakerTransport.AirPlay));
        var good = new Output(new("good", "Good", SpeakerTransport.Windows));
        var backend = new Backend();
        await using var player = new PlaybackCoordinator(backend, d => d.Id == "failed" ? failed : good);
        await player.StartAsync("music.wav", false, [(failed.Device, new()), (good.Device, new())], 100);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failed.Error = "Connection lost";
        await player.CheckOutputsAsync();
        Assert.True(player.IsPlaying); Assert.True(failed.Disposed); Assert.False(good.Disposed);
        Assert.Single(player.Outputs); Assert.True(player.Outputs.ContainsKey("good"));
        Assert.Equal("Connection lost", player.OutputErrors["failed"]);
        backend.Hub!.Publish([.2f, .2f]);
        Assert.Equal(1, good.Buffer.BufferedFrames); Assert.Equal(0, failed.Buffer.BufferedFrames);
    }
    [Fact]
    public async Task LastReceiverFailureStopsSourceAndCanBeRetried()
    {
        var failed = new Output(new("failed", "Failed", SpeakerTransport.Chromecast));
        var replacement = new Output(failed.Device);
        var attempts = 0;
        var backend = new Backend();
        await using var player = new PlaybackCoordinator(backend, _ => attempts++ == 0 ? failed : replacement);
        await player.StartAsync("music.wav", false, [(failed.Device, new())], 100);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failed.Error = "Disconnected";
        await player.CheckOutputsAsync();
        Assert.False(player.IsPlaying); Assert.Empty(player.Outputs); Assert.True(failed.Disposed);
        Assert.True(backend.Stopped.Task.IsCompleted);
        await player.StartAsync("music.wav", false, [(replacement.Device, new())], 100);
        Assert.True(player.IsPlaying); Assert.Null(player.Error); Assert.Empty(player.OutputErrors);
    }
    [Fact]
    public async Task OneNetworkReceiverNeverStartsBothProtocols()
    {
        var airplay = new SpeakerDevice("airplay", "Receiver", SpeakerTransport.AirPlay, "192.0.2.1", 7000);
        var cast = new SpeakerDevice("cast", "Receiver", SpeakerTransport.Chromecast, "192.0.2.1", 8009);
        var created = new List<string>();
        await using var player = new PlaybackCoordinator(new Backend(), d => { created.Add(d.Id); return new Output(d); });
        await player.StartAsync("music.wav", false, [(airplay, new()), (cast, new() { Selected = true })], 100);
        Assert.Equal(new[] { "cast" }, created); Assert.Single(player.Outputs);
    }
    [Fact]
    public async Task DuplicateEndpointDoesNotAllocateAnUnusedOutput()
    {
        var device = new SpeakerDevice("same", "Same", SpeakerTransport.Windows);
        var created = 0;
        await using var player = new PlaybackCoordinator(new Backend(), d => { created++; return new Output(d); });
        await player.StartAsync("music.wav", false, [(device, new()), (device, new())], 100);
        Assert.Equal(1, created); Assert.Single(player.Outputs);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialSourceFailureClosesPreparedOutputsEvenWhenStartingPaused(bool paused)
    {
        var backend = new Backend { FailSource = true };
        var output = new Output(new("good", "Good", SpeakerTransport.Windows));
        await using var player = new PlaybackCoordinator(backend, _ => output);
        var error = await Assert.ThrowsAsync<IOException>(() => player.StartAsync("broken.wav", false, [(output.Device, new())], 100, paused: paused));
        Assert.Equal("Source decode failed", error.Message);
        await player.CheckOutputsAsync();
        Assert.Equal("Source decode failed", player.Error);
        Assert.False(player.IsPlaying); Assert.Empty(player.Outputs); Assert.True(output.Disposed);
    }
}
