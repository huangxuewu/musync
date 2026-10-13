using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task SettingsSectionsKeepRelevantControlsAndSaveReachableInEveryLanguage(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        fixture.Model.SettingsOpen = true;
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        try
        {
            window.Show(); await Task.Delay(450); window.ApplyMotionPreference(false);
            var panel = window.FindControl<Border>("SettingsPanel")!;
            var languageInput = window.FindControl<ComboBox>("SettingsLanguageSelector")!;
            var capture = window.FindControl<ComboBox>("SettingsCaptureSelector")!;
            var clientId = window.FindControl<TextBox>("SettingsClientIdInput")!;
            var save = window.FindControl<Button>("SettingsSaveButton")!;
            var close = window.FindControl<Button>("SettingsCloseButton")!;
            Assert.Same(window.FindControl<PosterBackdrop>("HeroSurface"), window.FindControl<FrostedPosterGlass>("SettingsGlass")!.Backdrop);
            foreach (var (section, tabName, expectedHeight) in new[] { ("general", "SettingsGeneralTab", 440d), ("audio", "SettingsAudioTab", 470d), ("spotify", "SettingsSpotifyTab", 620d) })
            {
                var tab = window.FindControl<ToggleButton>(tabName)!; ClickPlaylistControl(window, tab);
                using var frame = window.CaptureRenderedFrame();
                Assert.True(tab.IsChecked); Assert.Equal(expectedHeight, panel.Bounds.Height);
                Assert.Equal(section == "general", languageInput.IsEffectivelyVisible);
                Assert.Equal(section == "audio", capture.IsEffectivelyVisible);
                Assert.Equal(section == "spotify", clientId.IsEffectivelyVisible);
                Assert.Equal(ScrollBarVisibility.Hidden, window.FindControl<ScrollViewer>("SettingsContentScroll")!.VerticalScrollBarVisibility);
                var origin = save.TranslatePoint(default, window)!.Value;
                Assert.InRange(origin.X, 0, width - save.Bounds.Width); Assert.InRange(origin.Y, 0, height - save.Bounds.Height);
                Assert.True(save.IsEffectivelyVisible); Assert.Equal(40, save.Bounds.Height);
                var closeRight = close.TranslatePoint(new Point(close.Bounds.Width, 0), panel)!.Value.X;
                Assert.Equal(panel.Bounds.Width - 24, closeRight);
                SaveReviewFrame(frame, $"settings-sections-{language}-{width}-{section}.png");
                ClickPlaylistControl(window, tab); window.CaptureRenderedFrame()?.Dispose();
                Assert.True(tab.IsChecked, $"The active {section} section lost selection on a second click.");
                tab.Focus();
                Assert.True(tab.IsFocused, $"The {section} section cannot receive keyboard focus.");
            }
            clientId.Text = ClientId;
            ClickPlaylistControl(window, save); await CommandFinished(fixture.Model.CloseSettings);
            Assert.False(fixture.Model.SettingsOpen); Assert.False(fixture.Model.IsPlaying);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsEscapeSavesClosesAndBackgroundPlaybackKeepsItsPosition()
    {
        await using var fixture = new PresentationFixture();
        await fixture.Model.PlayTrackAsync(fixture.First); await fixture.Model.SeekAsync(12);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        try
        {
            window.Show(); window.FindControl<Button>("HomeNav")!.Focus(); fixture.Model.SettingsOpen = true; await Task.Delay(300);
            var capacity = window.FindControl<NumericUpDown>("ArtworkCacheLimitInput")!;
            capacity.Value = 750; window.FindControl<ToggleButton>("SettingsGeneralTab")!.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await CommandFinished(fixture.Model.CloseSettings);
            Assert.False(fixture.Model.SettingsOpen); Assert.True(fixture.Model.IsPlaying); Assert.Equal(12, fixture.Model.Position);
            Assert.Equal(750, new Musync.Core.SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load().ArtworkCacheLimitMb);
            await Until(() => window.FindControl<Button>("HomeNav")!.IsFocused);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UnconnectedSpotifySourceOpensItsSetupSectionInsteadOfGeneralSettings()
    {
        await using var fixture = new PresentationFixture();
        fixture.Model.DisconnectSpotify.Execute(null); await CommandFinished(fixture.Model.DisconnectSpotify);
        fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "spotify");
        await Until(() => !fixture.Model.IsSwitchingSource);
        Assert.True(fixture.Model.SettingsOpen); Assert.True(fixture.Model.SettingsSpotify); Assert.False(fixture.Model.SettingsGeneral);
        Assert.False(fixture.Model.IsPlaying);
    }
}
