using Musync.Core;
using Musync.Network;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Xunit;

namespace Musync.Tests;

public sealed class AirPlay2LifecycleTests
{
    private sealed class Bridge : IAirPlayBridge
    {
        public readonly ConcurrentQueue<float> Gains = new();
        public readonly ConcurrentQueue<float> Volumes = new();
        public readonly ConcurrentQueue<byte[]> Packets = new();
        public readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource VolumeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? VolumeRelease;
        public bool FailOpen, FailSend;
        public int Closes;
        public string? Error(string id) => null;
        public Task OpenAsync(SpeakerDevice device, float gain, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Gains.Enqueue(gain);
            return FailOpen ? Task.FromException(new IOException("Open rejected")) : Task.CompletedTask;
        }
        public async Task SetVolumeAsync(string id, float gain)
        {
            Volumes.Enqueue(gain); VolumeEntered.TrySetResult();
            if (VolumeRelease is not null) await VolumeRelease;
        }
        public Task SendAsync(string id, byte[] pcm)
        {
            if (FailSend) throw new IOException("PCM pipe lost");
            Packets.Enqueue(pcm); Sent.TrySetResult(); return Task.CompletedTask;
        }
        public Task CloseAsync(string id) { Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    }
    private static AirPlay2Output Output(Bridge bridge) => new(new("homepod", "Simulated HomePod", SpeakerTransport.AirPlay), bridge);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    [Fact]
    public async Task PauseRetainsUnreadPcmAndResumeUsesAFreshSessionWithLatestControls()
    {
        var bridge = new Bridge(); await using var output = Output(bridge);
        output.Control.Gain = .5f;
        await output.StartAsync(CancellationToken.None);
        await bridge.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        output.Buffer.Write(Enumerable.Repeat(.25f, 40000).ToArray());
        await Until(() => bridge.Packets.Any(p => BinaryPrimitives.ReadInt16LittleEndian(p) != 0));
        await output.SetPausedAsync(true);
        var pausedCount = bridge.Packets.Count;
        await Task.Delay(40); Assert.Equal(pausedCount, bridge.Packets.Count);
        var held = output.Buffer.BufferedFrames;
        Assert.Equal(1, bridge.Closes); Assert.True(held > 0);
        Assert.Equal(0, output.Buffer.Read(new float[704])); Assert.Equal(held, output.Buffer.BufferedFrames);
        output.Control.Gain = .15f; output.Control.DelayMs = 120;
        await output.UpdateControlsAsync(); Assert.Empty(bridge.Volumes);
        await output.SetPausedAsync(false);
        Assert.Equal(new[] { .5f, .15f }, bridge.Gains); Assert.Null(output.Error);
        output.Buffer.Write(Enumerable.Repeat(.4f, 50000).ToArray());
        await Until(() => bridge.Packets.Skip(pausedCount).Any(p => BinaryPrimitives.ReadInt16LittleEndian(p) != 0));
        Assert.Contains(bridge.Packets.Skip(pausedCount), p => BinaryPrimitives.ReadInt16LittleEndian(p) is > 8000 and < 9000);
        await Until(() => bridge.Packets.Skip(pausedCount).Any(p => BinaryPrimitives.ReadInt16LittleEndian(p) > 13000));
        Assert.All(bridge.Packets.Skip(pausedCount), p => Assert.True(BinaryPrimitives.ReadInt16LittleEndian(p) is 0 or >= 8000));
        await output.StopAsync(); await output.StopAsync(); Assert.Equal(2, bridge.Closes);
        Assert.Equal(0, output.Buffer.BufferedFrames);
    }
    [Fact]
    public async Task FailedOpenStillClosesAnAttemptedSession()
    {
        var bridge = new Bridge { FailOpen = true }; await using var output = Output(bridge);
        await Assert.ThrowsAsync<IOException>(() => output.StartAsync(CancellationToken.None));
        Assert.Equal(1, bridge.Closes); Assert.Empty(bridge.Packets);
        await output.StopAsync(); Assert.Equal(1, bridge.Closes);
    }
    [Fact]
    public async Task FailedResumeClosesItsAttemptAndLeavesNoPump()
    {
        var bridge = new Bridge(); await using var output = Output(bridge);
        await output.StartAsync(CancellationToken.None);
        await output.SetPausedAsync(true); bridge.FailOpen = true;
        await Assert.ThrowsAsync<IOException>(() => output.SetPausedAsync(false));
        Assert.Equal(2, bridge.Closes); var count = bridge.Packets.Count;
        await Task.Delay(40); Assert.Equal(count, bridge.Packets.Count);
        await output.StopAsync(); Assert.Equal(2, bridge.Closes);
    }
    [Fact]
    public async Task QueuedVolumeUpdatesApplyOnlyTheLatestLevel()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new Bridge { VolumeRelease = release.Task }; await using var output = Output(bridge);
        await output.StartAsync(CancellationToken.None);
        output.Control.Gain = .4f; var first = output.UpdateControlsAsync();
        await bridge.VolumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        output.Control.Gain = .7f;
        var queued = Enumerable.Range(0, 12).Select(_ => output.UpdateControlsAsync()).ToArray();
        release.SetResult(); await first; await Task.WhenAll(queued);
        Assert.Equal(new[] { .4f, .7f }, bridge.Volumes);
    }
    [Fact]
    public async Task PumpFailureIsReportedAndClosed()
    {
        var bridge = new Bridge(); await using var output = Output(bridge);
        await output.StartAsync(CancellationToken.None);
        await bridge.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2)); bridge.FailSend = true;
        await Until(() => output.Error is not null);
        Assert.Equal("PCM pipe lost", output.Error); await output.StopAsync(); Assert.Equal(1, bridge.Closes);
    }
    [Fact]
    public async Task CancellationAndRepeatedDisposeDoNotLeaveAWorker()
    {
        var bridge = new Bridge(); var output = Output(bridge);
        using var lifetime = new CancellationTokenSource();
        await output.StartAsync(lifetime.Token); lifetime.Cancel();
        await output.DisposeAsync(); await output.DisposeAsync();
        Assert.Equal(1, bridge.Closes); Assert.Null(output.Error);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => output.StartAsync(CancellationToken.None));
    }
}
