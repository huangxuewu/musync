using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Musync.App;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1460, 940, 0)]
    [InlineData("zh-Hans", 1120, 720, 1)]
    [InlineData("ja", 1120, 720, 2)]
    public async Task AdjacentLyricsFlowIntoTheirNextRolesWithoutChangingThePosterLayout(string language, int width, int height, int palette)
    {
        var lrc = language switch
        {
            "zh-Hans" => "[00:01]沿着晚霞追逐微光\n[00:10]让城市与我们一起呼吸\n[00:20]把这一刻继续传递\n[00:30]音乐带我们回家",
            "ja" => "[00:01]夕暮れの光を追いかけて\n[00:10]街といっしょに息をしよう\n[00:20]この瞬間をつないで\n[00:30]音楽が帰る場所になる",
            _ => "[00:01]Follow the evening light\n[00:10]Let the city breathe with us\n[00:20]Keep this moment moving\n[00:30]Music brings us home"
        };
        await using var fixture = new PresentationFixture(language, palette, lrc: lrc);
        var model = fixture.Model; await model.PlayTrackAsync(fixture.First, 3); await WaitForLyrics(model);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            window.ApplyMotionPreference(true); await Task.Delay(650);
            var lyrics = window.FindControl<Grid>("HeroLyrics")!;
            var previous = window.FindControl<TextBlock>("PreviousLyric")!;
            var current = window.FindControl<TextBlock>("CurrentLyric")!;
            var next = window.FindControl<TextBlock>("NextLyric")!;
            var bounds = new[] { lyrics.Bounds, previous.Bounds, current.Bounds, next.Bounds };
            var oldCurrent = current.Text; var oldNext = next.Text;
            using (var before = window.CaptureRenderedFrame()) SaveReviewFrame(before, $"lyric-flow-{language}-{width}-before.png");
            fixture.Backend.Seek(12); await PollPlayback(model); await Task.Delay(25);
            using (var moving = window.CaptureRenderedFrame()) SaveReviewFrame(moving, $"lyric-flow-{language}-{width}-moving.png");
            Assert.Equal(oldCurrent, previous.Text); Assert.Equal(oldNext, current.Text);
            var previousTransform = previous.RenderTransform?.Value ?? Matrix.Identity;
            var currentTransform = current.RenderTransform?.Value ?? Matrix.Identity;
            Assert.True(previousTransform.M32 > 0, "The outgoing current line must travel to its previous-line role.");
            Assert.True(previousTransform.M11 > 1, "The outgoing current line must settle from its highlighted scale.");
            Assert.True(currentTransform.M32 > 0 && currentTransform.M11 < 1, "The incoming line must travel from the next-line role to the current highlight.");
            Assert.True(lyrics.ClipToBounds); Assert.Equal(bounds[0], lyrics.Bounds);
            var rows = new[] { previous, current, next };
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.Equal(bounds[i + 1].Width, rows[i].Bounds.Width);
                // Font fallback can change the natural text height by two pixels.
                // Its fixed role slot and rounded center must stay in place.
                Assert.InRange(Math.Abs(bounds[i + 1].Center.Y - rows[i].Bounds.Center.Y), 0, 1);
            }
            await Task.Delay(350);
            using (var settled = window.CaptureRenderedFrame()) SaveReviewFrame(settled, $"lyric-flow-{language}-{width}-settled.png");
            foreach (var row in new[] { previous, current, next })
            { Assert.Equal(Matrix.Identity, row.RenderTransform?.Value ?? Matrix.Identity); Assert.Equal(1, row.Opacity); }
            Assert.Equal(model.LyricCurrent, current.Text); Assert.Equal(12, model.Position); Assert.True(model.IsPlaying);
        }
        finally { window.Close(); }
    }

    private static void AssertLyricsAtRest(MainWindow window)
    {
        Assert.False(window.IsLyricFlowRunning);
        foreach (var name in new[] { "PreviousLyric", "CurrentLyric", "NextLyric" })
        {
            var row = window.FindControl<TextBlock>(name)!;
            Assert.Equal(Matrix.Identity, row.RenderTransform?.Value ?? Matrix.Identity);
            Assert.Equal(1, row.Opacity);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task ReducedMotionAndLargeLyricJumpsShowTheLatestTextImmediately(string language)
    {
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model; await model.PlayTrackAsync(fixture.First, 3); await WaitForLyrics(model);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            // Window activation refreshes the real OS preference. Apply the
            // simulated reduced-motion preference after activation settles.
            await Task.Delay(650);
            window.ApplyMotionPreference(false); using var before = window.CaptureRenderedFrame();
            fixture.Backend.Seek(12); await PollPlayback(model); await Task.Delay(25);
            using (var reduced = window.CaptureRenderedFrame()) SaveReviewFrame(reduced, $"lyric-flow-{language}-reduced.png");
            AssertLyricsAtRest(window); Assert.Equal(model.LyricCurrent, window.FindControl<TextBlock>("CurrentLyric")!.Text);
            window.ApplyMotionPreference(true);
            fixture.Backend.Seek(3); await PollPlayback(model); await Task.Delay(25);
            fixture.Backend.Seek(23); await PollPlayback(model); await Task.Delay(25);
            using var jumped = window.CaptureRenderedFrame();
            AssertLyricsAtRest(window); Assert.Equal("Next original test line", window.FindControl<TextBlock>("CurrentLyric")!.Text);
            Assert.Equal(23, model.Position); Assert.True(model.IsPlaying);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LyricFlowStopsWhenThePosterIsHiddenMinimizedOrCovered()
    {
        await using var fixture = new PresentationFixture();
        var model = fixture.Model; await model.PlayTrackAsync(fixture.First, 3); await WaitForLyrics(model);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            await Task.Delay(650);
            async Task BeginFlow()
            {
                window.ApplyMotionPreference(true);
                fixture.Backend.Seek(3); await PollPlayback(model); await Task.Delay(25);
                fixture.Backend.Seek(12); await PollPlayback(model); await Task.Delay(25);
                using var frame = window.CaptureRenderedFrame(); Assert.True(window.IsLyricFlowRunning);
            }
            await BeginFlow(); window.ApplyMotionPreference(false); AssertLyricsAtRest(window);
            await BeginFlow(); window.Hide(); AssertLyricsAtRest(window); window.Show();
            await BeginFlow(); window.WindowState = WindowState.Minimized; AssertLyricsAtRest(window); window.WindowState = WindowState.Normal;
            await BeginFlow(); model.SettingsOpen = true; AssertLyricsAtRest(window); model.SettingsOpen = false;
            await BeginFlow(); model.LinkPanelOpen = true; AssertLyricsAtRest(window); model.LinkPanelOpen = false;
            await BeginFlow(); model.PlaylistOpen = true; AssertLyricsAtRest(window); model.PlaylistOpen = false;
            await BeginFlow();
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsMiniPlayer); AssertLyricsAtRest(window);
            fixture.Backend.Seek(23); await PollPlayback(model); await Task.Delay(25);
            AssertLyricsAtRest(window); Assert.Equal(model.LyricCurrent, window.FindControl<TextBlock>("CurrentLyric")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RapidLyricChangesAndSourceReplacementLeaveOnlyTheLatestFrame()
    {
        await using var fixture = new PresentationFixture();
        var model = fixture.Model; await model.PlayTrackAsync(fixture.First, 3); await WaitForLyrics(model);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            window.ApplyMotionPreference(true); await Task.Delay(650);
            fixture.Backend.Seek(12); await PollPlayback(model); await Task.Delay(25);
            using (var first = window.CaptureRenderedFrame()) Assert.True(window.IsLyricFlowRunning);
            fixture.Backend.Seek(23); await PollPlayback(model); await Task.Delay(25);
            using (var latest = window.CaptureRenderedFrame())
            {
                Assert.True(window.IsLyricFlowRunning);
                Assert.Equal("此刻一起听见音乐", window.FindControl<TextBlock>("PreviousLyric")!.Text);
                Assert.Equal("Next original test line", window.FindControl<TextBlock>("CurrentLyric")!.Text);
            }
            await Task.Delay(350); AssertLyricsAtRest(window);
            fixture.Backend.Seek(3); await PollPlayback(model); await Task.Delay(25);
            fixture.Backend.Seek(12); await PollPlayback(model); await Task.Delay(25);
            Assert.True(window.IsLyricFlowRunning);
            model.Source = model.Sources.First(s => s.Id == "spotify"); await SourceTransitionSettled(model); await Task.Delay(25);
            using var replaced = window.CaptureRenderedFrame();
            AssertLyricsAtRest(window); Assert.False(model.HasSyncedLyrics);
            Assert.Equal(model.Text["NoSyncedLyrics"], window.FindControl<TextBlock>("CurrentLyric")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RepeatingLyricTextStillAdvancesAndPausedSeekingStaysAtRest()
    {
        await using var fixture = new PresentationFixture(lrc: "[00:01]One moment\n[00:10]One moment\n[00:20]One moment\n[00:30]One moment");
        var model = fixture.Model; await model.PlayTrackAsync(fixture.First, 12); await WaitForLyrics(model);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            window.ApplyMotionPreference(true); await Task.Delay(650);
            fixture.Backend.Seek(23); await PollPlayback(model); await Task.Delay(25);
            using var repeating = window.CaptureRenderedFrame(); Assert.True(window.IsLyricFlowRunning);
            Assert.Equal("One moment", model.LyricPrevious); Assert.Equal("One moment", model.LyricCurrent); Assert.Equal("One moment", model.LyricNext);
            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); Assert.False(model.IsPlaying); AssertLyricsAtRest(window);
            await model.SeekAsync(3); await Task.Delay(25); AssertLyricsAtRest(window);
            Assert.Equal(3, model.Position); Assert.Equal("One moment", window.FindControl<TextBlock>("CurrentLyric")!.Text);
        }
        finally { window.Close(); }
    }
}
