using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Musync.Core;

public interface ITokenVault
{
    string? Load();
    void Save(string json);
    void Clear();
}

public sealed record SpotifyTokens(string AccessToken, string RefreshToken, DateTime ExpiresAt);
public sealed record SpotifyDevice(string Id, string Name, string Type, bool Active, bool Restricted = false);
public sealed record SpotifyPlayback(MusicTrack? Track, double Position, bool Playing, bool Shuffle = false, string RepeatState = "off",
    int? VolumePercent = null, bool SupportsVolume = false, string? DeviceId = null);

public sealed class SpotifyClient : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    private readonly HttpClient _http;
    private readonly ITokenVault _vault;
    private readonly SemaphoreSlim _refresh = new(1);
    private readonly object _connectionGate = new();
    private readonly int _callbackPort;
    private readonly TimeSpan _callbackRequestTimeout;
    private readonly TimeSpan _authorizationTimeout;
    private CancellationTokenSource? _connectionLifetime;
    private long _accountGeneration;
    private bool _disposed;
    private volatile SpotifyTokens? _tokens;
    public string ClientId { get; set; } = "";
    public bool Connected => _tokens is not null;
    public SpotifyClient(ITokenVault vault) : this(vault, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }) { }
    internal SpotifyClient(ITokenVault vault, HttpClient http, int callbackPort = 43821,
        TimeSpan? callbackRequestTimeout = null, TimeSpan? authorizationTimeout = null)
    {
        _vault = vault; _http = http; _callbackPort = callbackPort;
        _callbackRequestTimeout = callbackRequestTimeout ?? TimeSpan.FromSeconds(5);
        _authorizationTimeout = authorizationTimeout ?? TimeSpan.FromMinutes(3);
        try { _tokens = JsonSerializer.Deserialize<SpotifyTokens>(vault.Load() ?? "null"); }
        catch (JsonException) { _vault.Clear(); }
    }
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        var clientId = ClientId;
        if (clientId.Length != 32 || !clientId.All(Uri.IsHexDigit)) throw new ArgumentException("A valid Spotify app client ID is required.");
        cancellationToken.ThrowIfCancellationRequested();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        using var timeout = new CancellationTokenSource(_authorizationTimeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        lock (_connectionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connectionLifetime is not null) throw new InvalidOperationException("Spotify sign-in is already in progress.");
            _connectionLifetime = lifetime;
        }
        try
        {
            using var listener = new SpotifyCallbackListener(_callbackPort, _callbackRequestTimeout);
            var scope = "user-read-playback-state user-modify-playback-state user-read-currently-playing playlist-read-private user-library-read";
            var uri = new Uri("https://accounts.spotify.com/authorize?response_type=code&client_id=" + clientId
                + "&redirect_uri=" + Uri.EscapeDataString(listener.RedirectUri)
                + "&scope=" + Uri.EscapeDataString(scope) + "&state=" + state
                + "&code_challenge_method=S256&code_challenge=" + Challenge(verifier));
            openBrowser(uri);
            await listener.WaitAsync(state, async (code, token) =>
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
                    ["redirect_uri"] = listener.RedirectUri, ["code_verifier"] = verifier
                });
                var result = await _http.PostAsync("https://accounts.spotify.com/api/token", form, token);
                await ReadToken(result, token, requireRefreshToken: true);
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_disposed)
        { throw new TimeoutException("Spotify sign-in timed out. Return to Settings and reconnect."); }
        finally { lock (_connectionGate) _connectionLifetime = null; }
    }
    private async Task ReadToken(HttpResponseMessage response, CancellationToken token, bool requireRefreshToken = false, long? generation = null)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Spotify authorization failed.", null, response.StatusCode);
            var tokenBody = await response.Content.ReadAsStringAsync(token);
            JsonDocument tokenJson;
            try { tokenJson = JsonDocument.Parse(tokenBody); }
            catch (JsonException) { throw new HttpRequestException("Spotify returned an invalid token response."); }
            using var json = tokenJson;
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString())
                || !root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out var seconds) || seconds <= 0)
                throw new HttpRequestException("Spotify returned an invalid token response.");
            var refresh = root.TryGetProperty("refresh_token", out var refreshValue) && refreshValue.ValueKind == JsonValueKind.String
                ? refreshValue.GetString() : requireRefreshToken ? null : _tokens?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refresh)) throw new HttpRequestException("Spotify returned an invalid token response.");
            var next = new SpotifyTokens(access.GetString()!, refresh, DateTime.UtcNow.AddSeconds(Math.Max(1, seconds - 60)));
            lock (_connectionGate)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (generation is not null && generation != _accountGeneration)
                    throw new InvalidOperationException("Spotify connection changed. Retry this action.");
                _vault.Save(JsonSerializer.Serialize(next));
                _tokens = next;
                if (requireRefreshToken) _accountGeneration++;
            }
        }
    }
    private async Task<string> AccessToken(CancellationToken token)
    {
        await _refresh.WaitAsync(token);
        try
        {
            SpotifyTokens current; long generation; string clientId;
            lock (_connectionGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                current = _tokens ?? throw new InvalidOperationException("Connect Spotify in Settings first.");
                generation = _accountGeneration; clientId = ClientId;
            }
            if (current.ExpiresAt <= DateTime.UtcNow)
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = current.RefreshToken, ["client_id"] = clientId
                });
                var response = await _http.PostAsync("https://accounts.spotify.com/api/token", form, token);
                await ReadToken(response, token, generation: generation);
            }
            lock (_connectionGate)
            {
                if (generation != _accountGeneration || _tokens is null)
                    throw new InvalidOperationException("Spotify connection changed. Retry this action.");
                return _tokens.AccessToken;
            }
        }
        finally { _refresh.Release(); }
    }
    private async Task<JsonDocument?> Request(HttpMethod method, string path, object? body, CancellationToken token, bool retryRateLimit = true)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(token));
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                lock (_connectionGate)
                    if (_tokens is { } current && current.AccessToken == request.Headers.Authorization?.Parameter)
                        _tokens = current with { ExpiresAt = DateTime.MinValue };
                continue;
            }
            if ((int)response.StatusCode == 429 && attempt == 0 && retryRateLimit)
            {
                var bodyText = await response.Content.ReadAsStringAsync(token);
                if (bodyText.Contains("QUOTA_EXCEEDED", StringComparison.Ordinal))
                    throw new SpotifyApiException("SpotifyQuotaExceeded", response.StatusCode);
                var delay = Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2, 1, 15);
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw await ApiError(response, token);
            // Player commands have no response model. A successful command must not
            // become a playback failure because a server returned an incidental body.
            if (method != HttpMethod.Get) return null;
            var content = await response.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(content)) return null;
            JsonDocument document;
            try { document = JsonDocument.Parse(content); }
            catch (JsonException) { throw new SpotifyApiException("SpotifyRequestFailed", response.StatusCode); }
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw new SpotifyApiException("SpotifyRequestFailed", response.StatusCode);
        }
        throw new HttpRequestException("Spotify authentication expired. Reconnect in Settings.");
    }
    private static async Task<SpotifyApiException> ApiError(HttpResponseMessage response, CancellationToken token)
    {
        var reason = "";
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("reason", out var value) && value.ValueKind == JsonValueKind.String) reason = value.GetString() ?? "";
                if (reason.Length == 0 && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    var text = message.GetString() ?? "";
                    if (text.Contains("Premium", StringComparison.OrdinalIgnoreCase)) reason = "PREMIUM_REQUIRED";
                    else if (text.Contains("scope", StringComparison.OrdinalIgnoreCase)) reason = "PERMISSIONS_MISSING";
                    else if (text.Contains("No active device", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Device not found", StringComparison.OrdinalIgnoreCase)) reason = "NO_ACTIVE_DEVICE";
                }
            }
        }
        catch (JsonException) { }
        var key = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "SpotifyReconnect",
            HttpStatusCode.Forbidden when reason == "PREMIUM_REQUIRED" => "SpotifyPremiumRequired",
            HttpStatusCode.Forbidden when reason == "PERMISSIONS_MISSING" => "SpotifyPermissionRequired",
            HttpStatusCode.Forbidden => "SpotifyAccessDenied",
            HttpStatusCode.NotFound when reason == "NO_ACTIVE_DEVICE" => "NoSpotifyDevices",
            HttpStatusCode.NotFound => "SpotifyUnavailable",
            HttpStatusCode.TooManyRequests when reason == "QUOTA_EXCEEDED" => "SpotifyQuotaExceeded",
            HttpStatusCode.TooManyRequests => "SpotifyRateLimited",
            _ => "SpotifyRequestFailed"
        };
        var retry = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return new(key, response.StatusCode, retry);
    }
    public async Task<MusicTrack?> CurrentAsync(CancellationToken token)
    {
        using var json = await Request(HttpMethod.Get, "me/player/currently-playing", null, token);
        if (json is null || !json.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        return ParseTrack(item);
    }
    public async Task<SpotifyPlayback> PlaybackAsync(CancellationToken token)
    {
        using var json = await Request(HttpMethod.Get, "me/player", null, token);
        if (json is null) return new(null, 0, false);
        var root = json.RootElement;
        var track = root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("artists", out _) && item.TryGetProperty("album", out _) ? ParseTrack(item) : null;
        var device = root.TryGetProperty("device", out var deviceValue) && deviceValue.ValueKind == JsonValueKind.Object ? deviceValue : default;
        var hasDevice = device.ValueKind == JsonValueKind.Object;
        int? volume = hasDevice && device.TryGetProperty("volume_percent", out var volumeValue) && volumeValue.ValueKind == JsonValueKind.Number && volumeValue.TryGetInt32(out var percent)
            && percent is >= 0 and <= 100 ? percent : null;
        var supportsVolume = hasDevice && device.TryGetProperty("supports_volume", out var supports) && supports.ValueKind == JsonValueKind.True
            && !(device.TryGetProperty("is_restricted", out var restricted) && restricted.ValueKind == JsonValueKind.True);
        var deviceId = hasDevice && device.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        return new(track, root.TryGetProperty("progress_ms", out var progress) && progress.ValueKind == JsonValueKind.Number ? progress.GetDouble() / 1000 : 0,
            root.TryGetProperty("is_playing", out var playing) && playing.ValueKind == JsonValueKind.True,
            root.TryGetProperty("shuffle_state", out var shuffle) && shuffle.ValueKind == JsonValueKind.True,
            root.TryGetProperty("repeat_state", out var repeat) && repeat.ValueKind == JsonValueKind.String ? repeat.GetString() ?? "off" : "off",
            volume, supportsVolume, deviceId);
    }
    public async Task<IReadOnlyList<SpotifyDevice>> DevicesAsync(CancellationToken token)
    {
        using var json = await Request(HttpMethod.Get, "me/player/devices", null, token);
        return json!.RootElement.GetProperty("devices").EnumerateArray()
            .Where(d => d.GetProperty("id").ValueKind == JsonValueKind.String)
            .Select(d => new SpotifyDevice(d.GetProperty("id").GetString()!, d.GetProperty("name").GetString()!,
                d.GetProperty("type").GetString()!, d.GetProperty("is_active").GetBoolean(),
                d.TryGetProperty("is_restricted", out var restricted) && restricted.ValueKind == JsonValueKind.True)).ToArray();
    }
    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken token)
        => await SearchCoreAsync(query, token, retryRateLimit: true);
    internal async Task<IReadOnlyList<MusicTrack>> SearchArtworkAsync(string query, CancellationToken token)
        => await SearchCoreAsync(query, token, retryRateLimit: false);
    private async Task<IReadOnlyList<MusicTrack>> SearchCoreAsync(string query, CancellationToken token, bool retryRateLimit)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        using var json = await Request(HttpMethod.Get, "search?type=track&limit=10&q=" + Uri.EscapeDataString(query), null, token, retryRateLimit);
        if (json is null) return [];
        return json!.RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray().Select(ParseTrack).ToArray();
    }
    public static MusicTrack ParseTrack(JsonElement item)
    {
        var artist = string.Join(", ", item.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        var image = item.GetProperty("album").GetProperty("images").EnumerateArray().FirstOrDefault();
        return new(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!, artist, null,
            item.GetProperty("uri").GetString(), image.ValueKind == JsonValueKind.Object ? image.GetProperty("url").GetString() : null,
            item.GetProperty("duration_ms").GetDouble() / 1000,
            Album: item.GetProperty("album").TryGetProperty("name", out var name) ? name.GetString() : null);
    }
    public async Task PlayAsync(string? trackUri, string? deviceId, CancellationToken token)
    {
        var path = "me/player/play" + (deviceId is null ? "" : "?device_id=" + Uri.EscapeDataString(deviceId));
        using var result = await Request(HttpMethod.Put, path, trackUri is null ? null : new { uris = new[] { trackUri } }, token);
    }
    public async Task PlayOnDeviceAsync(string? trackUri, string deviceId, CancellationToken token)
    {
        try { await PlayAsync(trackUri, deviceId, token); }
        catch (SpotifyApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // A visible Connect device can be asleep/inactive. Activate the chosen target once, then retry.
            // Do not retry Premium, permission, authentication or quota failures.
            using var transfer = await Request(HttpMethod.Put, "me/player", new { device_ids = new[] { deviceId }, play = false }, token);
            await PlayAsync(trackUri, deviceId, token);
        }
    }
    // Artwork is public CDN data. Never attach an account token, and bound chunked responses too.
    public async Task<byte[]?> ArtworkAsync(string? imageUrl, CancellationToken token)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        const int maximum = 5_000_000;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum) return null;
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum + 1 - (int)buffer.Length)), timeout.Token);
            if (read == 0) return buffer.Length == 0 ? null : buffer.ToArray();
            if (buffer.Length + read > maximum) return null;
            buffer.Write(chunk, 0, read);
        }
    }
    public async Task PauseAsync(CancellationToken token) { using var result = await Request(HttpMethod.Put, "me/player/pause", null, token); }
    public async Task NextAsync(CancellationToken token) { using var result = await Request(HttpMethod.Post, "me/player/next", null, token); }
    public async Task PreviousAsync(CancellationToken token) { using var result = await Request(HttpMethod.Post, "me/player/previous", null, token); }
    public async Task ShuffleAsync(bool enabled, CancellationToken token)
    { using var result = await Request(HttpMethod.Put, "me/player/shuffle?state=" + (enabled ? "true" : "false"), null, token); }
    public async Task RepeatAsync(bool enabled, CancellationToken token)
    { using var result = await Request(HttpMethod.Put, "me/player/repeat?state=" + (enabled ? "track" : "off"), null, token); }
    public async Task VolumeAsync(int percent, string deviceId, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        using var result = await Request(HttpMethod.Put, "me/player/volume?volume_percent=" + percent
            + "&device_id=" + Uri.EscapeDataString(deviceId), null, token);
    }
    public async Task SeekAsync(double seconds, CancellationToken token)
    { using var result = await Request(HttpMethod.Put, "me/player/seek?position_ms=" + (int)Math.Max(0, seconds * 1000), null, token); }
    public void Disconnect()
    {
        lock (_connectionGate) { _connectionLifetime?.Cancel(); _accountGeneration++; _tokens = null; _vault.Clear(); }
    }
    public void Dispose()
    {
        lock (_connectionGate) { if (_disposed) return; _disposed = true; _connectionLifetime?.Cancel(); }
        _http.Dispose();
        // In-flight refreshes still release this managed semaphore in finally.
        // No wait handle is created, so it can be reclaimed after they finish.
    }
}
