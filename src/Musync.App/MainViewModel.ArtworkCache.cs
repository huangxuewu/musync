using Musync.Core;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private Task _cacheMaintenance = Task.CompletedTask;
    private ArtworkCacheStats _cacheStats = new(0, 0, SpotifyArtworkResolver.DefaultCacheBytes);
    // Avoid recreating removed caches when deletion falls back to Library.
    private readonly HashSet<string> _cacheOmittedTracks = new(StringComparer.Ordinal);
    public int ArtworkCacheLimitMb
    {
        get => _settings.ArtworkCacheLimitMb;
        set
        {
            var capacity = Math.Clamp(value, 50, 10000);
            if (_disposed || _settings.ArtworkCacheLimitMb == capacity) return;
            _settings.ArtworkCacheLimitMb = capacity;
            Raise(); Raise(nameof(ArtworkCacheUsage)); SaveSoon();
            _ = RunSafely(RefreshArtworkCacheAsync);
        }
    }
    public string ArtworkCacheUsage => string.Format(Text["ArtworkCacheUsage"], _cacheStats.Bytes / (1024d * 1024), ArtworkCacheLimitMb, _cacheStats.Files);
    private async Task RefreshArtworkCacheAsync()
    {
        if (_disposed) return;
        var stats = await _listArtwork.MaintainAsync(ArtworkCacheLimitMb * 1024L * 1024, _library.ToArray(), _lifetime.Token);
        if (_disposed) return;
        _cacheStats = stats; Raise(nameof(ArtworkCacheUsage));
    }
    private async Task MaintainArtworkCacheAsync()
    {
        try
        {
            await RefreshArtworkCacheAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token)) await RefreshArtworkCacheAsync();
        }
        catch (OperationCanceledException) { }
    }
    private async Task ForgetUnusedTrackCachesAsync(IEnumerable<MusicTrack> removed)
    {
        var referenced = _settings.Playlists.Values.SelectMany(ids => ids).Concat(_settings.LikedTracks).ToHashSet(StringComparer.Ordinal);
        var unused = removed.Where(t => !referenced.Contains(t.Id)).ToArray();
        foreach (var track in unused) _cacheOmittedTracks.Add(track.Id);
        await _listArtwork.ForgetTracksAsync(unused, _lifetime.Token);
        await RefreshArtworkCacheAsync();
    }
    internal string TrackRemoveLabel(MusicTrack track) => string.Format(Text[_playlistFilter is not null || _view == "liked" ? "RemoveTrackFromPlaylist" : "RemoveTrackFromLibrary"], track.Title);
    internal bool CanRemoveTrack(MusicTrack track) => IsLocal && !track.IsDemo && !Busy;
    internal async Task RemoveTrackAsync(MusicTrack track)
    {
        if (!CanRemoveTrack(track)) return;
        if (_playlistFilter is { } name && _settings.Playlists.TryGetValue(name, out var ids)) ids.Remove(track.Id);
        else if (_view == "liked") _settings.LikedTracks.Remove(track.Id);
        else
        {
            _library.RemoveAll(t => t.Id == track.Id);
            foreach (var list in _settings.Playlists.Values) list.RemoveAll(id => id == track.Id);
            _settings.LikedTracks.Remove(track.Id);
            _settings.Library = _library.Where(t => !t.IsDemo).ToList();
        }
        RefreshTracks(); RefreshPlaylists(); Raise(nameof(CurrentLiked)); Raise(nameof(LikeCurrentLabel));
        SaveSoon(); ShowToast(Text["TrackRemoved"]);
        await ForgetUnusedTrackCachesAsync([track]);
    }
}
