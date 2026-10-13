using Musync.Core;

namespace Musync.App;

internal enum TrackPlaybackPhase { None, Preparing, Playing, Paused }

public sealed partial class MainViewModel
{
    internal TrackPlaybackPhase TrackPhase(MusicTrack track)
    {
        if ((!IsLocal && !SpotifyMode) || IsSwitchingSource || _current?.Id != track.Id) return TrackPlaybackPhase.None;
        if (IsPreparingPlayback) return TrackPlaybackPhase.Preparing;
        return !_session ? TrackPlaybackPhase.None : IsPlaying ? TrackPlaybackPhase.Playing : TrackPlaybackPhase.Paused;
    }
    internal async Task ActivateTrackAsync(MusicTrack track)
    {
        if (_disposed || Busy) return;
        if (_session && _current?.Id == track.Id && (IsLocal || SpotifyMode)) await TogglePlayback();
        else await PlayTrackAsync(track);
    }
    private void RefreshTrackPlayback()
    {
        foreach (var row in Tracks) row.RefreshPlayback();
    }
}
