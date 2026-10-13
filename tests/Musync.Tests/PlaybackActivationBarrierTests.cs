using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class PlaybackActivationBarrierTests
{
    private sealed class ClockedBackend : IAudioBackend
    {
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AudioHub? _hub;
        private volatile bool _paused;
        public int Produced { get; private set; }
        public double Position { get; private set; }
        public double Duration => 60;
        public bool IsFinished => false;
        public string? DefaultDeviceId => null;
        public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public IAudioOutput CreateOutput(SpeakerDevice device) => throw new NotSupportedException();
        public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        {
            _hub = hub; Position = startPosition; Initialized.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => PlayFileAsync(id, hub, token);
        public void AdvanceOneFrame()
        {
            if (_paused || _hub is null) return;
            _hub.Publish([++Produced / 10f, Produced / 10f]); Position += 1.0 / AudioFormat.SampleRate;
        }
        public void Pause(bool paused) => _paused = paused;
        public void Seek(double seconds) => Position = seconds;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ActivatingOutput(string id, bool pcm) : IAudioOutput
    {
        public SpeakerDevice Device { get; } = new(id, id, pcm ? SpeakerTransport.Windows : SpeakerTransport.Chromecast);
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public bool ReceivesPcm => pcm;
        public string? Error => null;
        public TaskCompletionSource ActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Activation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task BeginPlaybackAsync(CancellationToken token) { ActivationEntered.TrySetResult(); return Activation.Task.WaitAsync(token); }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFramesAndClockStayFrozenWhileAReadyReceiverIsStillActivating(bool capture)
    {
        var backend = new ClockedBackend(); var pc = new ActivatingOutput("pc", true); var cast = new ActivatingOutput("cast", false);
        pc.Activation.SetResult();
        await using var player = new PlaybackCoordinator(backend, d => d.Id == "pc" ? pc : cast);
        var start = player.StartAsync(capture ? "capture" : "song.flac", capture, [(pc.Device, new()), (cast.Device, new())], 100,
            position: capture ? 0 : 12);
        try
        {
            await cast.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await backend.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(player.IsPreparing); Assert.False(player.IsPlaying); Assert.Equal(2, player.ReadyOutputs);
            Assert.True(player.IsActivating);
            backend.AdvanceOneFrame(); await Task.Delay(60);
            Assert.Equal(0, backend.Produced); Assert.Equal(capture ? 0 : 12, player.Position);
            Assert.Equal(0, pc.Buffer.BufferedFrames);
            cast.Activation.SetResult(); await start.WaitAsync(TimeSpan.FromSeconds(2));
            backend.AdvanceOneFrame(); Assert.Equal(1, backend.Produced); Assert.Equal(1, pc.Buffer.BufferedFrames);
            Assert.True(player.IsPlaying); Assert.False(player.IsPreparing);
            Assert.False(player.IsActivating);
        }
        finally { cast.Activation.TrySetResult(); await player.StopAsync(); try { await start; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task CancelingActivationDoesNotAllowTheInitializedSourceToPublish()
    {
        var backend = new ClockedBackend(); var output = new ActivatingOutput("cast", true);
        await using var player = new PlaybackCoordinator(backend, _ => output);
        var start = player.StartAsync("song.flac", false, [(output.Device, new())], 100, 12);
        try
        {
            await output.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await backend.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
            backend.AdvanceOneFrame(); Assert.Equal(0, backend.Produced);
            await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            output.Activation.TrySetResult(); backend.AdvanceOneFrame();
            Assert.Equal(0, backend.Produced); Assert.False(player.IsPlaying); Assert.False(player.IsPreparing); Assert.Empty(player.OutputErrors);
            Assert.False(player.IsActivating);
        }
        finally { await player.StopAsync(); try { await start; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task ARejectedReceiverDoesNotConsumeTheHealthyOutputsFirstFrame()
    {
        var backend = new ClockedBackend(); var pc = new ActivatingOutput("pc", true); var cast = new ActivatingOutput("cast", false);
        pc.Activation.SetResult();
        await using var player = new PlaybackCoordinator(backend, d => d.Id == "pc" ? pc : cast);
        var start = player.StartAsync("song.flac", false, [(pc.Device, new()), (cast.Device, new())], 100, 12);
        try
        {
            await cast.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await backend.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
            backend.AdvanceOneFrame(); Assert.Equal(0, backend.Produced);
            cast.Activation.SetException(new IOException("Playback rejected")); await start.WaitAsync(TimeSpan.FromSeconds(2));
            backend.AdvanceOneFrame(); Assert.Equal(1, backend.Produced);
            pc.Buffer.SetDelay(0, baseBufferMs: 0); var first = new float[2]; Assert.Equal(2, pc.Buffer.Read(first));
            Assert.Equal([.1f, .1f], first); Assert.Equal(0, pc.Buffer.DroppedFrames);
            Assert.Single(player.Outputs); Assert.Contains("Playback rejected", player.OutputErrors["cast"]);
            Assert.True(player.IsPlaying); Assert.False(player.IsActivating);
        }
        finally { cast.Activation.TrySetResult(); await player.StopAsync(); try { await start; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task WhenEveryReceiverRejectsActivationTheInitializedSourceNeverStartsItsTimeline()
    {
        var backend = new ClockedBackend(); var cast = new ActivatingOutput("cast", false);
        await using var player = new PlaybackCoordinator(backend, _ => cast);
        var start = player.StartAsync("song.flac", false, [(cast.Device, new())], 100, 12);
        await cast.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await backend.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.AdvanceOneFrame(); Assert.Equal(0, backend.Produced); cast.Activation.SetException(new IOException("Playback rejected"));
        await Assert.ThrowsAsync<IOException>(() => start.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, backend.Produced); Assert.False(player.IsPlaying); Assert.False(player.IsActivating); Assert.Empty(player.Outputs);
    }
    [Fact]
    public async Task PausedStartupRemainsFrozenAndResumeUsesTheSameActivationBarrier()
    {
        var backend = new ClockedBackend(); var cast = new ActivatingOutput("cast", true);
        await using var player = new PlaybackCoordinator(backend, _ => cast);
        await player.StartAsync("song.flac", false, [(cast.Device, new())], 100, 12, paused: true);
        await backend.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2)); backend.AdvanceOneFrame();
        Assert.Equal(0, backend.Produced); Assert.False(cast.ActivationEntered.Task.IsCompleted); Assert.False(player.IsActivating);
        var resume = player.PauseAsync(false);
        try
        {
            await cast.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(player.IsActivating); backend.AdvanceOneFrame(); Assert.Equal(0, backend.Produced);
            cast.Activation.SetResult(); await resume.WaitAsync(TimeSpan.FromSeconds(2));
            backend.AdvanceOneFrame(); Assert.Equal(1, backend.Produced); Assert.True(player.IsPlaying); Assert.False(player.IsActivating);
        }
        finally { cast.Activation.TrySetResult(); await player.StopAsync(); await resume; }
    }
}
