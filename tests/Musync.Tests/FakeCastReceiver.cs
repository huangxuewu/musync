using Extensions.Api.CastChannel;
using Google.Protobuf;
using Musync.Core;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;

namespace Musync.Tests;

// A loopback TLS receiver exercises SharpCaster's real framing, dispatch and
// request correlation. It never serves audio or connects to physical speakers.
internal sealed class FakeCastReceiver : IAsyncDisposable
{
    internal sealed record Request(CastMessage Envelope, JsonElement Payload)
    {
        public int Id => Payload.GetProperty("requestId").GetInt32();
        public string Type => Payload.GetProperty("type").GetString()!;
    }
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(20));
    private readonly SemaphoreSlim _writes = new(1);
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificate;
    private readonly Task _worker;
    private TcpClient? _client;
    private SslStream? _stream;
    private bool _launched;
    private object[] _statuses = [];
    public object[]? StatusReplyOverride { get; set; }
    public TaskCompletionSource MediaPolled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SpeakerDevice Device => new("loopback-cast", "Loopback Cast", SpeakerTransport.Chromecast,
        "127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);
    public FakeCastReceiver()
    {
        var request = new CertificateRequest("CN=Musync loopback test", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Schannel needs an imported key handle for server TLS. The
        // default import deletes its temporary key when the certificate closes.
        _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        _listener.Start(); _worker = RunAsync();
    }
    public async Task<Request> NextAsync(string type)
    {
        var request = await _requests.Reader.ReadAsync(_lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Xunit.Assert.Equal(type, request.Type); return request;
    }
    public static object Status(long session, string state, double time = 12) => new
    { mediaSessionId = session, playerState = state, currentTime = time, playbackRate = 1, volume = new { level = .5, muted = false } };
    public Task ReplyStatusAsync(Request request, long session, string state = "PAUSED", double time = 12)
    {
        _statuses = [Status(session, state, time)];
        return SendAsync(request.Envelope, new { type = "MEDIA_STATUS", requestId = request.Id, status = _statuses });
    }
    public Task SendErrorAsync(Request request, string type) => SendAsync(request.Envelope,
        new { type, requestId = request.Id, reason = "INVALID_COMMAND", detailedErrorCode = 201 });
    public Task BroadcastAsync(Request request, object status) => SendAsync(request.Envelope,
        new { type = "MEDIA_STATUS", requestId = 0, status = new[] { status } });
    public Task BroadcastForeignApplicationAsync(Request request)
    {
        var envelope = request.Envelope.Clone(); envelope.Namespace = "urn:x-cast:com.google.cast.receiver";
        envelope.DestinationId = "receiver-0";
        return SendAsync(envelope, new { type = "RECEIVER_STATUS", requestId = 0, status = new
        { volume = new { level = .5, muted = false }, applications = new[] { new
            { appId = "OTHER", sessionId = "other-app", transportId = "other-transport", displayName = "Another receiver app",
                namespaces = new[] { new { name = "urn:x-cast:com.google.cast.media" } } } } } });
    }
    public Task ReplyLoadingAsync(Request request, long session)
    {
        // The receiver completes preparation without sending a second reply
        // with the original request ID. A fresh status poll can observe it.
        _statuses = [Status(session, "PAUSED")];
        return SendAsync(request.Envelope, new { type = "MEDIA_STATUS", requestId = request.Id,
            status = new[] { new { mediaSessionId = session, playerState = "IDLE", extendedStatus = new { playerState = "LOADING" } } } });
    }
    private async Task RunAsync()
    {
        var token = _lifetime.Token;
        try
        {
            _client = await _listener.AcceptTcpClientAsync(token);
            _stream = new SslStream(_client.GetStream(), false);
            await _stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, token);
            var header = new byte[4];
            while (true)
            {
                await _stream.ReadExactlyAsync(header, token);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is <= 0 or > 65536) throw new IOException("Unexpected loopback Cast frame length.");
                var bytes = new byte[length]; await _stream.ReadExactlyAsync(bytes, token);
                var envelope = CastMessage.Parser.ParseFrom(bytes);
                using var json = JsonDocument.Parse(envelope.PayloadUtf8);
                var payload = json.RootElement; var type = payload.GetProperty("type").GetString();
                var id = payload.TryGetProperty("requestId", out var requestId) ? requestId.GetInt32() : 0;
                if (type == "PING") { await SendAsync(envelope, new { type = "PONG" }); continue; }
                if (envelope.Namespace == "urn:x-cast:com.google.cast.receiver")
                {
                    if (type == "LAUNCH") _launched = true;
                    await SendAsync(envelope, new { type = "RECEIVER_STATUS", requestId = id, status = new
                    { volume = new { level = .5, muted = false }, applications = _launched ? new[] { new
                        { appId = "CC1AD845", sessionId = "loopback-app", transportId = "loopback-transport", displayName = "Default Media Receiver",
                          namespaces = new[] { new { name = "urn:x-cast:com.google.cast.media" } } } } : [] } });
                }
                else if (envelope.Namespace == "urn:x-cast:com.google.cast.media")
                {
                    if (type == "GET_STATUS")
                    {
                        await SendAsync(envelope, new { type = "MEDIA_STATUS", requestId = id, status = StatusReplyOverride ?? _statuses });
                        if (_statuses.Length > 0) MediaPolled.TrySetResult();
                    }
                    else await _requests.Writer.WriteAsync(new(envelope, payload.Clone()), token);
                }
            }
        }
        catch (Exception error) when (token.IsCancellationRequested || error is EndOfStreamException) { }
        catch (Exception error) { _requests.Writer.TryComplete(error); throw; }
        finally { _requests.Writer.TryComplete(); _stream?.Dispose(); _client?.Dispose(); }
    }
    private async Task SendAsync(CastMessage request, object payload)
    {
        var message = new CastMessage { ProtocolVersion = request.ProtocolVersion, PayloadType = request.PayloadType,
            Namespace = request.Namespace, SourceId = request.DestinationId, DestinationId = request.SourceId,
            PayloadUtf8 = JsonSerializer.Serialize(payload) };
        var bytes = message.ToByteArray(); var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await _writes.WaitAsync(_lifetime.Token);
        try
        {
            await _stream!.WriteAsync(header, _lifetime.Token); await _stream.WriteAsync(bytes, _lifetime.Token);
            await _stream.FlushAsync(_lifetime.Token);
        }
        finally { _writes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _listener.Stop(); _client?.Dispose();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { _stream?.Dispose(); _writes.Dispose(); _certificate.Dispose(); _key.Dispose(); _lifetime.Dispose(); }
    }
}
