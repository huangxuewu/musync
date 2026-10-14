using Musync.Core;

namespace Musync.App;

internal enum TrackPlaybackPhase { None, Preparing, Playing, Paused, Failed }

public sealed partial class MainViewModel
{
    private sealed record LocalTrackFailure(MusicTrack Track, long SourceVersion, string Detail);
    private LocalTrackFailure? _localTrackFailure;

    internal string? TrackPlaybackError(MusicTrack track) => IsLocal && !IsSwitchingSource
        && _localTrackFailure is { } failure && failure.SourceVersion == _sourceVersion
        && _current?.Id == track.Id && failure.Track.Id == track.Id
        && failure.Track.FilePath == track.FilePath ? failure.Detail : null;

    private void RememberLocalTrackFailure(MusicTrack? track, string detail)
    {
        if (!IsLocal || track?.FilePath is null || _current?.Id != track.Id || _current.FilePath != track.FilePath) return;
        _localTrackFailure = new(track, _sourceVersion, detail);
        Raise(nameof(PlayLabel)); RefreshTrackPlayback();
    }
    private void ClearLocalTrackFailure()
    {
        if (_localTrackFailure is null) return;
        _localTrackFailure = null;
        Raise(nameof(PlayLabel)); RefreshTrackPlayback();
    }
    internal TrackPlaybackPhase TrackPhase(MusicTrack track)
    {
        if ((!IsLocal && !SpotifyMode) || IsSwitchingSource || _current?.Id != track.Id) return TrackPlaybackPhase.None;
        if (IsPreparingPlayback) return TrackPlaybackPhase.Preparing;
        return !_session ? TrackPlaybackError(track) is not null ? TrackPlaybackPhase.Failed : TrackPlaybackPhase.None
            : IsPlaying ? TrackPlaybackPhase.Playing : TrackPlaybackPhase.Paused;
    }
    internal async Task ActivateTrackAsync(MusicTrack track)
    {
        if (_disposed || Busy) return;
        if (_current?.Id == track.Id && (IsLocal || SpotifyMode)
            && (_session || TrackPlaybackError(track) is not null)) await TogglePlayback();
        else await PlayTrackAsync(track);
    }
    private void RefreshTrackPlayback()
    {
        foreach (var row in Tracks) row.RefreshPlayback();
    }
}
