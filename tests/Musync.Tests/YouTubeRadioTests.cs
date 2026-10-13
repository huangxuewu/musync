using Musync.Core;
using System.Threading.Channels;
using Xunit;

namespace Musync.Tests;

public sealed class YouTubeRadioTests
{
    private const string Url = "https://www.youtube.com/watch?v=eZyI7epSb_w";
    [Theory]
    [InlineData("https://www.youtube.com/live/eZyI7epSb_w?is=DbH3UTcIZl6IAoJ6")]
    [InlineData("https://youtu.be/eZyI7epSb_w?si=test")]
    [InlineData("https://music.youtube.com/watch?v=eZyI7epSb_w&list=test")]
    [InlineData("https://m.youtube.com/shorts/eZyI7epSb_w")]
    [InlineData("https://www.youtube.com/embed/eZyI7epSb_w")]
    public void VideoAddressesCanonicalizeWithoutTrackingOrPlaylistParameters(string input)
    {
        Assert.True(YouTubeAddress.TryParse(input, out var address)); Assert.Equal(Url, address!.Url);
    }
    [Theory]
    [InlineData("https://youtube.com.evil.test/watch?v=eZyI7epSb_w")]
    [InlineData("https://user@youtube.com/watch?v=eZyI7epSb_w")]
    [InlineData("https://youtube.com:8080/watch?v=eZyI7epSb_w")]
    [InlineData("https://youtube.com/watch?v=eZyI7epSb_w&v=M7lc1UVf-VE")]
    [InlineData("https://youtube.com/playlist?list=PL123")]
    [InlineData("https://youtube.com/watch?v=bad")]
    [InlineData("https://youtu.be/eZyI7epSb_w/extra")]
    [InlineData("file:///C:/test.mp3")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public void UnsupportedOrAmbiguousAddressesAreRejected(string? input)
        => Assert.False(YouTubeAddress.TryParse(input, out _));

    [Fact]
    public async Task StationsRoundTripAndOlderSettingsStillLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-Radio-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path);
            await File.WriteAllTextAsync(path, "{\"Language\":\"ja\"}"); Assert.Empty(store.Load().RadioStations);
            await store.SaveAsync(new() { Language = "zh-Hans", RadioStations = [new("station", "晚风電台", Url, true)] });
            var loaded = store.Load(); Assert.Equal("zh-Hans", loaded.Language);
            Assert.Equal(new RadioStation("station", "晚风電台", Url, true), Assert.Single(loaded.RadioStations));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class LocalBackend : IAudioBackend
    {
        public int LocalStarts; public double Position => 12; public double Duration => 180; public bool IsFinished => false;
        public string? DefaultDeviceId => null; public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public IAudioOutput CreateOutput(SpeakerDevice device) => throw new NotSupportedException();
        public Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        { LocalStarts++; return Task.CompletedTask; }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => Task.CompletedTask;
        public void Pause(bool paused) { } public void Seek(double seconds) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Resolver : IYouTubeResolver
    {
        public Task<RadioAudioInfo> ResolveAsync(YouTubeAddress address, CancellationToken token)
            => Task.FromResult(new RadioAudioInfo(address, "Synthetic radio", "Synthetic channel", 180, false, "251"));
    }
    private sealed class Stream : IRadioPcmStream
    {
        public readonly Channel<float[]> Frames = Channel.CreateUnbounded<float[]>();
        public int Reads, Disposals;
        public async ValueTask<float[]?> ReadAsync(CancellationToken token)
        { var frame = await Frames.Reader.ReadAsync(token); Interlocked.Increment(ref Reads); return frame; }
        public ValueTask DisposeAsync() { Disposals++; Frames.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Decoder : IRadioPcmDecoder
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Stream Stream = new(); public int Cancellations;
        public async Task<IRadioPcmStream> PrepareAsync(RadioAudioInfo info, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Ready.Task.WaitAsync(token); return Stream; }
            catch (OperationCanceledException) { Cancellations++; throw; }
        }
    }
    private sealed class Output(string id, int latency) : IAudioOutput
    {
        public SpeakerDevice Device { get; } = new(id, id, id == "pc" ? SpeakerTransport.Windows : SpeakerTransport.Chromecast);
        public AudioRingBuffer Buffer { get; } = new(); public OutputControl Control { get; } = new();
        public string? Error => null; public TimeSpan EstimatedLatency => TimeSpan.FromMilliseconds(latency);
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts, Releases;
        public Task StartAsync(CancellationToken token) { Starts++; return Ready.Task.WaitAsync(token); }
        public Task BeginPlaybackAsync(CancellationToken token) { Releases++; return Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    [Fact]
    public async Task DecodePrebufferAndEveryReceiverGateTheSameTimelineWithIndependentManualTuning()
    {
        var decoder = new Decoder(); var local = new LocalBackend();
        var backend = new YouTubeAudioBackend(local, new Resolver(), decoder);
        await backend.ResolveRadioAsync(new("eZyI7epSb_w"), default);
        var pc = new Output("pc", 400); var cast = new Output("cast", 5000); pc.Ready.SetResult();
        var descriptors = new List<PlaybackSource>();
        await using var player = new PlaybackCoordinator(backend, (device, source) => { descriptors.Add(source); return device.Id == "pc" ? pc : cast; });
        var start = player.StartAsync(Url, false, [(pc.Device, new() { DelayMs = 100 }), (cast.Device, new())], 100);
        await decoder.Entered.Task; Assert.Equal(0, pc.Starts + cast.Starts); Assert.Equal(0, backend.Position);
        decoder.Ready.SetResult(); await Until(() => cast.Starts == 1);
        Assert.Equal(0, pc.Releases + cast.Releases); Assert.Equal(0, backend.Position);
        cast.Ready.SetResult(); await start;
        Assert.All(descriptors, descriptor => Assert.True(descriptor.IsStreaming));
        Assert.Equal(4600, pc.Control.AlignmentDelayMs); Assert.Equal(4700, pc.Control.EffectiveDelayMs);
        Assert.Equal(0, cast.Control.AlignmentDelayMs); Assert.Equal(TimeSpan.FromMilliseconds(5100), player.RequiredTail);
        var samples = Enumerable.Repeat(.25f, AudioFormat.SamplesPerSecond / 50).ToArray();
        decoder.Stream.Frames.Writer.TryWrite(samples); await Until(() => backend.Position > 0);
        Assert.Equal(samples.Length / 2, pc.Buffer.BufferedFrames); Assert.Equal(pc.Buffer.BufferedFrames, cast.Buffer.BufferedFrames);
        player.Update("pc", new() { DelayMs = 175 }, 100);
        Assert.Equal(4775, pc.Control.EffectiveDelayMs); Assert.Equal(4600, pc.Control.AlignmentDelayMs);
        await player.StopAsync(); Assert.Equal(1, decoder.Stream.Disposals); Assert.Equal(0, local.LocalStarts);
    }
    [Fact]
    public async Task StopCancelsPrebufferWithoutStartingOrReleasingAnyOutput()
    {
        var decoder = new Decoder(); var backend = new YouTubeAudioBackend(new LocalBackend(), new Resolver(), decoder);
        await backend.ResolveRadioAsync(new("eZyI7epSb_w"), default); int outputs = 0;
        await using var player = new PlaybackCoordinator(backend, _ => { outputs++; return new Output("pc", 400); });
        var start = player.StartAsync(Url, false, [(new("pc", "pc", SpeakerTransport.Windows), new())], 100);
        await decoder.Entered.Task; await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, decoder.Cancellations); Assert.Equal(0, outputs); Assert.False(player.IsPlaying);
    }
    [Fact]
    public async Task PauseRetainsAFetchedFrameAndLocalFilesStillDelegate()
    {
        var decoder = new Decoder(); decoder.Ready.SetResult(); var local = new LocalBackend();
        await using var backend = new YouTubeAudioBackend(local, new Resolver(), decoder);
        await backend.ResolveRadioAsync(new("eZyI7epSb_w"), default); await backend.PrepareSourceAsync(Url, default);
        var buffer = new AudioRingBuffer(); var hub = new AudioHub(); hub.SetOutputs([buffer]);
        backend.Pause(true); using var token = new CancellationTokenSource();
        var playback = backend.PlayFileAsync(Url, hub, token.Token);
        decoder.Stream.Frames.Writer.TryWrite([.1f, .2f]); await Until(() => decoder.Stream.Reads == 1);
        Assert.Equal(0, buffer.BufferedFrames); Assert.Equal(0, backend.Position);
        backend.Pause(false); await Until(() => buffer.BufferedFrames == 1);
        var output = new float[2]; Assert.Equal(2, buffer.Read(output)); Assert.Equal(new[] { .1f, .2f }, output);
        token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        await backend.ReleasePreparedSourceAsync(); await backend.PlayFileAsync("local.flac", hub, default);
        Assert.Equal(1, local.LocalStarts); Assert.Equal(12, backend.Position); Assert.Equal(180, backend.Duration);
    }
}
