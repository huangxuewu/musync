using System.Diagnostics;

namespace Musync.Core;

public sealed class PlaybackCoordinator : IAsyncDisposable
{
    private readonly IAudioBackend backend;
    private readonly Func<SpeakerDevice, PlaybackSource, IAudioOutput> factory;
    public PlaybackCoordinator(IAudioBackend backend, Func<SpeakerDevice, IAudioOutput> factory)
        : this(backend, (device, _) => factory(device)) { }
    public PlaybackCoordinator(IAudioBackend backend, Func<SpeakerDevice, PlaybackSource, IAudioOutput> factory)
    { this.backend = backend; this.factory = factory; }
    private readonly SemaphoreSlim _gate = new(1);
    private readonly AudioHub _hub = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAudioOutput> _outputs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _outputErrors = new();
    private CancellationTokenSource? _sourceLifetime;
    private Task? _sourceTask;
    private volatile bool _paused;
    private volatile bool _sourceFailed;
    private volatile bool _sourceEnded;
    private volatile bool _preparing;
    private int _readyOutputs;
    private int _preparingOutputs;
    private int _stopVersion;
    private sealed record ControlFailure(IAudioOutput Output, string Message);
    private sealed record ErrorState(CancellationTokenSource? Session, string? PlaybackError, ControlFailure[] Controls);
    private ErrorState _errors = new(null, null, []);
    public bool IsPreparing => _preparing;
    public int ReadyOutputs => Volatile.Read(ref _readyOutputs);
    public int PreparingOutputs => Volatile.Read(ref _preparingOutputs);
    public void CancelPreparation()
    {
        if (!_preparing) return;
        try { _sourceLifetime?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private readonly object _tailGate = new();
    private readonly Stopwatch _tailClock = new();
    private readonly Stopwatch _captureClock = new();
    public bool IsPlaying { get; private set; }
    public bool IsCapture { get; private set; }
    public string? Error
    {
        get
        {
            var state = Volatile.Read(ref _errors);
            return state.PlaybackError ?? state.Controls.LastOrDefault()?.Message;
        }
        private set
        {
            while (true)
            {
                var state = Volatile.Read(ref _errors);
                var next = value is null ? state with { PlaybackError = null, Controls = [] } : state with { PlaybackError = value };
                if (ReferenceEquals(Interlocked.CompareExchange(ref _errors, next, state), state)) return;
            }
        }
    }
    public double Position => IsCapture ? _captureClock.Elapsed.TotalSeconds : backend.Position;
    public double Duration => IsCapture ? 0 : backend.Duration;
    public bool Finished => !IsCapture && _sourceEnded;
    public TimeSpan RequiredTail => _outputs.Values.Select(o => o.EstimatedLatency + TimeSpan.FromMilliseconds(o.Control.DelayMs))
        .DefaultIfEmpty(TimeSpan.Zero).Max();
    public bool TailComplete { get { lock (_tailGate) return Finished && _tailClock.Elapsed >= RequiredTail; } }
    public IReadOnlyDictionary<string, IAudioOutput> Outputs => _outputs;
    public IReadOnlyDictionary<string, string> OutputErrors => _outputErrors;

    public async Task StartAsync(string source, bool capture, IReadOnlyList<(SpeakerDevice Device, SpeakerPreferences Preferences)> devices,
        double masterVolume, double position = 0, bool paused = false)
    {
        var startVersion = Volatile.Read(ref _stopVersion);
        await _gate.WaitAsync();
        try
        {
            await StopInternal();
            if (startVersion != Volatile.Read(ref _stopVersion)) throw new OperationCanceledException();
            _outputErrors.Clear(); IsCapture = capture; _paused = paused; _sourceFailed = false;
            _sourceLifetime = new();
            Volatile.Write(ref _errors, new(_sourceLifetime, null, []));
            _preparing = true; _readyOutputs = 0;
            var token = _sourceLifetime.Token;
            if (startVersion != Volatile.Read(ref _stopVersion)) _sourceLifetime.Cancel();
            token.ThrowIfCancellationRequested();
            var eligible = devices.Where(d => !capture || d.Device.Id != source).ToArray();
            foreach (var unavailable in eligible.Where(d => d.Device.UnsupportedReason is not null))
                RecordError(unavailable.Device, unavailable.Device.UnsupportedReason!);
            foreach (var (device, preferences) in ReceiverRoutes.Choose(eligible))
            {
                try
                {
                    var output = factory(device, new(source, capture, position));
                    Apply(output, preferences, masterVolume);
                    _outputs[device.Id] = output;
                }
                catch (Exception ex) { RecordError(device, ex.Message); }
            }
            if (_outputs.Count == 0)
            {
                if (Error is not null) throw new IOException(Error);
                throw new InvalidOperationException("NoOutputs");
            }
            _preparingOutputs = _outputs.Count;
            _hub.SetPaused(true);
            _hub.SetOutputs(_outputs.Values.Where(o => o.ReceivesPcm).Select(o => o.Buffer));
            IsPlaying = false; _captureClock.Reset();
            var starts = _outputs.Values.Select(async output =>
            {
                try
                {
                    await output.StartAsync(token);
                    if (paused) await output.SetPausedAsync(true);
                    Interlocked.Increment(ref _readyOutputs);
                    return (output, Error: (string?)null);
                }
                catch (Exception ex) { return (output, Error: ex.Message); }
            });
            foreach (var result in await Task.WhenAll(starts))
            {
                if (result.Error is null) continue;
                RecordError(result.output.Device, result.Error);
                _outputs.TryRemove(result.output.Device.Id, out _);
                await DisposeOutput(result.output);
            }
            token.ThrowIfCancellationRequested();
            _hub.SetOutputs(_outputs.Values.Where(o => o.ReceivesPcm).Select(o => o.Buffer));
            if (_outputs.Count == 0) { await StopInternal(); throw new IOException(Error ?? "NoOutputs"); }
            // Prepare every receiver before supplying the first music frame.
            // Slow handshakes must not consume the beginning of a track.
            _hub.Clear(); _captureClock.Restart();
            if (paused) _captureClock.Stop();
            backend.Pause(paused); _hub.SetPaused(paused);
            _sourceTask = Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (capture) await backend.CaptureAsync(source, _hub, token);
                    else
                    {
                        await backend.PlayFileAsync(source, _hub, token, position);
                        if (backend.IsFinished && !token.IsCancellationRequested) await DrainTail(token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Error = ex.Message; _sourceFailed = true; IsPlaying = false; }
            });
            if (!paused) await ReleasePreparedOutputs(token);
            token.ThrowIfCancellationRequested();
            IsPlaying = !paused;
        }
        catch { await StopInternal(); throw; }
        finally { _preparing = false; _gate.Release(); }
    }
    private async Task ReleasePreparedOutputs(CancellationToken token)
    {
        var results = await Task.WhenAll(_outputs.Values.Select(async output =>
        {
            try { await output.BeginPlaybackAsync(token); return (output, Error: (string?)null); }
            catch (Exception ex) { return (output, Error: ex.Message); }
        }));
        token.ThrowIfCancellationRequested();
        foreach (var result in results.Where(r => r.Error is not null))
        {
            RecordError(result.output.Device, result.Error!);
            _outputs.TryRemove(result.output.Device.Id, out _);
            await DisposeOutput(result.output);
        }
        _hub.SetOutputs(_outputs.Values.Where(o => o.ReceivesPcm).Select(o => o.Buffer));
        if (_outputs.IsEmpty) throw new IOException(Error ?? "NoOutputs");
    }
    private async Task DrainTail(CancellationToken token)
    {
        lock (_tailGate)
        {
            _sourceEnded = true; _tailClock.Reset();
            if (!_paused) _tailClock.Start();
        }
        // Fill the common timeline with silence after EOF. Without this, a
        // short track cannot prime an output whose delay exceeds its length.
        // The source position remains at EOF while each receiver drains.
        var silence = new float[882];
        var clock = Stopwatch.StartNew();
        long frames = 0;
        while (!token.IsCancellationRequested)
        {
            if (_paused)
            {
                await Task.Delay(25, token); clock.Restart(); frames = 0; continue;
            }
            _hub.Publish(silence); frames += silence.Length / AudioFormat.Channels;
            var wait = frames / (double)AudioFormat.SampleRate - clock.Elapsed.TotalSeconds;
            if (wait > .001) await Task.Delay(TimeSpan.FromSeconds(wait), token);
            if (wait < -.1) { clock.Restart(); frames = 0; }
        }
    }
    private static void Apply(IAudioOutput output, SpeakerPreferences preferences, double master)
    {
        output.Control.Gain = preferences.Muted ? 0 : (float)(preferences.Volume * master / 10000);
        output.Control.DelayMs = preferences.DelayMs;
    }
    public void Update(string id, SpeakerPreferences preferences, double master)
    {
        if (_outputs.TryGetValue(id, out var output))
        {
            Apply(output, preferences, master);
            _ = RefreshControls(output, _sourceLifetime);
        }
    }
    private async Task RefreshControls(IAudioOutput output, CancellationTokenSource? lifetime)
    {
        try
        {
            await output.UpdateControlsAsync();
            UpdateControlError(output, lifetime, null);
        }
        catch (Exception ex)
        {
            UpdateControlError(output, lifetime, output.Device.Name + ": " + ex.Message);
        }
    }
    private void UpdateControlError(IAudioOutput output, CancellationTokenSource? lifetime, string? message)
    {
        while (true)
        {
            var state = Volatile.Read(ref _errors);
            if (!ReferenceEquals(state.Session, lifetime) || lifetime is not { IsCancellationRequested: false }
                || !_outputs.TryGetValue(output.Device.Id, out var current) || !ReferenceEquals(current, output)) return;
            if (message is null && !state.Controls.Any(e => ReferenceEquals(e.Output, output))) return;
            var remaining = state.Controls.Where(e => !ReferenceEquals(e.Output, output)).ToArray();
            var controls = message is null ? remaining : [.. remaining, new ControlFailure(output, message)];
            var next = state with { Controls = controls };
            // An immutable snapshot keeps a retry from erasing another receiver's
            // error, and a stopped session cannot accept a late control response.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _errors, next, state), state)) return;
        }
    }
    private void EndControlSession()
    {
        while (true)
        {
            var state = Volatile.Read(ref _errors);
            var next = new ErrorState(null, state.PlaybackError ?? state.Controls.LastOrDefault()?.Message, []);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _errors, next, state), state)) return;
        }
    }
    public async Task PauseAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            if (_sourceLifetime is null || _outputs.IsEmpty || _paused == paused) return;
            if (!paused) { _preparing = true; _readyOutputs = 0; _preparingOutputs = _outputs.Count; }
            if (paused)
            {
                // File publication and Pause share the backend's reader lock.
                // Freeze it first so the hub does not discard a final music frame.
                backend.Pause(true); _hub.SetPaused(true); IsPlaying = false; _captureClock.Stop();
                lock (_tailGate) { _paused = true; _tailClock.Stop(); }
            }
            var results = await Task.WhenAll(_outputs.Values.Select(async output =>
            {
                try
                {
                    if (paused) await output.SetPausedAsync(true);
                    else { await output.PrepareResumeAsync(_sourceLifetime.Token); Interlocked.Increment(ref _readyOutputs); }
                    return (output, Error: (string?)null);
                }
                catch (Exception ex) { return (output, Error: ex.Message); }
            }));
            foreach (var result in results)
            {
                if (result.Error is null) continue;
                RecordError(result.output.Device, result.Error);
                _outputs.TryRemove(result.output.Device.Id, out _);
                await DisposeOutput(result.output);
            }
            _sourceLifetime.Token.ThrowIfCancellationRequested();
            _hub.SetOutputs(_outputs.Values.Where(o => o.ReceivesPcm).Select(o => o.Buffer));
            // Live capture resumes the present sound rather than replaying audio
            // from before Pause. Local files retain their unread music instead.
            if (paused && IsCapture) _hub.Clear();
            if (_outputs.IsEmpty)
            {
                await StopInternal();
                throw new IOException(Error ?? "NoOutputs");
            }
            if (!paused)
            {
                await ReleasePreparedOutputs(_sourceLifetime.Token);
                // A live receiver may need a new media queue on resume. Keep the
                // decoder/capture frozen until every remaining output is ready.
                _hub.SetPaused(false); backend.Pause(false); IsPlaying = true; _captureClock.Start();
                lock (_tailGate)
                {
                    _paused = false;
                    if (_sourceEnded)
                    {
                        // A fresh network session has a fresh transport lead-in.
                        if (_outputs.Values.Any(o => o.Device.Transport is SpeakerTransport.AirPlay or SpeakerTransport.Chromecast)) _tailClock.Reset();
                        _tailClock.Start();
                    }
                }
            }
        }
        finally { _preparing = false; _gate.Release(); }
    }
    public void Seek(double seconds) { backend.Seek(seconds); _hub.Clear(); }
    public async Task CheckOutputsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_sourceLifetime is null) return;
            var failed = _outputs.Values.Where(o => o.Error is not null).ToArray();
            foreach (var output in failed)
            {
                RecordError(output.Device, output.Error!);
                _outputs.TryRemove(output.Device.Id, out _);
            }
            // Detach failed buffers before awaiting network shutdown, so the
            // producer keeps feeding only the receivers that still work.
            if (failed.Length > 0)
            {
                _hub.SetOutputs(_outputs.Values.Where(o => o.ReceivesPcm).Select(o => o.Buffer));
                await Task.WhenAll(failed.Select(DisposeOutput));
            }
            if (_outputs.IsEmpty || _sourceFailed) await StopInternal();
        }
        finally { _gate.Release(); }
    }
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _stopVersion);
        EndControlSession();
        try { _sourceLifetime?.Cancel(); } catch (ObjectDisposedException) { }
        await _gate.WaitAsync();
        try { await StopInternal(); } finally { _gate.Release(); }
    }
    private async Task StopInternal()
    {
        EndControlSession();
        IsPlaying = false; _captureClock.Stop(); _hub.SetPaused(true); _sourceLifetime?.Cancel();
        if (_sourceTask is not null) await _sourceTask;
        _sourceTask = null;
        _hub.Clear();
        _hub.SetOutputs([]);
        var outputs = _outputs.Values.ToArray(); _outputs.Clear();
        await Task.WhenAll(outputs.Select(DisposeOutput));
        _sourceLifetime?.Dispose(); _sourceLifetime = null;
        _readyOutputs = 0; _preparingOutputs = 0;
        lock (_tailGate) { _paused = false; _sourceEnded = false; _tailClock.Reset(); }
    }
    private async Task DisposeOutput(IAudioOutput output)
    {
        try { await output.DisposeAsync(); }
        catch (Exception ex)
        {
            RecordError(output.Device, ex.Message);
        }
    }
    private void RecordError(SpeakerDevice device, string message)
    {
        Error = device.Name + ": " + message;
        _outputErrors[device.Id] = message;
    }
    public async ValueTask DisposeAsync() { await StopAsync(); await backend.DisposeAsync(); _gate.Dispose(); }
}
