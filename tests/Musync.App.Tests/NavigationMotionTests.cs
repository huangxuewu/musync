using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1460, 940)]
    [InlineData("zh-Hans", 1460, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task BrowsingChangesIllustrationAndOneActiveDestinationWithoutChangingMusic(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await WaitForLyrics(model); await model.SeekAsync(12);
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(650); using var initial = window.CaptureRenderedFrame();
            var nav = new[] { "HomeNav", "LibraryNav", "PlaylistsNav", "SettingsNav" }
                .Select(name => window.FindControl<Button>(name)!).ToArray();
            Assert.Same(nav[0], Assert.Single(nav, b => b.Classes.Contains("active")));
            var illustration = model.PosterArtwork;
            var palette = model.PosterPalette;
            var currentTitle = model.Title;
            var epoch = model.HeroEpoch;
            model.Navigation.Execute("library");
            Assert.NotSame(illustration, model.PosterArtwork); Assert.NotEqual(palette, model.PosterPalette);
            Assert.Equal(epoch + 1, model.HeroEpoch);
            Assert.Equal(currentTitle, model.Title); Assert.Equal(12, model.Position);
            Assert.True(model.IsPlaying); Assert.Equal("此刻一起听见音乐", model.LyricCurrent);
            await Task.Delay(650); using var library = window.CaptureRenderedFrame();
            Assert.Same(nav[1], Assert.Single(nav, b => b.Classes.Contains("active")));
            var presenter = nav[1].GetVisualDescendants().OfType<ContentPresenter>().Single();
            Assert.Equal(Color.Parse("#251A35"), ((ISolidColorBrush)presenter.Background!).Color);
            Assert.Equal(Colors.White, ((ISolidColorBrush)presenter.Foreground!).Color);
            Assert.Equal(Colors.White, ((ISolidColorBrush)nav[1].GetVisualDescendants().OfType<LucideIcon>().Single().Foreground!).Color);
            Assert.Equal(Color.Parse("#51465F"), ((ISolidColorBrush)nav[0].GetVisualDescendants().OfType<LucideIcon>().Single().Foreground!).Color);
            var search = window.FindControl<TextBox>("MusicSearchBox")!;
            var scene = window.FindControl<Grid>("HeroScene")!;
            var surface = window.FindControl<PosterBackdrop>("HeroSurface")!;
            var subject = window.FindControl<CrossfadeImage>("HeroArtwork")!;
            var searchBottom = search.TranslatePoint(new Point(0, search.Bounds.Height), window)!.Value.Y;
            var sceneTop = scene.TranslatePoint(default, window)!.Value.Y;
            var surfaceTop = surface.TranslatePoint(default, window)!.Value.Y;
            Assert.InRange(sceneTop - searchBottom, 12, 20);
            Assert.False(window.FindControl<TextBlock>("SearchFeedback")!.IsVisible);
            Assert.Equal(24, surfaceTop - sceneTop); Assert.True(subject.Bounds.Top < surface.Bounds.Top);
            SaveReviewFrame(library, $"navigation-{language}-{width}-library.png");

            model.Navigation.Execute("library"); Assert.Equal(epoch + 1, model.HeroEpoch);
            var favorites = Assert.Single(model.Playlists, p => p.IsFavorites);
            illustration = model.PosterArtwork;
            favorites.OpenCommand.Execute(null);
            Assert.NotSame(illustration, model.PosterArtwork); Assert.True(favorites.IsActive);
            Assert.DoesNotContain(nav, b => b.Classes.Contains("active"));
            foreach (var (destination, index) in new[] { ("playlists", 2), ("home", 0) })
            {
                illustration = model.PosterArtwork;
                model.Navigation.Execute(destination);
                Assert.NotSame(illustration, model.PosterArtwork);
                Assert.Same(nav[index], Assert.Single(nav, b => b.Classes.Contains("active")));
            }
            model.SettingsCommand.Execute(null);
            Assert.Same(nav[3], Assert.Single(nav, b => b.Classes.Contains("active")));
            model.SettingsOpen = false;
            Assert.Same(nav[0], Assert.Single(nav, b => b.Classes.Contains("active")));
            Assert.Equal(12, model.Position); Assert.True(model.IsPlaying);
            Assert.Equal("此刻一起听见音乐", model.LyricCurrent);

            var ordinary = model.Playlists.Where(p => !p.IsFavorites).ToArray();
            var first = ordinary[0]; var second = ordinary[1];
            illustration = model.PosterArtwork;
            first.OpenCommand.Execute(null); Assert.True(first.IsActive); Assert.NotSame(illustration, model.PosterArtwork);
            Assert.DoesNotContain(nav, b => b.Classes.Contains("active"));
            illustration = model.PosterArtwork;
            second.OpenCommand.Execute(null); Assert.True(second.IsActive); Assert.False(first.IsActive);
            Assert.NotSame(illustration, model.PosterArtwork); Assert.Equal(currentTitle, model.Title);
            await Task.Delay(650); using var folder = window.CaptureRenderedFrame();
            SaveReviewFrame(folder, $"navigation-{language}-{width}-playlist.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TwoLinePosterTitleAndLyricsFitAtMinimumSizeAfterLift()
    {
        await using var fixture = new PresentationFixture("ja");
        await fixture.Model.PlayTrackAsync(fixture.First with { Title = "A long original song title that occupies two lines" });
        await WaitForLyrics(fixture.Model); await fixture.Model.SeekAsync(12);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            await Task.Delay(650); using var frame = window.CaptureRenderedFrame();
            var scene = window.FindControl<Grid>("HeroScene")!;
            var surface = window.FindControl<PosterBackdrop>("HeroSurface")!;
            var lyrics = window.FindControl<Grid>("HeroLyrics")!;
            var title = scene.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == fixture.Model.Title);
            Assert.True(title.Bounds.Height > 43);
            foreach (var control in new Control[] { title, lyrics })
            {
                var point = control.TranslatePoint(default, surface)!.Value;
                Assert.True(point.X >= 0 && point.Y >= 0 && point.X + control.Bounds.Width <= surface.Bounds.Width);
                Assert.True(point.Y + control.Bounds.Height <= surface.Bounds.Height);
            }
            SaveReviewFrame(frame, "navigation-ja-1120-long-title.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PlayerFeedbackInterpolatesAndReducedMotionSettlesInteractions()
    {
        await using var fixture = new PresentationFixture();
        await fixture.Model.PlayTrackAsync(fixture.First);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(true);
        try
        {
            using var initial = window.CaptureRenderedFrame();
            var button = window.FindControl<Button>("PlayerPlayButton")!;
            var point = button.TranslatePoint(new Point(22, 22), window)!.Value;
            window.MouseMove(point); await Task.Delay(60); using var hover = window.CaptureRenderedFrame();
            Assert.InRange(button.RenderTransform!.Value.M11, 1.001, 1.055);
            SaveReviewFrame(hover, "navigation-en-1120-motion-hover.png");
            window.MouseDown(point, MouseButton.Left); await Task.Delay(70); using var pressed = window.CaptureRenderedFrame();
            Assert.InRange(button.RenderTransform!.Value.M11, .94, .999);
            SaveReviewFrame(pressed, "navigation-en-1120-motion-pressed.png");
            window.MouseUp(point, MouseButton.Left); await CommandFinished(fixture.Model.PlayCommand);
            Assert.False(fixture.Model.IsPlaying);
            await Task.Delay(240); using var released = window.CaptureRenderedFrame();
            Assert.Equal(1.055, button.RenderTransform!.Value.M11, 3);
            fixture.Model.Navigation.Execute("library");
            window.ApplyMotionPreference(false); using var reduced = window.CaptureRenderedFrame();
            Assert.Equal(1, button.RenderTransform!.Value.M11); Assert.Null(button.Transitions);
            Assert.False(window.FindControl<CrossfadeImage>("HeroArtwork")!.MotionEnabled);
            Assert.False(window.FindControl<PosterBackdrop>("HeroSurface")!.MotionEnabled);
            window.MouseDown(point, MouseButton.Left); using var direct = window.CaptureRenderedFrame();
            Assert.Equal(1, button.RenderTransform!.Value.M11); window.MouseUp(point, MouseButton.Left);
            SaveReviewFrame(reduced, "navigation-en-1120-reduced-motion.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OpeningLinkAndSettingsUsesShortInterruptibleReveal()
    {
        await using var fixture = new PresentationFixture();
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(true);
        try
        {
            using var initial = window.CaptureRenderedFrame();
            window.FindControl<ToggleButton>("SpeakerToggle")!.IsChecked = true;
            await Task.Delay(50); using var opening = window.CaptureRenderedFrame();
            var panel = window.FindControl<Border>("SpeakerPanel")!;
            Assert.InRange(panel.Opacity, .820001, .999999);
            Assert.InRange(panel.RenderTransform!.Value.M32, .001, 8);
            SaveReviewFrame(opening, "navigation-en-1120-link-opening.png");
            window.FindControl<ToggleButton>("SpeakerToggle")!.IsChecked = false;
            window.FindControl<ToggleButton>("SpeakerToggle")!.IsChecked = true;
            await Task.Delay(300); using var settled = window.CaptureRenderedFrame();
            Assert.Equal(1, panel.Opacity); Assert.Equal(0, panel.RenderTransform!.Value.M32);
            SaveReviewFrame(settled, "navigation-en-1120-link-popover.png");
            window.FindControl<ToggleButton>("SpeakerToggle")!.IsChecked = false;
            fixture.Model.SettingsOpen = true;
            using var firstSettings = window.CaptureRenderedFrame();
            var settingsPanel = window.FindControl<Border>("SettingsPanel")!;
            await Task.Delay(400); using var firstSettled = window.CaptureRenderedFrame();
            Assert.Equal(1, settingsPanel.Opacity);
            fixture.Model.SettingsOpen = false; using var hidden = window.CaptureRenderedFrame();
            fixture.Model.SettingsOpen = true;
            using var reopening = window.CaptureRenderedFrame();
            await Task.Delay(40);
            using var settings = window.CaptureRenderedFrame();
            Assert.True(window.FindControl<ComboBox>("SettingsLanguageSelector")!.IsEffectivelyVisible);
            window.ApplyMotionPreference(false); using var reduced = window.CaptureRenderedFrame();
            Assert.Equal(1, settingsPanel.Opacity); Assert.Equal(0, settingsPanel.RenderTransform!.Value.M32);
        }
        finally { window.Close(); }
    }
}
