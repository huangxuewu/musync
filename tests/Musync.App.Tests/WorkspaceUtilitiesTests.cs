using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Musync.App;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task HeaderUtilitiesSeparateConfigurationFromLiveSpeakerControls(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            using var initial = window.CaptureRenderedFrame(); window.ApplyMotionPreference(false);
            var settings = window.FindControl<Button>("SettingsNav")!;
            var config = window.FindControl<ToggleButton>("LinkConfigurationToggle")!;
            var quick = window.FindControl<ToggleButton>("SpeakerToggle")!;
            var sidebar = window.FindControl<Border>("SidebarSurface")!;
            Assert.DoesNotContain(sidebar, settings.GetVisualAncestors());
            var search = window.FindControl<TextBox>("MusicSearchBox")!;
            var searchRight = search.TranslatePoint(new Point(search.Bounds.Width, 0), window)!.Value.X;
            var configOrigin = config.TranslatePoint(default, window)!.Value;
            Assert.True(configOrigin.X >= searchRight + 8); Assert.InRange(configOrigin.Y, 32, 50);
            Assert.Equal(36, config.Bounds.Width); Assert.Equal(36, settings.Bounds.Width);
            Assert.True(quick.TranslatePoint(default, window)!.Value.Y > height - 96);
            using (var header = window.CaptureRenderedFrame()) SaveReviewFrame(header, $"workspace-header-{language}-{width}.png");
            ClickPlaylistControl(window, config); using var overview = window.CaptureRenderedFrame();
            Assert.True(model.LinkPanelOpen); Assert.False(model.SpeakerControlsOpen);
            var configPanel = window.FindControl<Border>("SpeakerPanel")!;
            Assert.True(configPanel.TranslatePoint(default, window)!.Value.Y >= configOrigin.Y + config.Bounds.Height);
            Assert.DoesNotContain(configPanel.GetVisualDescendants().OfType<Slider>(), slider => slider.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("OverviewRescanButton")!.IsEffectivelyVisible);
            SaveReviewFrame(overview, $"workspace-links-{language}-{width}.png");
            ClickPlaylistControl(window, quick); using var controls = window.CaptureRenderedFrame();
            Assert.True(model.SpeakerControlsOpen); Assert.False(model.LinkPanelOpen);
            var quickPanel = window.FindControl<Border>("SpeakerControlsPanel")!;
            Assert.DoesNotContain(quickPanel.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);
            Assert.DoesNotContain(quickPanel.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible);
            Assert.DoesNotContain(quickPanel.GetVisualDescendants().OfType<Button>(), b => b.Command == model.CreateLink || b.Command == model.EditLink || b.Command == model.ScanCommand || b.Command == model.OpenCalibration);
            Assert.True(quickPanel.Bounds.Height < 300);
            var speaker = Assert.Single(model.LinkedSpeakers);
            var volume = Assert.Single(quickPanel.GetVisualDescendants().OfType<Slider>());
            var before = speaker.Volume; Assert.True(volume.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Assert.True(speaker.Volume < before);
            var mute = quickPanel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == speaker.MuteCommand);
            ClickPlaylistControl(window, mute); await CommandFinished(speaker.MuteCommand); Assert.True(speaker.Muted);
            var delay = Assert.Single(quickPanel.GetVisualDescendants().OfType<NumericUpDown>());
            var editor = delay.GetVisualDescendants().OfType<TextBox>().Single(); editor.Focus(); editor.Text = "137";
            ClickPlaylistControl(window, window.FindControl<Button>("CloseSpeakerControlsButton")!);
            Assert.Equal(137, speaker.DelayMs); Assert.False(model.SpeakerControlsOpen);
            ClickPlaylistControl(window, quick); using var tuned = window.CaptureRenderedFrame();
            SaveReviewFrame(tuned, $"workspace-controls-{language}-{width}.png");
            ClickPlaylistControl(window, settings); using var preferences = window.CaptureRenderedFrame();
            Assert.True(model.SettingsOpen); Assert.False(model.LinkPanelOpen); Assert.False(model.SpeakerControlsOpen);
            Assert.Equal(620, window.FindControl<Border>("SettingsPanel")!.Bounds.Height);
            model.CloseSettings.Execute(null); await CommandFinished(model.CloseSettings);
            var stored = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load();
            var saved = Assert.Single(stored.Links).Speakers[speaker.Id].Preferences;
            Assert.Equal(speaker.Volume, saved.Volume); Assert.True(saved.Muted); Assert.Equal(137, saved.DelayMs);
            model.Navigation.Execute("radio"); using var radio = window.CaptureRenderedFrame();
            Assert.True(config.IsEffectivelyVisible); Assert.True(settings.IsEffectivelyVisible);
            var scene = window.FindControl<RadioView>("RadioPage")!.FindControl<Grid>("RadioScene")!;
            Assert.True(scene.TranslatePoint(default, window)!.Value.Y >= configOrigin.Y + 46);
            SaveReviewFrame(radio, $"workspace-radio-header-{language}-{width}.png");
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ManySpeakerControlsStayScrollableWithTheLastVolumeAndDelayReachable()
    {
        var receivers = Enumerable.Range(0, 12).Select(i => new SpeakerDevice("quick:" + i, $"Speaker {i:00} · 音響", SpeakerTransport.Chromecast, "192.0.2.10", 8009)).ToArray();
        await using var fixture = new PresentationFixture("ja", discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>(receivers));
        foreach (var receiver in receivers) fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true, Volume = 54, DelayMs = 100 } };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            using var initial = window.CaptureRenderedFrame(); window.ApplyMotionPreference(false);
            ClickPlaylistControl(window, window.FindControl<ToggleButton>("SpeakerToggle")!); using var opened = window.CaptureRenderedFrame();
            var scroll = window.FindControl<ScrollViewer>("SpeakerControlsList")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.Equal(ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
            Assert.DoesNotContain(scroll.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.IsVisible);
            var panel = window.FindControl<Border>("SpeakerControlsPanel")!;
            Assert.True(panel.TranslatePoint(default, window)!.Value.Y >= 32);
            var row = scroll.GetVisualDescendants().OfType<Border>().Last(b => b.Classes.Contains("speakerControlRow"));
            var speaker = (SpeakerViewModel)row.DataContext!;
            row.BringIntoView(); using var last = window.CaptureRenderedFrame();
            var numeric = Assert.Single(row.GetVisualDescendants().OfType<NumericUpDown>());
            var point = numeric.TranslatePoint(default, scroll)!.Value;
            Assert.InRange(point.Y, 0, scroll.Viewport.Height - numeric.Bounds.Height);
            var plus = row.GetVisualDescendants().OfType<Button>().Single(b => b.Command == speaker.Plus);
            ClickPlaylistControl(window, plus); await CommandFinished(speaker.Plus);
            Assert.Equal(110, speaker.DelayMs); Assert.Equal(110, fixture.Settings.Links[0].Speakers[speaker.Id].Preferences.DelayMs);
            SaveReviewFrame(last, "workspace-controls-ja-many.png");
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SpeakerControlsEmptyStateAndEscapeKeepConfigurationAndDraftFocusSeparate()
    {
        await using var fixture = new PresentationFixture(discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        fixture.Settings.Links.Clear(); fixture.Settings.ActiveLinkId = null;
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            using var initial = window.CaptureRenderedFrame(); window.ApplyMotionPreference(false);
            var config = window.FindControl<ToggleButton>("LinkConfigurationToggle")!;
            var quick = window.FindControl<ToggleButton>("SpeakerToggle")!;
            ClickPlaylistControl(window, quick); using var empty = window.CaptureRenderedFrame();
            Assert.True(model.SpeakerControlsOpen); Assert.False(model.IsLinkEditing);
            var panel = window.FindControl<Border>("SpeakerControlsPanel")!;
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == model.Text["SpeakerControlsEmpty"]);
            Assert.Empty(panel.GetVisualDescendants().OfType<NumericUpDown>());
            SaveReviewFrame(empty, "workspace-controls-empty.png");
            window.FindControl<Button>("CloseSpeakerControlsButton")!.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(model.SpeakerControlsOpen); Assert.True(quick.IsFocused);
            ClickPlaylistControl(window, config); using var setup = window.CaptureRenderedFrame();
            model.CreateLink.Execute(null); await CommandFinished(model.CreateLink); using var draft = window.CaptureRenderedFrame();
            Assert.True(model.IsLinkEditing); Assert.True(window.FindControl<TextBox>("LinkNameEditor")!.IsFocused);
            model.LinkName = "Unsaved draft";
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(model.LinkPanelOpen); Assert.False(model.IsLinkEditing); Assert.Empty(model.Links); Assert.True(config.IsFocused);
            model.SettingsOpen = true; using var settings = window.CaptureRenderedFrame();
            Assert.False(model.SpeakerControlsOpen); Assert.False(model.LinkPanelOpen);
            model.SettingsOpen = false; using var closed = window.CaptureRenderedFrame();
            ClickPlaylistControl(window, config); using var reopened = window.CaptureRenderedFrame(); Assert.True(model.LinkPanelOpen);
            var search = window.FindControl<TextBox>("MusicSearchBox")!;
            var searchPoint = search.TranslatePoint(new Point(100, search.Bounds.Height / 2), window)!.Value;
            window.MouseDown(searchPoint, MouseButton.Left); window.MouseUp(searchPoint, MouseButton.Left);
            Assert.False(model.LinkPanelOpen); Assert.True(search.IsFocused);
        }
        finally { window.Close(); }
    }
}
