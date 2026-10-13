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
    private sealed class StatusOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error { get; set; }
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken token) => Prepared.Task.WaitAsync(token);
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task SpeakerStatesDescribeTheRealStartupBarrierPauseAndReceiverFailure(string language, int width, int height)
    {
        var receiver = new SpeakerDevice("status-cast", "Onkyo · Synthetic test receiver", SpeakerTransport.Chromecast, "192.0.2.10", 8009);
        var outputs = new Dictionary<string, StatusOutput>();
        var prepareImmediately = false;
        TaskCompletionSource<IReadOnlyList<SpeakerDevice>>? pendingScan = null;
        await using var fixture = new PresentationFixture(language, outputFactory: d =>
        {
            var output = outputs[d.Id] = new(d);
            if (prepareImmediately) output.Prepared.SetResult();
            return output;
        }, discoverNetwork: _ => pendingScan?.Task ?? Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        fixture.Settings.Links[0].Speakers[receiver.Id] = new() { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true } };
        await fixture.Model.InitializeAsync();
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        Task? start = null;
        try
        {
            window.Show(); fixture.Model.LinkPanelOpen = true; await Task.Delay(350); window.ApplyMotionPreference(false);
            start = fixture.Model.PlayTrackAsync(fixture.First, 12);
            await Until(() => outputs.Count == 2);
            outputs["test-output"].Prepared.SetResult();
            await Task.Delay(40); await PollPlayback(fixture.Model);
            var pc = fixture.Model.LinkedSpeakers.Single(s => s.Id == "test-output");
            var cast = fixture.Model.LinkedSpeakers.Single(s => s.Id == receiver.Id);
            Assert.Equal(fixture.Model.Text["SpeakerWaitingForSync"], pc.Status);
            Assert.Equal(fixture.Model.Text["SpeakerConnecting"], cast.Status);
            Assert.False(fixture.Backend.Started.Task.IsCompleted); Assert.Equal(12, fixture.Model.Position);
            using (var preparing = window.CaptureRenderedFrame()) SaveReviewFrame(preparing, $"speaker-status-{language}-{width}-waiting.png");
            var list = window.FindControl<ScrollViewer>("LinkDeviceList")!;
            var castStatus = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("speakerStatus") && ReferenceEquals(t.DataContext, cast));
            var statusPoint = castStatus.TranslatePoint(default, list)!.Value;
            Assert.InRange(statusPoint.Y, 0, list.Bounds.Height - castStatus.Bounds.Height);
            Assert.True(window.FindControl<Border>("SpeakerPanel")!.TranslatePoint(default, window)!.Value.Y >= 32);
            outputs[receiver.Id].Prepared.SetResult(); await start; await PollPlayback(fixture.Model);
            Assert.True(fixture.Model.IsPlaying); Assert.Equal(fixture.Model.Text["Playing"], pc.Status); Assert.Equal(fixture.Model.Text["Playing"], cast.Status);
            fixture.Model.PlayCommand.Execute(null); await CommandFinished(fixture.Model.PlayCommand);
            Assert.Equal(fixture.Model.Text["Paused"], pc.Status); Assert.Equal(fixture.Model.Text["Paused"], cast.Status); Assert.Equal(pc.Status, pc.Details);
            fixture.Model.PlayCommand.Execute(null); await CommandFinished(fixture.Model.PlayCommand);
            outputs[receiver.Id].Error = "Synthetic connection timed out. Check the speaker connection, then rescan.";
            await PollPlayback(fixture.Model);
            Assert.True(fixture.Model.IsPlaying); Assert.Equal(fixture.Model.Text["Playing"], pc.Status);
            Assert.True(cast.HasIssue); Assert.Equal(outputs[receiver.Id].Error, cast.Details);
            using var failed = window.CaptureRenderedFrame(); SaveReviewFrame(failed, $"speaker-status-{language}-{width}-failed.png");
            var issue = window.FindControl<Border>("SpeakerPanel")!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("speakerIssue") && ReferenceEquals(t.DataContext, cast));
            Assert.True(issue.IsEffectivelyVisible); Assert.Equal(cast.Details, issue.Text); Assert.Equal(2, issue.MaxLines);
            var point = issue.TranslatePoint(default, window.FindControl<Border>("SpeakerPanel")!)!.Value;
            Assert.True(point.X >= 0 && point.X + issue.Bounds.Width <= window.FindControl<Border>("SpeakerPanel")!.Bounds.Width);
            var issuePoint = issue.TranslatePoint(default, list)!.Value;
            Assert.InRange(issuePoint.Y, 0, list.Bounds.Height - issue.Bounds.Height);
            var rescan = window.FindControl<Button>("OverviewRescanButton")!;
            Assert.True(rescan.IsEffectivelyVisible); Assert.True(rescan.IsEnabled); Assert.False(fixture.Model.IsLinkEditing);
            pendingScan = new(TaskCreationOptions.RunContinuationsAsynchronously); prepareImmediately = true;
            ClickPlaylistControl(window, rescan);
            using (var scanning = window.CaptureRenderedFrame()) SaveReviewFrame(scanning, $"speaker-status-{language}-{width}-rescan.png");
            Assert.True(fixture.Model.IsScanning); Assert.False(rescan.IsEnabled);
            Assert.True(window.FindControl<Grid>("NetworkScanNotice")!.IsEffectivelyVisible);
            var scanWave = window.FindControl<Musync.App.Controls.PreparationIndicator>("NetworkScanWave")!;
            Assert.True(scanWave.Active); Assert.False(scanWave.AnimationRunning);
            window.ApplyMotionPreference(true); Assert.True(scanWave.AnimationRunning);
            window.ApplyMotionPreference(false); Assert.False(scanWave.AnimationRunning);
            pendingScan.SetResult([receiver]); await CommandFinished(fixture.Model.ScanCommand);
            await Until(() => !cast.HasIssue && cast.Status == fixture.Model.Text["Playing"] && !fixture.Model.Busy);
            Assert.True(fixture.Model.IsPlaying); Assert.Equal(12, fixture.Model.Position); Assert.False(fixture.Model.IsLinkEditing);
            Assert.Equal(2, fixture.Settings.Links[0].Speakers.Count);
        }
        finally
        {
            foreach (var output in outputs.Values) output.Prepared.TrySetResult();
            pendingScan?.TrySetResult([receiver]);
            if (start is not null) await start;
            window.Close();
        }
    }
    [AvaloniaFact]
    public async Task RediscoveredSpeakerRefreshesItsVisibleColorAndExplanation()
    {
        await using var fixture = new PresentationFixture();
        var receiver = new SpeakerDevice("offline", "Synthetic receiver", SpeakerTransport.AirPlay, UnsupportedReason: "DeviceOffline");
        var speaker = new SpeakerViewModel(receiver, new(), fixture.Model.Text, (_, _) => { });
        var names = new List<string?>(); speaker.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        try
        {
            Assert.True(speaker.HasIssue); var offlineColor = speaker.StatusColor;
            speaker.UpdateDevice(receiver with { UnsupportedReason = null, Host = "192.0.2.20" });
            Assert.False(speaker.HasIssue); Assert.NotEqual(offlineColor, speaker.StatusColor);
            Assert.Equal(fixture.Model.Text["SpeakerAvailable"], speaker.Details);
            Assert.Contains(nameof(speaker.StatusColor), names); Assert.Contains(nameof(speaker.Details), names); Assert.Contains(nameof(speaker.HasIssue), names);
            speaker.UpdateStatus(SpeakerPlaybackPhase.WaitingForSync, null); fixture.Model.Text.Switch("ja");
            Assert.Equal("同期を待機中", speaker.Status); Assert.Equal(speaker.Status, speaker.Details);
        }
        finally { speaker.Detach(); }
    }
}
