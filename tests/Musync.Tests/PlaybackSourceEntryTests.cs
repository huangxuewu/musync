using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class PlaybackSourceEntryTests
{
    private sealed class Backend : IAudioBackend
    {
        private readonly ManualResetEventSlim _initialize = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Paused { get; private set; }
        public double Position { get; private set; } = 99;
        public double Duration { get; private set; } = 240;
        public bool IsFinished => false;
        public bool ThrowDuringEntry { get; set; }
        public bool ReturnFailedTask { get; set; }
        public Task? RuntimeFailure { get; set; }
        public string? DefaultDeviceId => null;
        public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public IAudioOutput CreateOutput(SpeakerDevice device) => throw new NotSupportedException();
        public void ReleaseInitialization() => _initialize.Set();
        public Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        {
            // Model the synchronous file/device open before the backend's first
            // asynchronous wait, while retaining the previous source's clock.
            Entered.TrySetResult(); _initialize.Wait(token);
            if (ThrowDuringEntry) throw new IOException("Synthetic source cannot be opened");
            if (ReturnFailedTask) return Task.FromException(new IOException("Synthetic source cannot be opened"));
            Position = startPosition; Duration = 60;
            return RuntimeFailure?.WaitAsync(token) ?? Task.Delay(Timeout.Infinite, token);
        }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => PlayFileAsync(id, hub, token);
        public void Pause(bool paused) => Paused = paused;
        public void Seek(double seconds) => Position = seconds;
        public ValueTask DisposeAsync() { _initialize.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Output : IAudioOutput
    {
        public SpeakerDevice Device { get; } = new("synthetic-output", "Synthetic output", SpeakerTransport.Windows);
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error => null;
        public int Activations { get; private set; }
        public int Disposals { get; private set; }
        public TaskCompletionSource ActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Activation { get; set; }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task BeginPlaybackAsync(CancellationToken token)
        { Activations++; ActivationEntered.TrySetResult(); return Activation?.WaitAsync(token) ?? Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparedReceiversWaitForThisSourceToEnterBeforeStartupCompletes(bool capture, bool paused)
    {
        var backend = new Backend(); var output = new Output();
        await using var player = new PlaybackCoordinator(backend, _ => output);
        var start = player.StartAsync(capture ? "synthetic-input" : "song.flac", capture, [(output.Device, new())], 100,
            position: capture ? 0 : 12, paused: paused);
        try
        {
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(start.IsCompleted); Assert.True(player.IsPreparing); Assert.False(player.IsPlaying);
            Assert.True(player.IsInitializingSource);
            Assert.True(backend.Paused); Assert.Equal(0, output.Activations); Assert.Equal(1, player.ReadyOutputs);
            backend.ReleaseInitialization(); await start.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(capture ? 0 : 12, backend.Position);
            if (capture && !paused) Assert.InRange(player.Position, 0, 2);
            else Assert.Equal(capture ? 0 : 12, player.Position);
            Assert.Equal(capture ? 0 : 60, player.Duration);
            Assert.Equal(!paused, player.IsPlaying); Assert.Equal(paused, backend.Paused);
            Assert.Equal(paused ? 0 : 1, output.Activations); Assert.False(player.IsPreparing);
            Assert.False(player.IsInitializingSource);
        }
        finally { backend.ReleaseInitialization(); await player.StopAsync(); try { await start; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingSourceEntryReleasesPreparationWithoutActivatingAnOutput(bool capture)
    {
        var backend = new Backend(); var output = new Output();
        await using var player = new PlaybackCoordinator(backend, _ => output);
        var start = player.StartAsync(capture ? "synthetic-input" : "song.flac", capture, [(output.Device, new())], 100, 12);
        try
        {
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            player.CancelPreparation(); await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.Equal(0, output.Activations); Assert.Equal(1, output.Disposals);
            Assert.False(player.IsPlaying); Assert.False(player.IsPreparing); Assert.Empty(player.Outputs);
            Assert.False(player.IsInitializingSource);
            Assert.Null(player.Error); Assert.Empty(player.OutputErrors);
        }
        finally { backend.ReleaseInitialization(); await player.StopAsync(); try { await start; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialSourceFailureNeverActivatesThePreparedReceiversAndCanBeRetried(bool returnedTask)
    {
        var backend = new Backend { ThrowDuringEntry = !returnedTask, ReturnFailedTask = returnedTask };
        backend.ReleaseInitialization(); var created = new List<Output>();
        await using var player = new PlaybackCoordinator(backend, _ => { var output = new Output(); created.Add(output); return output; });
        var device = new Output().Device;
        var error = await Assert.ThrowsAsync<IOException>(() => player.StartAsync("broken.flac", false, [(device, new())], 100, 12));
        Assert.Equal("Synthetic source cannot be opened", error.Message); Assert.Equal(error.Message, player.Error);
        Assert.Single(created); Assert.Equal(0, created[0].Activations); Assert.Equal(1, created[0].Disposals);
        Assert.False(player.IsPlaying); Assert.False(player.IsPreparing); Assert.Empty(player.Outputs);
        Assert.False(player.IsInitializingSource);
        backend.ThrowDuringEntry = false; backend.ReturnFailedTask = false;
        await player.StartAsync("working.flac", false, [(device, new())], 100, 12);
        Assert.True(player.IsPlaying); Assert.Equal(12, player.Position); Assert.Equal(60, player.Duration);
        Assert.Equal(2, created.Count); Assert.Equal(1, created[1].Activations); Assert.Null(player.Error);
    }

    [Fact]
    public async Task SourceFailureDuringReceiverActivationCannotBecomeConfirmedPlayback()
    {
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { RuntimeFailure = fail.Task }; backend.ReleaseInitialization();
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new Output { Activation = activation.Task };
        await using var player = new PlaybackCoordinator(backend, _ => output);
        var start = player.StartAsync("song.flac", false, [(output.Device, new())], 100, 12);
        try
        {
            await output.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fail.SetException(new IOException("Synthetic source failed before startup completed"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (player.Error is null) await Task.Delay(5, deadline.Token);
            activation.SetResult();
            var error = await Assert.ThrowsAsync<IOException>(() => start);
            Assert.Equal("Synthetic source failed before startup completed", error.Message);
            Assert.True(backend.Paused); Assert.False(player.IsPlaying); Assert.False(player.IsPreparing);
            Assert.Empty(player.Outputs); Assert.Equal(1, output.Disposals); Assert.Equal(0, output.Buffer.BufferedFrames);
        }
        finally { activation.TrySetResult(); await player.StopAsync(); try { await start; } catch (IOException) { } }
    }
}
