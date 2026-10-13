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
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task PreparingPlaybackDisablesLinkSelectionAndCancellationRestoresIt(string language)
    {
        PreparingOutput? output = null;
        await using var fixture = new PresentationFixture(language, outputFactory: d => output = new(d),
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        fixture.Settings.Links.Add(new() { Id = "other-link", Name = "Other link", Speakers = new()
            { ["test-output"] = new() { Name = "Test output", Transport = SpeakerTransport.Windows, Preferences = new() { Volume = 25 } } } });
        await fixture.Model.InitializeAsync();
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(false);
        Task start = Task.CompletedTask;
        try
        {
            fixture.Model.LinkPanelOpen = true;
            using var initial = window.CaptureRenderedFrame();
            var selector = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => ReferenceEquals(c.ItemsSource, fixture.Model.Links));
            Assert.True(selector.IsEffectivelyVisible); Assert.True(selector.IsEnabled);
            start = fixture.Model.PlayTrackAsync(fixture.First);
            using var busy = window.CaptureRenderedFrame();
            Assert.True(fixture.Model.IsPreparingPlayback); Assert.True(fixture.Model.Busy);
            SaveReviewFrame(busy, $"link-preparing-{language}-1120.png");
            if (selector.Focus())
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            }
            Assert.Equal(fixture.Model.ActiveLink?.Id, (selector.SelectedItem as Choice)?.Id);
            Assert.False(selector.IsEnabled);
            Assert.False(fixture.Backend.Started.Task.IsCompleted);

            fixture.Model.CancelPreparation.Execute(null);
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            await CommandFinished(fixture.Model.CancelPreparation);
            using var cancelled = window.CaptureRenderedFrame();
            Assert.True(selector.IsEnabled); Assert.False(fixture.Model.Busy);
            Assert.False(fixture.Model.IsPreparingPlayback); Assert.False(fixture.Backend.Started.Task.IsCompleted);
            selector.SelectedItem = fixture.Model.Links.Single(l => l.Id == "other-link");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (fixture.Model.Busy || fixture.Settings.ActiveLinkId != "other-link") await Task.Delay(10, timeout.Token);
            Assert.Equal("other-link", fixture.Model.ActiveLink?.Id);
            Assert.Equal("other-link", (selector.SelectedItem as Choice)?.Id);
        }
        finally { output?.Ready.TrySetResult(); await start; window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task NewLinkSavePersistsNameMembershipVolumeDelayAndMute(string language)
    {
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model;
        model.CreateLink.Execute(null);
        model.LinkName = " 新的链接 ";
        var speaker = Assert.Single(model.DraftSpeakers);
        speaker.Selected = true; speaker.Volume = 37; speaker.DelayMs = 230; speaker.Muted = true;
        model.SaveLink.Execute(null); await CommandFinished(model.SaveLink);
        Assert.False(model.IsLinkEditing);
        var stored = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load();
        var link = Assert.Single(stored.Links, l => l.Name == "新的链接");
        Assert.Equal(link.Id, stored.ActiveLinkId);
        var member = Assert.Single(link.Speakers);
        Assert.Equal("test-output", member.Key); Assert.Equal(37, member.Value.Preferences.Volume);
        Assert.Equal(230, member.Value.Preferences.DelayMs); Assert.True(member.Value.Preferences.Muted);
        model.EditLink.Execute(null);
        var reopened = Assert.Single(model.DraftSpeakers);
        Assert.True(reopened.Selected); Assert.Equal(37, reopened.Volume); Assert.Equal(230, reopened.DelayMs);
        Assert.True(reopened.Muted);
    }

    [AvaloniaFact]
    public async Task EditingLinkReplacesItsExistingConfigurationWithoutAddingAnotherLink()
    {
        await using var fixture = new PresentationFixture();
        var model = fixture.Model;
        model.EditLink.Execute(null); model.LinkName = "Renamed";
        var speaker = Assert.Single(model.DraftSpeakers); speaker.Volume = 26; speaker.DelayMs = 140;
        model.SaveLink.Execute(null); await CommandFinished(model.SaveLink);
        var stored = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load();
        var link = Assert.Single(stored.Links);
        Assert.Equal("test-link", link.Id); Assert.Equal("Renamed", link.Name);
        Assert.Equal(26, link.Speakers["test-output"].Preferences.Volume);
        Assert.Equal(140, link.Speakers["test-output"].Preferences.DelayMs);
    }

    [AvaloniaFact]
    public async Task ClickingSaveCommitsDelayTextBeforePersistingTheDraft()
    {
        await using var fixture = new PresentationFixture("zh-Hans");
        var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            model.LinkPanelOpen = true; model.EditLink.Execute(null);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var panel = window.FindControl<Border>("SpeakerPanel")!;
            var delay = panel.GetVisualDescendants().OfType<NumericUpDown>().Single();
            var editor = delay.GetVisualDescendants().OfType<TextBox>().Single();
            editor.Focus(); editor.Text = "230";
            var save = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.SaveLink);
            var point = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await CommandFinished(model.SaveLink);
            Assert.False(model.IsLinkEditing);
            var stored = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load();
            Assert.Equal(230, Assert.Single(stored.Links).Speakers["test-output"].Preferences.DelayMs);
        }
        finally { window.Close(); }
    }
}
