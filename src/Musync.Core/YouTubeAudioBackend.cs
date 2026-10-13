using System.Collections.Concurrent;
using System.Diagnostics;

namespace Musync.Core;

public sealed record RadioAudioInfo(YouTubeAddress Address, string Title, string Artist,
    double Duration, bool IsLive, string FormatId);

public interface IYouTubeResolver
{
    Task<RadioAudioInfo> ResolveAsync(YouTubeAddress address, CancellationToken token);
}

public interface IRadioPcmStream : IAsyncDisposable
{
    ValueTask<float[]?> ReadAsync(CancellationToken token);
}

public interface IRadioPcmDecoder
{
    Task<IRadioPcmStream> PrepareAsync(RadioAudioInfo info, CancellationToken token);
}

public interface IYouTubeRadioBackend
{
    Task<MusicTrack> ResolveRadioAsync(YouTubeAddress address, CancellationToken token);
}

/// <summary>One paced PCM source feeds the existing speaker hub and common startup barrier.</summary>
public sealed class YouTubeAudioBackend(IAudioBackend local, IYouTubeResolver resolver, IRadioPcmDecoder decoder)
    : IAudioBackend, IYouTubeRadioBackend
{
    private readonly ConcurrentDictionary<string, RadioAudioInfo> _metadata = new();
    private readonly object _publication = new();
    private IRadioPcmStream? _prepared;
    private string? _preparedKey;
    private bool _paused, _streaming, _finished;
    private double _position, _duration;

    public bool IsStreamingSource(string source) => YouTubeAddress.TryParse(source, out _);
    public async Task<MusicTrack> ResolveRadioAsync(YouTubeAddress address, CancellationToken token)
    {
        var info = await resolver.ResolveAsync(address, token);
        token.ThrowIfCancellationRequested();
        _metadata[address.Url] = info;
        return new("youtube:" + address.VideoId, info.Title, info.Artist, address.Url,
            DurationSeconds: info.IsLive ? 0 : info.Duration);
    }
    public async Task PrepareSourceAsync(string source, CancellationToken token)
    {
        if (!IsStreamingSource(source)) { _streaming = false; return; }
        await ReleasePreparedSourceAsync();
        if (!_metadata.TryGetValue(source, out var info)) throw new InvalidOperationException("Resolve the radio station before playback.");
        lock (_publication) { _streaming = true; _finished = false; _position = 0; _duration = info.IsLive ? 0 : info.Duration; }
        var stream = await decoder.PrepareAsync(info, token);
        if (token.IsCancellationRequested) { await stream.DisposeAsync(); token.ThrowIfCancellationRequested(); }
        _prepared = stream; _preparedKey = source;
    }
    public async Task ReleasePreparedSourceAsync()
    {
        var stream = _prepared; _prepared = null; _preparedKey = null;
        if (stream is not null) await stream.DisposeAsync();
    }
    public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
    {
        if (!IsStreamingSource(path)) { _streaming = false; await local.PlayFileAsync(path, hub, token, startPosition); return; }
        if (startPosition != 0) throw new NotSupportedException("Radio seeking is not available.");
        var stream = _preparedKey == path ? _prepared : null;
        if (stream is null) throw new InvalidOperationException("Radio has not finished buffering.");
        var clock = Stopwatch.StartNew(); double next = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var samples = await stream.ReadAsync(token);
            if (samples is null) { lock (_publication) _finished = true; return; }
            if (samples.Length == 0 || samples.Length % AudioFormat.Channels != 0)
                throw new IOException("The radio decoder returned an incomplete stereo frame.");
            // Retain the fetched block on Pause; never drop a frame into a paused hub.
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (_publication)
                {
                    if (!_paused)
                    {
                        hub.Publish(samples); _position += samples.Length / (double)AudioFormat.SamplesPerSecond;
                        break;
                    }
                }
                await Task.Delay(20, token); next = clock.Elapsed.TotalSeconds;
            }
            next += samples.Length / (double)AudioFormat.SamplesPerSecond;
            var wait = next - clock.Elapsed.TotalSeconds;
            if (wait > .001) await Task.Delay(TimeSpan.FromSeconds(wait), token);
            if (wait < -.1) next = clock.Elapsed.TotalSeconds;
        }
    }
    public IReadOnlyList<SpeakerDevice> GetDevices() => local.GetDevices();
    public string? DefaultDeviceId => local.DefaultDeviceId;
    public IAudioOutput CreateOutput(SpeakerDevice device) => local.CreateOutput(device);
    public MusicTrack ProbeFile(string path) => local.ProbeFile(path);
    public Task CaptureAsync(string id, AudioHub hub, CancellationToken token)
    { _streaming = false; return local.CaptureAsync(id, hub, token); }
    public void Pause(bool paused) { lock (_publication) _paused = paused; local.Pause(paused); }
    public void Seek(double seconds) { if (_streaming) throw new NotSupportedException("Radio seeking is not available."); local.Seek(seconds); }
    public double Position { get { lock (_publication) return _streaming ? _position : local.Position; } }
    public double Duration { get { lock (_publication) return _streaming ? _duration : local.Duration; } }
    public bool IsFinished { get { lock (_publication) return _streaming ? _finished : local.IsFinished; } }
    public async ValueTask DisposeAsync() { await ReleasePreparedSourceAsync(); await local.DisposeAsync(); }
}
