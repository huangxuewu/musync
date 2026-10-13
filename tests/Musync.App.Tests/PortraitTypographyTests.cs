using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaFact]
    public async Task ScheduledPortraitChangePreservesPlaybackAndSkipsSeekGestures()
    {
        await using var fixture = new PresentationFixture("zh-Hans");
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await WaitForLyrics(model); await model.SeekAsync(12);
        var window = new MainWindow { DataContext = model };
        window.Show(); window.Activate();
        try
        {
            await Task.Delay(150); using var initial = window.CaptureRenderedFrame();
            window.ApplyMotionPreference(true);
            Assert.True(window.IsPosterRotationRunning);
            var artwork = model.PosterArtwork; var epoch = model.HeroEpoch;
            var palette = model.PosterPalette; var title = model.Title;
            var source = model.Source; var link = model.ActiveLink;
            model.IsSeeking = true; window.AdvancePosterRotation();
            Assert.Equal(epoch, model.HeroEpoch); Assert.True(window.IsPosterRotationRunning);
            model.IsSeeking = false; window.AdvancePosterRotation();
            Assert.NotSame(artwork, model.PosterArtwork); Assert.NotEqual(palette, model.PosterPalette);
            Assert.Equal(epoch + 1, model.HeroEpoch);
            Assert.Equal(title, model.Title); Assert.Equal(12, model.Position); Assert.True(model.IsPlaying);
            Assert.Same(source, model.Source); Assert.Equal(link!.Id, model.ActiveLink!.Id);
            Assert.Equal("此刻一起听见音乐", model.LyricCurrent);
            await Task.Delay(650); using var rotated = window.CaptureRenderedFrame();
            Assert.Same(model.PosterArtwork, window.FindControl<CrossfadeImage>("HeroArtwork")!.Source);
            SaveReviewFrame(rotated, "portrait-zh-Hans-1280-rotated.png");
        }
        finally { window.Close(); }
        Assert.False(window.IsPosterRotationRunning);
    }

    [AvaloniaFact]
    public async Task PortraitRotationPausesForHiddenContentOverlaysAndReducedMotion()
    {
        await using var fixture = new PresentationFixture();
        var model = fixture.Model;
        var window = new MainWindow { DataContext = model };
        window.Show(); window.Activate();
        try
        {
            await Task.Delay(150); using var frame = window.CaptureRenderedFrame();
            window.ApplyMotionPreference(true); Assert.True(window.IsPosterRotationRunning);
            void Stopped()
            {
                var epoch = model.HeroEpoch;
                Assert.False(window.IsPosterRotationRunning); window.AdvancePosterRotation();
                Assert.Equal(epoch, model.HeroEpoch);
            }
            window.ApplyMotionPreference(false); Stopped();
            window.ApplyMotionPreference(true); Assert.True(window.IsPosterRotationRunning);
            model.SettingsOpen = true; Stopped();
            model.SettingsOpen = false; Assert.True(window.IsPosterRotationRunning);
            model.LinkPanelOpen = true; Stopped();
            model.LinkPanelOpen = false; Assert.True(window.IsPosterRotationRunning);
            model.PlaylistOpen = true; Stopped();
            model.PlaylistOpen = false; Assert.True(window.IsPosterRotationRunning);
            model.Search = "Original"; Stopped();
            model.Search = ""; await Task.Delay(150); using var restored = window.CaptureRenderedFrame();
            Assert.True(window.IsPosterRotationRunning);
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Stopped();
        }
        finally { window.Close(); }
        Assert.False(window.IsPosterRotationRunning);
    }

    [AvaloniaTheory]
    [InlineData(1280, 940)]
    [InlineData(1120, 720)]
    public async Task EmbeddedChineseWeightsRenderAndUpdateLiveWithoutClipping(int width, int height)
    {
        await using var fixture = new PresentationFixture("zh-Hans");
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await WaitForLyrics(model); await model.SeekAsync(12);
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(650); using var chinese = window.CaptureRenderedFrame();
            Assert.Equal(14, window.FontSize);
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
            {
                Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(window.FontFamily, weight: weight), out var face));
                Assert.StartsWith("Noto Sans SC", face.FamilyName); Assert.Equal(weight, face.Weight);
                Assert.Equal(FontSimulations.None, face.FontSimulations);
                foreach (var character in "音乐播放喜爱曲目此刻一起听见")
                    Assert.NotEqual(0, face.CharacterToGlyphMap.GetGlyph(character));
            }
            var lyric = window.FindControl<TextBlock>("CurrentLyric")!;
            Assert.Equal(window.FontFamily, lyric.FontFamily);
            var scene = window.FindControl<Grid>("HeroScene")!;
            var lyrics = window.FindControl<Grid>("HeroLyrics")!;
            var point = lyrics.TranslatePoint(default, scene)!.Value;
            Assert.True(point.X >= 0 && point.Y + lyrics.Bounds.Height <= scene.Bounds.Height);
            SaveReviewFrame(chinese, $"portrait-zh-Hans-{width}-initial.png");
            foreach (var language in new[] { "en", "ja", "zh-Hans" })
            {
                model.Text.Switch(language); await Task.Delay(100);
                Assert.Equal(language == "zh-Hans" ? 14 : 13, window.FontSize);
                Assert.Equal(model.InterfaceFont, window.FontFamily);
                Assert.Equal(window.FontFamily, lyric.FontFamily);
            }
        }
        finally { window.Close(); }
    }
}
