using Avalonia;
using Avalonia.Controls;
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
    public async Task FailedRescanToastFitsTheMinimumWindowAndRetryPreservesDraftEdits(string language)
    {
        var receiver = new SpeakerDevice("network-output", "Synthetic speaker", SpeakerTransport.Chromecast, "192.0.2.10", 8009);
        var fail = false;
        await using var fixture = new PresentationFixture(language, discoverNetwork: _ => fail
            ? Task.FromException<IReadOnlyList<SpeakerDevice>>(new IOException("Synthetic discovery failure"))
            : Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        var model = fixture.Model;
        fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        {
            Name = receiver.Name, Transport = receiver.Transport,
            Preferences = new() { Selected = true, Volume = 37, DelayMs = 120 }
        };
        await model.InitializeAsync(); model.EditLink.Execute(null);
        var draft = Assert.Single(model.DraftSpeakers, s => s.Id == receiver.Id);
        draft.Volume = 64; draft.DelayMs = 245;
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            using var initial = window.CaptureRenderedFrame();
            var rescan = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.ScanCommand && b.IsEffectivelyVisible);
            Assert.True(rescan.IsEffectivelyVisible); Assert.True(rescan.IsEnabled);
            var point = rescan.TranslatePoint(new Point(rescan.Bounds.Width / 2, rescan.Bounds.Height / 2), window)!.Value;
            fail = true;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await CommandFinished(model.ScanCommand);
            using var frame = window.CaptureRenderedFrame();
            var toast = window.FindControl<Border>("StatusToast")!;
            var text = window.FindControl<TextBlock>("StatusToastText")!;
            Assert.True(toast.IsEffectivelyVisible); Assert.Equal(model.Text["ScanNetworkFailed"], text.Text);
            var origin = toast.TranslatePoint(new Point(), window)!.Value;
            Assert.InRange(origin.X, 8, 1112 - toast.Bounds.Width);
            Assert.InRange(origin.Y, 8, 616 - toast.Bounds.Height);
            Assert.InRange(toast.Bounds.Width, 1, 440);
            Assert.True(toast.Bounds.Height >= text.Bounds.Height + 24);
            var panel = window.FindControl<Border>("SpeakerPanel")!;
            var panelOrigin = panel.TranslatePoint(new Point(), window)!.Value;
            Assert.False(new Rect(origin, toast.Bounds.Size).Intersects(new Rect(panelOrigin, panel.Bounds.Size)),
                "The speaker popup must not cover the scan failure toast.");
            Assert.Same(draft, Assert.Single(model.DraftSpeakers, s => s.Id == receiver.Id));
            Assert.True(draft.Selected); Assert.True(draft.Available);
            Assert.Equal(64, draft.Volume); Assert.Equal(245, draft.DelayMs);
            Assert.Equal(37, fixture.Settings.Links[0].Speakers[receiver.Id].Preferences.Volume);
            SaveReviewFrame(frame, $"network-scan-{language}-1120-error.png");

            fail = false; receiver = receiver with { Name = "Recovered speaker" };
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await CommandFinished(model.ScanCommand);
            Assert.Equal(model.Text["ScanDone"], model.ToastMessage);
            Assert.Same(draft, Assert.Single(model.DraftSpeakers, s => s.Id == receiver.Id));
            Assert.Equal("Recovered speaker", draft.Name);
            Assert.Equal(64, draft.Volume); Assert.Equal(245, draft.DelayMs);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en", SpeakerTransport.Chromecast)]
    [InlineData("zh-Hans", SpeakerTransport.Chromecast)]
    [InlineData("ja", SpeakerTransport.Chromecast)]
    [InlineData("en", SpeakerTransport.AirPlay)]
    [InlineData("zh-Hans", SpeakerTransport.AirPlay)]
    [InlineData("ja", SpeakerTransport.AirPlay)]
    public async Task FailedNetworkRescanKeepsTheCurrentSpeakerAndPlayingOutput(string language, SpeakerTransport transport)
    {
        var receiver = new SpeakerDevice("network-output", "Synthetic speaker", transport, "192.0.2.10", 7000);
        var fail = false; var created = 0;
        await using var fixture = new PresentationFixture(language,
            outputFactory: device => { created++; return new PresentationOutput(device); },
            discoverNetwork: _ => fail
                ? Task.FromException<IReadOnlyList<SpeakerDevice>>(new IOException("Synthetic discovery failure"))
                : Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        var model = fixture.Model;
        fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        {
            Name = receiver.Name, Transport = transport,
            Preferences = new() { Selected = true, Volume = 37, DelayMs = 120 }
        };
        await model.InitializeAsync();
        var row = Assert.Single(model.Speakers, s => s.Id == receiver.Id);
        await model.PlayTrackAsync(fixture.First); Assert.Equal(2, created);
        fail = true;
        model.ScanCommand.Execute(null); await CommandFinished(model.ScanCommand);
        await Task.Delay(650); // A spurious routing restart is debounced for 500 ms.
        Assert.Equal(2, created);
        Assert.Same(row, Assert.Single(model.Speakers, s => s.Id == receiver.Id));
        Assert.True(model.IsPlaying); Assert.False(model.Busy);
        Assert.Equal(model.Text["ScanNetworkFailed"], model.ToastMessage);
        Assert.NotEqual(model.Text["ScanDone"], model.Status);
        Assert.Equal(37, row.Volume); Assert.Equal(120, row.DelayMs);
        Assert.Equal(37, fixture.Settings.Links[0].Speakers[receiver.Id].Preferences.Volume);

        receiver = receiver with { Name = "Recovered speaker", Host = "192.0.2.11" };
        fail = false;
        model.ScanCommand.Execute(null); await CommandFinished(model.ScanCommand);
        Assert.Same(row, Assert.Single(model.Speakers, s => s.Id == receiver.Id));
        Assert.Equal("Recovered speaker", row.Name); Assert.Equal("192.0.2.11", row.Device.Host);
        Assert.Equal(model.Text["ScanDone"], model.ToastMessage);
        Assert.False(model.Busy);
    }

    [AvaloniaTheory]
    [InlineData(SpeakerTransport.Chromecast)]
    [InlineData(SpeakerTransport.AirPlay)]
    public async Task SuccessfulEmptyScanStillMarksThePreviouslyDiscoveredReceiverOffline(SpeakerTransport transport)
    {
        var receiver = new SpeakerDevice("network-output", "Synthetic speaker", transport, "192.0.2.10", 7000);
        var found = true;
        await using var fixture = new PresentationFixture(discoverNetwork: _ =>
            Task.FromResult<IReadOnlyList<SpeakerDevice>>(found ? [receiver] : []));
        fixture.Settings.Links[0].Speakers[receiver.Id] = new() { Name = receiver.Name, Transport = transport };
        await fixture.Model.InitializeAsync();
        Assert.Contains(fixture.Model.Speakers, s => s.Id == receiver.Id);
        found = false;
        fixture.Model.ScanCommand.Execute(null); await CommandFinished(fixture.Model.ScanCommand);
        Assert.DoesNotContain(fixture.Model.Speakers, s => s.Id == receiver.Id);
        Assert.False(Assert.Single(fixture.Model.LinkedSpeakers, s => s.Id == receiver.Id).Available);
        Assert.Equal(fixture.Model.Text["ScanDone"], fixture.Model.ToastMessage);
    }
}
