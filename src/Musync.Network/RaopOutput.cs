using Musync.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Musync.Network;

public sealed class RaopOutput : IAudioOutput
{
    private RaopRtspConnection? _rtsp;
    private readonly SemaphoreSlim _lifecycle = new(1);
    private UdpClient? _audio, _control, _timing;
    private CancellationTokenSource? _lifetime;
    private Task[] _workers = [];
    private string? _session;
    private TimeSpan _keepAliveInterval = TimeSpan.FromSeconds(20);
    private string _keepAliveMethod = "GET_PARAMETER";
    private int _disposed;
    private CancellationToken _sessionToken;
    private bool _active, _paused;
    private volatile string? _error;
    private string _uri = "";
    private IPEndPoint? _audioEndpoint, _controlEndpoint;
    private readonly object _cacheGate = new();
    private readonly Dictionary<ushort, byte[]> _cache = new();
    private uint _latency = 11025;
    public RaopOutput(SpeakerDevice device) { Device = device; Buffer.SetDelay(0); }
    public SpeakerDevice Device { get; }
    public AudioRingBuffer Buffer { get; } = new();
    public OutputControl Control { get; } = new();
    public string? Error => _error;
    public TimeSpan EstimatedLatency => TimeSpan.FromSeconds(.3 + _latency / (double)AudioFormat.SampleRate);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_lifetime is not null) await StopInternal();
            Buffer.SetTransportPaused(true);
            _active = false; _paused = false; _sessionToken = cancellationToken;
            await StartCore(cancellationToken);
            _active = true;
            Buffer.SetTransportPaused(false);
        }
        catch { await StopInternal(); throw; }
        finally { _lifecycle.Release(); }
    }
    private async Task StartCore(CancellationToken cancellationToken)
    {
        if (Device.UnsupportedReason is not null) throw new NotSupportedException(Device.UnsupportedReason);
        _error = null; _session = null; _latency = 11025; _keepAliveMethod = "GET_PARAMETER";
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _lifetime.Token;
        _rtsp = new RaopRtspConnection();
        await _rtsp.ConnectAsync(Device.Host!, Device.Port, token);
        _audio = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _control = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _timing = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var local = _rtsp.LocalAddress;
        var address = _rtsp.RemoteAddress;
        var id = RandomNumberGenerator.GetInt32(100000000, int.MaxValue);
        _uri = $"rtsp://{local}/{id}";
        var options = await Request("OPTIONS", "*", "", null, token);
        if (options.TryGetValue("Public", out var supported)
            && !supported.Split(',').Any(s => s.Trim().Equals("GET_PARAMETER", StringComparison.OrdinalIgnoreCase)))
            _keepAliveMethod = "OPTIONS";
        var sdp = $"v=0\r\no=Musync {id} 0 IN IP4 {local}\r\ns=Musync\r\nc=IN IP4 {address}\r\nt=0 0\r\nm=audio 0 RTP/AVP 96\r\na=rtpmap:96 AppleLossless\r\na=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100\r\n";
        await Request("ANNOUNCE", _uri, sdp, new() { ["Content-Type"] = "application/sdp" }, token);
        var response = await Request("SETUP", _uri, "", new()
        {
            ["Transport"] = $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={((IPEndPoint)_control.Client.LocalEndPoint!).Port};timing_port={((IPEndPoint)_timing.Client.LocalEndPoint!).Port}"
        }, token);
        var transport = response.GetValueOrDefault("Transport") ?? throw new IOException("RAOP transport header missing.");
        int Port(string name)
        {
            var match = Regex.Match(transport, name + @"=(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : throw new IOException("RAOP UDP port missing.");
        }
        _audioEndpoint = new(address, Port("server_port"));
        _controlEndpoint = new(address, Port("control_port"));
        var sessionHeader = response.GetValueOrDefault("Session") ?? "";
        _session = sessionHeader.Split(';')[0].Trim();
        if (_session.Length == 0) _session = null;
        _rtsp.Session = _session;
        var timeout = Regex.Match(sessionHeader, @"(?:^|;)\s*timeout\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        var seconds = timeout.Success && int.TryParse(timeout.Groups[1].Value, out var value) && value > 0 ? value : 60;
        _keepAliveInterval = TimeSpan.FromSeconds(Math.Clamp(seconds / 2.0, .5, 20));
        var record = await Request("RECORD", _uri, "", new() { ["Range"] = "npt=0-", ["RTP-Info"] = "seq=0;rtptime=0" }, token);
        if (uint.TryParse(record.GetValueOrDefault("Audio-Latency"), out var latency)) _latency = Math.Max(latency, 11025);
        _workers = [Guard(() => Pump(token)), Guard(() => Timing(token)), Guard(() => Retransmit(token)), Guard(() => KeepAlive(token))];
    }
    private async Task Guard(Func<Task> task)
    {
        try { await task(); }
        catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested == true) { }
        catch (ObjectDisposedException) when (_lifetime?.IsCancellationRequested == true) { }
        catch (Exception ex) { Interlocked.CompareExchange(ref _error, ex.Message, null); CancelLifetime(); }
    }
    private async Task KeepAlive(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(_keepAliveInterval, token);
            var method = _keepAliveMethod;
            var response = await _rtsp!.RequestAsync(method, method == "OPTIONS" ? "*" : _uri, "",
                method == "GET_PARAMETER" ? new Dictionary<string, string> { ["Content-Type"] = "text/parameters" } : null, token);
            if (method == "GET_PARAMETER" && response.Status is 405 or 451 or 501)
            {
                _keepAliveMethod = "OPTIONS";
                await Request("OPTIONS", "*", "", null, token);
            }
            else if (response.Status != 200) throw new IOException($"AirPlay keepalive failed ({response.Status}).");
        }
    }
    private async Task Pump(CancellationToken token)
    {
        var samples = new float[RaopProtocol.FramesPerPacket * 2];
        ushort sequence = 0;
        uint timestamp = 0;
        var ssrc = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        var initial = RaopProtocol.NtpNow();
        var clock = Stopwatch.StartNew();
        long packets = 0;
        while (!token.IsCancellationRequested)
        {
            Buffer.SetDelay(Control.DelayMs);
            Buffer.Read(samples, Control.Gain);
            var encoded = RaopProtocol.EncodeAlac(PcmEncoding.ToInt16(samples));
            var packet = RaopProtocol.AudioPacket(encoded, sequence, timestamp, ssrc, packets == 0);
            lock (_cacheGate)
            {
                _cache[sequence] = packet;
                _cache.Remove(unchecked((ushort)(sequence - 512)));
            }
            await _audio!.SendAsync(packet, _audioEndpoint!, token);
            if (packets % 125 == 0)
            {
                var ntp = RaopProtocol.NtpAtFrame(initial, packets * RaopProtocol.FramesPerPacket);
                var sync = RaopProtocol.SyncPacket(timestamp, _latency, ntp, packets == 0);
                await _control!.SendAsync(sync, _controlEndpoint!, token);
            }
            sequence = unchecked((ushort)(sequence + 1)); timestamp = unchecked(timestamp + RaopProtocol.FramesPerPacket); packets++;
            var deadline = (double)packets * RaopProtocol.FramesPerPacket / AudioFormat.SampleRate;
            var remaining = deadline - clock.Elapsed.TotalSeconds;
            if (remaining > .001) await Task.Delay(TimeSpan.FromSeconds(remaining), token);
        }
    }
    private async Task Timing(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var request = await _timing!.ReceiveAsync(token);
            if (request.Buffer.Length < 32 || (request.Buffer[0] & 0xc0) != 0x80 || (request.Buffer[1] & 0x7f) != 0x52
                || !request.RemoteEndPoint.Address.Equals(_audioEndpoint!.Address)) continue;
            var result = new byte[32];
            result[0] = 0x80; result[1] = 0xd3;
            request.Buffer.AsSpan(2, 2).CopyTo(result.AsSpan(2));
            request.Buffer.AsSpan(24, 8).CopyTo(result.AsSpan(8));
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(16), RaopProtocol.NtpNow());
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(24), RaopProtocol.NtpNow());
            await _timing.SendAsync(result, request.RemoteEndPoint, token);
        }
    }
    private async Task Retransmit(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var request = await _control!.ReceiveAsync(token);
            var data = request.Buffer;
            if (data.Length < 8 || (data[0] & 0xc0) != 0x80 || (data[1] & 0x7f) != 0x55
                || !request.RemoteEndPoint.Address.Equals(_controlEndpoint!.Address)) continue;
            var first = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
            var count = Math.Min(512, (int)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6)));
            for (var i = 0; i < count; i++)
            {
                byte[]? original;
                lock (_cacheGate) _cache.TryGetValue(unchecked((ushort)(first + i)), out original);
                if (original is null) continue;
                var packet = new byte[original.Length + 4];
                packet[0] = 0x80; packet[1] = 0xd6;
                data.AsSpan(2, 2).CopyTo(packet.AsSpan(2));
                original.CopyTo(packet, 4);
                await _control.SendAsync(packet, request.RemoteEndPoint, token);
            }
        }
    }
    private async Task<IReadOnlyDictionary<string, string>> Request(string method, string uri, string body,
        Dictionary<string, string>? extra, CancellationToken cancellationToken)
    {
        var response = await _rtsp!.RequestAsync(method, uri, body, extra, cancellationToken);
        if (response.Status != 200) throw new IOException($"AirPlay {method} failed ({response.Status}).");
        return response.Headers;
    }
    private void CancelLifetime() { try { _lifetime?.Cancel(); } catch (ObjectDisposedException) { } }
    public async Task SetPausedAsync(bool paused)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!_active || _paused == paused) return;
            Buffer.SetTransportPaused(true);
            if (paused) await StopInternal(preserveBuffer: true);
            else
            {
                try { await StartCore(_sessionToken); }
                catch { await StopInternal(); throw; }
            }
            _paused = paused;
            Buffer.SetTransportPaused(paused);
        }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        CancelLifetime();
        await _lifecycle.WaitAsync();
        try { _active = false; _paused = false; await StopInternal(); }
        finally { _lifecycle.Release(); }
    }
    private async Task StopInternal(bool preserveBuffer = false)
    {
        CancelLifetime();
        await Task.WhenAll(_workers); _workers = [];
        if (_rtsp?.CanRequest == true && _session is not null)
            try { await Request("TEARDOWN", _uri, "", null, CancellationToken.None); } catch (Exception) { }
        _rtsp?.Dispose(); _audio?.Dispose(); _control?.Dispose(); _timing?.Dispose();
        _rtsp = null; _audio = null; _control = null; _timing = null; _session = null;
        lock (_cacheGate) _cache.Clear();
        _lifetime?.Dispose(); _lifetime = null;
        if (!preserveBuffer) Buffer.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await StopAsync();
    }
}
