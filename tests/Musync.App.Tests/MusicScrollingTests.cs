using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Musync.App;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static void PopulateScrollingLibrary(MainViewModel model)
    {
        foreach (var row in model.Tracks) row.Dispose();
        model.Tracks.Clear();
        for (var index = 0; index < 120; index++)
            model.Tracks.Add(new(new MusicTrack("local:scrolling" + index,
                $"Music collection {index + 1:000} · 音楽 · 我的音乐", "Synthetic artist", null, DurationSeconds: 180), model));
    }

    private static ScrollBar MusicBar(ScrollViewer viewer) => Assert.Single(viewer.GetVisualDescendants().OfType<ScrollBar>());

    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task MusicScrollbarAppearsOnHoverWithoutChangingContentAndScrollsByWheelAndDrag(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        PopulateScrollingLibrary(fixture.Model);
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            using var initial = window.CaptureRenderedFrame();
            // The first headless frame activates the window and reapplies the OS preference.
            window.ApplyMotionPreference(false);
            var list = window.FindControl<ItemsControl>("TrackList")!;
            var viewer = list.GetVisualAncestors().OfType<ScrollViewer>().First();
            var contentWidth = list.Bounds.Width; var viewport = viewer.Viewport;
            SaveReviewFrame(initial, $"scroll-{language}-{width}-idle.png");
            var bar = MusicBar(viewer);
            Assert.True(bar.IsVisible); Assert.Equal(0, bar.Opacity);
            Assert.Equal(Orientation.Vertical, bar.Orientation); Assert.InRange(bar.Bounds.Width, 14, 18);
            Assert.Equal(viewer.ScrollBarMaximum.Y, bar.Maximum, 3);
            Assert.Equal(viewer.Viewport.Height, bar.ViewportSize, 3);
            var point = list.TranslatePoint(new Point(100, 20), window)!.Value;
            window.MouseMove(point);
            Assert.Equal(1, bar.Opacity);
            using var hover = window.CaptureRenderedFrame();
            SaveReviewFrame(hover, $"scroll-{language}-{width}-hover.png");
            Assert.Equal(contentWidth, list.Bounds.Width); Assert.Equal(viewport, viewer.Viewport);
            window.MouseWheel(point, new Vector(0, -3)); using var wheeled = window.CaptureRenderedFrame();
            Assert.True(viewer.Offset.Y > 0); Assert.Equal(viewer.Offset.Y, bar.Value, 3);
            // The rail lies in the existing right margin, outside the song actions.
            var barLeft = bar.TranslatePoint(default, window)!.Value.X;
            Assert.True(barLeft >= list.TranslatePoint(new Point(list.Bounds.Width, 0), window)!.Value.X);
            var thumb = Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
            Assert.InRange(thumb.Bounds.Height, 36, bar.Bounds.Height);
            var grip = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseMove(grip); await Task.Delay(30);
            window.MouseDown(grip, MouseButton.Left);
            var beforeDrag = viewer.Offset.Y;
            var destination = new Point(grip.X - 25, grip.Y + 100);
            window.MouseMove(destination, RawInputModifiers.LeftMouseButton);
            using var dragged = window.CaptureRenderedFrame();
            Assert.True(viewer.Offset.Y > beforeDrag); Assert.Equal(1, bar.Opacity);
            // Pointer capture keeps the thumb usable after leaving the entire content pane.
            destination = new Point(12, destination.Y + 30);
            window.MouseMove(destination, RawInputModifiers.LeftMouseButton);
            using var outside = window.CaptureRenderedFrame(); Assert.Equal(1, bar.Opacity);
            window.MouseUp(destination, MouseButton.Left);
            window.MouseMove(new Point(12, 40)); await Task.Delay(60);
            Assert.Equal(0, bar.Opacity); Assert.Equal(contentWidth, list.Bounds.Width);
            Assert.False(fixture.Model.IsPlaying);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MusicScrollbarHidesWhenFilteringFitsTheViewportAndOtherPanelsKeepTheirOwnScrolling()
    {
        await using var fixture = new PresentationFixture();
        PopulateScrollingLibrary(fixture.Model);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            using var initial = window.CaptureRenderedFrame();
            var viewer = window.FindControl<ItemsControl>("TrackList")!.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.True(MusicBar(viewer).IsVisible);
            viewer.Offset = new Vector(0, viewer.Extent.Height);
            using var bottom = window.CaptureRenderedFrame(); Assert.True(viewer.Offset.Y > 0);
            fixture.Model.Search = "Original test";
            using var filtered = window.CaptureRenderedFrame();
            Assert.Single(fixture.Model.Tracks); Assert.Equal(0, viewer.ScrollBarMaximum.Y);
            Assert.False(MusicBar(viewer).IsVisible); Assert.Equal(0, viewer.Offset.Y);
            foreach (var name in new[] { "SidebarScroll", "LinkDeviceList", "SettingsContentScroll" })
                Assert.DoesNotContain(window.FindControl<ScrollViewer>(name)!.GetVisualDescendants().OfType<ScrollBar>(), b => b.IsVisible);
            fixture.Model.Navigation.Execute("playlists"); using var playlists = window.CaptureRenderedFrame();
            Assert.DoesNotContain(viewer.GetVisualDescendants().OfType<ScrollBar>(), b => b.IsVisible);
            fixture.Model.Search = "";
            fixture.Model.Navigation.Execute("library"); using var library = window.CaptureRenderedFrame();
            Assert.True(MusicBar(viewer).IsVisible);
            Assert.Equal(viewer.ScrollBarMaximum.Y, MusicBar(viewer).Maximum, 3);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MusicScrollbarFadesAndReducedMotionSettlesImmediatelyWhilePageScrollingStillWorks()
    {
        await using var fixture = new PresentationFixture();
        PopulateScrollingLibrary(fixture.Model);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(true);
        try
        {
            using var initial = window.CaptureRenderedFrame();
            var viewer = window.FindControl<ItemsControl>("TrackList")!.GetVisualAncestors().OfType<ScrollViewer>().First();
            var bar = MusicBar(viewer);
            Assert.NotNull(bar.Transitions);
            var point = viewer.TranslatePoint(new Point(100, 440), window)!.Value;
            window.MouseMove(point); await Task.Delay(260); Assert.Equal(1, bar.Opacity, 3);
            window.MouseMove(new Point(12, 40)); Assert.InRange(bar.Opacity, .001, 1);
            await Task.Delay(260); Assert.Equal(0, bar.Opacity, 3);
            window.ApplyMotionPreference(false); Assert.Null(bar.Transitions);
            window.MouseMove(point); Assert.Equal(1, bar.Opacity);
            bar.PageDown(); using var paged = window.CaptureRenderedFrame(); Assert.True(viewer.Offset.Y > 0);
            bar.ScrollToEnd(); using var end = window.CaptureRenderedFrame();
            Assert.Equal(viewer.ScrollBarMaximum.Y, viewer.Offset.Y, 3);
            bar.ScrollToHome(); using var home = window.CaptureRenderedFrame(); Assert.Equal(0, viewer.Offset.Y);
            window.MouseMove(new Point(12, 40)); Assert.Equal(0, bar.Opacity);
        }
        finally { window.Close(); }
    }
}
