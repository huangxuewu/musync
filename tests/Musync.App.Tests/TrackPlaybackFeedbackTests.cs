using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static void InvalidateTrackReviewFrame(Window window)
    {
        // Request a complete Skia snapshot rather than capturing a retained,
        // partially repainted headless frame during a live activity animation.
        foreach (var control in window.GetVisualDescendants().OfType<Control>()) control.InvalidateVisual();
    }
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task TrackRowsFollowPlaybackAndToggleWithoutRestartingWhileMotionFollowsVisibility(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language, title: language == "ja" ? new string('音', 40) + " · 夜に駆ける" : null);
        var model = fixture.Model;
        var row = model.Tracks.Single(t => t.Track.Id == fixture.First.Id);
        Assert.False(row.IsCurrentTrack);
        await model.PlayTrackAsync(fixture.First, 43);
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show();
        PreparationIndicator? wave = null;
        try
        {
            await Task.Delay(250); window.ApplyMotionPreference(true); using var initial = window.CaptureRenderedFrame();
            var button = window.FindControl<ItemsControl>("TrackList")!.GetVisualDescendants().OfType<Button>().Single(b => b.Command == row.PlayCommand);
            var grid = button.GetVisualAncestors().OfType<Grid>().First();
            wave = grid.GetVisualDescendants().OfType<PreparationIndicator>().Single();
            Assert.True(row.IsCurrentTrack); Assert.True(row.IsRowPlaying); Assert.True(wave.AnimationRunning);
            Assert.Equal(string.Format(model.Text["PauseTrack"], row.Title), AutomationProperties.GetName(button));
            Assert.Equal(model.Text["Playing"], row.PlaybackStatus);
            Assert.Contains("current", grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("trackTitle")).Classes);
            window.MouseMove(button.TranslatePoint(new Point(24, 24), window)!.Value); await Task.Delay(200);
            InvalidateTrackReviewFrame(window);
            using var playing = window.CaptureRenderedFrame(); SaveReviewFrame(playing, $"track-playing-{language}-{width}.png");
            var overlay = button.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("trackPlayOverlay"));
            Assert.Equal(1, overlay.Opacity, 3);
            Assert.Equal("pause", overlay.GetVisualDescendants().OfType<LucideIcon>().Single().Kind);

            ClickPlaylistControl(window, button); await CommandFinished(row.PlayCommand); await Task.Delay(180);
            InvalidateTrackReviewFrame(window);
            using var paused = window.CaptureRenderedFrame(); SaveReviewFrame(paused, $"track-paused-{language}-{width}.png");
            Assert.False(model.IsPlaying); Assert.Equal(43, model.Position); Assert.Equal(43, fixture.Backend.Position);
            Assert.True(row.IsRowPaused); Assert.False(wave.AnimationRunning); Assert.False(wave.IsVisible);
            Assert.Equal(string.Format(model.Text["PlayTrack"], row.Title), AutomationProperties.GetName(button));
            Assert.Equal("play", overlay.GetVisualDescendants().OfType<LucideIcon>().Single().Kind);
            ClickPlaylistControl(window, button); await CommandFinished(row.PlayCommand);
            Assert.True(model.IsPlaying); Assert.True(row.IsRowPlaying); Assert.Equal(43, model.Position);

            window.ApplyMotionPreference(false); Assert.False(wave.AnimationRunning);
            Assert.True(row.IsCurrentTrack); Assert.Null(overlay.Transitions);
            window.ApplyMotionPreference(true); Assert.True(wave.AnimationRunning);
            window.WindowState = WindowState.Minimized; Assert.False(wave.AnimationRunning);
            window.WindowState = WindowState.Normal; Assert.True(wave.AnimationRunning);
            model.Navigation.Execute("playlists"); Assert.False(wave.AnimationRunning);
            model.Navigation.Execute("library"); await Task.Delay(180); using var library = window.CaptureRenderedFrame();
            row = model.Tracks.Single(t => t.Track.Id == fixture.First.Id); Assert.True(row.IsRowPlaying);
            wave = window.FindControl<ItemsControl>("TrackList")!.GetVisualDescendants().OfType<PreparationIndicator>().Single(w => w.Active);
            Assert.True(wave.AnimationRunning);
            var next = model.Tracks.First(t => t.Track.Id != fixture.First.Id);
            next.PlayCommand.Execute(null); await CommandFinished(next.PlayCommand);
            Assert.True(next.IsRowPlaying); Assert.False(row.IsCurrentTrack); Assert.Equal(0, model.Position);
            Assert.False(wave.AnimationRunning);
        }
        finally { window.Close(); }
        Assert.False(wave?.AnimationRunning ?? false);
    }

    [AvaloniaFact]
    public async Task PreparingTrackShowsPendingActivityAndCancellingClearsItWithoutStartingAudio()
    {
        await using var fixture = new PresentationFixture();
        fixture.Backend.SourcePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = fixture.Model.Tracks.Single(t => t.Track.Id == fixture.First.Id);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); await Task.Delay(250); window.ApplyMotionPreference(true);
        var start = fixture.Model.PlayTrackAsync(fixture.First, 12);
        try
        {
            await Task.Delay(180); using var waiting = window.CaptureRenderedFrame();
            Assert.True(row.IsCurrentTrack); Assert.False(row.IsRowPlaying); Assert.True(row.HasPlaybackActivity);
            Assert.False(row.CanPlay); Assert.False(row.PlayCommand.CanExecute(null));
            Assert.Equal(fixture.Model.Text["PreparingSpeakers"], row.PlaybackStatus);
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
            fixture.Model.CancelPreparation.Execute(null); await start.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(row.IsCurrentTrack); Assert.True(row.CanPlay); Assert.False(fixture.Model.IsPlaying);
        }
        finally { fixture.Backend.SourcePreparation.TrySetResult(); await start; window.Close(); }
    }

    [AvaloniaFact]
    public async Task SpotifyRowTogglePausesThenResumesTheQueueWithoutResendingItsTrackUri()
    {
        var commands = new List<(string Path, string Body)>();
        var handler = new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + TrackJson + "]}}");
            commands.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            return Response(HttpStatusCode.NoContent);
        });
        await using var fixture = new Fixture(handler);
        fixture.Model.Search = "song"; fixture.Model.SearchCommand.Execute(null); await CommandFinished(fixture.Model.SearchCommand);
        var row = Assert.Single(fixture.Model.Tracks);
        row.PlayCommand.Execute(null); await CommandFinished(row.PlayCommand);
        Assert.True(row.IsRowPlaying); Assert.Contains("spotify:track:song", commands[0].Body);
        row.PlayCommand.Execute(null); await CommandFinished(row.PlayCommand); Assert.True(row.IsRowPaused);
        row.PlayCommand.Execute(null); await CommandFinished(row.PlayCommand); Assert.True(row.IsRowPlaying);
        Assert.Equal(new[] { "/v1/me/player/play", "/v1/me/player/pause", "/v1/me/player/play" }, commands.Select(c => c.Path));
        Assert.DoesNotContain("uris", commands[2].Body);
    }
}
