using Avalonia;
using Avalonia.Automation;
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
    public async Task DelayTuningSelectorUpdatesButtonsKeyboardAndAccessibleNames(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language,
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        var model = fixture.Model; fixture.Model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            model.LinkPanelOpen = true;
            using (var initial = CaptureTrackReviewFrame(window)) SaveReviewFrame(initial, $"delay-tuning-{language}-{width}-initial.png");
            var panel = window.FindControl<Border>("SpeakerPanel")!;
            var selector = window.FindControl<ComboBox>("DelayStepSelector")!;
            var speaker = Assert.Single(model.LinkedSpeakers);
            var numeric = panel.GetVisualDescendants().OfType<NumericUpDown>().Single();
            var minus = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == speaker.Minus);
            var plus = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == speaker.Plus);
            Assert.True(selector.IsEffectivelyVisible);
            Assert.Equal(model.Text["DelayStep"], AutomationProperties.GetName(selector));
            Assert.Equal(model.Text["DelayTuningHint"], ToolTip.GetTip(selector));
            Assert.Equal(model.Text["DelayTuningHint"], AutomationProperties.GetHelpText(selector));
            Assert.False(minus.IsEffectivelyEnabled); Assert.True(plus.IsEffectivelyEnabled); Assert.Equal(10m, numeric.Increment);
            var list = window.FindControl<ScrollViewer>("LinkDeviceList")!;
            var row = list.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("linkDeviceRow"));
            Assert.True(row.Bounds.Height <= list.Bounds.Height + 1, "The single speaker's delay controls must fit in the list viewport.");
            selector.SelectedItem = model.DelaySteps.Single(c => c.Id == "100");
            using (var step = window.CaptureRenderedFrame()) Assert.Equal(100m, numeric.Increment);
            Assert.Equal(string.Format(model.Text["IncreaseDelay"], 100), AutomationProperties.GetName(plus));
            Assert.Equal(string.Format(model.Text["DecreaseDelay"], 100), AutomationProperties.GetName(minus));
            ClickPlaylistControl(window, plus); await CommandFinished(speaker.Plus);
            Assert.Equal(100, speaker.DelayMs); Assert.True(minus.IsEffectivelyEnabled);
            selector.Focus(); selector.IsDropDownOpen = true;
            using (var popup = CaptureTrackReviewFrame(window)) SaveReviewFrame(popup, $"delay-tuning-{language}-{width}-choices.png");
            selector.IsDropDownOpen = false;
            selector.SelectedItem = model.DelaySteps.Single(c => c.Id == "1");
            using (var fine = window.CaptureRenderedFrame()) Assert.Equal(1m, numeric.Increment);
            var editor = numeric.GetVisualDescendants().OfType<TextBox>().Single(); Assert.True(editor.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Assert.Equal(101, speaker.DelayMs);
            Assert.Equal(101, fixture.Settings.Links[0].Speakers[speaker.Id].Preferences.DelayMs);
            using (var tuned = CaptureTrackReviewFrame(window)) SaveReviewFrame(tuned, $"delay-tuning-{language}-{width}-tuned.png");
            var test = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.CalibrateCommand);
            Assert.Equal(model.Text["Calibrate"], test.Content);
            Assert.Equal(model.Text["CalibrationInfo"], ToolTip.GetTip(test));
            var reset = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.ResetDelays);
            Assert.Equal(model.Text["ResetSyncDelays"], AutomationProperties.GetName(reset));
            Assert.Equal(model.Text["ResetSyncDelays"], ToolTip.GetTip(reset));
            ClickPlaylistControl(window, reset); await CommandFinished(model.ResetDelays);
            Assert.Equal(0, speaker.DelayMs); using var resetFrame = window.CaptureRenderedFrame(); Assert.False(minus.IsEffectivelyEnabled);
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task DelayTuningDraftCommitsExactTextAndRetainsStepAcrossSaveAndReopen(string language)
    {
        await using var fixture = new PresentationFixture(language,
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        model.DelayStep = model.DelaySteps.Single(c => c.Id == "100");
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            model.EditLink.Execute(null); await CommandFinished(model.EditLink);
            using var frame = window.CaptureRenderedFrame();
            var panel = window.FindControl<Border>("SpeakerPanel")!;
            var draft = Assert.Single(model.DraftSpeakers); Assert.Equal(100, draft.DelayStepMs);
            var numeric = panel.GetVisualDescendants().OfType<NumericUpDown>().Single();
            var editor = numeric.GetVisualDescendants().OfType<TextBox>().Single(); editor.Focus(); editor.Text = "137";
            var plus = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == draft.Plus);
            ClickPlaylistControl(window, plus); await CommandFinished(draft.Plus);
            Assert.Equal(237, draft.DelayMs); Assert.Equal(0, model.LinkedSpeakers[0].DelayMs);
            var save = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.SaveLink);
            ClickPlaylistControl(window, save); await CommandFinished(model.SaveLink);
            var stored = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load();
            Assert.Equal(237, Assert.Single(stored.Links).Speakers["test-output"].Preferences.DelayMs);
            model.EditLink.Execute(null); await CommandFinished(model.EditLink);
            Assert.Equal(237, Assert.Single(model.DraftSpeakers).DelayMs); Assert.Equal(100, model.DraftSpeakers[0].DelayStepMs);
            model.CreateLink.Execute(null); await CommandFinished(model.CreateLink);
            using var empty = window.CaptureRenderedFrame(); Assert.False(model.HasDelayControls);
            Assert.False(window.FindControl<Grid>("SyncTuningControls")!.IsEffectivelyVisible);
            model.DraftSpeakers[0].Selected = true;
            using var selected = window.CaptureRenderedFrame(); Assert.True(model.HasDelayControls);
            Assert.True(window.FindControl<Grid>("SyncTuningControls")!.IsEffectivelyVisible);
            model.CancelLink.Execute(null); await CommandFinished(model.CancelLink);
            Assert.Equal(237, model.LinkedSpeakers[0].DelayMs);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DelayTuningLimitsIgnoreUnchangedValuesAndKeepCommandsInSync()
    {
        var calls = 0; var text = new Localizer("en");
        var speaker = new SpeakerViewModel(new("test", "Test", SpeakerTransport.Windows), new(), text, (_, _) => calls++);
        try
        {
            Assert.False(speaker.Minus.CanExecute(null)); speaker.Minus.Execute(null); speaker.DelayMs = -10;
            Assert.Equal(0, calls);
            speaker.DelayStepMs = 100; speaker.Plus.Execute(null); Assert.Equal(100, speaker.DelayMs); Assert.Equal(1, calls);
            speaker.DelayMs = decimal.MaxValue; Assert.Equal(10000, speaker.DelayMs); Assert.False(speaker.Plus.CanExecute(null));
            var changed = calls; speaker.Plus.Execute(null); speaker.DelayMs = 10001; speaker.DelayMs = 10000;
            Assert.Equal(changed, calls); speaker.Minus.Execute(null); Assert.Equal(9900, speaker.DelayMs);
            speaker.DelayStepMs = 7; Assert.Equal(100, speaker.DelayStepMs);
            text.Switch("ja"); Assert.Equal(string.Format(text["IncreaseDelay"], 100), speaker.IncreaseDelayLabel);
            speaker.DelayMs = decimal.MinValue; Assert.Equal(0, speaker.DelayMs); Assert.False(speaker.Minus.CanExecute(null));
        }
        finally { speaker.Detach(); }
    }

    private sealed class DelayTuningOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error => null;
        public List<int> Applied { get; } = [];
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task UpdateControlsAsync() { Applied.Add(Control.DelayMs); return Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [AvaloniaFact]
    public async Task DelayTuningChangesReachCurrentOutputWithoutReloadOrRedundantCommands()
    {
        var outputs = new List<DelayTuningOutput>();
        await using var fixture = new PresentationFixture(outputFactory: d => { var output = new DelayTuningOutput(d); outputs.Add(output); return output; },
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync(); await model.PlayTrackAsync(fixture.First);
        await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var output = Assert.Single(outputs); var speaker = Assert.Single(model.LinkedSpeakers);
        model.DelayStep = model.DelaySteps.Single(c => c.Id == "100"); Assert.Empty(output.Applied);
        speaker.Plus.Execute(null); speaker.Plus.Execute(null); speaker.Plus.Execute(null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (output.Applied.LastOrDefault() != 300) await Task.Delay(10, deadline.Token);
        var updates = output.Applied.Count; speaker.DelayMs = 300; await Task.Delay(50, deadline.Token); Assert.Equal(updates, output.Applied.Count);
        model.DelayStep = model.DelaySteps.Single(c => c.Id == "1"); speaker.Minus.Execute(null);
        while (output.Applied.LastOrDefault() != 299) await Task.Delay(10, deadline.Token);
        Assert.Single(outputs); Assert.True(model.IsPlaying); Assert.Equal(299, fixture.Settings.Links[0].Speakers[speaker.Id].Preferences.DelayMs);
    }

    [AvaloniaFact]
    public async Task DelayTuningStepSurvivesOfflineRowsRescanAndLanguageChangeWithoutChangingDelays()
    {
        var devices = new List<SpeakerDevice>();
        await using var fixture = new PresentationFixture(discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>(devices.ToArray()));
        fixture.Settings.Links[0].Speakers["offline"] = new() { Name = "Onkyo test", Transport = SpeakerTransport.Chromecast, Preferences = new() { DelayMs = 173 } };
        var model = fixture.Model; model.DelayStep = model.DelaySteps.Single(c => c.Id == "100"); await model.InitializeAsync();
        Assert.All(model.LinkedSpeakers, speaker => Assert.Equal(100, speaker.DelayStepMs));
        Assert.Equal(173, model.LinkedSpeakers.Single(s => s.Id == "offline").DelayMs);
        model.EditLink.Execute(null); await CommandFinished(model.EditLink); model.DelayStep = model.DelaySteps.Single(c => c.Id == "1");
        devices.Add(new("late", "New test receiver", SpeakerTransport.AirPlay, "127.0.0.1", 7000));
        model.ScanCommand.Execute(null); await CommandFinished(model.ScanCommand);
        Assert.All(model.DraftSpeakers, speaker => Assert.Equal(1, speaker.DelayStepMs));
        model.Text.Switch("zh-Hans"); Assert.Equal("1", model.DelayStep.Id);
        Assert.All(model.DraftSpeakers, speaker => Assert.Equal(string.Format(model.Text["IncreaseDelay"], 1), speaker.IncreaseDelayLabel));
        Assert.Equal(173, fixture.Settings.Links[0].Speakers["offline"].Preferences.DelayMs);
        model.DelayStep = new("7", "Invalid step"); Assert.Equal("1", model.DelayStep.Id);
    }
}
