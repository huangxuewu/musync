using System.Collections.Concurrent;
using Musync.Core;
using Musync.Network;
using Xunit;

namespace Musync.Tests;

public sealed class ControlUpdateBurstTests
{
    private sealed class Backend : IAudioBackend
    {
        public int Starts;
        public string? DefaultDeviceId => null;
        public double Position => 8;
        public double Duration => 30;
        public bool IsFinished => false;
        public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public IAudioOutput CreateOutput(SpeakerDevice device) => throw new NotSupportedException();
        public async Task PlayFileAsync(string source, AudioHub hub, CancellationToken token, double startPosition = 0)
        { Interlocked.Increment(ref Starts); await Task.Delay(Timeout.Infinite, token); }
        public Task CaptureAsync(string source, AudioHub hub, CancellationToken token) => PlayFileAsync(source, hub, token);
        public void Pause(bool paused) { }
        public void Seek(double seconds) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Output(string id) : IAudioOutput
    {
        public SpeakerDevice Device { get; } = new(id, id, SpeakerTransport.AirPlay);
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error { get; set; }
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<(float Gain, int Delay)> Applied = new();
        public int Calls, Active, MaxActive;
        public bool Fail, Disposed;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public async Task UpdateControlsAsync()
        {
            var call = Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active);
            int previous;
            do { previous = Volatile.Read(ref MaxActive); }
            while (active > previous && Interlocked.CompareExchange(ref MaxActive, active, previous) != previous);
            try
            {
                var values = (Control.Gain, Control.DelayMs);
                if (call == 1) { Entered.TrySetResult(); await Release.Task; }
                if (Fail) throw new IOException("Synthetic volume rejection");
                Applied.Enqueue(values);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class FailingVolumeBridge : IAirPlayBridge
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Fail = true;
        public string? Error(string id) => null;
        public Task OpenAsync(SpeakerDevice device, float gain, CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(string id, byte[] pcm, CancellationToken token = default) => Task.CompletedTask;
        public Task CloseAsync(string id, CancellationToken token = default) => Task.CompletedTask;
        public async Task SetVolumeAsync(string id, float gain, CancellationToken token = default)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            if (Fail) throw new IOException("Synthetic volume rejection");
        }
    }

    [Fact]
    public async Task ReceiverControlErrorDetailsStayIndependentAndSuccessfulRetryClearsOnlyItsOwnFailure()
    {
        var backend = new Backend(); var first = new Output("first") { Fail = true }; var second = new Output("second") { Fail = true };
        first.Release.TrySetResult(); second.Release.TrySetResult();
        await using var player = new PlaybackCoordinator(backend, d => d.Id == first.Device.Id ? first : second);
        await player.StartAsync("song.flac", false, [(first.Device, new()), (second.Device, new())], 100);
        player.Update(first.Device.Id, new() { Volume = 40 }, 100);
        player.Update(second.Device.Id, new() { Volume = 40 }, 100);
        await Until(() => player.GetOutputControlError(first.Device.Id) is not null && player.GetOutputControlError(second.Device.Id) is not null);
        Assert.Equal("Synthetic volume rejection", player.GetOutputControlError(first.Device.Id));
        Assert.Equal("Synthetic volume rejection", player.GetOutputControlError(second.Device.Id));
        Assert.Null(player.GetOutputControlError("unrelated")); Assert.Empty(player.OutputErrors);
        await player.CheckOutputsAsync(); Assert.Equal(2, player.Outputs.Count); Assert.True(player.IsPlaying);
        first.Fail = false; player.Update(first.Device.Id, new() { Volume = 45 }, 100);
        await Until(() => player.GetOutputControlError(first.Device.Id) is null);
        Assert.Equal("Synthetic volume rejection", player.GetOutputControlError(second.Device.Id));
        Assert.Equal("second: Synthetic volume rejection", player.Error);
        Assert.True(player.IsPlaying); Assert.Equal(1, backend.Starts);
        await player.StopAsync(); Assert.Null(player.GetOutputControlError(first.Device.Id)); Assert.Null(player.GetOutputControlError(second.Device.Id));
    }

    [Fact]
    public async Task AirPlayVolumeFailureBurstUsesOnlyOneTrailingRetryAndCanRecover()
    {
        var bridge = new FailingVolumeBridge(); var device = new SpeakerDevice("homepod", "Simulated HomePod", SpeakerTransport.AirPlay);
        await using var player = new PlaybackCoordinator(new Backend(), d => new AirPlay2Output(d, bridge));
        await player.StartAsync("song.flac", false, [(device, new())], 100);
        player.Update(device.Id, new() { Volume = 40 }, 100);
        try
        {
            await bridge.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 40; i++) player.Update(device.Id, new() { Volume = i }, 100);
            bridge.Release.TrySetResult(); await Until(() => Volatile.Read(ref bridge.Calls) >= 2);
            await Task.Delay(40); Assert.Equal(2, bridge.Calls);
            Assert.Equal("Simulated HomePod: Synthetic volume rejection", player.Error); Assert.True(player.IsPlaying);
            bridge.Fail = false; player.Update(device.Id, new() { Volume = 47 }, 100);
            await Until(() => player.Error is null); Assert.Equal(3, bridge.Calls);
            Assert.Equal(.47f, player.Outputs[device.Id].Control.Gain, 3);
            await player.PauseAsync(true).WaitAsync(TimeSpan.FromSeconds(1)); Assert.False(player.IsPlaying);
        }
        finally { bridge.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DragBurstKeepsOneControlCallInFlightAndAppliesTheLatestVolumeMuteAndDelay(bool muted)
    {
        var backend = new Backend(); var output = new Output("slow");
        await using var player = new PlaybackCoordinator(backend, _ => output);
        await player.StartAsync("song.flac", false, [(output.Device, new())], 100);
        player.Update(output.Device.Id, new() { Volume = 40 }, 100);
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            for (var i = 0; i < 40; i++) player.Update(output.Device.Id, new() { Volume = i, DelayMs = i * 10 }, 80);
            player.Update(output.Device.Id, new() { Volume = 73, DelayMs = 260, Muted = muted }, 60);
            Assert.Equal(muted ? 0 : .438f, output.Control.Gain, 3); Assert.Equal(260, output.Control.DelayMs);
            Assert.Equal(1, output.Calls); Assert.Equal(1, output.MaxActive);
            output.Release.TrySetResult();
            await Until(() => output.Applied.Count == 2 && Volatile.Read(ref output.Active) == 0);
            Assert.Equal(muted ? 0 : .438f, output.Applied.Last().Gain, 3); Assert.Equal(260, output.Applied.Last().Delay);
            Assert.Equal(2, output.Calls); Assert.True(player.IsPlaying); Assert.Equal(8, player.Position);
            await Until(() => backend.Starts == 1); Assert.Null(player.Error);
        }
        finally { output.Release.TrySetResult(); await Until(() => Volatile.Read(ref output.Active) == 0); }
    }

    [Fact]
    public async Task SlowReceiverDoesNotQueueUpdatesForAnotherReceiver()
    {
        var slow = new Output("slow"); var fast = new Output("fast"); fast.Release.TrySetResult();
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == slow.Device.Id ? slow : fast);
        await player.StartAsync("song.flac", false, [(slow.Device, new()), (fast.Device, new())], 100);
        player.Update(slow.Device.Id, new() { Volume = 40 }, 100);
        try
        {
            await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 40; i++) player.Update(slow.Device.Id, new() { Volume = i }, 100);
            player.Update(fast.Device.Id, new() { Volume = 25 }, 80);
            Assert.Single(fast.Applied); Assert.Equal(.2f, fast.Applied.Single().Gain, 3);
            Assert.Equal(1, slow.Calls); Assert.Equal(1, fast.Calls); Assert.True(player.IsPlaying);
        }
        finally { slow.Release.TrySetResult(); await Until(() => Volatile.Read(ref slow.Active) == 0); }
    }

    [Fact]
    public async Task ConcurrentEditsStaySerializedAndRetainTheLastCompletedEdit()
    {
        var output = new Output("slow");
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("song.flac", false, [(output.Device, new())], 100);
        player.Update(output.Device.Id, new() { Volume = 20 }, 100);
        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(Enumerable.Range(0, 500).Select(i => Task.Run(() => player.Update(output.Device.Id, new() { Volume = i % 100 }, 100))));
            player.Update(output.Device.Id, new() { Volume = 61, DelayMs = 320 }, 100);
            Assert.Equal(1, output.Calls); Assert.Equal(1, output.MaxActive);
            output.Release.TrySetResult(); await Until(() => output.Applied.Count == 2 && Volatile.Read(ref output.Active) == 0);
            Assert.Equal(.61f, output.Applied.Last().Gain, 3); Assert.Equal(320, output.Applied.Last().Delay);
            Assert.Equal(2, output.Calls); Assert.Equal(1, output.MaxActive);
        }
        finally { output.Release.TrySetResult(); await Until(() => Volatile.Read(ref output.Active) == 0); }
    }

    [Fact]
    public async Task RemovedReceiverDiscardsPendingEditsWhileAnotherReceiverKeepsPlaying()
    {
        var slow = new Output("slow"); var fast = new Output("fast"); fast.Release.TrySetResult();
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == slow.Device.Id ? slow : fast);
        await player.StartAsync("song.flac", false, [(slow.Device, new()), (fast.Device, new())], 100);
        player.Update(slow.Device.Id, new() { Volume = 20 }, 100);
        try
        {
            await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 40; i++) player.Update(slow.Device.Id, new() { Volume = i }, 100);
            slow.Error = "Synthetic receiver connection lost"; await player.CheckOutputsAsync();
            Assert.False(player.Outputs.ContainsKey(slow.Device.Id)); Assert.True(slow.Disposed); Assert.True(player.IsPlaying);
            slow.Fail = true; slow.Release.TrySetResult(); await Until(() => Volatile.Read(ref slow.Active) == 0);
            await Task.Delay(25); Assert.Equal(1, slow.Calls);
            player.Update(fast.Device.Id, new() { Volume = 36 }, 100);
            Assert.Single(fast.Applied); Assert.Equal(.36f, fast.Applied.Single().Gain, 3);
            Assert.Equal("slow: Synthetic receiver connection lost", player.Error);
        }
        finally { slow.Release.TrySetResult(); await Until(() => Volatile.Read(ref slow.Active) == 0); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedOrReplacedSessionDiscardsQueuedEditsAndLateFailures(bool replace)
    {
        var old = new Output("speaker"); var replacement = new Output("speaker"); replacement.Release.TrySetResult();
        var next = old;
        await using var player = new PlaybackCoordinator(new Backend(), _ => next);
        await player.StartAsync("first.flac", false, [(old.Device, new())], 100);
        player.Update(old.Device.Id, new() { Volume = 20 }, 100);
        try
        {
            await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 40; i++) player.Update(old.Device.Id, new() { Volume = i }, 100);
            if (replace)
            {
                next = replacement; await player.StartAsync("second.flac", false, [(replacement.Device, new())], 100);
                player.Update(replacement.Device.Id, new() { Volume = 65 }, 100);
                Assert.Single(replacement.Applied); Assert.Equal(.65f, replacement.Applied.Single().Gain, 3);
            }
            else await player.StopAsync();
            old.Fail = true; old.Release.TrySetResult(); await Until(() => Volatile.Read(ref old.Active) == 0);
            await Task.Delay(25);
            Assert.Equal(1, old.Calls); Assert.True(old.Disposed); Assert.Null(player.Error);
            Assert.Equal(replace, player.IsPlaying);
        }
        finally { old.Release.TrySetResult(); await Until(() => Volatile.Read(ref old.Active) == 0); }
    }
}
