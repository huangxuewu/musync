using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Musync.App;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static string PlaybackJson(bool playing = true, int position = 75_000, string? track = null,
        bool shuffle = false, string repeat = "off") =>
        "{\"item\":" + (track ?? TrackJson) + ",\"progress_ms\":" + position
        + ",\"is_playing\":" + playing.ToString().ToLowerInvariant()
        + ",\"shuffle_state\":" + shuffle.ToString().ToLowerInvariant() + ",\"repeat_state\":\"" + repeat + "\"}";

    [AvaloniaFact]
    public async Task SelectingSpotifyObservesExternalPlaybackWithoutStartingMusic()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/me/player", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(HttpStatusCode.OK, PlaybackJson(shuffle: true, repeat: "context")));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        Assert.Equal("Song", fixture.Model.Title); Assert.Equal("Artist", fixture.Model.Artist);
        Assert.True(fixture.Model.IsPlaying); Assert.True(fixture.Model.Seekable);
        Assert.Equal("pause", fixture.Model.PlayIcon); Assert.Equal("1:15", fixture.Model.PositionText);
        Assert.Equal("3:00", fixture.Model.DurationText); Assert.Equal(fixture.Model.Text["Playing"], fixture.Model.Status);
        Assert.True(fixture.Model.Shuffle); Assert.True(fixture.Model.Repeat);
        Assert.Equal(1, handler.Calls);
    }

    [AvaloniaFact]
    public async Task ObservedPausedTrackUsesResumeAndThenPauseRatherThanRestartingATrack()
    {
        var methods = new List<string>();
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, PlaybackJson(playing: false)));
            methods.Add(request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        Assert.False(fixture.Model.IsPlaying); Assert.True(fixture.Model.Seekable);
        Assert.Equal(fixture.Model.Text["Paused"], fixture.Model.Status);
        fixture.Model.PlayCommand.Execute(null); await CommandFinished(fixture.Model.PlayCommand);
        Assert.True(fixture.Model.IsPlaying);
        fixture.Model.PlayCommand.Execute(null); await CommandFinished(fixture.Model.PlayCommand);
        Assert.False(fixture.Model.IsPlaying);
        Assert.Equal(["/v1/me/player/play", "/v1/me/player/pause"], methods);
        Assert.Equal("1:15", fixture.Model.PositionText);
    }

    [AvaloniaFact]
    public async Task ProgressUsesElapsedTimeAndResynchronizesAfterExternalSeekAndPause()
    {
        var state = PlaybackJson();
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, state)));
        await using var fixture = new Fixture(handler, observePlayback: true);
        await Task.Delay(1100);
        fixture.Model.AdvanceSpotifyProgress();
        Assert.InRange(fixture.Model.Position, 76, 78);
        state = PlaybackJson(playing: false, position: 140_000);
        await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.Equal("2:20", fixture.Model.PositionText);
        await Task.Delay(350); fixture.Model.AdvanceSpotifyProgress();
        Assert.Equal(140, fixture.Model.Position); Assert.False(fixture.Model.IsPlaying);
    }

    [AvaloniaFact]
    public async Task SlowCoverDoesNotDelayMetadataOrBlockFollowingTrackUpdates()
    {
        var cover = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = PlaybackJson(track: TrackJson.Replace("\"images\":[]", "\"images\":[{\"url\":\"https://i.scdn.co/image/test\"}]"));
        var handler = new Handler((request, _) => request.RequestUri!.Host == "api.spotify.com"
            ? Task.FromResult(Response(HttpStatusCode.OK, state)) : cover.Task);
        await using var fixture = new Fixture(handler, observePlayback: true);
        try
        {
            Assert.Equal("Song", fixture.Model.Title); Assert.Equal("1:15", fixture.Model.PositionText);
            Assert.True(fixture.Model.IsPlaying); Assert.Null(fixture.Model.SpotifyArtwork);
            state = PlaybackJson(playing: false, position: 10_000, track: TrackJson.Replace("song", "next").Replace("Song", "Next song").Replace("180000", "240000"));
            await fixture.Model.RefreshSpotifyPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("Next song", fixture.Model.Title); Assert.Equal("4:00", fixture.Model.DurationText);
            Assert.Equal("0:10", fixture.Model.PositionText); Assert.False(fixture.Model.IsPlaying);
        }
        finally { cover.TrySetResult(Response(HttpStatusCode.ServiceUnavailable)); await Task.Yield(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoPlaybackClearsOldMetadataArtworkAndSeekState(bool emptyResponse)
    {
        var state = PlaybackJson();
        var handler = new Handler((_, _) => Task.FromResult(state.Length == 0
            ? Response(HttpStatusCode.NoContent) : Response(HttpStatusCode.OK, state)));
        await using var fixture = new Fixture(handler, observePlayback: true);
        fixture.Model.IsSeeking = true;
        state = emptyResponse ? "" : "{\"item\":null,\"progress_ms\":null,\"is_playing\":false}";
        await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.Equal("Spotify", fixture.Model.Title); Assert.Equal("0:00", fixture.Model.PositionText);
        Assert.Equal("0:00", fixture.Model.DurationText); Assert.False(fixture.Model.IsPlaying);
        Assert.False(fixture.Model.Seekable); Assert.False(fixture.Model.IsSeeking); Assert.False(fixture.Model.HasSpotifyArtwork);
    }

    [AvaloniaFact]
    public async Task MetadataContinuesUpdatingWhileTheSeekThumbIsHeld()
    {
        var state = PlaybackJson();
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, state)));
        await using var fixture = new Fixture(handler, observePlayback: true);
        var heldPosition = fixture.Model.Position;
        fixture.Model.IsSeeking = true;
        state = PlaybackJson(playing: false, position: 100_000);
        await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.False(fixture.Model.IsPlaying); Assert.Equal("play", fixture.Model.PlayIcon);
        Assert.Equal(heldPosition, fixture.Model.Position);
        fixture.Model.IsSeeking = false;
        await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.Equal(100, fixture.Model.Position);
    }

    [AvaloniaFact]
    public async Task LateSpotifyResponseCannotOverwriteAnotherSource()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => pending.Task);
        await using var fixture = new Fixture(handler, observePlayback: true);
        fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "local");
        var localTitle = fixture.Model.Title;
        pending.SetResult(Response(HttpStatusCode.OK, PlaybackJson()));
        await Task.Delay(50);
        Assert.Equal(localTitle, fixture.Model.Title); Assert.True(fixture.Model.IsLocal);
        Assert.False(fixture.Model.IsPlaying); Assert.Null(fixture.Model.SpotifyArtwork);
    }

    [AvaloniaFact]
    public async Task ReadFailurePreservesTheLastTrackAndTheNextSuccessRecovers()
    {
        var fail = false;
        var handler = new Handler((_, _) => Task.FromResult(fail
            ? Response(HttpStatusCode.ServiceUnavailable, "{}") : Response(HttpStatusCode.OK, PlaybackJson())));
        await using var fixture = new Fixture(handler, observePlayback: true);
        fail = true; await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.Equal("Song", fixture.Model.Title); Assert.Contains(fixture.Model.Text["SpotifyRequestFailed"], fixture.Model.Status);
        fail = false; await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.Equal(fixture.Model.Text["Playing"], fixture.Model.Status);
    }

    [AvaloniaFact]
    public async Task SeekAndNextRefreshProgressAndTrackDuration()
    {
        var state = PlaybackJson(); var paths = new List<string>();
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, state));
            paths.Add(request.RequestUri!.PathAndQuery);
            if (request.Method == HttpMethod.Post) state = PlaybackJson(position: 0,
                track: TrackJson.Replace("song", "next").Replace("Song", "Next song").Replace("180000", "240000"));
            return Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        await fixture.Model.SeekAsync(120);
        Assert.Equal("2:00", fixture.Model.PositionText);
        fixture.Model.NextCommand.Execute(null); await CommandFinished(fixture.Model.NextCommand);
        Assert.Equal("Next song", fixture.Model.Title); Assert.Equal("0:00", fixture.Model.PositionText);
        Assert.Equal("4:00", fixture.Model.DurationText);
        Assert.Equal(["/v1/me/player/seek?position_ms=120000", "/v1/me/player/next"], paths);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShuffleAndRepeatSendSpotifyCommandsAndKeepStateOnFailure(bool fail)
    {
        var paths = new List<string>();
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, PlaybackJson()));
            paths.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Put, request.Method);
            return Task.FromResult(fail ? Response(HttpStatusCode.Forbidden, "{\"error\":{\"reason\":\"PREMIUM_REQUIRED\"}}") : Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        fixture.Model.ShuffleCommand.Execute(null); await CommandFinished(fixture.Model.ShuffleCommand);
        fixture.Model.RepeatCommand.Execute(null); await CommandFinished(fixture.Model.RepeatCommand);
        Assert.Equal(!fail, fixture.Model.Shuffle); Assert.Equal(!fail, fixture.Model.Repeat);
        Assert.Equal(["/v1/me/player/shuffle?state=true", "/v1/me/player/repeat?state=track"], paths);
    }

    // Synthetic artwork exists only in this test's mocked CDN response.
    private static byte[] TestCover()
    {
        var art = new Border { Width = 256, Height = 256, Background = Brush.Parse("#343047"),
            Child = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
            {
                new Avalonia.Controls.Shapes.Ellipse { Width = 136, Height = 136, Fill = Brush.Parse("#F67C90"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right },
                new TextBlock { Text = "MUSYNC\nSESSIONS", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Brushes.White }
            } } };
        art.Measure(new Size(256, 256)); art.Arrange(new Rect(0, 0, 256, 256));
        using var bitmap = new RenderTargetBitmap(new PixelSize(256, 256)); bitmap.Render(art);
        using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default); return stream.ToArray();
    }

    [AvaloniaTheory]
    [InlineData("en", 1120, 720)]
    [InlineData("zh-Hans", 1460, 940)]
    [InlineData("ja", 1120, 720)]
    public async Task NowPlayingArtworkAndControlsBindInFullAndMiniPlayers(string language, int width, int height)
    {
        var data = TestCover(); var state = PlaybackJson(track: TrackJson.Replace("\"images\":[]", "\"images\":[{\"url\":\"https://i.scdn.co/image/test\"}]"));
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.spotify.com"
            ? Response(HttpStatusCode.OK, state) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }));
        await using var fixture = new Fixture(handler, language, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        window.Show();
        try
        {
            Assert.True(fixture.Model.HasSpotifyArtwork);
            await Task.Delay(650); // Capture the settled composition, after cover and layer transitions.
            var cover = window.FindControl<Musync.App.Controls.CrossfadeImage>("PlayerArtwork")!;
            Assert.Same(fixture.Model.SpotifyArtwork, cover.Source); Assert.True(cover.IsVisible);
            var seek = window.FindControl<Slider>("SeekSlider")!;
            Assert.True(seek.IsEnabled); Assert.InRange(seek.Value, 75, 78); Assert.Equal(180, seek.Maximum);
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                Assert.Equal(new Size(48, 48), cover.Bounds.Size);
                SaveReviewFrame(frame, $"now-playing-{language}-{width}.png");
            }
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsMiniPlayer);
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                Assert.Same(fixture.Model.SpotifyArtwork, window.FindControl<Musync.App.Controls.CrossfadeImage>("MiniPlayerArtwork")!.Source);
                Assert.InRange(window.FindControl<Slider>("MiniSeekSlider")!.Value, 75, 78);
                SaveReviewFrame(frame, $"now-playing-mini-{language}.png");
            }
            state = PlaybackJson(playing: false, position: 100_000,
                track: TrackJson.Replace("song", "another").Replace("Song", "Another song").Replace("180000", "240000"));
            await fixture.Model.RefreshSpotifyPlaybackAsync();
            Assert.Equal("play", fixture.Model.PlayIcon); Assert.Equal("Another song", fixture.Model.Title);
            Assert.Equal(fixture.Model.Text["Paused"], fixture.Model.Status);
            Assert.False(fixture.Model.HasSpotifyArtwork);
            using var paused = window.CaptureRenderedFrame();
            SaveReviewFrame(paused, $"now-playing-mini-paused-{language}.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReleasingSeekOutsideTheSliderDoesNotStrandPlaybackUpdates()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, PlaybackJson()) : Response(HttpStatusCode.NoContent)));
        await using var fixture = new Fixture(handler, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            var slider = window.FindControl<Slider>("SeekSlider")!;
            var center = slider.TranslatePoint(new Point(slider.Bounds.Width * 75 / 180, slider.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            Assert.True(fixture.Model.IsSeeking);
            window.MouseUp(new Point(center.X, center.Y - 100), MouseButton.Left);
            Assert.False(fixture.Model.IsSeeking);
            await fixture.Model.RefreshSpotifyPlaybackAsync();
            Assert.InRange(fixture.Model.Position, 75, 76);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LosingPointerCaptureCancelsSeekingAndTheNextDragStillWorks()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, PlaybackJson()) : Response(HttpStatusCode.NoContent)));
        await using var fixture = new Fixture(handler, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            var slider = window.FindControl<Slider>("SeekSlider")!;
            IPointer? pointer = null;
            slider.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel, true);
            var center = slider.TranslatePoint(new Point(slider.Bounds.Width * 75 / 180, slider.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            Assert.True(fixture.Model.IsSeeking); Assert.NotNull(pointer);
            pointer.Capture(null);
            Assert.False(fixture.Model.IsSeeking);
            window.MouseUp(center, MouseButton.Left);
            window.MouseDown(center, MouseButton.Left);
            Assert.True(fixture.Model.IsSeeking);
            window.MouseUp(center, MouseButton.Left);
            Assert.False(fixture.Model.IsSeeking);
        }
        finally { window.Close(); }
    }
}
