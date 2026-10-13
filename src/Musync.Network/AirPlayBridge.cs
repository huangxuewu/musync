using Musync.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Musync.Network;

internal interface IAirPlayBridge
{
    string? Error(string id);
    Task OpenAsync(SpeakerDevice device, float gain, CancellationToken token);
    Task SetVolumeAsync(string id, float gain, CancellationToken token = default);
    Task SendAsync(string id, byte[] pcm, CancellationToken token = default);
    Task CloseAsync(string id, CancellationToken token = default);
}

public sealed class AirPlayBridge(string directory, string nodePath) : IAsyncDisposable, IAirPlayBridge
{
    private readonly SemaphoreSlim _start = new(1);
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
    private readonly ConcurrentDictionary<string, string> _errors = new();
    private Process? _process;
    private Task? _reader;
    public string? Error(string id) => _errors.GetValueOrDefault(id) ?? _errors.GetValueOrDefault("*");
    private async Task EnsureStarted(CancellationToken token)
    {
        await _start.WaitAsync(token);
        try
        {
            if (_process is { HasExited: false }) return;
            if (!File.Exists(Path.Combine(directory, "bridge.mjs"))) throw new FileNotFoundException("AirPlay bridge is missing.");
            var info = new ProcessStartInfo(nodePath) { WorkingDirectory = directory, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Path.Combine(directory, "bridge.mjs"));
            _process = Process.Start(info) ?? throw new IOException("Could not start AirPlay bridge.");
            var process = _process;
            _errors.Clear();
            _reader = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        ObserveReply(json.RootElement);
                    }
                    catch (JsonException) { }
                }
                foreach (var completion in _pending.Values) completion.TrySetException(new IOException("AirPlay bridge stopped."));
            });
            _ = Task.Run(async () =>
            {
                // Drain stderr so diagnostics cannot block the protocol process.
                while (await process.StandardError.ReadLineAsync() is { } line)
                    if (line.Length > 0) _errors["*"] = line[..Math.Min(300, line.Length)];
            });
        }
        finally { _start.Release(); }
    }
    internal void ObserveReply(JsonElement root)
    {
        var error = root.TryGetProperty("error", out var value) ? value.GetString() : null;
        if (root.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.String)
        {
            // A cancelled request can still receive a reply. Its error belongs
            // to that request, rather than a receiver's newer playback session.
            if (_pending.TryRemove(request.GetString()!, out var completion))
            {
                if (error is null) completion.TrySetResult(); else completion.TrySetException(new IOException(error));
            }
        }
        else if (error is not null) _errors[root.TryGetProperty("id", out var id) ? id.GetString() ?? "*" : "*"] = error;
    }
    public async Task OpenAsync(SpeakerDevice device, float gain, CancellationToken token)
    {
        await EnsureStarted(token);
        _errors.TryRemove(device.Id, out _);
        await RequestAsync(new { op = "open", id = device.Id, host = device.Host, port = device.Port, properties = device.Properties, volume = gain * 100 }, token);
    }
    public Task SetVolumeAsync(string id, float gain, CancellationToken token = default)
        => RequestAsync(new { op = "volume", id, volume = gain * 100 }, token);
    private async Task RequestAsync(object command, CancellationToken token)
    {
        var request = Guid.NewGuid().ToString("N");
        var values = JsonSerializer.SerializeToNode(command)!.AsObject(); values["request"] = request;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request] = completion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try { await WriteAsync(values, deadline.Token); await completion.Task.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("AirPlay bridge request timed out."); }
        finally { _pending.TryRemove(request, out _); }
    }
    private async Task WriteAsync(object command, CancellationToken token)
    {
        await _write.WaitAsync(token);
        try
        {
            if (_process is null || _process.HasExited) throw new IOException("AirPlay bridge is not running.");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
        }
        finally { _write.Release(); }
    }
    public Task SendAsync(string id, byte[] pcm, CancellationToken token = default)
        => WriteAsync(new { op = "pcm", id, data = Convert.ToBase64String(pcm) }, token);
    public async Task CloseAsync(string id, CancellationToken token = default)
    {
        if (_process is not { HasExited: false }) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await RequestAsync(new { op = "close", id }, deadline.Token); } catch (Exception) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { _process.Kill(true); }
            if (_reader is not null) await _reader;
            _process.Dispose();
        }
        _start.Dispose(); _write.Dispose();
    }
}

public sealed class AirPlay2Output : IAudioOutput
{
    private readonly IAirPlayBridge _bridge;
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationToken _sessionToken;
    private bool _active, _paused, _opened;
    private float _appliedGain;
    private int _disposed;
    public AirPlay2Output(SpeakerDevice device, AirPlayBridge bridge) : this(device, (IAirPlayBridge)bridge) { }
    internal AirPlay2Output(SpeakerDevice device, IAirPlayBridge bridge) { Device = device; _bridge = bridge; }
    public SpeakerDevice Device { get; }
    public AudioRingBuffer Buffer { get; } = new();
    public OutputControl Control { get; } = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private volatile string? _error;
    public string? Error => _error ?? (_opened ? _bridge.Error(Device.Id) : null);
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await EndSession(); _active = false; _paused = false; _error = null;
            Buffer.SetTransportPaused(true);
            _sessionToken = cancellationToken;
            try { await BeginSession(cancellationToken); _active = true; Buffer.SetTransportPaused(false); }
            catch { await EndSession(); throw; }
        }
        finally { _gate.Release(); }
    }
    private async Task BeginSession(CancellationToken token)
    {
        var gain = Control.Gain;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var lifetime = _lifetime;
        // Mark an attempted open for cleanup even if cancellation arrives after
        // the helper accepted it but before its response reached this process.
        _opened = true;
        await _bridge.OpenAsync(Device, gain, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        _appliedGain = gain;
        _worker = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            var samples = new float[704];
            long packet = 0;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var wait = packet * 352.0 / AudioFormat.SampleRate - clock.Elapsed.TotalSeconds;
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), lifetime.Token);
                    Buffer.SetDelay(Control.DelayMs);
                    Buffer.Read(samples, Control.Gain == 0 ? 0 : 1);
                    await _bridge.SendAsync(Device.Id, PcmEncoding.ToInt16(samples), lifetime.Token);
                    packet++;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { _error = ex.Message; }
        });
    }
    public async Task SetPausedAsync(bool paused)
    {
        if (paused) CancelLifetime();
        await _gate.WaitAsync();
        try
        {
            if (!_active || _paused == paused) return;
            Buffer.SetTransportPaused(true);
            if (paused) await EndSession(preserveBuffer: true);
            else
            {
                try { await BeginSession(_sessionToken); }
                catch { await EndSession(); throw; }
            }
            _paused = paused;
            Buffer.SetTransportPaused(paused);
        }
        finally { _gate.Release(); }
    }
    public async Task UpdateControlsAsync()
    {
        await _gate.WaitAsync();
        var lifetime = _lifetime;
        try
        {
            while (_opened && _appliedGain != Control.Gain)
            {
                var gain = Control.Gain;
                await _bridge.SetVolumeAsync(Device.Id, gain, lifetime!.Token);
                _appliedGain = gain;
            }
        }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true) { }
        finally { _gate.Release(); }
    }
    public async Task StopAsync()
    {
        CancelLifetime();
        await _gate.WaitAsync();
        try { _active = false; _paused = false; await EndSession(); }
        finally { _gate.Release(); }
    }
    private void CancelLifetime() { try { _lifetime?.Cancel(); } catch (ObjectDisposedException) { } }
    private async Task EndSession(bool preserveBuffer = false)
    {
        CancelLifetime();
        if (_worker is not null) await _worker;
        _worker = null;
        try { if (_opened) await _bridge.CloseAsync(Device.Id); }
        finally { _opened = false; _lifetime?.Dispose(); _lifetime = null; if (!preserveBuffer) Buffer.Clear(); }
    }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await StopAsync(); }
}
