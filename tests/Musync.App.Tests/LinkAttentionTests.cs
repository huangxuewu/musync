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
    public async Task LinkAttentionPersistsAfterToastAndKeyboardOpensTheAffectedSpeakers(string language, int width, int height)
    {
        var receiver = new SpeakerDevice("attention-cast", "Onkyo · Synthetic receiver", SpeakerTransport.Chromecast, "192.0.2.10", 8009);
        var outputs = new Dictionary<string, StatusOutput>();
        await using var fixture = new PresentationFixture(language, outputFactory: d =>
        {
            var output = outputs[d.Id] = new(d); output.Prepared.TrySetResult(); return output;
        }, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        fixture.Settings.Links[0].Speakers[receiver.Id] = new() { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true, Volume = 54, DelayMs = 100 } };
        await fixture.Model.InitializeAsync(); var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            await model.PlayTrackAsync(fixture.First, 12); await PollPlayback(model);
            var toggle = window.FindControl<ToggleButton>("SpeakerToggle")!;
            Assert.Equal(model.Text["SpeakerControls"], AutomationProperties.GetName(toggle));
            outputs[receiver.Id].Error = "Synthetic receiver connection lost. Rescan to reconnect.";
            await PollPlayback(model); Assert.True(model.IsPlaying); Assert.True(model.HasToast);
            await Task.Delay(5300); Assert.False(model.HasToast);
            window.ApplyMotionPreference(false);
            using (var failed = CaptureTrackReviewFrame(window)) SaveReviewFrame(failed, $"link-attention-{language}-{width}-failed.png");
            var badge = window.FindControl<Border>("LinkIssueBadge"); Assert.NotNull(badge);
            Assert.True(badge.IsEffectivelyVisible); Assert.False(badge.IsHitTestVisible);
            var name = string.Format(model.Text["LinkAttentionOne"], 1);
            Assert.Equal(name, AutomationProperties.GetName(toggle)); Assert.Equal(name, ToolTip.GetTip(toggle));
            var point = badge.TranslatePoint(default, toggle)!.Value;
            Assert.InRange(point.X, 0, toggle.Bounds.Width - badge.Bounds.Width);
            Assert.InRange(point.Y, 0, toggle.Bounds.Height - badge.Bounds.Height);
            Assert.True(toggle.Focus()); window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await Task.Delay(220); Assert.True(model.SpeakerControlsOpen); Assert.False(model.LinkPanelOpen);
            var list = window.FindControl<ScrollViewer>("SpeakerControlsList")!;
            var row = list.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("speakerControlRow") && b.DataContext is SpeakerViewModel speaker && speaker.Id == receiver.Id);
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => Equals(ToolTip.GetTip(t), outputs[receiver.Id].Error));
            var delay = Assert.Single(row.GetVisualDescendants().OfType<NumericUpDown>());
            var rowPoint = row.TranslatePoint(default, list)!.Value; var delayPoint = delay.TranslatePoint(default, list)!.Value;
            Assert.InRange(rowPoint.Y, 0, list.Bounds.Height - row.Bounds.Height);
            Assert.InRange(delayPoint.Y, 0, list.Bounds.Height - delay.Bounds.Height);
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, list.VerticalScrollBarVisibility);
            Assert.Equal(336, window.FindControl<Border>("SpeakerControlsPanel")!.Bounds.Width);
            using (var panel = CaptureTrackReviewFrame(window)) SaveReviewFrame(panel, $"link-attention-{language}-{width}-panel.png");
            ClickPlaylistControl(window, window.FindControl<ToggleButton>("LinkConfigurationToggle")!);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(model.LinkPanelOpen); Assert.False(model.SpeakerControlsOpen);
            ClickPlaylistControl(window, window.FindControl<Button>("OverviewRescanButton")!); await CommandFinished(model.ScanCommand);
            await Until(() => !model.Busy && !model.LinkedSpeakers.Any(s => s.HasIssue)); await PollPlayback(model);
            Assert.False(badge.IsEffectivelyVisible); Assert.Equal(model.Text["SpeakerControls"], AutomationProperties.GetName(toggle));
            Assert.Equal(model.LinkSummary, ToolTip.GetTip(toggle)); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
            model.LinkPanelOpen = false; await Task.Delay(220);
            using (var recovered = CaptureTrackReviewFrame(window)) SaveReviewFrame(recovered, $"link-attention-{language}-{width}-recovered.png");
            var saved = fixture.Settings.Links[0].Speakers[receiver.Id].Preferences;
            Assert.Equal(54, saved.Volume); Assert.Equal(100, saved.DelayMs);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task LinkAttentionUsesOnlyTheActiveLinkAndRoutedSourceAndUpdatesItsLanguage(string language)
    {
        await using var fixture = new PresentationFixture(language, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        await fixture.Model.InitializeAsync(); var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        var unrelated = new SpeakerViewModel(new("other", "Other receiver", SpeakerTransport.AirPlay, UnsupportedReason: "DeviceOffline"), new(), model.Text, (_, _) => { });
        try
        {
            var badge = window.FindControl<Border>("LinkIssueBadge")!; Assert.NotNull(badge);
            var toggle = window.FindControl<ToggleButton>("SpeakerToggle")!;
            model.Speakers.Add(unrelated); Assert.False(badge.IsVisible);
            var linked = Assert.Single(model.LinkedSpeakers); linked.UpdateStatus(false, "Synthetic control failure");
            Assert.True(badge.IsVisible); Assert.Equal(string.Format(model.Text["LinkAttentionOne"], 1), AutomationProperties.GetName(toggle));
            model.LinkedSpeakers.Add(unrelated);
            Assert.Equal(string.Format(model.Text["LinkAttentionMany"], 2), AutomationProperties.GetName(toggle));
            model.Language = model.Languages.First(l => l.Id == (language == "ja" ? "en" : "ja"));
            Assert.Equal(string.Format(model.Text["LinkAttentionMany"], 2), AutomationProperties.GetName(toggle));
            model.Source = model.Sources.First(s => s.Id == "spotify"); await SourceTransitionSettled(model);
            Assert.False(badge.IsVisible); Assert.Equal(model.Text["SpeakerControls"], AutomationProperties.GetName(toggle));
            model.Source = model.Sources.First(s => s.Id == "local"); await SourceTransitionSettled(model);
            Assert.True(badge.IsVisible); // The offline linked receiver still needs attention after leaving Spotify.
            model.LinkedSpeakers.Remove(unrelated); linked.UpdateStatus(false, null);
            Assert.False(badge.IsVisible); Assert.Equal(model.LinkSummary, ToolTip.GetTip(toggle));
        }
        finally { model.LinkedSpeakers.Remove(unrelated); model.Speakers.Remove(unrelated); unrelated.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LinkAttentionDetachesRemovedSpeakersAndDoesNotReactAfterDisposal()
    {
        var fixture = new PresentationFixture(discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        var disposed = false;
        try
        {
            await fixture.Model.InitializeAsync(); var model = fixture.Model; var linked = Assert.Single(model.LinkedSpeakers);
            var changes = new List<string?>(); model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            linked.UpdateStatus(false, "Synthetic control failure"); Assert.Contains("HasLinkIssue", changes);
            model.LinkedSpeakers.Clear(); changes.Clear(); linked.UpdateStatus(false, "Different failure");
            Assert.DoesNotContain("HasLinkIssue", changes);
            model.LinkedSpeakers.Add(linked); changes.Clear(); await fixture.DisposeAsync(); disposed = true; changes.Clear();
            linked.UpdateStatus(false, null); Assert.DoesNotContain("HasLinkIssue", changes);
        }
        finally { if (!disposed) await fixture.DisposeAsync(); }
    }
}
