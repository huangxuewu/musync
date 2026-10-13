using Musync.Core;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private long _spotifyStateVersion;
    private readonly PlaybackProgressClock _spotifyProgress = new();
    private readonly SemaphoreSlim _spotifySeekGate = new(1);
    private int _spotifySeeksPending;
    private DateTime _spotifySeekGuardUntil;
    private long _progressEpoch;
    public long ProgressEpoch => _progressEpoch;
    private void ResetProgressTransition() { _progressEpoch++; Raise(nameof(ProgressEpoch)); }

    private void AnchorSpotifyProgress(double seconds)
    {
        _spotifyProgress.Anchor(seconds, IsPlaying, Duration);
    }

    internal void AdvanceSpotifyProgress()
    {
        if (!SpotifyMode || !IsPlaying || IsSeeking || Duration <= 0) return;
        Position = _spotifyProgress.Position;
    }

    // Spotify can start in its desktop app or on a Connect receiver. Observing it
    // never requires Musync to have issued Play, and never starts audio itself.
    internal async Task RefreshSpotifyPlaybackAsync()
    {
        if (_disposed || !SpotifyMode || !_spotify.Connected || _spotifyPolling || Busy || _spotifySeeksPending > 0) return;
        var version = _spotifyStateVersion;
        var volumeVersion = _volumeEditVersion;
        var volumeWasPending = _volumePending;
        _spotifyPolling = true;
        _spotifyNextPoll = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var state = await _spotify.PlaybackAsync(_lifetime.Token);
            if (_disposed || !SpotifyMode || !_spotify.Connected || version != _spotifyStateVersion) return;
            ApplySpotifyVolume(state, volumeVersion, volumeWasPending);
            if (state.Track is not { } track)
            {
                ClearSpotifyPlayback(resetVolume: state.DeviceId is null);
                Status = Text["Idle"];
                return;
            }
            var trackChanged = track.Id != _current?.Id;
            var artworkChanged = trackChanged || track.ImageUrl != _current?.ImageUrl;
            var playingChanged = IsPlaying != state.Playing;
            _current = track;
            _session = true;
            Duration = track.DurationSeconds;
            IsPlaying = state.Playing;
            Shuffle = state.Shuffle; Repeat = state.RepeatState is "track" or "context";
            // A Connect command may succeed before its next read reflects the
            // seek. Preserve our confirmed target briefly, without hiding metadata.
            var staleSeek = !trackChanged && !playingChanged && DateTime.UtcNow < _spotifySeekGuardUntil
                && Math.Abs(state.Position - _spotifyProgress.Position) > 2;
            if (!IsSeeking && !staleSeek)
            {
                _spotifySeekGuardUntil = default;
                if (_spotifyProgress.Synchronize(state.Position, state.Playing, Duration, trackChanged)) ResetProgressTransition();
                Position = _spotifyProgress.Position;
            }
            Status = Text[IsPlaying ? "Playing" : "Paused"];
            RefreshCurrent();
            // Metadata and controls update immediately, even if the CDN is slow.
            if (artworkChanged) _ = RunSafely(() => LoadArtwork(track));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && SpotifyMode && version == _spotifyStateVersion)
            {
                ShowError(ex); _spotifyNextPoll = DateTime.UtcNow.AddSeconds(30);
            }
        }
        finally { _spotifyPolling = false; }
    }

    private void ClearSpotifyPlayback(bool resetVolume = true)
    {
        if (resetVolume) ResetSpotifyVolume();
        _artworkRequest?.Cancel(); SpotifyArtwork = null; _current = null;
        _session = false; IsPlaying = false; IsSeeking = false;
        ResetProgressTransition(); _spotifySeekGuardUntil = default;
        Duration = 0; Position = 0; Shuffle = false; Repeat = false;
        AnchorSpotifyProgress(0); RefreshCurrent();
    }

    private async Task ToggleSpotifyOptionAsync(bool shuffle)
    {
        var enabled = !(shuffle ? Shuffle : Repeat);
        if (SpotifyMode)
        {
            if (!_spotify.Connected) { SettingsOpen = true; return; }
            _spotifyStateVersion++;
            if (shuffle) await _spotify.ShuffleAsync(enabled, _lifetime.Token);
            else await _spotify.RepeatAsync(enabled, _lifetime.Token);
            _spotifyNextPoll = DateTime.UtcNow.AddSeconds(1);
        }
        if (shuffle) Shuffle = enabled; else Repeat = enabled;
    }
}
