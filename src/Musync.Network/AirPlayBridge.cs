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
    private readonly ConcurrentDictionary<string, BridgeRun> _receivers = new();
    private volatile BridgeRun? _run;
    private int _disposed;
    private sealed class BridgeRun
    {
        public BridgeRun(Process process) { Process = process; Token = Lifetime.Token; }
        public Process Process { get; }
        public AirPlayBridgeFeedback Feedback { get; } = new();
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationToken Token { get; }
        public Task Reader { get; set; } = Task.CompletedTask;
        public Task Diagnostics { get; set; } = Task.CompletedTask;
        public bool IsRunning => !Token.IsCancellationRequested && !Process.HasExited;
        public void Stop()
        {
            Feedback.Stop();
            try { Lifetime.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
    public string? Error(string id) => _receivers.TryGetValue(id, out var owner)
        ? owner.Feedback.Error(id) : _run?.Feedback.Error(id);
    private async Task<BridgeRun> EnsureStarted(CancellationToken token)
    {
        await _start.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_run is { IsRunning: true } active) return active;
            if (_run is { } previous) { await StopRun(previous); _run = null; }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(directory, "bridge.mjs"))) throw new FileNotFoundException("AirPlay bridge is missing.");
            var info = new ProcessStartInfo(nodePath) { WorkingDirectory = directory, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Path.Combine(directory, "bridge.mjs"));
            var run = new BridgeRun(Process.Start(info) ?? throw new IOException("Could not start AirPlay bridge."));
            _run = run;
            run.Reader = Task.Run(() => ReadOutput(run));
            run.Diagnostics = Task.Run(() => ReadDiagnostics(run));
            return run;
        }
        finally { _start.Release(); }
    }
    private static async Task ReadOutput(BridgeRun run)
    {
        try
        {
            while (await run.Process.StandardOutput.ReadLineAsync(run.Token) is { } line)
            {
                try { using var json = JsonDocument.Parse(line); run.Feedback.ObserveReply(json.RootElement); }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (run.Token.IsCancellationRequested) { }
        catch (Exception ex) { run.Feedback.ReportError("*", ex.Message); }
        finally { run.Stop(); }
    }
    private static async Task ReadDiagnostics(BridgeRun run)
    {
        try
        {
            // Drain stderr so diagnostics cannot block the protocol process.
            while (await run.Process.StandardError.ReadLineAsync(run.Token) is { } line)
                if (line.Length > 0) run.Feedback.ReportError("*", line[..Math.Min(300, line.Length)]);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (run.Token.IsCancellationRequested) { }
        catch (Exception ex) { run.Feedback.ReportError("*", ex.Message); }
    }
    public async Task OpenAsync(SpeakerDevice device, float gain, CancellationToken token)
    {
        var run = await EnsureStarted(token);
        _receivers[device.Id] = run;
        run.Feedback.ClearDeviceError(device.Id);
        await RequestAsync(run, new { op = "open", id = device.Id, host = device.Host, port = device.Port, properties = device.Properties, volume = gain * 100 }, token);
    }
    private BridgeRun ReceiverRun(string id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _receivers.TryGetValue(id, out var run) ? run : throw new IOException("AirPlay session is not open.");
    }
    public Task SetVolumeAsync(string id, float gain, CancellationToken token = default)
        => RequestAsync(ReceiverRun(id), new { op = "volume", id, volume = gain * 100 }, token);
    private async Task RequestAsync(BridgeRun run, object command, CancellationToken token)
    {
        var request = Guid.NewGuid().ToString("N");
        var values = JsonSerializer.SerializeToNode(command)!.AsObject(); values["request"] = request;
        var reply = run.Feedback.RegisterRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, run.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try { await WriteAsync(run, values, deadline.Token); await reply.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && run.Token.IsCancellationRequested)
        { throw new IOException("AirPlay bridge stopped."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("AirPlay bridge request timed out."); }
        finally { run.Feedback.ForgetRequest(request); if (reply.IsFaulted) _ = reply.Exception; }
    }
    private async Task WriteAsync(BridgeRun run, object command, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, run.Token);
        var entered = false;
        try
        {
            await _write.WaitAsync(lifetime.Token); entered = true;
            if (!run.IsRunning) throw new IOException("AirPlay bridge is not running.");
            await run.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), lifetime.Token);
            await run.Process.StandardInput.FlushAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && run.Token.IsCancellationRequested)
        { throw new IOException("AirPlay bridge stopped."); }
        finally { if (entered) _write.Release(); }
    }
    public Task SendAsync(string id, byte[] pcm, CancellationToken token = default)
        => WriteAsync(ReceiverRun(id), new { op = "pcm", id, data = Convert.ToBase64String(pcm) }, token);
    public async Task CloseAsync(string id, CancellationToken token = default)
    {
        if (!_receivers.TryGetValue(id, out var run)) return;
        try
        {
            if (!run.IsRunning) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await RequestAsync(run, new { op = "close", id }, deadline.Token);
        }
        catch (Exception) { }
        finally { _receivers.TryRemove(new KeyValuePair<string, BridgeRun>(id, run)); }
    }
    private async Task StopRun(BridgeRun run)
    {
        run.Stop();
        var entered = await _write.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Closing the pipe itself avoids a synchronous writer flush when
            // an unresponsive helper has stopped reading stdin.
            if (entered) run.Process.StandardInput.BaseStream.Close();
            else if (!run.Process.HasExited) run.Process.Kill(true);
        }
        finally { if (entered) _write.Release(); }
        try
        {
            try { await run.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                if (!run.Process.HasExited) run.Process.Kill(true);
                await run.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await Task.WhenAll(run.Reader, run.Diagnostics);
        }
        finally { run.Lifetime.Dispose(); run.Process.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _start.WaitAsync();
        try { if (_run is { } run) await StopRun(run); _run = null; _receivers.Clear(); }
        finally { _start.Release(); }
        // Queued callers still need these managed gates to settle. No wait
        // handle is created; the gates are collected with the disposed bridge.
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
