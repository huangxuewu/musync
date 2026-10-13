using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class AudioTailTests
{
    private sealed class Backend : IAudioBackend
    {
        public volatile bool Paused;
        public bool IsFinished { get; private set; }
        public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public string? DefaultDeviceId => null;
        public IAudioOutput CreateOutput(SpeakerDevice device) => new Output(device);
        public double Position => .05;
        public double Duration => .05;
        public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        {
            if (path == "hold") { await Task.Delay(Timeout.Infinite, token); return; }
            IsFinished = false;
            while (Paused) await Task.Delay(5, token);
            var music = Enumerable.Repeat(.25f, 4410).ToArray(); music[^2] = music[^1] = -.25f;
            hub.Publish(music); IsFinished = true;
        }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public void Pause(bool paused) => Paused = paused;
        public void Seek(double seconds) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Output(SpeakerDevice device, double latency = .6) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error { get; set; }
        public TimeSpan EstimatedLatency => TimeSpan.FromSeconds(latency);
        public bool Disposed;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync() { Buffer.Clear(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; Buffer.Clear(); return ValueTask.CompletedTask; }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    [Fact]
    public async Task TrackShorterThanDelayStillReachesTheOutputInFull()
    {
        var output = new Output(new("delayed", "Delayed", SpeakerTransport.Windows));
        output.Buffer.SetDelay(80);
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("short", false, [(output.Device, new() { DelayMs = 80 })], 100);
        await Until(() => player.Finished);
        var received = new List<float>(); var chunk = new float[882];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (received.Count < 4410)
        {
            output.Buffer.Read(chunk);
            received.AddRange(chunk.Where(sample => sample != 0));
            await Task.Delay(10, deadline.Token);
        }
        Assert.Equal(4410, received.Count); Assert.Equal(-.25f, received[^1]);
        Assert.All(received.Take(4408), sample => Assert.Equal(.25f, sample));
        Assert.Equal(.05, player.Position); Assert.True(player.IsPlaying);
    }
    [Fact]
    public async Task PausingAtEofHoldsTheCompletionClockAndRetainsUnreadMusic()
    {
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.Windows), .2);
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("short", false, [(output.Device, new())], 100);
        await Until(() => player.Finished); await player.PauseAsync(true);
        var held = output.Buffer.BufferedFrames;
        await Task.Delay(250);
        Assert.False(player.TailComplete); Assert.True(held >= 2205); Assert.Equal(held, output.Buffer.BufferedFrames);
        Assert.Equal(0, output.Buffer.Read(new float[882]));
        await player.PauseAsync(false); await Until(() => player.TailComplete);
    }
    [Fact]
    public async Task NetworkResumeAtEofStartsANewTransportTailAllowance()
    {
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.AirPlay), .15);
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("short", false, [(output.Device, new())], 100);
        await Until(() => player.TailComplete);
        await player.PauseAsync(true); await player.PauseAsync(false);
        Assert.False(player.TailComplete); await Until(() => player.TailComplete);
    }
    [Fact]
    public async Task TailUsesEachActiveRoutesOwnDelayAndLatency()
    {
        var windows = new Output(new("windows", "Windows", SpeakerTransport.Windows), .4);
        var cast = new Output(new("cast", "Cast", SpeakerTransport.Chromecast), 8.6);
        await using var player = new PlaybackCoordinator(new Backend(), d => d.Id == "cast" ? cast : windows);
        await player.StartAsync("short", false, [(windows.Device, new() { DelayMs = 5000 }), (cast.Device, new())], 100);
        Assert.Equal(TimeSpan.FromSeconds(8.6), player.RequiredTail);
        cast.Error = "Receiver lost"; await player.CheckOutputsAsync();
        Assert.Equal(TimeSpan.FromSeconds(5.4), player.RequiredTail); Assert.True(cast.Disposed);
    }
    [Fact]
    public async Task StopCancelsTheTailProducerAndResetsCompletion()
    {
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.Windows));
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("short", false, [(output.Device, new())], 100);
        await Until(() => player.Finished);
        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(player.Finished); Assert.False(player.TailComplete); Assert.Empty(player.Outputs);
        Assert.Equal(0, output.Buffer.BufferedFrames); Assert.True(output.Disposed);
    }
    [Fact]
    public async Task BackendEofFromAnOldTrackDoesNotFinishANewSession()
    {
        var backend = new Backend(); var device = new SpeakerDevice("speaker", "Speaker", SpeakerTransport.Windows);
        await using var player = new PlaybackCoordinator(backend, d => new Output(d));
        await player.StartAsync("short", false, [(device, new())], 100); await Until(() => player.Finished);
        await player.StartAsync("hold", false, [(device, new())], 100);
        Assert.True(backend.IsFinished); Assert.False(player.Finished); Assert.False(player.TailComplete);
    }
    [Fact]
    public async Task PausedStartWaitsForResumeBeforeCompletionOrPadding()
    {
        var output = new Output(new("speaker", "Speaker", SpeakerTransport.Windows));
        await using var player = new PlaybackCoordinator(new Backend(), _ => output);
        await player.StartAsync("short", false, [(output.Device, new())], 100, paused: true);
        await Task.Delay(40); Assert.False(player.Finished); Assert.Equal(0, output.Buffer.BufferedFrames);
        await player.PauseAsync(false); await Until(() => player.Finished);
        Assert.True(output.Buffer.BufferedFrames > 0);
    }
}
