using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Musync.Core;

internal sealed class SpotifyCallbackListener : IDisposable
{
    private const int MaxRequestBytes = 16384;
    private const int MaxRequestLineBytes = 8192;
    private readonly TcpListener _listener;
    private readonly TimeSpan _requestTimeout;
    internal string RedirectUri { get; }

    internal SpotifyCallbackListener(int port = 43821, TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.ExclusiveAddressUse = true;
        try { _listener.Start(4); }
        catch { _listener.Stop(); throw; }
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback";
    }

    internal async Task WaitAsync(string state, Func<string, CancellationToken, Task> exchange, CancellationToken token)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(token);
            using var stream = client.GetStream();
            Callback callback;
            using (var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                requestLifetime.CancelAfter(_requestTimeout);
                try { callback = Parse(await ReadRequest(stream, requestLifetime.Token), state); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                catch (IOException) { token.ThrowIfCancellationRequested(); continue; }
                catch (InvalidDataException)
                {
                    await Respond(stream, 400, "Invalid authorization response", "Return to Musync to finish connecting.", token);
                    continue;
                }
            }
            if (callback.Code is null && callback.Error is null)
            {
                await Respond(stream, callback.Status, "Invalid authorization response", "Return to Musync to finish connecting.", token);
                continue;
            }
            if (callback.Error is not null)
            {
                await Respond(stream, 200, "Spotify was not connected", "Authorization was declined or could not be completed. You can return to Musync.", token);
                throw new InvalidOperationException("Spotify authorization was declined or could not be completed.");
            }
            try { await exchange(callback.Code!, token); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                await Respond(stream, 502, "Spotify was not connected", "Musync could not finish connecting. Return to the app to retry.", token);
                throw;
            }
            await Respond(stream, 200, "Musync is connected", "You can return to the app.", token);
            return;
        }
    }

    private static async Task<string> ReadRequest(NetworkStream stream, CancellationToken token)
    {
        var bytes = new byte[MaxRequestBytes];
        var count = 0;
        var searched = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) throw new IOException("Incomplete callback request.");
            count += read;
            for (var i = Math.Max(0, searched - 3); i + 3 < count; i++)
            {
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                {
                    // The redirect is a GET with no body. Extra bytes are never
                    // treated as a second request on this one-use connection.
                    return Encoding.Latin1.GetString(bytes, 0, i + 2);
                }
            }
            searched = count;
            if (!bytes.AsSpan(0, count).Contains((byte)10) && count > MaxRequestLineBytes)
                throw new InvalidDataException("Callback request line is too large.");
        }
        throw new InvalidDataException("Callback headers are too large.");
    }

    private readonly record struct Callback(int Status, string? Code = null, string? Error = null);
    private Callback Parse(string request, string expectedState)
    {
        var lines = request.Split("\r\n", StringSplitOptions.None);
        if (lines.Length > 66 || lines[0].Length > MaxRequestLineBytes) return new(400);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET" || first[2] is not ("HTTP/1.1" or "HTTP/1.0")
            || first[1].Any(c => c < 33 || c > 126) || first[1].Contains('#') || first[1].Contains('\\')) return new(400);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))
                || line[(colon + 1)..].Any(c => (c < 32 && c != '\t') || c == 127)
                || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) return new(400);
        }
        var authority = new Uri(RedirectUri).Authority;
        if (headers.GetValueOrDefault("Host") != authority || headers.ContainsKey("Transfer-Encoding")
            || (headers.TryGetValue("Content-Length", out var length) && length != "0")) return new(400);
        var target = first[1];
        var question = target.IndexOf('?');
        if ((question < 0 ? target : target[..question]) != "/callback") return new(404);
        if (question < 0) return new(400);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = field.Split('=', 2);
            if (!TryDecode(pair[0], out var key) || !TryDecode(pair.ElementAtOrDefault(1) ?? "", out var value)
                || key.Length == 0 || !values.TryAdd(key, value)) return new(400);
        }
        var state = values.GetValueOrDefault("state");
        if (state is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            return new(400);
        var hasCode = values.TryGetValue("code", out var code);
        var hasError = values.TryGetValue("error", out var error);
        if (hasCode == hasError || (hasCode && string.IsNullOrWhiteSpace(code)) || (hasError && string.IsNullOrWhiteSpace(error))) return new(400);
        return hasCode ? new(200, Code: code) : new(200, Error: error);
    }

    private static bool TryDecode(string input, out string decoded)
    {
        decoded = "";
        var bytes = new byte[input.Length];
        var count = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '%')
            {
                if (i + 2 >= input.Length || !Uri.IsHexDigit(input[i + 1]) || !Uri.IsHexDigit(input[i + 2])) return false;
                bytes[count++] = (byte)((Uri.FromHex(input[++i]) << 4) | Uri.FromHex(input[++i]));
            }
            else bytes[count++] = input[i] == '+' ? (byte)' ' : (byte)input[i];
        }
        try { decoded = new UTF8Encoding(false, true).GetString(bytes, 0, count); }
        catch (DecoderFallbackException) { return false; }
        return !decoded.Any(char.IsControl);
    }

    private static async Task Respond(NetworkStream stream, int status, string title, string detail, CancellationToken token)
    {
        var html = Encoding.UTF8.GetBytes($"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>{title}</title><h2>{title}</h2><p>{detail}</p></html>");
        var reason = status switch { 200 => "OK", 404 => "Not Found", 502 => "Bad Gateway", _ => "Bad Request" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(2));
        try { await stream.WriteAsync(header, lifetime.Token); await stream.WriteAsync(html, lifetime.Token); }
        catch (IOException) { token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    public void Dispose() => _listener.Stop();
}
