using Musync.Core;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace Musync.App;

public sealed class TrackViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _owner;
    private Bitmap? _artwork;
    private CancellationTokenSource? _artworkRequest;
    private Task? _loading;
    private bool _disposed, _attempted, _visible;
    private string? _artworkSpotifyUri;
    public TrackViewModel(MusicTrack track, MainViewModel owner)
    {
        Track = track; _owner = owner;
        PlayCommand = new AsyncCommand(_ => owner.PlayTrackAsync(Track), owner.ShowError);
        LikeCommand = new AsyncCommand(_ => { owner.ToggleLike(track.Id); return Task.CompletedTask; }, owner.ShowError);
        AddCommand = new AsyncCommand(_ => { owner.OpenPlaylist(track); return Task.CompletedTask; }, owner.ShowError);
        OpenArtworkSource = new AsyncCommand(_ =>
        {
            if (SpotifyArtworkResolver.TrySpotifyLink(_artworkSpotifyUri, out var link)) owner.OpenArtworkSource(link!);
            return Task.CompletedTask;
        }, owner.ShowError);
    }
    public MusicTrack Track { get; private set; }
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Duration => TimeSpan.FromSeconds(Track.DurationSeconds).ToString(@"m\:ss");
    public bool Liked => _owner.IsLiked(Track.Id);
    public string LikeLabel => _owner.Text[Liked ? "Unlike" : "Like"];
    public string PlayLabel => _owner.Text["Play"];
    public string AddPlaylistLabel => _owner.Text["AddPlaylist"];
    public string ArtworkSourceLabel => _owner.Text["ArtworkSourceSpotify"];
    public Bitmap? Artwork => _artwork;
    public bool HasArtwork => _artwork is not null;
    public ICommand OpenArtworkSource { get; }
    internal void UpdateMetadata(MusicTrack metadata)
    {
        Track = metadata; Raise(nameof(Title)); Raise(nameof(Artist));
    }
    internal void SetArtworkVisible(bool visible)
    {
        _visible = visible;
        if (visible) _ = EnsureArtworkAsync();
        else _artworkRequest?.Cancel();
    }
    internal Task EnsureArtworkAsync()
    {
        if (_disposed || _attempted) return Task.CompletedTask;
        if (_loading is not null) return _loading;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_owner.ArtworkLifetime);
        _artworkRequest = request;
        return _loading = LoadArtworkAsync(request);
    }
    private async Task LoadArtworkAsync(CancellationTokenSource request)
    {
        await Task.Yield();
        try
        {
            var result = await _owner.ResolveListArtworkAsync(Track, request.Token);
            if (_disposed || request.IsCancellationRequested) return;
            _owner.ApplyLocalMetadata(result.Metadata);
            if (result.Image is { Length: > 0 })
            {
                using var stream = new MemoryStream(result.Image, writable: false);
                Bitmap? bitmap;
                // Skia's scaled decoder can throw NullReferenceException for an
                // unrecognized encoded stream. Keep this guard at the decoder boundary.
                try { bitmap = Bitmap.DecodeToWidth(stream, 96, BitmapInterpolationMode.HighQuality); }
                catch (NullReferenceException) { bitmap = null; }
                if (_disposed || request.IsCancellationRequested) { bitmap?.Dispose(); return; }
                if (bitmap is not null)
                {
                    _artwork = bitmap; _artworkSpotifyUri = result.SpotifyUri;
                    Raise(nameof(Artwork)); Raise(nameof(HasArtwork));
                }
            }
            _attempted = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or HttpRequestException or ArgumentException or InvalidOperationException or NotSupportedException)
        { _attempted = true; /* Optional artwork cannot change playback/toast status. */ }
        finally
        {
            if (ReferenceEquals(_artworkRequest, request)) { _artworkRequest = null; _loading = null; }
            var cancelled = request.IsCancellationRequested;
            request.Dispose();
            if (cancelled && _visible && !_disposed && !_owner.ArtworkLifetime.IsCancellationRequested) _ = EnsureArtworkAsync();
        }
    }
    public void Dispose()
    {
        _disposed = true; _artworkRequest?.Cancel(); _artwork?.Dispose(); _artwork = null;
    }
    public void RefreshLike() { Raise(nameof(Liked)); Raise(nameof(LikeLabel)); }
    public string Color => (Track.Id.GetHashCode(StringComparison.Ordinal) % 3) switch
    { 0 => "#D95369", 1 => "#9D9FBF", _ => "#D2AE79" };
    public ICommand PlayCommand { get; }
    public ICommand LikeCommand { get; }
    public ICommand AddCommand { get; }
}

public sealed class PlaylistViewModel(string name, int count, ICommand openCommand, ICommand removeCommand, MainViewModel owner, bool isFavorites = false) : ObservableObject
{
    private bool _isActive;
    private int _count = count;
    public bool IsFavorites { get; } = isFavorites;
    public string Name => IsFavorites ? owner.Text["Liked"] : name;
    public int Count { get => _count; private set => Set(ref _count, value); }
    public ICommand OpenCommand { get; } = openCommand;
    public ICommand RemoveCommand { get; } = removeCommand;
    public string RemoveLabel => string.Format(owner.Text["RemovePlaylistNamed"], Name);
    public bool CanRemove => !IsFavorites && owner.CanManagePlaylists;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public void RefreshText() { Raise(nameof(Name)); Raise(nameof(RemoveLabel)); }
    public void RefreshCount(int count) => Count = count;
    public void RefreshAvailability() => Raise(nameof(CanRemove));
}
