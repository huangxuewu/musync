using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using System.Diagnostics;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task CoverHoverKeepsTheArtworkSurfaceAndHitTargetStable(string language)
    {
        await using var fixture = new PresentationFixture(language);
        var row = fixture.Model.Tracks.Single(t => t.Track.Id == fixture.First.Id);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 }; window.Show();
        window.ApplyMotionPreference(true);
        try
        {
            using var ready = window.CaptureRenderedFrame();
            var cover = window.FindControl<ItemsControl>("TrackList")!.GetVisualDescendants().OfType<Button>().Single(b => b.Command == row.PlayCommand);
            var image = cover.GetVisualDescendants().OfType<TrackArtworkImage>().Single(); image.Source = fixture.Model.PosterArtwork;
            var presenter = cover.GetVisualDescendants().OfType<ContentPresenter>().Single();
            var color = ((ISolidColorBrush)cover.Background!).Color; var bounds = cover.Bounds;
            var point = cover.TranslatePoint(new Point(24, 24), window)!.Value;
            using (var idle = CaptureTrackReviewFrame(window)) SaveReviewFrame(idle, $"cover-{language}-idle.png");
            window.MouseMove(point); await Task.Delay(250);
            using (var hover = CaptureTrackReviewFrame(window)) SaveReviewFrame(hover, $"cover-{language}-hover.png");
            Assert.Equal(color, ((ISolidColorBrush)presenter.Background!).Color); Assert.Equal(bounds, cover.Bounds);
            window.MouseDown(point, MouseButton.Left);
            Assert.Equal(1, cover.Opacity); Assert.Equal(bounds, cover.Bounds);
            window.MouseMove(new Point(12, 40)); window.MouseUp(new Point(12, 40), MouseButton.Left);
            await Task.Delay(250); using var restored = window.CaptureRenderedFrame();
            Assert.Equal(color, ((ISolidColorBrush)presenter.Background!).Color); Assert.Equal(1, cover.Opacity);
            Assert.False(fixture.Model.IsPlaying);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task CoverHoverReversesWithoutSnappingAndReducedMotionShowsTheFinalState()
    {
        await using var fixture = new PresentationFixture();
        var row = fixture.Model.Tracks.Single(t => t.Track.Id == fixture.First.Id);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 }; window.Show(); window.ApplyMotionPreference(true);
        try
        {
            using var ready = window.CaptureRenderedFrame();
            var cover = window.FindControl<ItemsControl>("TrackList")!.GetVisualDescendants().OfType<Button>().Single(b => b.Command == row.PlayCommand);
            var overlay = cover.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("trackPlayOverlay"));
            var point = cover.TranslatePoint(new Point(24, 24), window)!.Value;
            window.MouseMove(point); await Task.Delay(300); Assert.Equal(1, overlay.Opacity, 3);
            window.MouseMove(new Point(12, 40)); Assert.InRange(overlay.Opacity, .001, 1);
            await Task.Delay(45); var fading = overlay.Opacity; var sampled = Stopwatch.GetTimestamp();
            window.MouseMove(point);
            // Headless pointer input can pump another render frame before returning.
            var elapsed = Stopwatch.GetElapsedTime(sampled).TotalSeconds;
            Assert.InRange(Math.Abs(overlay.Opacity - fading), 0, elapsed * 10 + .02);
            await Task.Delay(300); Assert.Equal(1, overlay.Opacity, 3);
            window.ApplyMotionPreference(false); Assert.Null(overlay.Transitions);
            window.MouseMove(new Point(12, 40)); Assert.Equal(0, overlay.Opacity);
            window.MouseMove(point); Assert.Equal(1, overlay.Opacity);
            window.MouseMove(new Point(12, 40));
            var focusTarget = window.FindControl<TextBox>("MusicSearchBox")!; focusTarget.Focus();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            cover.Focus(NavigationMethod.Tab); Assert.Equal(1, overlay.Opacity);
        }
        finally { window.Close(); }
    }
}
