using Musync.Core;
using Musync.Network;
using Musync.Platform.Windows;
using System.Buffers.Binary;
using System.Net;
using System.Diagnostics;
using System.Collections.Concurrent;
using Xunit;

namespace Musync.Tests;

public sealed class CastLifecycleTests
{
    [Fact]
    public async Task StartupDoesNotWaitForSixSecondsOfSilentPlayback()
    {
        await using var server = new LocalStreamServer();
        var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Mp3StreamEncoder(), session);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await output.StartAsync(deadline.Token);
        Assert.Single(session.Urls);
    }
    private class Encoder : IAudioStreamEncoder
    {
        public virtual void EncodeMp3(AudioSampleReader read, Action<ReadOnlyMemory<byte>> write, CancellationToken token)
        {
            var samples = new float[1152 * 2];
            // A complete MPEG-1/Layer-III 192 kbps, 44.1 kHz stereo frame.
            var frame = new byte[626]; frame[0] = 255; frame[1] = 251; frame[2] = 176;
            while (!token.IsCancellationRequested && read(samples) > 0) write(frame);
        }
    }
    private sealed class BlockingEncoder : Encoder, IDisposable
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Released = new(false);
        public override void EncodeMp3(AudioSampleReader read, Action<ReadOnlyMemory<byte>> write, CancellationToken token)
        {
            base.EncodeMp3(samples =>
            {
                var count = read(samples);
                if (samples[..count].ContainsAnyExcept(0f))
                {
                    Entered.TrySetResult();
                    if (!Released.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test encoder was not released.");
                }
                return count;
            }, write, token);
        }
        public void Dispose() { Released.Set(); Released.Dispose(); }
    }
    private sealed class RecordingEncoder : Encoder
    {
        public readonly TaskCompletionSource MusicStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<float[]> Music = new();
        public readonly Stopwatch Clock = new();
        public override void EncodeMp3(AudioSampleReader read, Action<ReadOnlyMemory<byte>> write, CancellationToken token)
        {
            base.EncodeMp3(samples =>
            {
                var count = read(samples);
                if (count > 0 && (Clock.IsRunning || samples[..count].ContainsAnyExcept(0f)))
                {
                    Clock.Start(); Music.Enqueue(samples[..count].ToArray()); MusicStarted.TrySetResult();
                }
                return count;
            }, write, token);
        }
    }
    private sealed class FailedEncoder : IAudioStreamEncoder
    {
        public void EncodeMp3(AudioSampleReader read, Action<ReadOnlyMemory<byte>> write, CancellationToken token)
            => throw new IOException("Encoder unavailable");
    }

    [Fact]
    public async Task PrebufferedMusicIsPacedRatherThanBurstingPastTheLiveWindow()
    {
        await using var server = new LocalStreamServer(); var session = new Session(); var encoder = new RecordingEncoder();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, encoder, session);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        output.Buffer.Write(Enumerable.Repeat(.25f, AudioFormat.SamplesPerSecond * 3).ToArray());
        await encoder.MusicStarted.Task.WaitAsync(deadline.Token);
        await Task.Delay(260, deadline.Token);
        await output.SetPausedAsync(true);
        var secondsRead = encoder.Music.Sum(c => c.Length) / (double)AudioFormat.SamplesPerSecond;
        Assert.InRange(secondsRead, .2, encoder.Clock.Elapsed.TotalSeconds + .06);
    }

    [Fact]
    public async Task ProducerStallWaitsWithoutInsertingSilentMusicBlocks()
    {
        await using var server = new LocalStreamServer(); var session = new Session(); var encoder = new RecordingEncoder();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, encoder, session);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        output.Buffer.Write(Enumerable.Repeat(.25f, 1152 * 2 * 16).ToArray());
        while (encoder.Music.Count < 16) await Task.Delay(10, deadline.Token);
        await Task.Delay(200, deadline.Token);
        Assert.Equal(16, encoder.Music.Count);
        output.Buffer.Write(Enumerable.Repeat(-.25f, 1152 * 2 * 16).ToArray());
        while (encoder.Music.Count < 32) await Task.Delay(10, deadline.Token);
        await output.SetPausedAsync(true);
        var samples = encoder.Music.SelectMany(c => c).ToArray();
        Assert.Equal(1152 * 2 * 32, samples.Length);
        Assert.All(samples[..(1152 * 2 * 16)], v => Assert.Equal(.25f, v));
        Assert.All(samples[(1152 * 2 * 16)..], v => Assert.Equal(-.25f, v));
        Assert.Equal(0, output.Buffer.Underruns);
    }

    [Fact]
    public async Task EncoderFailureEndsPreparationImmediatelyAndRestoresReceiverVolume()
    {
        await using var server = new LocalStreamServer(); var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new FailedEncoder(), session);
        var error = await Assert.ThrowsAsync<IOException>(() => output.StartAsync(CancellationToken.None));
        Assert.Equal("Encoder unavailable", error.Message); Assert.Empty(session.Urls);
        Assert.Equal(.47, session.Levels[^1]); Assert.Contains("disconnect", session.Events);
    }
    private sealed class Session : ICastSession
    {
        public string? Error { get; set; }
        public bool ConnectionLost { get; set; }
        public bool ControlLost { get; set; }
        public Task? CleanupReply;
        public bool DropDuringStop;
        public bool LoseControlDuringStop;
        public readonly List<string> Events = [];
        public readonly List<double> Levels = [];
        public readonly List<string> Urls = [];
        public TaskCompletionSource? VolumeSent;
        public Task? VolumeReady;
        public bool RejectVolume;
        public bool RejectLoad;
        public Task ConnectAsync(SpeakerDevice device) { Events.Add("connect"); return Task.CompletedTask; }
        public Task<double?> ReadVolumeAsync() => Task.FromResult<double?>(.47);
        public Task LaunchAsync() { Events.Add("launch"); return Task.CompletedTask; }
        public async Task SetVolumeAsync(double level)
        {
            Events.Add("volume"); Levels.Add(level);
            if (CleanupReply is { } cleanup) { await cleanup; return; }
            if (RejectVolume && level != .47) throw new IOException("Volume rejected");
            VolumeSent?.TrySetResult();
            if (VolumeReady is { } ready) { VolumeReady = null; await ready; }
        }
        public Task LoadAsync(string url)
        {
            Events.Add("load"); Urls.Add(url);
            return RejectLoad ? Task.FromException(new IOException("Receiver rejected the audio stream")) : Task.CompletedTask;
        }
        public Task StopMediaAsync()
        {
            Events.Add("stop");
            if (DropDuringStop) { ConnectionLost = true; Error = "Synthetic connection lost"; return Task.CompletedTask; }
            if (LoseControlDuringStop) { ControlLost = true; Error = "Synthetic playback replaced"; return Task.CompletedTask; }
            return CleanupReply ?? Task.CompletedTask;
        }
        public Task DisconnectAsync() { Events.Add("disconnect"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Events.Add("dispose"); return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalPlaybackReplacementRevokesTheStreamWithoutStoppingOrChangingTheNewSourcesVolume(bool duringStop)
    {
        await using var server = new LocalStreamServer(); var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        await output.StartAsync(default);
        session.ControlLost = !duringStop; session.Error = duringStop ? null : "Synthetic playback replaced";
        session.LoseControlDuringStop = duringStop; var levels = session.Levels.Count;
        if (!duringStop)
        {
            output.Control.Gain = .25f;
            await Assert.ThrowsAsync<IOException>(() => output.UpdateControlsAsync());
            await Assert.ThrowsAsync<IOException>(() => output.SetPausedAsync(true));
        }
        await output.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(duringStop ? 1 : 0, session.Events.Count(e => e == "stop")); Assert.Equal(levels, session.Levels.Count);
        Assert.Contains("disconnect", session.Events); Assert.Equal(session.Error, output.Error);
        using var http = new HttpClient(); Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Assert.Single(session.Urls))).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedConnectionLossRevokesTheLiveStreamWithoutDeadControlRequests(bool duringStop)
    {
        await using var server = new LocalStreamServer(); var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        await output.StartAsync(default);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConnectionLost = !duringStop; session.Error = duringStop ? null : "Synthetic connection lost";
        session.DropDuringStop = duringStop; session.CleanupReply = pending.Task;
        var levels = session.Levels.Count;
        var stop = output.StopAsync();
        try
        {
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            if (duringStop) Assert.Contains("stop", session.Events); else Assert.DoesNotContain("stop", session.Events);
            Assert.Equal(levels, session.Levels.Count);
            Assert.Contains("disconnect", session.Events);
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Assert.Single(session.Urls))).StatusCode);
        }
        finally { pending.TrySetResult(); await stop; }
    }

    [Fact]
    public async Task MediaErrorsWithAnIntactConnectionStillStopLiveMediaAndRestoreVolume()
    {
        await using var server = new LocalStreamServer(); var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        await output.StartAsync(default); session.Error = "Synthetic media rejected";
        await output.StopAsync();
        Assert.Contains("stop", session.Events); Assert.Equal(.47, session.Levels.Last()); Assert.Contains("disconnect", session.Events);
    }
    [Fact]
    public async Task RejectedMediaLoadRevokesTheStreamAndRestoresReceiverVolume()
    {
        await using var server = new LocalStreamServer(); var session = new Session { RejectLoad = true };
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<IOException>(() => output.StartAsync(deadline.Token));
        Assert.Contains("rejected", error.Message);
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Assert.Single(session.Urls), deadline.Token)).StatusCode);
        Assert.Equal(.47, session.Levels[^1]);
        Assert.Contains("stop", session.Events); Assert.Contains("disconnect", session.Events);
    }
    [Fact]
    public async Task PauseRevokesOldStreamAndResumeLoadsFreshTimelineWithLatestVolume()
    {
        await using var server = new LocalStreamServer();
        var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        output.Control.Gain = .25f;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await output.StartAsync(deadline.Token);
        Assert.True(session.Events.IndexOf("volume") < session.Events.IndexOf("load"));
        using var http = new HttpClient();
        var oldUrl = Assert.Single(session.Urls);
        Assert.Contains("5.mp3", await http.GetStringAsync(oldUrl));
        session.Error = "Chromecast connection lost.";
        Assert.Equal(session.Error, output.Error);
        session.Error = null;

        // A slider drag during a slow receiver update must coalesce to the final value.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.VolumeSent = new(TaskCreationOptions.RunContinuationsAsynchronously); session.VolumeReady = released.Task;
        output.Control.Gain = .2f;
        var update = output.UpdateControlsAsync();
        await session.VolumeSent.Task.WaitAsync(deadline.Token);
        output.Control.Gain = .1f;
        var queued = Enumerable.Range(0, 20).Select(_ => output.UpdateControlsAsync()).ToArray();
        released.SetResult(); await update; await Task.WhenAll(queued);
        Assert.Equal(3, session.Levels.Count); Assert.Equal(.1, session.Levels[^1], 5);

        await output.SetPausedAsync(true);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(oldUrl)).StatusCode);
        await output.SetPausedAsync(false);
        Assert.Equal(2, session.Urls.Count); Assert.NotEqual(oldUrl, session.Urls[1]);
        var playlist = await http.GetStringAsync(session.Urls[1]);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", playlist);
        var segment = await http.GetByteArrayAsync(new Uri(new Uri(session.Urls[1]), "0.mp3"));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64BigEndian(segment.AsSpan(segment.Length - 12, 8)));

        await output.StopAsync();
        Assert.Equal(.47, session.Levels[^1]);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(session.Urls[1])).StatusCode);
        Assert.Equal(2, session.Events.Count(e => e == "stop")); Assert.Contains("disconnect", session.Events);
    }
    [Fact]
    public async Task PauseRestoresMusicFromAnUnfinishedSegmentInOrder()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new LocalStreamServer(); var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        await output.StartAsync(deadline.Token);
        var music = Enumerable.Range(1, 15000).SelectMany(n => new[] { n / 15001f, n / 15001f }).ToArray();
        output.Buffer.Write(music);
        while (output.Buffer.BufferedFrames == music.Length / 2) await Task.Delay(5, deadline.Token);
        await output.SetPausedAsync(true);
        Assert.Equal(music.Length / 2, output.Buffer.BufferedFrames);
        var received = new float[music.Length]; Assert.Equal(0, output.Buffer.Read(received));
        // Inspect the retained raw queue while the simulated receiver stays stopped.
        output.Buffer.SetTransportPaused(false); Assert.Equal(music.Length, output.Buffer.Read(received)); Assert.Equal(music, received);
        await output.StopAsync(); Assert.Equal(0, output.Buffer.BufferedFrames);
    }
    [Fact]
    public async Task PauseDuringEncodingRestoresTheUnpublishedSegment()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var encoder = new BlockingEncoder(); await using var server = new LocalStreamServer(); var session = new Session();
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, encoder, session);
        await output.StartAsync(deadline.Token);
        var music = Enumerable.Repeat(.25f, 250000).ToArray(); output.Buffer.Write(music);
        await encoder.Entered.Task.WaitAsync(deadline.Token);
        var pause = output.SetPausedAsync(true);
        try { Assert.False(pause.IsCompleted); }
        finally { encoder.Released.Set(); }
        await pause;
        Assert.Equal(music.Length / 2, output.Buffer.BufferedFrames);
        output.Buffer.SetTransportPaused(false); var received = new float[music.Length];
        Assert.Equal(music.Length, output.Buffer.Read(received)); Assert.Equal(music, received);
    }
    [Fact]
    public async Task FailedPreparationRestoresVolumeAndDisconnectsWithoutLoadingMedia()
    {
        await using var server = new LocalStreamServer();
        var session = new Session { RejectVolume = true };
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        await Assert.ThrowsAsync<IOException>(() => output.StartAsync(CancellationToken.None));
        Assert.Equal(.47, session.Levels[^1]); Assert.Empty(session.Urls); Assert.Contains("disconnect", session.Events);
    }
    [Fact]
    public async Task CancelledPrimingRestoresVolumeAndDoesNotLoadMedia()
    {
        await using var server = new LocalStreamServer();
        var session = new Session { VolumeSent = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var output = new CastOutput(new("cast", "Test Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), server, new Encoder(), session);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var starting = output.StartAsync(deadline.Token);
        // Cancel after preparation actually reaches priming, rather than depending
        // on server startup finishing inside a wall-clock interval on a busy PC.
        await session.VolumeSent.Task.WaitAsync(deadline.Token);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(.47, session.Levels[^1]); Assert.Empty(session.Urls); Assert.Contains("disconnect", session.Events);
    }
}
