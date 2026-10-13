using Musync.Core;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using Sharpcaster.Messages.Media;

namespace Musync.Network;

internal interface ICastSession : IAsyncDisposable
{
    string? Error { get; }
    Task ConnectAsync(SpeakerDevice device);
    Task<double?> ReadVolumeAsync();
    Task LaunchAsync();
    Task SetVolumeAsync(double level);
    Task LoadAsync(string url);
    Task PrepareFileAsync(string url, double duration, double position, CancellationToken token) => throw new NotSupportedException();
    Task PlayPreparedAsync() => throw new NotSupportedException();
    Task PausePreparedAsync() => throw new NotSupportedException();
    Task<double?> ReadPositionAsync() => throw new NotSupportedException();
    Task SeekPreparedAsync(double seconds) => throw new NotSupportedException();
    Task StopMediaAsync();
    Task DisconnectAsync();
}

internal sealed class CastSession : ICastSession
{
    private readonly ChromecastClient _client = new();
    private volatile bool _expectedDisconnect = true;
    private volatile string? _error;
    private readonly CastMediaFeedback _media = new();
    public string? Error => _error ?? _media.Error;
    public CastSession()
    {
        _client.Disconnected += OnDisconnected;
        _client.MediaChannel.LoadFailed += OnLoadFailed;
        _client.MediaChannel.InvalidRequest += OnInvalidRequest;
        _client.MediaChannel.ErrorHappened += OnMediaError;
        _client.MediaChannel.StatusChanged += OnMediaStatus;
    }
    private void OnLoadFailed(object? sender, LoadFailedMessage message) => _media.Fail("Chromecast could not load the audio stream.");
    private void OnInvalidRequest(object? sender, InvalidRequestMessage message) => _media.Fail("Chromecast rejected the audio request: " + message.Reason);
    private void OnMediaError(object? sender, ErrorMessage message) => _media.Fail($"Chromecast media error ({message.DetailedErrorCode}).");
    private void OnMediaStatus(object? sender, MediaStatus status) => _media.Observe(status);
    private void OnDisconnected(object? sender, EventArgs args)
    {
        if (!_expectedDisconnect) _error = "Chromecast connection lost.";
    }
    public async Task ConnectAsync(SpeakerDevice device)
    {
        _expectedDisconnect = true; _error = null;
        await _client.ConnectChromecast(new ChromecastReceiver
        {
            Name = device.Name, DeviceUri = new Uri("http://" + device.Host), Port = device.Port
        });
        _expectedDisconnect = false;
    }
    public async Task<double?> ReadVolumeAsync() => (await _client.ReceiverChannel.GetChromecastStatusAsync())?.Volume?.Level;
    public async Task LaunchAsync() => await _client.LaunchApplicationAsync("CC1AD845");
    public async Task SetVolumeAsync(double level) => await _client.ReceiverChannel.SetVolume(level);
    internal static Media CreateMedia(string url) => new()
    {
        ContentId = url,
        ContentUrl = url,
        ContentType = "application/vnd.apple.mpegurl",
        StreamType = StreamType.Live,
        Duration = -1,
        // MPL receivers need the audio segment format explicitly. The stream
        // contains ID3-timestamped MP3, rather than AAC in a TS container.
        HlsSegmentFormat = HlsSegmentFormat.MP3,
        Metadata = new MusicTrackMetadata { Title = "Musync", Artist = "Live audio" }
    };
    public async Task LoadAsync(string url)
    {
        _media.BeginLoad();
        var status = await _client.MediaChannel.LoadAsync(CreateMedia(url));
        if (status is null) _media.Fail("Chromecast did not accept the audio stream.");
        else _media.Observe(status);
        if (_media.Error is { } error) throw new IOException(error);
    }
    internal static Media CreateFileMedia(string url, double duration) => new()
    {
        ContentId = url, ContentUrl = url, ContentType = "audio/mpeg",
        StreamType = StreamType.Buffered, Duration = duration,
        Metadata = new MusicTrackMetadata { Title = "Musync", Artist = "Local music" }
    };
    public async Task PrepareFileAsync(string url, double duration, double position, CancellationToken token)
    {
        _media.BeginLoad();
        var status = await _client.MediaChannel.LoadAsync(CreateFileMedia(url, duration), autoPlay: false).WaitAsync(token);
        if (status is null) throw new IOException("Chromecast did not preload the music.");
        if (position > 0)
        {
            status = await _client.MediaChannel.SeekAsync(Math.Clamp(position, 0, duration)).WaitAsync(token)
                ?? throw new IOException("Chromecast rejected the start position.");
            status = await _client.MediaChannel.PauseAsync().WaitAsync(token)
                ?? throw new IOException("Chromecast could not hold the prepared music.");
        }
        while (true)
        {
            _media.Observe(status);
            if (_media.Error is { } error) throw new IOException(error);
            if (status.PlayerState == PlayerStateType.Paused) return;
            token.ThrowIfCancellationRequested();
            await Task.Delay(100, token);
            status = await _client.MediaChannel.GetMediaStatusAsync().WaitAsync(token)
                ?? throw new IOException("Chromecast lost the prepared music.");
        }
    }
    public async Task PlayPreparedAsync()
    {
        var status = await _client.MediaChannel.PlayAsync();
        if (status is null) throw new IOException("Chromecast rejected Play.");
        _media.Observe(status);
        if (_media.Error is { } error) throw new IOException(error);
    }
    public async Task PausePreparedAsync()
    {
        var status = await _client.MediaChannel.PauseAsync();
        if (status is null) throw new IOException("Chromecast rejected Pause.");
        _media.Observe(status);
        if (_media.Error is { } error) throw new IOException(error);
    }
    public async Task<double?> ReadPositionAsync() => (await _client.MediaChannel.GetMediaStatusAsync())?.CurrentTime;
    public async Task SeekPreparedAsync(double seconds)
    {
        var status = await _client.MediaChannel.SeekAsync(seconds);
        if (status is null) throw new IOException("Chromecast rejected the synchronization adjustment.");
        _media.Observe(status);
        if (_media.Error is { } error) throw new IOException(error);
    }
    public async Task StopMediaAsync()
    {
        _media.ExpectStop();
        await _client.MediaChannel.StopAsync();
    }
    public Task DisconnectAsync() { _expectedDisconnect = true; _media.ExpectStop(); return _client.DisconnectAsync(); }
    public async ValueTask DisposeAsync()
    {
        _expectedDisconnect = true; _media.ExpectStop();
        _client.Disconnected -= OnDisconnected;
        _client.MediaChannel.LoadFailed -= OnLoadFailed;
        _client.MediaChannel.InvalidRequest -= OnInvalidRequest;
        _client.MediaChannel.ErrorHappened -= OnMediaError;
        _client.MediaChannel.StatusChanged -= OnMediaStatus;
        await _client.Dispose();
    }
}

internal sealed class CastMediaFeedback
{
    private readonly object _gate = new();
    private bool _expectedMedia;
    private string? _error;
    public string? Error { get { lock (_gate) return _error; } }
    public void BeginLoad() { lock (_gate) { _error = null; _expectedMedia = true; } }
    public void ExpectStop() { lock (_gate) _expectedMedia = false; }
    public void Fail(string message) { lock (_gate) { if (_expectedMedia) _error = message; } }
    public void Observe(MediaStatus status)
    {
        if (status.PlayerState == PlayerStateType.Idle && string.Equals(status.IdleReason, "ERROR", StringComparison.OrdinalIgnoreCase))
            Fail("Chromecast stopped playing because the audio stream failed.");
    }
}
