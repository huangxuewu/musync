using Musync.Core;
using Musync.Platform.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace Musync.Tests;

[SupportedOSPlatform("windows")]
public sealed class AcousticCaptureLifecycleTests
{
    private sealed class Recorder : IAcousticRecorder
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        public event CaptureDataAvailableHandler? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public bool DelayFirstPacket, Starting, Capturing, Disposed, FailBeforeData;
        public TaskCompletionSource? Disposal;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopRequests;
        public void StartRecording()
        {
            Starting = true; Entered.TrySetResult();
            if (FailBeforeData) { Starting = false; RecordingStopped?.Invoke(this, new(new IOException("Synthetic microphone unplugged"))); }
            else if (!DelayFirstPacket) FirstPacket();
        }
        public void FirstPacket()
        {
            Starting = false; Capturing = true;
            var samples = new float[64]; DataAvailable?.Invoke(MemoryMarshal.AsBytes(samples.AsSpan()), AudioClientBufferFlags.None, 0, 0);
        }
        public void StopRecording()
        {
            StopRequests++;
            // Reproduce the library race: a stop while starting does not
            // prevent the capture thread from entering its capturing state.
            if (Starting || !Capturing) return;
            Capturing = false; RecordingStopped?.Invoke(this, new(null));
        }
        public async ValueTask DisposeAsync() { if (Disposal is not null) await Disposal.Task; Disposed = true; }
    }
    [Fact]
    public async Task CancellationBeforeStartNeverCreatesAMicrophoneRecorder()
    {
        var calls = 0; var provider = new WindowsAcousticInputProvider(_ => { calls++; return new Recorder(); }, TimeSpan.FromMilliseconds(40));
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.StartAsync("fake", TimeSpan.FromSeconds(2), token.Token));
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task CancelingBeforeTheFirstPacketStopsTheLateStartedCaptureAndKeepsItsLeaseUntilClosed()
    {
        var first = new Recorder { DelayFirstPacket = true }; var second = new Recorder(); var calls = 0;
        var provider = new WindowsAcousticInputProvider(_ => calls++ == 0 ? first : second, TimeSpan.FromMilliseconds(40));
        using var token = new CancellationTokenSource();
        var start = provider.StartAsync("fake", TimeSpan.FromSeconds(2), token.Token); await first.Entered.Task;
        token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await Assert.ThrowsAsync<IOException>(() => provider.StartAsync("fake", TimeSpan.FromSeconds(2), default));
        first.FirstPacket();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!first.Disposed) await Task.Delay(5, wait.Token);
        Assert.False(first.Capturing); Assert.True(first.StopRequests >= 2);
        await using var session = await provider.StartAsync("fake", TimeSpan.FromSeconds(2), default);
        Assert.Equal(64, session.SamplesCaptured); Assert.True(second.Capturing);
    }
    [Fact]
    public async Task CleanupTimeoutDoesNotReleaseTheRecorderForAnOverlappingTest()
    {
        var first = new Recorder { Disposal = new() }; var second = new Recorder(); var calls = 0;
        var provider = new WindowsAcousticInputProvider(_ => calls++ == 0 ? first : second, TimeSpan.FromMilliseconds(40));
        var session = await provider.StartAsync("fake", TimeSpan.FromSeconds(2), default);
        var snapshot = await session.CompleteAsync(default); Assert.Equal(64, snapshot.Samples.Length);
        await Assert.ThrowsAsync<TimeoutException>(() => session.DisposeAsync().AsTask()); Assert.False(first.Capturing);
        await Assert.ThrowsAsync<IOException>(() => provider.StartAsync("fake", TimeSpan.FromSeconds(2), default));
        first.Disposal.TrySetResult(); await session.DisposeAsync(); Assert.True(first.Disposed);
        await using var next = await provider.StartAsync("fake", TimeSpan.FromSeconds(2), default);
        Assert.Equal(64, next.SamplesCaptured);
    }
    [Fact]
    public async Task UnexpectedStopAndUnsupportedFormatReleaseResourcesAndAllowARetry()
    {
        foreach (var unsupported in new[] { false, true })
        {
            var first = new Recorder { FailBeforeData = !unsupported };
            if (unsupported) first.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 4);
            var second = new Recorder(); var calls = 0;
            var provider = new WindowsAcousticInputProvider(_ => calls++ == 0 ? first : second, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<Exception>(() => provider.StartAsync("fake", TimeSpan.FromSeconds(2), default));
            Assert.True(first.Disposed); Assert.False(first.Capturing);
            await using var session = await provider.StartAsync("fake", TimeSpan.FromSeconds(2), default);
            Assert.Equal(64, session.SamplesCaptured);
        }
    }
    [Fact]
    public async Task RecordingDeadlineCannotReturnAValidMeasurement()
    {
        var recorder = new Recorder(); var provider = new WindowsAcousticInputProvider(_ => recorder, TimeSpan.FromMilliseconds(100));
        await using var session = await provider.StartAsync("fake", TimeSpan.FromMilliseconds(50), default);
        await Task.Delay(100); await Assert.ThrowsAsync<IOException>(() => session.CompleteAsync(default));
        Assert.False(recorder.Capturing);
    }
}
