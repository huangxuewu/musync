using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Platform;
using Musync.Core;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private SyncedLyrics _lyrics = SyncedLyrics.Empty;
    private LyricWindow _lyricWindow = new(-1, "", "", "");
    private CancellationTokenSource? _lyricsRequest;
    private string? _presentationKey;
    private string? _posterContextKey, _posterTrackKey;
    private int _posterPalette;
    private int _posterArtworkIndex;
    private long _heroEpoch;
    private bool _loadingLyrics;
    private readonly HashSet<Bitmap> _retiringArtwork = [];
    public int PosterPalette => _posterPalette;
    public long HeroEpoch => _heroEpoch;
    public Bitmap PosterArtwork => PosterAssets.Get(_posterArtworkIndex == 1 ? 1 : 0);
    public string LyricPrevious => _lyricWindow.Previous;
    public string LyricCurrent => _loadingLyrics ? Text["LoadingLyrics"]
        : _lyrics.Lines.Count == 0 ? Text["NoSyncedLyrics"] : _lyricWindow.Current;
    public string LyricNext => _lyricWindow.Next;
    public bool HasSyncedLyrics => _lyrics.Lines.Count > 0;
    public FontFamily InterfaceFont => Text.Language == "zh-Hans" ? InterfaceTypography.Chinese : InterfaceTypography.Default;
    public double InterfaceFontSize => Text.Language == "zh-Hans" ? 14 : 13;

    internal void RotatePosterArtwork()
    {
        if (_disposed || !ShowHero || !ShowArtist) return;
        _posterPalette = (_posterPalette + 1) % 3;
        _posterArtworkIndex ^= 1;
        PublishPoster();
    }

    private void RefreshPresentation()
    {
        RefreshPoster();
        var key = $"{Source?.Id}:{_current?.Id}:{_current?.FilePath}";
        if (key == _presentationKey) return;
        _presentationKey = key;
        _ = LoadLyricsAsync(Source?.Id == "local" ? _current?.FilePath : null, key);
    }

    private void RefreshPoster()
    {
        var context = $"{Source?.Id}:{_view}:{_playlistFilter}";
        var track = $"{_current?.Id}:{_current?.FilePath}";
        if (context == _posterContextKey && track == _posterTrackKey) return;
        if (_posterContextKey is not null && context != _posterContextKey)
        {
            // Every different browsing destination changes the actual subject,
            // even when two folder names would hash to the same illustration.
            _posterPalette = (_posterPalette + 1) % 3;
            _posterArtworkIndex ^= 1;
        }
        else
        {
            uint hash = 2166136261;
            foreach (var c in _current?.Id ?? "musync") hash = unchecked((hash ^ c) * 16777619);
            _posterPalette = (int)(hash % 3);
            _posterArtworkIndex = _posterPalette == 1 ? 1 : 0;
        }
        _posterContextKey = context; _posterTrackKey = track;
        PublishPoster();
    }

    private void PublishPoster()
    {
        Raise(nameof(PosterPalette)); Raise(nameof(PosterArtwork));
        _heroEpoch++; Raise(nameof(HeroEpoch));
    }

    private async Task LoadLyricsAsync(string? path, string key)
    {
        _lyricsRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _lyricsRequest = request;
        _lyrics = SyncedLyrics.Empty; _loadingLyrics = path is not null;
        RefreshLyricWindow(force: true);
        try
        {
            var lyrics = path is null ? SyncedLyrics.Empty
                : await Task.Run(() => SyncedLyrics.LoadAlongsideAsync(path, request.Token), request.Token);
            if (_disposed || request.IsCancellationRequested || _presentationKey != key) return;
            _lyrics = lyrics; _loadingLyrics = false;
            RefreshLyricWindow(force: true);
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_lyricsRequest, request)) _lyricsRequest = null; }
    }

    private void RefreshLyricWindow(bool force = false)
    {
        var window = _lyrics.At(Position);
        if (!force && window == _lyricWindow) return;
        _lyricWindow = window;
        Raise(nameof(LyricPrevious)); Raise(nameof(LyricCurrent)); Raise(nameof(LyricNext)); Raise(nameof(HasSyncedLyrics));
    }

    private void RetireArtwork(Bitmap? bitmap)
    {
        if (bitmap is null) return;
        // Render controls may still use the previous bitmap during a 280ms fade.
        _retiringArtwork.Add(bitmap);
        _ = RetireAsync();
        async Task RetireAsync()
        {
            try { await Task.Delay(800, _lifetime.Token); }
            catch (OperationCanceledException) { }
            if (_retiringArtwork.Remove(bitmap)) bitmap.Dispose();
        }
    }
}

internal static class PosterAssets
{
    private static readonly Lazy<Bitmap> Blue = new(() => Load("artist-electric-blue-v1.png"));
    private static readonly Lazy<Bitmap> Coral = new(() => Load("artist-coral.png"));
    public static Bitmap Get(int palette) => palette == 1 ? Coral.Value : Blue.Value;
    private static Bitmap Load(string name)
    {
        using var stream = AssetLoader.Open(new Uri("avares://Musync/Assets/" + name));
        return new Bitmap(stream);
    }
}
