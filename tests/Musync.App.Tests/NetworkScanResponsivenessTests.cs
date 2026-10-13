using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task SlowNetworkScanKeepsLocalProgressAndTransportResponsive(string language)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SpeakerDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanning = false; var discoveries = 0;
        await using var fixture = new PresentationFixture(language, discoverNetwork: token =>
        {
            discoveries++;
            return scanning ? pending.Task.WaitAsync(token) : Task.FromResult<IReadOnlyList<SpeakerDevice>>([]);
        });
        var model = fixture.Model;
        await model.InitializeAsync();
        await model.PlayTrackAsync(fixture.First);
        await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        model.EditLink.Execute(null);
        var draft = Assert.Single(model.DraftSpeakers);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            using var initial = window.CaptureRenderedFrame();
            scanning = true;
            model.ScanCommand.Execute(null);
            Assert.False(model.ScanCommand.CanExecute(null));
            Assert.False(model.HasToast); // The previous successful scan is superseded by this wait.
            fixture.Backend.Seek(12); // Advance the silent fake decoder independently of the UI.
            await Task.Delay(700);
            Assert.Equal(12, model.Position);
            Assert.Equal("0:12", model.PositionText);
            Assert.Equal("此刻一起听见音乐", model.LyricCurrent);
            Assert.False(model.Busy);
            Assert.True(model.CanSaveLink);
            Assert.True(window.FindControl<Button>("PlayerPlayButton")!.IsEnabled);
            draft.Volume = 64; draft.DelayMs = 245;

            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
            Assert.False(model.IsPlaying);
            await model.SeekAsync(18); Assert.Equal(18, model.Position);
            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
            Assert.True(model.IsPlaying);
            Assert.False(model.ScanCommand.CanExecute(null));
            model.ScanCommand.Execute(null); Assert.Equal(2, discoveries);

            var wave = window.FindControl<PreparationIndicator>("NetworkScanWave")!;
            var notice = window.FindControl<Control>("NetworkScanNotice")!;
            // Activation refreshes the Windows animation preference; override it
            // after awaited transport commands to test reduced motion explicitly.
            window.ApplyMotionPreference(false);
            using var waiting = window.CaptureRenderedFrame();
            Assert.True(notice.IsEffectivelyVisible);
            Assert.False(wave.AnimationRunning);
            var text = window.FindControl<TextBlock>("NetworkScanStatus")!;
            Assert.Equal(model.Text["Scanning"], text.Text);
            var panel = window.FindControl<Border>("SpeakerPanel")!;
            var origin = notice.TranslatePoint(new Point(), panel)!.Value;
            Assert.True(origin.X >= 0 && origin.X + notice.Bounds.Width <= panel.Bounds.Width);
            Assert.True(origin.Y >= 0 && origin.Y + notice.Bounds.Height <= panel.Bounds.Height);
            SaveReviewFrame(waiting, $"network-wait-{language}-1120.png");
            window.ApplyMotionPreference(true); Assert.True(wave.AnimationRunning);
            window.WindowState = WindowState.Minimized; Assert.False(wave.AnimationRunning);
            window.WindowState = WindowState.Normal;
            model.EditLink.Execute(null); using var restored = window.CaptureRenderedFrame();
            Assert.True(wave.AnimationRunning);
            window.Hide(); Assert.False(wave.AnimationRunning);
            await Task.Delay(30);
            window.Show(); model.EditLink.Execute(null);
            using var shown = window.CaptureRenderedFrame(); window.ApplyMotionPreference(true);
            var popup = window.FindControl<Avalonia.Controls.Primitives.Popup>("SpeakerPopover")!;
            Assert.True(wave.AnimationRunning, $"window={window.IsVisible}; panel={model.LinkPanelOpen}; editing={model.IsLinkEditing}; popup={popup.IsOpen}; active={wave.Active}; motion={wave.MotionEnabled}");
            model.LinkPanelOpen = false; Assert.False(wave.AnimationRunning);
            model.EditLink.Execute(null); using var reopened = window.CaptureRenderedFrame();
            Assert.True(wave.AnimationRunning);
            pending.SetResult([]); await CommandFinished(model.ScanCommand);
            Assert.False(wave.AnimationRunning); Assert.False(notice.IsEffectivelyVisible);
            Assert.Equal(model.Text["Playing"], model.Status);
            Assert.Equal(model.Text["ScanDone"], model.ToastMessage);
        }
        finally { pending.TrySetResult([]); await CommandFinished(model.ScanCommand); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScanCompletionCannotClearAnotherPlaybackOperationsBusyState()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SpeakerDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        PreparingOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: device => output = new(device),
            discoverNetwork: token => pending.Task.WaitAsync(token));
        var start = fixture.Model.PlayTrackAsync(fixture.First);
        Assert.True(fixture.Model.Busy); Assert.True(fixture.Model.IsPreparingPlayback);
        fixture.Model.ScanCommand.Execute(null); pending.SetResult([]);
        try
        {
            await Task.Delay(150);
            Assert.True(fixture.Model.Busy);
            Assert.True(fixture.Model.IsPreparingPlayback);
            Assert.False(fixture.Model.CanConfigureLink);
            Assert.False(fixture.Model.ScanCommand.CanExecute(null));
            fixture.Model.CancelPreparation.Execute(null);
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            await CommandFinished(fixture.Model.CancelPreparation);
            await CommandFinished(fixture.Model.ScanCommand);
            Assert.False(fixture.Model.Busy); Assert.False(fixture.Model.IsPlaying);
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
        }
        finally { output?.Ready.TrySetResult(); await start; }
    }

    [AvaloniaFact]
    public async Task SlowSpeakerScanDoesNotBlockSpotifyStateRefreshOrSeek()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SpeakerDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = PlaybackJson(); var seeks = 0;
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/seek") { seeks++; return Task.FromResult(Response(HttpStatusCode.NoContent)); }
            return Task.FromResult(Response(HttpStatusCode.OK, state));
        });
        await using var fixture = new Fixture(handler, observePlayback: true, discoverNetwork: token => pending.Task.WaitAsync(token));
        fixture.Model.ScanCommand.Execute(null);
        try
        {
            state = PlaybackJson(playing: false, position: 140_000);
            await fixture.Model.RefreshSpotifyPlaybackAsync();
            Assert.False(fixture.Model.IsPlaying); Assert.Equal("2:20", fixture.Model.PositionText);
            await fixture.Model.SeekAsync(20); Assert.Equal(1, seeks); Assert.Equal("0:20", fixture.Model.PositionText);
            Assert.False(fixture.Model.ScanCommand.CanExecute(null));
            pending.SetResult([]); await CommandFinished(fixture.Model.ScanCommand);
            Assert.Equal(fixture.Model.Text["Paused"], fixture.Model.Status);
        }
        finally { pending.TrySetResult([]); await CommandFinished(fixture.Model.ScanCommand); }
    }

    [AvaloniaFact]
    public async Task ClosingThePlayerCancelsAWaitingScanEvenIfDiscoveryHasNotReturned()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SpeakerDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new PresentationFixture(discoverNetwork: _ => pending.Task);
        fixture.Model.ScanCommand.Execute(null);
        Assert.True(fixture.Model.IsScanning);
        await fixture.DisposeAsync();
        try
        {
            await CommandFinished(fixture.Model.ScanCommand);
            Assert.False(fixture.Model.IsScanning);
            Assert.False(fixture.Model.HasToast);
            var originalSpeakers = fixture.Model.Speakers.ToArray();
            pending.SetResult([new("late", "Late synthetic speaker", SpeakerTransport.Chromecast, "192.0.2.10", 8009)]);
            await Task.Delay(30);
            Assert.Equal(originalSpeakers, fixture.Model.Speakers.ToArray());
        }
        finally { pending.TrySetCanceled(); await CommandFinished(fixture.Model.ScanCommand); }
    }

    [AvaloniaFact]
    public async Task StartingAScanPreservesANewerUnrelatedNotification()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SpeakerDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = false;
        await using var fixture = new PresentationFixture(discoverNetwork: token => waiting
            ? pending.Task.WaitAsync(token) : Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        await fixture.Model.InitializeAsync();
        fixture.Model.ShowError(new IOException("Unrelated notification"));
        var message = fixture.Model.ToastMessage; var epoch = fixture.Model.ToastEpoch;
        waiting = true; fixture.Model.ScanCommand.Execute(null);
        try
        {
            Assert.True(fixture.Model.IsScanning); Assert.True(fixture.Model.HasToast);
            Assert.Equal(message, fixture.Model.ToastMessage); Assert.Equal(epoch, fixture.Model.ToastEpoch);
            pending.SetResult([]); await CommandFinished(fixture.Model.ScanCommand);
            Assert.Equal(message, fixture.Model.Status);
        }
        finally { pending.TrySetResult([]); await CommandFinished(fixture.Model.ScanCommand); }
    }
}
