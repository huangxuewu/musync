using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Musync.Tests;

internal sealed class FakeRaopReceiver : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(10));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _worker;
    private TcpClient? _client;
    private bool _recorded;
    private readonly int _connections;
    public UdpClient Audio { get; } = new(new IPEndPoint(IPAddress.Loopback, 0));
    public UdpClient Control { get; } = new(new IPEndPoint(IPAddress.Loopback, 0));
    public UdpClient Timing { get; } = new(new IPEndPoint(IPAddress.Loopback, 0));
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int ClientControlPort { get; private set; }
    public int ClientTimingPort { get; private set; }
    public ConcurrentQueue<string> Methods { get; } = new();
    public ConcurrentQueue<string?> PingSessions { get; } = new();
    public TaskCompletionSource PingSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PingAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool RejectGetParameter { get; init; }
    public bool DropOnPing { get; init; }
    public bool HoldPing { get; init; }
    public string? PublicMethods { get; init; }
    public FakeRaopReceiver(int connections = 1) { _connections = connections; _listener.Start(); _worker = Run(); }
    private static int BoundPort(UdpClient client) => ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    private async Task Run()
    {
        var token = _lifetime.Token;
        try
        {
            for (var connection = 0; connection < _connections; connection++)
            {
            _recorded = false;
            using var client = await _listener.AcceptTcpClientAsync(token); _client = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            while (!token.IsCancellationRequested)
            {
                var request = await reader.ReadLineAsync(token);
                if (request is null) return;
                var method = request.Split(' ')[0]; Methods.Enqueue(method);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
                {
                    var pair = line.Split(':', 2); headers[pair[0]] = pair[1].Trim();
                }
                var length = int.Parse(headers.GetValueOrDefault("Content-Length", "0"));
                if (length > 0) await reader.ReadBlockAsync(new char[length], token);
                var extra = "";
                if (method == "SETUP")
                {
                    int ClientPort(string name) => int.Parse(Regex.Match(headers["Transport"], name + @"=(\d+)").Groups[1].Value);
                    ClientControlPort = ClientPort("control_port"); ClientTimingPort = ClientPort("timing_port");
                    extra = $"Session: test-session;timeout=1\r\nTransport: RTP/AVP/UDP;server_port={BoundPort(Audio)};control_port={BoundPort(Control)};timing_port={BoundPort(Timing)}\r\n";
                }
                if (method == "OPTIONS") extra += "Public: " + (PublicMethods ?? "OPTIONS, GET_PARAMETER, ANNOUNCE, SETUP, RECORD, TEARDOWN") + "\r\n";
                var ping = _recorded && method is "GET_PARAMETER" or "OPTIONS";
                if (ping)
                {
                    PingSessions.Enqueue(headers.GetValueOrDefault("Session")); PingSeen.TrySetResult();
                    if (DropOnPing) return;
                    if (HoldPing) await Task.Delay(Timeout.Infinite, token);
                }
                var status = method == "GET_PARAMETER" && RejectGetParameter ? "501 Not Implemented" : "200 OK";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 {status}\r\nCSeq: {headers["CSeq"]}\r\n{extra}Content-Length: 0\r\n\r\n"), token);
                if (ping && status == "200 OK") PingAccepted.TrySetResult();
                if (method == "RECORD") _recorded = true;
                if (method == "TEARDOWN") break;
            }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // The sender can close an in-flight request during Stop. That is a
        // normal end to this simulated receiver's connection.
        catch (IOException) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _client?.Dispose(); _listener.Stop();
        await _worker;
        Audio.Dispose(); Control.Dispose(); Timing.Dispose(); _lifetime.Dispose();
    }
}
