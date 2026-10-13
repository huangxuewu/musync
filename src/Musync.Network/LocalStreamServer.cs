using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Musync.Core;

namespace Musync.Network;

public sealed class HlsStream
{
    private readonly object _gate = new();
    private readonly Queue<(long Id, byte[] Bytes, long StartFrame, int Frames)> _segments = new();
    private long _sequence;
    private long _frames;
    // The playlist stays near the live edge; retain older bytes separately so
    // delayed/retried GETs do not turn into expired-segment 404s.
    public int SegmentCount { get { lock (_gate) return Math.Min(8, _segments.Count); } }
    public void Add(byte[] segment, int frames = Mp3Segmenter.SamplesPerSegment)
    {
        if (frames <= 0 || frames > AudioFormat.SampleRate) throw new ArgumentOutOfRangeException(nameof(frames));
        lock (_gate)
        {
            _segments.Enqueue((_sequence++, segment, _frames, frames)); _frames += frames;
            while (_segments.Count > 64) _segments.Dequeue();
        }
    }
    public string Playlist()
    {
        lock (_gate)
        {
            var window = _segments.TakeLast(8).ToArray();
            var result = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n");
            result.Append("#EXT-X-MEDIA-SEQUENCE:").Append(window.Length == 0 ? _sequence : window[0].Id).Append('\n');
            foreach (var segment in window)
            {
                // Optional wall-clock tags caused the tested Onkyo receiver to
                // repeatedly rebuffer this short live stream. Timing stays on
                // the sample clock (EXTINF and each segment's ID3 timestamp).
                // Do not claim a wall-clock mapping for instant silence priming
                // or producer stalls until that mapping is receiver-verified.
                result.Append("#EXTINF:").Append((segment.Frames / (double)AudioFormat.SampleRate).ToString("F9", CultureInfo.InvariantCulture))
                    .Append(",\n").Append(segment.Id).Append(".mp3\n");
            }
            return result.ToString();
        }
    }
    public byte[]? Get(long id) { lock (_gate) return _segments.FirstOrDefault(s => s.Id == id).Bytes; }
}

public sealed class LocalStreamServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HlsStream> _streams = new();
    private readonly ConcurrentDictionary<string, PreparedAudioFile> _files = new();
    private readonly SemaphoreSlim _startGate = new(1);
    private WebApplication? _app;
    public int Port { get; private set; }
    public async Task StartAsync(CancellationToken token)
    {
        await _startGate.WaitAsync(token);
        try
        {
            if (_app is not null) return;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, 0));
            var app = builder.Build();
            app.MapGet("/f/{key}/audio.mp3", (string key, HttpContext context) =>
            {
                context.Response.Headers.AccessControlAllowOrigin = "*";
                context.Response.Headers.CacheControl = "no-store";
                return _files.TryGetValue(key, out var media)
                    ? Results.File(media.Path, "audio/mpeg", enableRangeProcessing: true) : Results.NotFound();
            });
            app.MapGet("/s/{key}/index.m3u8", (string key, HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.AccessControlAllowOrigin = "*";
                return _streams.TryGetValue(key, out var stream)
                    ? Results.Text(stream.Playlist(), "application/vnd.apple.mpegurl") : Results.NotFound();
            });
            app.MapGet("/s/{key}/{segment}", (string key, string segment, HttpContext context) =>
            {
                context.Response.Headers.AccessControlAllowOrigin = "*";
                if (!segment.EndsWith(".mp3") || !long.TryParse(segment[..^4], out var id)) return Results.NotFound();
                var bytes = _streams.GetValueOrDefault(key)?.Get(id);
                return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "audio/mpeg");
            });
            await app.StartAsync(token);
            var server = app.Services.GetRequiredService<IServer>();
            Port = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            _app = app;
        }
        finally { _startGate.Release(); }
    }
    public (string Key, HlsStream Stream) CreateStream()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var stream = new HlsStream();
        _streams[key] = stream;
        return (key, stream);
    }
    public void RemoveStream(string key) => _streams.TryRemove(key, out _);
    public string AddFile(PreparedAudioFile file)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _files[key] = file; return key;
    }
    public void RemoveFile(string key) => _files.TryRemove(key, out _);
    public string FileUrl(string remoteHost, string key) => RouteUrl(remoteHost, $"/f/{key}/audio.mp3");
    public string Url(string remoteHost, string key)
        => RouteUrl(remoteHost, $"/s/{key}/index.m3u8");
    private string RouteUrl(string remoteHost, string path)
    {
        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var remote = Dns.GetHostAddresses(remoteHost).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        route.Connect(remote, 8009);
        var local = ((IPEndPoint)route.LocalEndPoint!).Address;
        return $"http://{local}:{Port}{path}";
    }
    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _streams.Clear(); _files.Clear();
    }
}
