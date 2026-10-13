using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Musync.Core;

public sealed record TrackArtwork(MusicTrack Metadata, byte[]? Image, string? SpotifyUri);

// Bounded, expiring memory/disk caches. Spotify associations never alter a local
// track's playback URI/file or get written into the persistent library.
public sealed class SpotifyArtworkResolver
{
    private readonly SpotifyClient _spotify;
    private readonly TimeProvider _clock;
    private readonly TemporaryArtworkCache? _disk;
    private readonly SemaphoreSlim _work = new(1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, MatchEntry> _matches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageEntry> _images = new(StringComparer.Ordinal);
    private DateTimeOffset _searchAfter, _blockedUntil;
    private long _generation;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    private sealed record MatchEntry(MusicTrack Metadata, MusicTrack? SpotifyTrack, DateTimeOffset Expires);
    private sealed record ImageEntry(byte[]? Data, DateTimeOffset Expires);

    public SpotifyArtworkResolver(SpotifyClient spotify, string? cacheDirectory = null) : this(spotify, TimeProvider.System, cacheDirectory) { }
    internal SpotifyArtworkResolver(SpotifyClient spotify, TimeProvider clock, string? cacheDirectory = null)
    { _spotify = spotify; _clock = clock; _disk = cacheDirectory is null ? null : new(cacheDirectory, clock); }

    public void Clear()
    {
        lock (_cacheGate) { _generation++; _matches.Clear(); _images.Clear(); _searchAfter = _blockedUntil = default; _disk?.Clear(); }
    }

    public async Task<TrackArtwork> ResolveAsync(MusicTrack track, CancellationToken token)
    {
        await _work.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_cacheGate) generation = _generation;
            var now = _clock.GetUtcNow();
            var key = track.FilePath is null ? "spotify:" + track.Id + ":" + track.ImageUrl
                : "local:" + track.FilePath + ":" + FileStamp(track.FilePath)
                    + (File.Exists(track.FilePath) ? "" : ":" + track.Title + ":" + track.Artist);
            MatchEntry? entry;
            lock (_cacheGate) { Prune(now); _matches.TryGetValue(key, out entry); }
            if (entry is null && _disk is not null)
            {
                var saved = await Task.Run(() => _disk.ReadMatch(key), token).ConfigureAwait(false);
                if (saved is not null)
                    entry = new(track with { Title = saved.Title, Artist = saved.Artist, Album = saved.Album }, saved.Catalog, saved.Expires);
            }
            var metadata = entry is null ? await Task.Run(() => LocalTrackMetadata.Read(track), token).ConfigureAwait(false)
                : track with { Title = entry.Metadata.Title, Artist = entry.Metadata.Artist, Album = entry.Metadata.Album };
            if (!_spotify.Connected || track.IsDemo) return new(metadata, null, null);
            try
            {
                MusicTrack? catalog = entry?.SpotifyTrack;
                if (entry is null)
                {
                    if (track.FilePath is null && track.SpotifyUri is not null) catalog = track;
                    else if (CanSearch(metadata))
                    {
                        lock (_cacheGate) if (_blockedUntil > now) return new(metadata, null, null);
                        var delay = _searchAfter - now;
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                        _searchAfter = _clock.GetUtcNow().AddMilliseconds(500);
                        var query = "track:\"" + QueryValue(metadata.Title) + "\"";
                        if (!string.IsNullOrWhiteSpace(metadata.Artist)) query += " artist:\"" + QueryValue(Artists(metadata.Artist).First()) + "\"";
                        var candidates = await _spotify.SearchArtworkAsync(query, token).ConfigureAwait(false);
                        catalog = Match(metadata, candidates);
                    }
                    entry = new(metadata, catalog, _clock.GetUtcNow().AddMinutes(catalog is null ? 10 : 60));
                    lock (_cacheGate) if (_generation == generation)
                    {
                        LimitMatches(); _matches[key] = entry;
                        _disk?.SaveMatch(key, new(metadata.Title, metadata.Artist, metadata.Album, catalog, entry.Expires));
                    }
                }
                if (catalog?.ImageUrl is null || !TrySpotifyLink(catalog.SpotifyUri, out _)) return new(metadata, null, null);
                ImageEntry? image;
                lock (_cacheGate) _images.TryGetValue(catalog.ImageUrl, out image);
                if (image is null && _disk is not null)
                {
                    var saved = await Task.Run(() => _disk.ReadCover(catalog.ImageUrl), token).ConfigureAwait(false);
                    if (saved is not null && (saved.Data?.Length ?? 0) <= 5_000_000) image = new(saved.Data, saved.Expires);
                }
                var data = image?.Data;
                if (image is null)
                {
                    try { data = await _spotify.ArtworkAsync(catalog.ImageUrl, token).ConfigureAwait(false); }
                    catch (HttpRequestException) { data = null; }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { data = null; }
                }
                token.ThrowIfCancellationRequested();
                lock (_cacheGate)
                {
                    if (_generation != generation) return new(metadata, null, null);
                    if (!_images.ContainsKey(catalog.ImageUrl))
                    {
                        while (_images.Count > 0 && (_images.Count >= 128 || _images.Values.Sum(i => (long)(i.Data?.Length ?? 0)) + (data?.Length ?? 0) > MaxImageBytes))
                            _images.Remove(_images.Keys.First());
                        var cached = image ?? new(data, _clock.GetUtcNow().AddMinutes(data is null ? 1 : 60));
                        _images[catalog.ImageUrl] = cached;
                        if (image is null) _disk?.SaveCover(catalog.ImageUrl, new(data, cached.Expires));
                    }
                }
                return new(metadata, data, data is null ? null : catalog.SpotifyUri);
            }
            catch (SpotifyApiException error)
            {
                if (error.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    lock (_cacheGate) if (_generation == generation)
                        _blockedUntil = _clock.GetUtcNow().Add(error.RetryAfter is { } retry && retry > TimeSpan.Zero ? retry : TimeSpan.FromMinutes(1));
                return new(metadata, null, null);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or InvalidOperationException
                or System.Text.Json.JsonException or KeyNotFoundException)
            { return new(metadata, null, null); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return new(metadata, null, null); } // CDN timeout is an optional cover failure.
        }
        finally { _work.Release(); }
    }
    // Closing the player keeps still-valid disk results for the next launch.
    public void ReleaseMemory()
    { lock (_cacheGate) { _generation++; _matches.Clear(); _images.Clear(); } }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _matches.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _matches.Remove(key);
        foreach (var key in _images.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _images.Remove(key);
    }
    private void LimitMatches() { while (_matches.Count >= 128) _matches.Remove(_matches.Keys.First()); }
    private static long FileStamp(string path)
    { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }
    private static string QueryValue(string text) => text.Replace('"', ' ').Trim();
    private static bool CanSearch(MusicTrack track) => !track.IsDemo && Normalize(track.Title).Any(char.IsLetter)
        && (!string.IsNullOrWhiteSpace(track.Artist) || track.DurationSeconds > 0);
    internal static MusicTrack? Match(MusicTrack local, IEnumerable<MusicTrack> candidates)
    {
        var matches = candidates.Where(c => c.ImageUrl is not null && NormalizeTitle(c.Title) == NormalizeTitle(local.Title)
            && (string.IsNullOrWhiteSpace(local.Artist) || SameArtists(local.Artist, c.Artist))
            && (local.DurationSeconds <= 0 || Math.Abs(local.DurationSeconds - c.DurationSeconds) <= 3))
            // Local album fields sometimes hold a download site or a compilation.
            // Prefer an exact album, while song/artist/duration still establish identity.
            .OrderByDescending(c => !string.IsNullOrWhiteSpace(local.Album) && Normalize(c.Album ?? "") == Normalize(local.Album))
            .ThenBy(c => Math.Abs(c.DurationSeconds - local.DurationSeconds)).ToArray();
        // A title-only query must identify one artist; never take the first popularity hit.
        if (string.IsNullOrWhiteSpace(local.Artist) && matches.Select(c => Normalize(c.Artist)).Distinct().Count() != 1) return null;
        return matches.FirstOrDefault();
    }
    private static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static string NormalizeTitle(string text) => Normalize(Regex.Replace(text,
        @"\s*[\(\[]\s*(?:feat\.?|ft\.?|with)\s+[^\)\]]+[\)\]]\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    private static string[] Artists(string text) => text.Split([",", ";", "&", "、", "/", " feat. ", " ft. "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static bool SameArtists(string local, string catalog)
    {
        if (Normalize(local) == Normalize(catalog)) return true;
        var artists = Artists(catalog).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return Artists(local).Select(Normalize).All(artists.Contains);
    }
    public static bool TrySpotifyLink(string? spotifyUri, out Uri? link)
    {
        const string prefix = "spotify:track:";
        link = null;
        if (spotifyUri is null || !spotifyUri.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = spotifyUri[prefix.Length..];
        if (id.Length is < 1 or > 64 || !id.All(char.IsAsciiLetterOrDigit)) return false;
        link = new Uri("https://open.spotify.com/track/" + id);
        return true;
    }
}
