using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData(800)]
    [InlineData(1000)]
    public async Task FrostedPosterSoftensActualEdgesAndTracksPaletteAndLayout(int width)
    {
        var backdrop = new PosterBackdrop { MotionEnabled = false };
        var glass = new FrostedPosterGlass { Backdrop = backdrop, Width = width * .6, Height = 280,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(40, 60, 0, 0) };
        var host = new Grid { Children = { backdrop, glass } };
        var window = new Window { Width = width, Height = 400, Content = host };
        window.Show();
        try
        {
            await Task.Delay(60);
            // Just outside the blue diagonal the unfiltered poster is pure
            // yellow. A real blur includes blue from the neighboring pixels.
            Point Sample() => new((.72 - .26 * .45) * backdrop.Bounds.Width - 4, .45 * backdrop.Bounds.Height);
            Color At(Bitmap frame, Point point) => ReadGlassPixel(frame, backdrop.TranslatePoint(point, window)!.Value);
            glass.IsVisible = false;
            using (var sharp = window.CaptureRenderedFrame()!) Assert.Equal(255, At(sharp, Sample()).R);
            glass.IsVisible = true;
            using (var frosted = window.CaptureRenderedFrame()!)
            {
                Assert.InRange(At(frosted, Sample()).R, 160, 249);
                Assert.InRange(At(frosted, new Point(120, 180)).R, 253, 255);
            }
            using var previous = window.CaptureRenderedFrame()!;
            var before = At(previous, Sample());
            backdrop.Palette = 2;
            using (var updated = window.CaptureRenderedFrame()!) Assert.NotEqual(before, At(updated, Sample()));
            backdrop.Palette = 0;
            glass.Margin = new Thickness(90, 80, 0, 0);
            window.Width = width + 100;
            await Task.Delay(60);
            using (var relocated = window.CaptureRenderedFrame()!) Assert.InRange(At(relocated, Sample()).R, 160, 249);
            glass.Backdrop = null;
            using var fallback = window.CaptureRenderedFrame()!;
            Assert.True(At(fallback, Sample()).R > 250);
        }
        finally { window.Close(); }
    }

    private static Color ReadGlassPixel(Bitmap frame, Point point)
    {
        Assert.True(frame.Format == PixelFormat.Bgra8888 || frame.Format == PixelFormat.Rgba8888);
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            frame.CopyPixels(new PixelRect((int)point.X, (int)point.Y, 1, 1), buffer, 4, 4);
            var bytes = new byte[4]; Marshal.Copy(buffer, bytes, 0, 4);
            return frame.Format == PixelFormat.Rgba8888 ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
                : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [AvaloniaFact]
    public async Task FrostedChineseLyricsStaySharpDuringPosterChanges()
    {
        await using var fixture = new PresentationFixture("zh-Hans", 1,
            title: "晚风与节拍", lrc: "[00:01]让晚风轻轻穿过耳边\n[00:10]此刻一起听见音乐\n[00:20]跟着节拍慢慢向前");
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await WaitForLyrics(model); await model.SeekAsync(12);
        var window = new MainWindow { DataContext = model, Width = 1280, Height = 820 };
        window.Show();
        try
        {
            await Task.Delay(650);
            window.ApplyMotionPreference(false);
            using var initial = window.CaptureRenderedFrame();
            SaveReviewFrame(initial, "frosted-glass-zh-Hans-1280.png");
            var lyric = window.FindControl<TextBlock>("CurrentLyric")!;
            Assert.Equal("此刻一起听见音乐", lyric.Text);
            Assert.Null(lyric.Effect);
            var glass = window.FindControl<FrostedPosterGlass>("HeroGlass")!;
            Assert.Same(window.FindControl<PosterBackdrop>("HeroSurface"), glass.Backdrop);
            model.RotatePosterArtwork();
            using var updated = window.CaptureRenderedFrame();
            SaveReviewFrame(updated, "frosted-glass-zh-Hans-1280-rotated.png");
            Assert.Equal("此刻一起听见音乐", lyric.Text);
            Assert.Equal(12, model.Position);
            Assert.Equal(1, glass.Backdrop!.Blend);
        }
        finally { window.Close(); }
    }
}
