using Musync.Core;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private sealed record InterruptedLocalPlayback(MusicTrack Track, long SourceVersion, double Position);
    private InterruptedLocalPlayback? _interruptedLocalPlayback;

    private double InterruptedLocalResumePosition => _interruptedLocalPlayback is { } interrupted
        && MatchesInterruptedLocalTrack(_current, interrupted) ? interrupted.Position : 0;

    private bool MatchesInterruptedLocalTrack(MusicTrack? track, InterruptedLocalPlayback interrupted) =>
        IsLocal && interrupted.SourceVersion == _sourceVersion
        && track?.Id == interrupted.Track.Id && track.FilePath == interrupted.Track.FilePath;

    private void KeepLocalRetryPosition(MusicTrack? track, double position)
    {
        if (_interruptedLocalPlayback is { } interrupted
            && (!MatchesInterruptedLocalTrack(track, interrupted) || position != interrupted.Position))
            _interruptedLocalPlayback = null;
    }

    private void RememberInterruptedLocalPlayback(double position, double duration)
    {
        if (!IsLocal || _current?.FilePath is null || duration <= 0 || !double.IsFinite(duration) || !double.IsFinite(position)) return;
        Position = Math.Clamp(position, 0, duration); Duration = duration;
        _interruptedLocalPlayback = new(_current, _sourceVersion, Position);
    }
}
