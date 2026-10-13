namespace Musync.App;

// API reads are intermittent observations. Small differences change the clock's
// speed briefly instead of moving the visible playhead backwards on every poll.
internal sealed class PlaybackProgressClock(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _anchorTime;
    private double _anchor, _correction, _duration;
    private bool _playing, _initialized;
    private const double CorrectionSeconds = 5;

    public double Position
    {
        get
        {
            var elapsed = _playing ? _time.GetElapsedTime(_anchorTime).TotalSeconds : 0;
            return Math.Clamp(_anchor + elapsed + _correction * Math.Min(elapsed / CorrectionSeconds, 1), 0, _duration);
        }
    }

    public void Anchor(double seconds, bool playing, double duration)
    {
        _duration = Math.Max(0, duration);
        _anchor = Math.Clamp(seconds, 0, _duration);
        _anchorTime = _time.GetTimestamp();
        _playing = playing; _initialized = true; _correction = 0;
    }

    // Returns true for an intentional discontinuity: a new track, seek or pause.
    public bool Synchronize(double seconds, bool playing, double duration, bool reset)
    {
        var predicted = Position;
        var sample = Math.Clamp(seconds, 0, Math.Max(0, duration));
        var difference = sample - predicted;
        var snap = !_initialized || reset || playing != _playing || !playing || Math.Abs(difference) > 2;
        Anchor(snap ? sample : predicted, playing, duration);
        if (!snap) _correction = Math.Clamp(difference, -1, 1);
        return snap;
    }
}
