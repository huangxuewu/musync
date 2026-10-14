using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1120, 720)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    [InlineData("en", 1460, 940)]
    [InlineData("zh-Hans", 1460, 940)]
    [InlineData("ja", 1460, 940)]
    public async Task TimeDigitsAndDurationChangesKeepThePlaybackAxisInTheSamePlace(string language, int width, int height)
    {
        static string Snapshot(int position, int duration = 180_000)
        {
            var track = JsonNode.Parse(TrackJson)!; track["duration_ms"] = duration;
            return PlaybackJson(playing: false, position: position, track: track.ToJsonString());
        }
        var state = Snapshot(0);
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, state)));
        await using var fixture = new Fixture(handler, language, observePlayback: true);
        var model = fixture.Model; await SourceTransitionSettled(model); await model.RefreshSpotifyPlaybackAsync();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            var slider = window.FindControl<PlaybackSlider>("SeekSlider")!;
            using (var initial = window.CaptureRenderedFrame()) SaveReviewFrame(initial, $"axis-{language}-{width}-initial.png");
            var track = slider.GetVisualDescendants().OfType<Track>().Single();
            var baseline = new Rect(track.TranslatePoint(default, window)!.Value, track.Bounds.Size);
            Assert.True(baseline.Width > 50);
            foreach (var (position, duration) in new[] { (11_000, 180_000), (59_000, 180_000), (60_000, 180_000), (71_000, 180_000), (111_000, 600_000), (599_000, 3_599_000) })
            {
                state = Snapshot(position, duration); await model.RefreshSpotifyPlaybackAsync();
                using var frame = window.CaptureRenderedFrame();
                if (position == 599_000) SaveReviewFrame(frame, $"axis-{language}-{width}-long-time.png");
                var bounds = new Rect(track.TranslatePoint(default, window)!.Value, track.Bounds.Size);
                Assert.Equal(baseline.X, bounds.X, precision: 4); Assert.Equal(baseline.Width, bounds.Width, precision: 4);
            }
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (var mini = window.CaptureRenderedFrame()) SaveReviewFrame(mini, $"axis-{language}-{width}-mini.png");
            var miniSlider = window.FindControl<PlaybackSlider>("MiniSeekSlider")!;
            var miniTrack = miniSlider.GetVisualDescendants().OfType<Track>().Single();
            var miniBaseline = new Rect(miniTrack.TranslatePoint(default, window)!.Value, miniTrack.Bounds.Size);
            state = Snapshot(0); await model.RefreshSpotifyPlaybackAsync();
            using var final = window.CaptureRenderedFrame();
            var miniBounds = new Rect(miniTrack.TranslatePoint(default, window)!.Value, miniTrack.Bounds.Size);
            Assert.Equal(miniBaseline.X, miniBounds.X, precision: 4); Assert.Equal(miniBaseline.Width, miniBounds.Width, precision: 4);
        }
        finally { window.Close(); }
    }
}
