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
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task SpeakerControlFailureRemainsVisibleAfterToastAndRetryClearsOnlyTheAffectedSpeaker(string language)
    {
        var receiver = new SpeakerDevice("control-homepod", "HomePod · Synthetic receiver", SpeakerTransport.AirPlay, "192.0.2.10");
        var outputs = new Dictionary<string, RecoverableControlOutput>();
        await using var fixture = new PresentationFixture(language, outputFactory: d => outputs[d.Id] = new(d),
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true } };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            using var initial = window.CaptureRenderedFrame(); window.ApplyMotionPreference(false);
            await model.PlayTrackAsync(fixture.First, 12); await PollPlayback(model);
            var pc = model.LinkedSpeakers.Single(s => s.Id == "test-output");
            var homepod = model.LinkedSpeakers.Single(s => s.Id == receiver.Id);
            const string detail = "Synthetic receiver rejected volume adjustment";
            outputs[receiver.Id].Controls = Task.FromException(new IOException(detail));
            homepod.Volume = 40;
            await WaitForControlStatus(model, receiver.Name + ": " + detail);
            Assert.True(homepod.HasIssue); Assert.Equal(detail, homepod.Details);
            Assert.Equal(model.Text["DeviceError"], homepod.Status);
            Assert.False(pc.HasIssue); Assert.Equal(model.Text["Playing"], pc.Status);
            Assert.True(model.HasLinkIssue); Assert.True(model.HasToast);
            await Task.Delay(5300); Assert.False(model.HasToast); await PollPlayback(model);
            Assert.True(model.HasLinkIssue); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
            model.SpeakerControlsOpen = true;
            using var controls = window.CaptureRenderedFrame();
            var row = window.FindControl<Border>("SpeakerControlsPanel")!.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("speakerControlRow") && b.DataContext == homepod);
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => Equals(ToolTip.GetTip(t), detail));
            Assert.True(window.FindControl<Border>("LinkIssueBadge")!.IsEffectivelyVisible);
            SaveReviewFrame(controls, $"speaker-control-error-{language}-1120.png");
            model.LinkPanelOpen = true;
            using var configuration = window.CaptureRenderedFrame();
            var issue = window.FindControl<Border>("SpeakerPanel")!.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("speakerIssue") && t.DataContext == homepod);
            Assert.True(issue.IsEffectivelyVisible); Assert.Equal(detail, issue.Text);
            SaveReviewFrame(configuration, $"speaker-control-details-{language}-1120.png");
            outputs[receiver.Id].Controls = Task.CompletedTask; homepod.Volume = 45;
            await WaitForControlStatus(model, model.Text["Playing"]);
            Assert.False(homepod.HasIssue); Assert.False(pc.HasIssue); Assert.False(model.HasLinkIssue);
            Assert.Equal(model.Text["Playing"], homepod.Status); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
            model.SpeakerControlsOpen = true;
            using var recovered = window.CaptureRenderedFrame(); SaveReviewFrame(recovered, $"speaker-control-recovered-{language}-1120.png");
            Assert.False(window.FindControl<Border>("LinkIssueBadge")!.IsVisible);
        }
        finally { window.Close(); }
    }
}
