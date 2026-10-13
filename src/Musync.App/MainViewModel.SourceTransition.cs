namespace Musync.App;

public sealed partial class MainViewModel
{
    private long _sourceVersion;
    private bool _switchingSource;
    private Task _sourceTransition = Task.CompletedTask;
    private CancellationTokenSource? _sourceLookup;

    public bool IsSwitchingSource
    {
        get => _switchingSource;
        private set
        {
            var previous = Busy;
            if (!Set(ref _switchingSource, value)) return;
            if (previous != Busy) Raise(nameof(Busy));
            Raise(nameof(PlayLabel)); Raise(nameof(MusicSourceHint));
            RefreshBusyProperties();
        }
    }
    public string MusicSourceHint => Text[IsSwitchingSource ? "SwitchingSource" : "MusicSource"];
    private bool OwnsSource(long version) => !_disposed && version == _sourceVersion;
    private void RefreshBusyProperties()
    {
        foreach (var name in new[] { nameof(CanPlayLink), nameof(CanConfigureLink), nameof(CanEditLink),
            nameof(CanSaveLink), nameof(CanManagePlaylists), nameof(PlayEnabled), nameof(Seekable) }) Raise(name);
        foreach (var playlist in Playlists) playlist.RefreshAvailability();
        foreach (var track in Tracks) track.RefreshAvailability();
        foreach (var station in RadioStations) station.RefreshAvailability();
    }
    private async Task SwitchSourceAsync(long version)
    {
        var acquired = false;
        try
        {
            // The old Play/Pause must settle before Stop, and Stop must settle
            // before admitting playback for the new source. Rapid choices keep
            // only the latest queued transition; an in-flight Stop still finishes.
            await _playGate.WaitAsync(_lifetime.Token); acquired = true;
            if (!OwnsSource(version)) return;
            await StopAsync(resetStatus: false);
            if (!OwnsSource(version) || !SpotifyMode) return;
            if (!_spotify.Connected) ShowSpotifySettings();
            else
            {
                using var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _sourceLookup = lookup;
                try { await RefreshSpotifyDevicesAsync(reportStatus: false, lookup.Token); }
                finally { if (ReferenceEquals(_sourceLookup, lookup)) _sourceLookup = null; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (OwnsSource(version)) ShowError(ex);
        }
        finally
        {
            if (acquired) _playGate.Release();
            if (version == _sourceVersion)
            {
                if (!_disposed && Status == Text["SwitchingSource"]) Status = Text["Idle"];
                IsSwitchingSource = false;
            }
        }
        if (OwnsSource(version) && SpotifyMode) await RefreshSpotifyPlaybackAsync();
    }
}
