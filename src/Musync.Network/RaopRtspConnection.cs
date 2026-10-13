using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Musync.Network;

internal sealed record RtspReply(int Status, IReadOnlyDictionary<string, string> Headers);

internal sealed class RaopRtspConnection : IDisposable
{
    private readonly TcpClient _client = new();
    private readonly SemaphoreSlim _requests = new(1);
    private int _sequence;
    private volatile bool _failed;
    public string? Session { get; set; }
    public bool CanRequest => !_failed && _client.Connected;
    public IPAddress LocalAddress => Normalize(((IPEndPoint)_client.Client.LocalEndPoint!).Address);
    public IPAddress RemoteAddress => Normalize(((IPEndPoint)_client.Client.RemoteEndPoint!).Address);
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public async Task ConnectAsync(string host, int port, CancellationToken token)
    {
        await _client.ConnectAsync(host, port, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
    }

    public async Task<RtspReply> RequestAsync(string method, string uri, string body,
        IReadOnlyDictionary<string, string>? extra, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var entered = false;
        try
        {
            await _requests.WaitAsync(timeout.Token); entered = true;
            if (_failed) throw new IOException("AirPlay control connection is closed.");
            var sequence = ++_sequence;
            var headers = new StringBuilder($"{method} {uri} RTSP/1.0\r\nCSeq: {sequence}\r\nUser-Agent: Musync/0.1\r\n");
            if (Session is not null) headers.Append("Session: ").Append(Session).Append("\r\n");
            if (extra is not null) foreach (var (key, value) in extra) headers.Append(key).Append(": ").Append(value).Append("\r\n");
            headers.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n\r\n").Append(body);
            var stream = _client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(headers.ToString()), timeout.Token);

            // Consume exactly one response; a partial reply cannot safely be
            // reused for the next request, including a shutdown request.
            var buffer = new byte[16384];
            var count = 0;
            while (true)
            {
                if (count == buffer.Length) throw new IOException("AirPlay control response headers are too large.");
                if (await stream.ReadAsync(buffer.AsMemory(count, 1), timeout.Token) == 0)
                    throw new IOException("AirPlay control connection closed.");
                count++;
                if (count >= 4 && buffer.AsSpan(count - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
            }
            var lines = Encoding.ASCII.GetString(buffer, 0, count).Split("\r\n");
            var statusLine = lines[0].Split(' ', 3);
            if (statusLine.Length < 2 || statusLine[0] != "RTSP/1.0"
                || !int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status)
                || status is < 100 or > 599)
                throw new IOException("Invalid AirPlay control status line.");
            var response = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
            {
                var split = line.IndexOf(':');
                if (split <= 0 || !response.TryAdd(line[..split].Trim(), line[(split + 1)..].Trim()))
                    throw new IOException("Invalid or duplicate AirPlay control header.");
            }
            if (!int.TryParse(response.GetValueOrDefault("CSeq"), NumberStyles.None, CultureInfo.InvariantCulture, out var received)
                || received != sequence) throw new IOException("AirPlay control response sequence does not match.");
            if (response.TryGetValue("Content-Length", out var text))
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > 65536)
                    throw new IOException("Invalid AirPlay control response length.");
                await stream.ReadExactlyAsync(new byte[length], timeout.Token);
            }
            return new(status, response);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _failed = true; _client.Dispose();
            throw new TimeoutException("AirPlay control request timed out.");
        }
        catch
        {
            if (entered) { _failed = true; _client.Dispose(); }
            throw;
        }
        finally { if (entered) _requests.Release(); }
    }
    public void Dispose() { _failed = true; _client.Dispose(); }
}
