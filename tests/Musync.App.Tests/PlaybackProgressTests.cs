using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private sealed class ProgressTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(double seconds) => _timestamp += (long)(seconds * 1000);
    }

    [Theory]
    [InlineData(-0.8)]
    [InlineData(0.8)]
    public void SmallPollDriftCorrectsGraduallyWithoutMovingBackwards(double drift)
    {
        var time = new ProgressTime(); var clock = new PlaybackProgressClock(time);
        clock.Anchor(75, true, 180); time.Advance(5);
        Assert.Equal(80, clock.Position);
        Assert.False(clock.Synchronize(80 + drift, true, 180, false));
        Assert.Equal(80, clock.Position);
        var previous = clock.Position;
        for (var i = 0; i < 50; i++)
        {
            time.Advance(0.1);
            Assert.InRange(clock.Position - previous, 0.07, 0.13);
            previous = clock.Position;
        }
        Assert.Equal(85 + drift, clock.Position, precision: 6);
        time.Advance(10);
        Assert.Equal(95 + drift, clock.Position, precision: 6);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(140)]
    public void ExternalSeekResetsImmediatelyAndPauseFreezesTheClock(double target)
    {
        var time = new ProgressTime(); var clock = new PlaybackProgressClock(time);
        clock.Anchor(75, true, 180); time.Advance(5);
        Assert.True(clock.Synchronize(target, true, 180, false));
        Assert.Equal(target, clock.Position);
        time.Advance(1); clock.Synchronize(target + 1, false, 180, false);
        time.Advance(30); Assert.Equal(target + 1, clock.Position);
    }

    [Fact]
    public void TrackChangeResetsEvenNearTheOldPositionAndProgressStaysWithinDuration()
    {
        var time = new ProgressTime(); var clock = new PlaybackProgressClock(time);
        clock.Anchor(1, true, 180);
        Assert.True(clock.Synchronize(0, true, 2, true)); Assert.Equal(0, clock.Position);
        time.Advance(20); Assert.Equal(2, clock.Position);
    }

    [AvaloniaFact]
    public async Task AStaleReadAfterSeekCannotUndoTheConfirmedTarget()
    {
        var state = PlaybackJson();
        var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, state) : Response(HttpStatusCode.NoContent)));
        await using var fixture = new Fixture(handler, observePlayback: true);
        await fixture.Model.SeekAsync(120);
        await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.InRange(fixture.Model.Position, 120, 121);
        state = PlaybackJson(position: 120_000);
        await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.InRange(fixture.Model.Position, 120, 121);
        state = PlaybackJson(position: 20_000);
        await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.InRange(fixture.Model.Position, 20, 21);
    }

    [AvaloniaFact]
    public async Task RepeatedSeeksAreSerializedAndTheLastGestureWins()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, PlaybackJson()));
            writes.Add(request.RequestUri!.Query);
            return writes.Count == 1 ? first.Task : Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        var a = fixture.Model.SeekAsync(100);
        var b = fixture.Model.SeekAsync(110);
        var c = fixture.Model.SeekAsync(120);
        try
        {
            Assert.Equal(120, fixture.Model.Position); Assert.Single(writes);
            first.SetResult(Response(HttpStatusCode.NoContent));
            await Task.WhenAll(a, b, c);
            Assert.Equal(["?position_ms=100000", "?position_ms=120000"], writes);
            Assert.Equal(120, fixture.Model.Position);
        }
        finally { first.TrySetResult(Response(HttpStatusCode.NoContent)); await Task.WhenAll(a, b, c); }
    }

    [AvaloniaFact]
    public async Task SeekFailureRestoresTheObservedPositionAndOldSourceCompletionIsIgnored()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = true;
        var handler = new Handler((request, _) => request.Method == HttpMethod.Get
            ? Task.FromResult(Response(HttpStatusCode.OK, PlaybackJson())) : fail
            ? Task.FromResult(Response(HttpStatusCode.Forbidden, "{}")) : pending.Task);
        await using var fixture = new Fixture(handler, observePlayback: true);
        await Assert.ThrowsAsync<Musync.Core.SpotifyApiException>(() => fixture.Model.SeekAsync(120));
        Assert.InRange(fixture.Model.Position, 75, 76);
        fail = false; var seek = fixture.Model.SeekAsync(140);
        fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "local");
        pending.SetResult(Response(HttpStatusCode.NoContent)); await seek;
        Assert.True(fixture.Model.IsLocal); Assert.Equal(0, fixture.Model.Position);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackThumbMovesBetweenTicksAndDirectChangesCancelTheTransition(bool mini)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, PlaybackJson())));
        await using var fixture = new Fixture(handler, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            if (mini) window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            using var frame = window.CaptureRenderedFrame();
            var slider = window.FindControl<PlaybackSlider>(mini ? "MiniSeekSlider" : "SeekSlider")!;
            // Isolate compositor motion from the fixture's periodic observation timer.
            slider.DataContext = null; slider.Maximum = 180; slider.MotionEnabled = true;
            slider.IsAdvancing = true; slider.PlaybackPosition = 75;
            slider.ProgressEpoch++;
            slider.PlaybackPosition = 75.3; // first tick after a direct state change
            slider.PlaybackPosition = 75.6;
            await Task.Delay(100); using var mid = window.CaptureRenderedFrame();
            Assert.InRange(slider.Value, 75.301, 75.599);
            slider.ProgressEpoch++; slider.PlaybackPosition = 130;
            Assert.Equal(130, slider.Value);
            slider.PlaybackPosition = 130.3;
            slider.IsAdvancing = false;
            Assert.Equal(130.3, slider.Value);
            slider.IsAdvancing = true; slider.MotionEnabled = false; slider.PlaybackPosition = 131;
            Assert.Equal(131, slider.Value);
            using var end = window.CaptureRenderedFrame();
            SaveReviewFrame(end, mini ? "progress-mini-direct.png" : "progress-full-direct.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task KeyboardSeekingIsImmediateWhileNormalPlaybackIsAnimating()
    {
        var writes = new List<string>();
        var handler = new Handler((request, _) =>
        {
            if (request.Method != HttpMethod.Get) writes.Add(request.RequestUri!.Query);
            return Task.FromResult(request.Method == HttpMethod.Get ? Response(HttpStatusCode.OK, PlaybackJson()) : Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            var slider = window.FindControl<PlaybackSlider>("SeekSlider")!;
            Assert.True(slider.Focus());
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Assert.Equal(180, slider.Value);
            window.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
            Assert.Equal(180, fixture.Model.Position); Assert.Equal(["?position_ms=180000"], writes);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingAPausedDragImmediatelyRestoresTheRealPosition(bool mini)
    {
        var writes = 0;
        var handler = new Handler((request, _) =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return Task.FromResult(request.Method == HttpMethod.Get
                ? Response(HttpStatusCode.OK, PlaybackJson(playing: false)) : Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            if (mini) window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var frame = window.CaptureRenderedFrame();
            var slider = window.FindControl<PlaybackSlider>(mini ? "MiniSeekSlider" : "SeekSlider")!;
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            IPointer? pointer = null;
            slider.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel, true);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 45, start.Y));
            Assert.True(fixture.Model.IsSeeking); Assert.NotNull(pointer);
            Assert.True(slider.Value > fixture.Model.Position + 10);
            pointer.Capture(null);
            Assert.False(fixture.Model.IsSeeking);
            Assert.Equal(fixture.Model.Position, slider.Value);
            window.MouseUp(start, MouseButton.Left);
            Assert.Equal(0, writes);
            using var canceled = window.CaptureRenderedFrame();
            SaveReviewFrame(canceled, mini ? "progress-mini-canceled.png" : "progress-full-canceled.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OrdinaryReleaseKeepsTheChosenThumbUntilPlaybackPublishesItsPosition()
    {
        // Isolate the input ordering with playback publication deliberately absent:
        // a local decoder can still be restarting when the Thumb releases capture.
        var window = new MainWindow { Width = 1120, Height = 720,
            DataContext = new { SettingsOpen = false, PlaylistOpen = false } };
        window.Show();
        try
        {
            var slider = window.FindControl<PlaybackSlider>("SeekSlider")!;
            slider.IsEnabled = true; slider.Maximum = 180; slider.PlaybackPosition = 75;
            using var frame = window.CaptureRenderedFrame();
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            var end = new Point(start.X + 45, start.Y);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end);
            var chosen = slider.Value;
            Assert.True(chosen > 85, $"chosen={chosen}; position={slider.PlaybackPosition}; slider={slider.Bounds}; thumb={thumb.Bounds}; start={start}; enabled={slider.IsEffectivelyEnabled}; visible={slider.IsEffectivelyVisible}");
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(chosen, slider.Value);
            Assert.Equal(75, slider.PlaybackPosition);
            slider.PlaybackPosition = chosen;
            Assert.Equal(chosen, slider.Value);
        }
        finally { window.Close(); }
    }
}
