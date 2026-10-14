using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Musync.App;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", "Another source is playing on this speaker. Rescan to retry Musync.")]
    [InlineData("zh-Hans", "音响正在播放其他内容。重新扫描以重试 Musync。")]
    [InlineData("ja", "別の音源を再生中です。再検索して Musync を再試行してください。")]
    public async Task ARecognizedReceiverIssueShowsLocalizedRecoveryAndRetainsItsDiagnostic(string language, string expected)
    {
        const string raw = "Chromecast playback was replaced by another source.";
        var receiver = new SpeakerDevice("localized-cast", "Onkyo · Synthetic receiver", SpeakerTransport.Chromecast, "192.0.2.30", 8009);
        var outputs = new Dictionary<string, StatusOutput>();
        await using var fixture = new PresentationFixture(language, outputFactory: device =>
        {
            var output = outputs[device.Id] = new(device); output.Prepared.TrySetResult(); return output;
        }, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true } };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            using var initial = CaptureTrackReviewFrame(window); window.ApplyMotionPreference(false);
            await model.PlayTrackAsync(fixture.First, 12); await PollPlayback(model);
            outputs[receiver.Id].Error = raw; await PollPlayback(model);
            var speaker = model.LinkedSpeakers.Single(s => s.Id == receiver.Id);
            model.LinkPanelOpen = true;
            using var configuration = CaptureTrackReviewFrame(window);
            SaveReviewFrame(configuration, $"speaker-recovery-{language}-1120.png");
            Assert.Equal(expected, speaker.Details);
            Assert.True(speaker.HasIssue); Assert.True(model.HasLinkIssue); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
            Assert.Contains(expected, model.Status); Assert.Contains(expected, model.ToastMessage);
            var issue = window.FindControl<Border>("SpeakerPanel")!.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("speakerIssue") && t.DataContext == speaker);
            Assert.Equal(expected, issue.Text); Assert.Contains(raw, Assert.IsType<string>(ToolTip.GetTip(issue)));
            model.SpeakerControlsOpen = true;
            using var controls = CaptureTrackReviewFrame(window); SaveReviewFrame(controls, $"speaker-recovery-controls-{language}-1120.png");
            var row = window.FindControl<Border>("SpeakerControlsPanel")!.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("speakerControlRow") && b.DataContext == speaker);
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => Equals(ToolTip.GetTip(t), expected));
            var epoch = model.ToastEpoch; await Task.Delay(5300); Assert.False(model.HasToast);
            model.Language = model.Languages.Single(choice => choice.Id == (language == "ja" ? "zh-Hans" : "ja"));
            await PollPlayback(model);
            Assert.False(model.HasToast); Assert.Equal(epoch, model.ToastEpoch); Assert.True(speaker.HasIssue);
            Assert.Equal(model.Text["SpeakerPlaybackReplaced"], speaker.Details); Assert.Contains(speaker.Details, model.Status);
            model.LinkPanelOpen = true;
            ClickPlaylistControl(window, window.FindControl<Button>("OverviewRescanButton")!); await CommandFinished(model.ScanCommand);
            await Until(() => !model.Busy && !model.LinkedSpeakers.Any(s => s.HasIssue)); await PollPlayback(model);
            Assert.True(model.IsPlaying); Assert.Equal(12, model.Position); Assert.False(model.HasLinkIssue);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task SpeakerRecoveryCopyPreservesOriginalDiagnosticsAndReceiverNameBoundaries(string language)
    {
        await using var fixture = new PresentationFixture(language);
        var text = fixture.Model.Text;
        var speaker = new SpeakerViewModel(new("copy-test", "Kitchen: room 2", SpeakerTransport.Chromecast), new(), text, (_, _) => { });
        try
        {
            var messages = new[]
            {
                ("Chromecast connection lost.", "SpeakerDisconnected"),
                ("Chromecast stopped playing because the audio stream failed.", "SpeakerStreamInterrupted"),
                ("Chromecast playback was stopped on the receiver.", "SpeakerPlaybackStopped"),
                ("Chromecast playback was replaced by another source.", "SpeakerPlaybackReplaced"),
                ("Chromecast did not identify the requested music in its load response.", "SpeakerLoadUnconfirmed"),
                ("Chromecast could not load the audio stream.", "SpeakerLoadFailed"),
                ("Chromecast rejected the audio request: INVALID_COMMAND", "SpeakerRequestRejected"),
                ("Chromecast playback position timed out. Try adjusting the delay again.", "SpeakerDelayUnavailable")
            };
            foreach (var (raw, key) in messages)
            {
                speaker.UpdateStatus(SpeakerPlaybackPhase.Playing, raw);
                Assert.Equal(text[key], speaker.Details); Assert.NotEqual(key, speaker.Details);
                Assert.Contains(raw, speaker.DiagnosticDetails); Assert.Contains(text["TechnicalDetails"], speaker.DiagnosticDetails);
                Assert.Equal("Kitchen: room 2: " + text[key], PlaybackErrorPresentation.Format("Kitchen: room 2: " + raw, text));
            }
            foreach (var unknown in new[] { "Synthetic codec failure: pcm reader", "A song titled Chromecast connection lost.", "Note Chromecast rejected the audio request: INVALID_COMMAND" })
            {
                speaker.UpdateStatus(SpeakerPlaybackPhase.Available, unknown);
                Assert.Equal(unknown, speaker.Details); Assert.Equal(unknown, speaker.DiagnosticDetails);
            }
            speaker.UpdateStatus(SpeakerPlaybackPhase.Playing, null);
            Assert.False(speaker.HasIssue); Assert.Equal(text["Playing"], speaker.DiagnosticDetails);
        }
        finally { speaker.Detach(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task TranslatingAnActiveReceiverToastKeepsItsDeadlineAndSuccessfulRetryClearsItsOwnMessage(string language)
    {
        RecoverableControlOutput? output = null;
        await using var fixture = new PresentationFixture(language, outputFactory: device => output = new(device));
        var model = fixture.Model;
        model.Speakers.Clear(); await model.InitializeAsync();
        await model.PlayTrackAsync(fixture.First, 12); await PollPlayback(model);
        Assert.NotNull(output);
        output.Controls = Task.FromException(new IOException("Chromecast rejected the audio request: INVALID_COMMAND"));
        var speaker = model.LinkedSpeakers.Single(); speaker.Volume = 40;
        var oldMessage = output.Device.Name + ": " + model.Text["SpeakerRequestRejected"];
        await WaitForControlStatus(model, oldMessage); var epoch = model.ToastEpoch;
        await Task.Delay(2200);
        model.Language = model.Languages.Single(choice => choice.Id == (language == "ja" ? "zh-Hans" : "ja"));
        var translated = output.Device.Name + ": " + model.Text["SpeakerRequestRejected"];
        Assert.Equal(translated, model.Status); Assert.Equal(translated, model.ToastMessage); Assert.Equal(epoch, model.ToastEpoch);
        await Task.Delay(3200); Assert.False(model.HasToast); Assert.Equal(epoch, model.ToastEpoch);
        output.Controls = Task.CompletedTask; speaker.Volume = 45;
        await WaitForControlStatus(model, model.Text["Playing"]);
        Assert.False(model.HasToast); Assert.False(speaker.HasIssue); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task LinkConfigurationOverviewFitsActualRowsAndRetainsTheBoundedDraftViewport(string language)
    {
        var receivers = new[]
        {
            new SpeakerDevice("copy-one", "Living room · Synthetic receiver", SpeakerTransport.AirPlay, "192.0.2.10"),
            new SpeakerDevice("copy-two", "Onkyo · " + new string('音', 32), SpeakerTransport.Chromecast, "192.0.2.20", 8009)
        };
        await using var fixture = new PresentationFixture(language, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>(receivers));
        foreach (var receiver in receivers) fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true } };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            using var initial = CaptureTrackReviewFrame(window); window.ApplyMotionPreference(false);
            model.LinkedSpeakers.Single(s => s.Id == "copy-two").UpdateStatus(SpeakerPlaybackPhase.Available, "Chromecast connection lost.");
            model.LinkPanelOpen = true; using var frame = CaptureTrackReviewFrame(window);
            var panel = window.FindControl<Border>("SpeakerPanel")!; var list = window.FindControl<ScrollViewer>("LinkDeviceList")!;
            var rows = list.GetVisualDescendants().OfType<Border>().Where(row => row.Classes.Contains("linkDeviceRow")).ToArray();
            Assert.Equal(3, rows.Length);
            var rowBottom = rows.Max(row => row.TranslatePoint(default, list)!.Value.Y + row.Bounds.Height);
            Assert.InRange(list.Bounds.Height - rowBottom, -1, 1);
            Assert.InRange(panel.Bounds.Height, 300, 480);
            Assert.True(double.IsNaN(panel.Height)); Assert.Equal(GridLength.Auto, window.FindControl<Grid>("LinkConfigurationLayout")!.RowDefinitions[2].Height);
            SaveReviewFrame(frame, $"link-overview-compact-{language}-1120.png");
            model.CreateLink.Execute(null); using var draft = CaptureTrackReviewFrame(window);
            Assert.True(model.IsLinkEditing); Assert.Equal(568, panel.Bounds.Height); Assert.Equal(440, panel.Bounds.Width);
            Assert.Equal(GridUnitType.Star, window.FindControl<Grid>("LinkConfigurationLayout")!.RowDefinitions[2].Height.GridUnitType);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task ALongLinkOverviewKeepsItsFooterReachableWhileScanning(string language)
    {
        var receivers = Enumerable.Range(1, 12).Select(index => new SpeakerDevice("long-link-" + index,
            "Room " + index + " · Synthetic receiver", SpeakerTransport.Chromecast, "192.0.2." + index, 8009)).ToArray();
        TaskCompletionSource<IReadOnlyList<SpeakerDevice>>? pending = null;
        await using var fixture = new PresentationFixture(language, discoverNetwork: _ => pending?.Task
            ?? Task.FromResult<IReadOnlyList<SpeakerDevice>>(receivers));
        foreach (var receiver in receivers) fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true } };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            using var initial = CaptureTrackReviewFrame(window); window.ApplyMotionPreference(false);
            model.LinkPanelOpen = true; using var open = CaptureTrackReviewFrame(window);
            var panel = window.FindControl<Border>("SpeakerPanel")!; var list = window.FindControl<ScrollViewer>("LinkDeviceList")!;
            var step = panel.GetVisualDescendants().OfType<ComboBox>().Single(combo => ReferenceEquals(combo.ItemsSource, model.DelaySteps));
            var before = list.MaxHeight; Assert.True(list.Extent.Height > list.Viewport.Height);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ClickPlaylistControl(window, window.FindControl<Button>("OverviewRescanButton")!);
            using var scanning = CaptureTrackReviewFrame(window);
            Assert.True(model.IsScanning); Assert.Equal(before - 36, list.MaxHeight);
            Assert.InRange(panel.Bounds.Height, 300, Math.Clamp(window.Bounds.Height - 152, 360, 740));
            var footer = step.TranslatePoint(default, panel)!.Value;
            Assert.InRange(footer.Y + step.Bounds.Height, 0, panel.Bounds.Height - 19);
            var listOrigin = list.TranslatePoint(default, panel)!.Value;
            Assert.True(listOrigin.Y + list.Bounds.Height <= footer.Y - 15);
            list.ScrollToEnd(); using var end = CaptureTrackReviewFrame(window);
            var last = list.GetVisualDescendants().OfType<Border>().Single(row => row.Classes.Contains("linkDeviceRow")
                && row.DataContext is SpeakerViewModel speaker && speaker.Id == "long-link-12");
            Assert.InRange(last.TranslatePoint(default, list)!.Value.Y, 0, list.Bounds.Height - last.Bounds.Height);
            SaveReviewFrame(end, $"link-overview-scanning-{language}-1120.png");
            pending.SetResult(receivers); await CommandFinished(model.ScanCommand);
            Assert.False(model.IsScanning); Assert.Equal(before, list.MaxHeight);
        }
        finally { pending?.TrySetResult(receivers); await CommandFinished(model.ScanCommand); window.Close(); }
    }
}
