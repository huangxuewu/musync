using Musync.Core;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using Sharpcaster.Messages.Media;

namespace Musync.Network;

internal interface ICastSession : IAsyncDisposable
{
    string? Error { get; }
    bool ConnectionLost => false;
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

internal sealed partial class CastSession : ICastSession
{
    private readonly ChromecastClient _client = new();
    private readonly CastConnectionFeedback _connection = new();
    private readonly CastMediaFeedback _media = new();
    public string? Error => _connection.Error ?? _media.Error;
    public bool ConnectionLost => _connection.ConnectionLost;
    public CastSession()
    {
        _client.Disconnected += OnDisconnected;
        _client.MediaChannel.StatusChanged += OnMediaStatus;
    }
    private void OnMediaStatus(object? sender, MediaStatus status) => _media.Observe(status);
    private void OnDisconnected(object? sender, EventArgs args)
    {
        _connection.ObserveDisconnect();
    }
    public async Task ConnectAsync(SpeakerDevice device)
    {
        _connection.BeginConnect();
        await _client.ConnectChromecast(new ChromecastReceiver
        {
            Name = device.Name, DeviceUri = new Uri("http://" + device.Host), Port = device.Port
        });
        _connection.Connected();
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
        await LoadMediaAsync(CreateMedia(url), autoPlay: true, CancellationToken.None);
    }
    internal static Media CreateFileMedia(string url, double duration) => new()
    {
        ContentId = url, ContentUrl = url, ContentType = "audio/mpeg",
        StreamType = StreamType.Buffered, Duration = duration,
        Metadata = new MusicTrackMetadata { Title = "Musync", Artist = "Local music" }
    };
    public async Task PrepareFileAsync(string url, double duration, double position, CancellationToken token)
    {
        var (binding, status) = await LoadMediaAsync(CreateFileMedia(url, duration), autoPlay: false, token);
        if (position > 0)
        {
            status = await SendOwnedMediaAsync(binding, new SeekMessage { MediaSessionId = binding.Session, CurrentTime = Math.Clamp(position, 0, duration) },
                Sharpcaster.Extensions.SharpcasteSerializationContext.Default.SeekMessage, token)
                ?? throw new IOException("Chromecast rejected the start position.");
            status = await SendOwnedMediaAsync(binding, new PauseMessage { MediaSessionId = binding.Session },
                Sharpcaster.Extensions.SharpcasteSerializationContext.Default.PauseMessage, token)
                ?? throw new IOException("Chromecast could not hold the prepared music.");
        }
        while (true)
        {
            _media.Observe(status);
            if (_media.Error is { } error) throw new IOException(error);
            if (status.PlayerState == PlayerStateType.Paused) return;
            token.ThrowIfCancellationRequested();
            await Task.Delay(100, token);
            status = await ReadOwnedStatusAsync(binding, token)
                ?? throw new IOException("Chromecast lost the prepared music.");
        }
    }
    public async Task PlayPreparedAsync()
    {
        var binding = RequireMediaBinding();
        var status = await SendOwnedMediaAsync(binding, new PlayMessage { MediaSessionId = binding.Session },
            Sharpcaster.Extensions.SharpcasteSerializationContext.Default.PlayMessage);
        if (status is null) throw new IOException("Chromecast rejected Play.");
        _media.Observe(status);
        if (_media.Error is { } error) throw new IOException(error);
    }
    public async Task PausePreparedAsync()
    {
        var binding = RequireMediaBinding();
        var status = await SendOwnedMediaAsync(binding, new PauseMessage { MediaSessionId = binding.Session },
            Sharpcaster.Extensions.SharpcasteSerializationContext.Default.PauseMessage);
        if (status is null) throw new IOException("Chromecast rejected Pause.");
        _media.Observe(status);
        if (_media.Error is { } error) throw new IOException(error);
    }
    public async Task<double?> ReadPositionAsync() => (await ReadOwnedStatusAsync(RequireMediaBinding()))?.CurrentTime;
    public async Task SeekPreparedAsync(double seconds)
    {
        var binding = RequireMediaBinding();
        var status = await SendOwnedMediaAsync(binding, new SeekMessage { MediaSessionId = binding.Session, CurrentTime = seconds },
            Sharpcaster.Extensions.SharpcasteSerializationContext.Default.SeekMessage);
        if (status is null) throw new IOException("Chromecast rejected the synchronization adjustment.");
        _media.Observe(status);
        if (_media.Error is { } error) throw new IOException(error);
    }
    public async Task StopMediaAsync()
    {
        var binding = _media.StopBinding();
        if (binding is { } owned)
            await SendOwnedMediaAsync(owned, new StopMediaMessage { MediaSessionId = owned.Session },
                Sharpcaster.Extensions.SharpcasteSerializationContext.Default.StopMediaMessage, observe: false);
    }
    public Task DisconnectAsync() { _connection.ExpectDisconnect(); _media.ExpectStop(); return _client.DisconnectAsync(); }
    public async ValueTask DisposeAsync()
    {
        _connection.ExpectDisconnect(); _media.ExpectStop();
        _client.Disconnected -= OnDisconnected;
        _client.MediaChannel.StatusChanged -= OnMediaStatus;
        // Disconnect also cancels pending requests and stops the heartbeat.
        // This is needed after an incomplete Connect, before an output could
        // mark itself connected and run its normal Stop/Disconnect path.
        await _client.DisconnectAsync();
    }
}

// Transport loss and media rejection have different teardown requirements.
// A receiver can reject audio while its connection still accepts Stop/volume.
internal sealed class CastConnectionFeedback
{
    private volatile bool _expectedDisconnect = true;
    private volatile string? _error;
    public string? Error => _error;
    public bool ConnectionLost => _error is not null;
    public void BeginConnect() { _expectedDisconnect = true; _error = null; }
    public void Connected() => _expectedDisconnect = false;
    public void ExpectDisconnect() => _expectedDisconnect = true;
    public void ObserveDisconnect()
    {
        if (!_expectedDisconnect) _error = "Chromecast connection lost.";
    }
}

internal sealed class CastMediaFeedback
{
    private const string StreamFailure = "Chromecast stopped playing because the audio stream failed.";
    private readonly object _gate = new();
    private bool _expectedMedia;
    private long _loadVersion;
    private long? _mediaSessionId;
    private string? _transportId;
    private readonly HashSet<long?> _pendingFailures = [];
    private string? _error;
    public string? Error { get { lock (_gate) return _error; } }
    public CastMediaBinding? Binding { get { lock (_gate) return CurrentBinding(); } }
    private CastMediaBinding? CurrentBinding() => _expectedMedia && _mediaSessionId is { } session && _transportId is { } transport
        ? new(_loadVersion, session, transport) : null;
    public bool IsCurrent(long load) { lock (_gate) return _expectedMedia && _loadVersion == load; }
    public long BeginLoad(string? transportId = null)
    {
        lock (_gate)
        {
            _error = null; _expectedMedia = true; _mediaSessionId = null; _transportId = transportId; _pendingFailures.Clear();
            return ++_loadVersion;
        }
    }
    public void ExpectStop() { lock (_gate) { _expectedMedia = false; _pendingFailures.Clear(); } }
    public CastMediaBinding? StopBinding()
    {
        lock (_gate)
        {
            var binding = CurrentBinding(); _expectedMedia = false; _pendingFailures.Clear(); return binding;
        }
    }
    public void Fail(string message) { lock (_gate) { if (_expectedMedia) _error = message; } }
    public void Fail(long load, string message)
    { lock (_gate) { if (_expectedMedia && load == _loadVersion) _error = message; } }
    public void AcceptLoad(long load, MediaStatus status)
    {
        lock (_gate)
        {
            if (!_expectedMedia || load != _loadVersion) return;
            // Bind to the response to our LOAD, never an unsolicited broadcast
            // that can still describe a previous playback during this wait.
            _mediaSessionId = status.MediaSessionId;
            if (_pendingFailures.Contains(_mediaSessionId)) _error ??= StreamFailure;
            _pendingFailures.Clear();
            ObserveError(status);
        }
    }
    public void Observe(MediaStatus status)
    {
        lock (_gate)
        {
            if (!_expectedMedia) return;
            if (_mediaSessionId is null)
            {
                // An actual failure can precede its LOAD response. Keep only
                // its session ID until the response identifies our playback.
                if (IsFailure(status)) _pendingFailures.Add(status.MediaSessionId);
                return;
            }
            if (status.MediaSessionId != _mediaSessionId) return;
            ObserveError(status);
        }
    }
    private void ObserveError(MediaStatus status)
    {
        if (IsFailure(status)) _error = StreamFailure;
    }
    private static bool IsFailure(MediaStatus status) => status.PlayerState == PlayerStateType.Idle
        && string.Equals(status.IdleReason, "ERROR", StringComparison.OrdinalIgnoreCase);
}
