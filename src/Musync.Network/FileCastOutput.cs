using Musync.Core;
using System.Diagnostics;

namespace Musync.Network;

// Finite music can be preloaded while the receiver is paused. Live capture
// keeps CastOutput's streaming path because future audio is not available.
public sealed class FileCastOutput : IAudioOutput
{
    private readonly LocalStreamServer _server;
    private readonly IAudioFilePreparer _preparer;
    private readonly PlaybackSource _source;
    private readonly ICastSession _session;
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _scheduledPlay;
    private Task? _playTask;
    private PreparedAudioFile? _media;
    private string? _key;
    private double? _originalVolume;
    private float? _appliedVolume;
    private int _appliedDelay;
    private int _remainingDelay;
    private readonly Stopwatch _delayClock = new();
    private volatile string? _error;
    private bool _connected;
    private bool _loaded;
    private bool _paused = true;
    private int _disposed;
    public FileCastOutput(SpeakerDevice device, PlaybackSource source, LocalStreamServer server, IAudioFilePreparer preparer)
        : this(device, source, server, preparer, new CastSession()) { }
    internal FileCastOutput(SpeakerDevice device, PlaybackSource source, LocalStreamServer server, IAudioFilePreparer preparer, ICastSession session)
    { Device = device; _source = source; _server = server; _preparer = preparer; _session = session; }
    public SpeakerDevice Device { get; }
    public AudioRingBuffer Buffer { get; } = new();
    public OutputControl Control { get; } = new();
    public bool ReceivesPcm => false;
    public string? Error => _error ?? (_connected ? _session.Error : null);
    // Retain the live path's conservative EOF allowance until finite-file
    // receiver timing has been measured. This does not delay the initial Play.
    public TimeSpan EstimatedLatency => TimeSpan.FromSeconds(5);
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await StopInternal();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _lifetime.Token; _error = null; _paused = true;
            _media = await _preparer.PrepareAsync(_source.Path, token);
            await _server.StartAsync(token);
            await _session.ConnectAsync(Device).WaitAsync(TimeSpan.FromSeconds(10), token); _connected = true;
            _originalVolume = await _session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            await _session.LaunchAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
            await ApplyVolume(token);
            _key = _server.AddFile(_media); _loaded = true; _appliedDelay = _remainingDelay = Control.DelayMs;
            await _session.PrepareFileAsync(_server.FileUrl(Device.Host!, _key), _media.Duration,
                _source.Position, token).WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        catch { await StopInternal(); throw; }
        finally { _gate.Release(); }
    }
    public async Task BeginPlaybackAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_loaded) throw new InvalidOperationException("Chromecast music is not prepared.");
            _paused = false;
            SchedulePlay(_remainingDelay);
            if (_remainingDelay == 0) await _playTask!;
            if (_error is { } error) throw new IOException(error);
        }
        finally { _gate.Release(); }
    }
    public Task PrepareResumeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_loaded) throw new InvalidOperationException("Chromecast music is not prepared.");
        return Task.CompletedTask;
    }
    private void SchedulePlay(int delayMs)
    {
        _scheduledPlay?.Cancel(); _scheduledPlay?.Dispose();
        _scheduledPlay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token);
        _remainingDelay = delayMs; _delayClock.Restart();
        _playTask = PlayAsync(delayMs, _scheduledPlay.Token);
    }
    private async Task PlayAsync(int delayMs, CancellationToken token)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, token);
            token.ThrowIfCancellationRequested();
            _remainingDelay = 0; _delayClock.Stop();
            await _session.PlayPreparedAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _error = ex.Message; }
    }
    public async Task SetPausedAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_loaded || _paused == paused) return;
            var token = _lifetime!.Token;
            if (paused)
            {
                _remainingDelay = Math.Max(0, _remainingDelay - (int)_delayClock.ElapsedMilliseconds); _delayClock.Reset();
                _scheduledPlay?.Cancel();
                if (_playTask is not null) await _playTask;
                await _session.PausePreparedAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            else
            {
                SchedulePlay(_remainingDelay);
                if (_remainingDelay == 0) await _playTask!;
                if (_error is { } error) throw new IOException(error);
            }
            _paused = paused;
        }
        finally { _gate.Release(); }
    }
    public async Task UpdateControlsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_connected) return;
            var token = _lifetime!.Token;
            await ApplyVolume(token);
            if (_loaded && Control.DelayMs != _appliedDelay)
            {
                var seconds = await ReadAdjustmentPositionAsync(token);
                // A status request can outlive several slider changes. Apply
                // the latest desired value, or leave playback alone if undone.
                var delay = Control.DelayMs;
                if (delay != _appliedDelay)
                {
                    var pending = Math.Max(0, _remainingDelay - (int)_delayClock.ElapsedMilliseconds);
                    var target = seconds - (delay - _appliedDelay + pending) / 1000d;
                    _scheduledPlay?.Cancel();
                    if (_playTask is not null) await _playTask;
                    await _session.SeekPreparedAsync(Math.Clamp(target, 0, _media!.Duration)).WaitAsync(TimeSpan.FromSeconds(5), token);
                    _remainingDelay = target < 0 ? (int)Math.Ceiling(-target * 1000) : 0; _delayClock.Reset();
                    if (!_paused)
                    {
                        if (_remainingDelay > 0) await _session.PausePreparedAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
                        SchedulePlay(_remainingDelay);
                    }
                    _appliedDelay = delay;
                }
            }
        }
        finally { _gate.Release(); }
    }
    private async Task<double> ReadAdjustmentPositionAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var position = await _session.ReadPositionAsync().WaitAsync(deadline.Token);
                if (position is { } seconds && double.IsFinite(seconds) && seconds >= 0) return seconds;
                if (attempt < 2) await Task.Delay(100 * (attempt + 1), deadline.Token);
            }
            throw new IOException("Chromecast did not report a valid playback position. Try adjusting the delay again.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Chromecast playback position timed out. Try adjusting the delay again."); }
    }
    private async Task ApplyVolume(CancellationToken token)
    {
        while (_appliedVolume != Control.Gain)
        {
            var gain = Control.Gain;
            await _session.SetVolumeAsync(gain).WaitAsync(TimeSpan.FromSeconds(5), token);
            _appliedVolume = gain;
        }
    }
    public async Task StopAsync()
    {
        _lifetime?.Cancel();
        await _gate.WaitAsync();
        try { await StopInternal(); }
        finally { _gate.Release(); }
    }
    private async Task StopInternal()
    {
        _lifetime?.Cancel(); _scheduledPlay?.Cancel();
        if (_playTask is not null) await _playTask;
        _playTask = null; _scheduledPlay?.Dispose(); _scheduledPlay = null;
        if (_key is not null) _server.RemoveFile(_key);
        _key = null;
        if (_loaded && !_session.ConnectionLost) try { await _session.StopMediaAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _loaded = false;
        if (_originalVolume is { } level && !_session.ConnectionLost)
            try { await _session.SetVolumeAsync(level).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _originalVolume = null; _appliedVolume = null;
        if (_session.ConnectionLost) _error ??= _session.Error;
        if (_connected) try { await _session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _connected = false; _paused = true; _remainingDelay = 0; _delayClock.Reset();
        if (_media is { } media) { _media = null; await media.DisposeAsync(); }
        _lifetime?.Dispose(); _lifetime = null;
        Buffer.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await StopAsync();
            await _session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _gate.Dispose(); }
    }
}
