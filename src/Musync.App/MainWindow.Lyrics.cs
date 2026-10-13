using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;

namespace Musync.App;

public partial class MainWindow
{
    private static readonly TransformOperations LyricRest = TransformOperations.Parse("translate(0px, 0px) scale(1)");
    private (string? Track, int Index)? _lyricCursor;
    private CancellationTokenSource? _lyricMotion;
    internal bool IsLyricFlowRunning => _lyricMotion is not null;
    private bool CanAnimateLyrics => !_closing && IsVisible && !IsMiniPlayer && WindowState != WindowState.Minimized
        && _posterInViewport && HeroScene.IsEffectivelyVisible && Classes.Contains("motionEnabled")
        && _linkModel is { IsPlaying: true, HasSyncedLyrics: true, ShowHero: true,
            SettingsOpen: false, PlaylistOpen: false, LinkPanelOpen: false, IsPreparingPlayback: false };

    private void UpdateLyricMotionState()
    {
        if (!CanAnimateLyrics) SettleLyricRows();
    }
    private void AdvanceLyricRows()
    {
        var cursor = _linkModel?.LyricCursor;
        if (_lyricCursor == cursor) return;
        var previous = _lyricCursor; _lyricCursor = cursor;
        SettleLyricRows();
        if (!CanAnimateLyrics || previous is not { } old || cursor is not { } current
            || old.Track != current.Track || old.Index < 0 || current.Index != old.Index + 1) return;
        var precedingDistance = CurrentLyric.Bounds.Center.Y - PreviousLyric.Bounds.Center.Y;
        var upcomingDistance = NextLyric.Bounds.Center.Y - CurrentLyric.Bounds.Center.Y;
        if (precedingDistance <= 0 || upcomingDistance <= 0) return;
        MoveLyricRow(PreviousLyric, precedingDistance, CurrentLyric.FontSize / PreviousLyric.FontSize, 1);
        MoveLyricRow(CurrentLyric, upcomingDistance, NextLyric.FontSize / CurrentLyric.FontSize, 1);
        MoveLyricRow(NextLyric, upcomingDistance, 1, 0);
        var motion = new CancellationTokenSource(); _lyricMotion = motion;
        _ = FinishLyricFlowAsync(motion);
    }
    private static void MoveLyricRow(TextBlock row, double distance, double scale, double opacity)
    {
        row.RenderTransformOrigin = new RelativePoint(0, .5, RelativeUnit.Relative);
        row.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translate(0px, {distance}px) scale({scale})"));
        row.Opacity = opacity;
        row.Transitions =
        [
            new TransformOperationsTransition { Property = RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(220), Easing = new ExponentialEaseOut() },
            new DoubleTransition { Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(180), Easing = new ExponentialEaseOut() }
        ];
        row.RenderTransform = LyricRest; row.Opacity = 1;
    }
    private async Task FinishLyricFlowAsync(CancellationTokenSource motion)
    {
        try { await Task.Delay(220, motion.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_lyricMotion, motion)) { _lyricMotion = null; SettleLyricRows(); }
            motion.Dispose();
        }
    }
    private void SettleLyricRows()
    {
        var motion = _lyricMotion; _lyricMotion = null; motion?.Cancel();
        foreach (var row in new[] { PreviousLyric, CurrentLyric, NextLyric })
        { row.Transitions = null; row.RenderTransform = LyricRest; row.Opacity = 1; }
    }
}
