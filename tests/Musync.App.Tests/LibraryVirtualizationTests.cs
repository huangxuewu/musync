using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("zh-Hans", 1280, 940)]
    [InlineData("ja", 1120, 720)]
    public async Task LargeLibraryRealizesViewportRowsAndRecyclingKeepsTheCorrectSongActions(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model;
        foreach (var row in model.Tracks) row.Dispose();
        model.Tracks.Clear();
        for (var index = 0; index < 1000; index++)
            model.Tracks.Add(new(new MusicTrack("local:large-library" + index,
                "Synthetic library song " + index, "Unknown artist", null, DurationSeconds: 180), model));
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(150); using var top = window.CaptureRenderedFrame();
            var list = window.FindControl<ItemsControl>("TrackList")!;
            TrackArtworkImage[] Rows() => list.GetVisualDescendants().OfType<TrackArtworkImage>().ToArray();
            Assert.InRange(Rows().Length, 1, 40);
            Assert.Contains(Rows(), image => image.DataContext == model.Tracks[0]);
            Assert.DoesNotContain(Rows(), image => image.DataContext == model.Tracks[999]);
            SaveReviewFrame(top, $"virtualized-library-{language}-{width}-top.png");
            var scroll = list.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await Task.Delay(150); using var bottom = window.CaptureRenderedFrame();
            Assert.InRange(Rows().Length, 1, 40);
            var last = Assert.Single(Rows(), image => image.DataContext == model.Tracks[999]);
            var row = (TrackViewModel)last.DataContext!;
            var like = list.GetVisualDescendants().OfType<Button>().Single(button => button.Command == row.LikeCommand);
            like.Command!.Execute(null);
            Assert.Contains("local:large-library999", fixture.Settings.LikedTracks);
            Assert.True(row.Liked); Assert.False(model.Tracks[0].Liked);
            Assert.False(model.IsPlaying);
            SaveReviewFrame(bottom, $"virtualized-library-{language}-{width}-bottom.png");
            scroll.Offset = default;
            await Task.Delay(150); using var returned = window.CaptureRenderedFrame();
            Assert.Contains(Rows(), image => image.DataContext == model.Tracks[0]);
            Assert.DoesNotContain(Rows(), image => image.DataContext == model.Tracks[999]);
            Assert.InRange(Rows().Length, 1, 40);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await Task.Delay(150); window.CaptureRenderedFrame()?.Dispose();
            model.Search = "Original test";
            await Task.Delay(150); using var filtered = window.CaptureRenderedFrame();
            Assert.Single(model.Tracks);
            var filteredImage = Assert.Single(Rows());
            Assert.Same(model.Tracks[0], filteredImage.DataContext);
            var location = filteredImage.TranslatePoint(default, scroll)!.Value;
            Assert.InRange(location.Y, 0, scroll.Viewport.Height - 48);
            Assert.False(model.IsPlaying);
        }
        finally { window.Close(); }
    }
}
