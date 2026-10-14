using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private sealed class CalibrationStub : IAcousticCalibrationService
    {
        public int Runs, InputReads;
        public bool Pending, Unreliable, NoInputs;
        public CalibrationRequest? Request;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AcousticInputDevice> GetInputs() { InputReads++; return NoInputs ? [] : [new("test-mic", "Synthetic USB microphone")]; }
        public async Task<CalibrationRunResult> RunAsync(CalibrationRequest request, IProgress<CalibrationProgress>? progress, CancellationToken token)
        {
            Runs++; Request = request; Entered.TrySetResult(); progress?.Report(new(CalibrationPhase.Recording, 0, request.Speakers.Count));
            if (Pending) await Task.Delay(Timeout.Infinite, token);
            var status = Unreliable ? AcousticMeasurementStatus.MissingSignal : AcousticMeasurementStatus.Reliable;
            var rows = request.Speakers.Select(s => new AcousticSpeakerMeasurement(s.Device.Id, status, s.Device.Id == "test-output" ? 0 : 100, 1.5, .8)).ToArray();
            var delays = Unreliable ? new Dictionary<string, int>() : request.Speakers.ToDictionary(s => s.Device.Id, s => s.Device.Id == "test-output" ? 100 : 0);
            return new(request, new(rows, delays), delays);
        }
    }
    private static async Task PrepareCalibrationFixture(PresentationFixture fixture)
    {
        fixture.Settings.Links[0].Speakers["cal-cast"] = new() { Name = "Onkyo · Synthetic receiver", Transport = SpeakerTransport.Chromecast,
            Preferences = new() { Selected = true, Volume = 65 } };
        fixture.Model.Speakers.Clear(); await fixture.Model.InitializeAsync();
    }
    private static readonly SpeakerDevice CalibrationReceiver = new("cal-cast", "Onkyo · Synthetic receiver", SpeakerTransport.Chromecast, "192.0.2.1", 8009);

    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task CalibrationWorkflowDoesNotRecordOnOpenAndEscapeCancelsAnActiveTest(string language, int width, int height)
    {
        var service = new CalibrationStub { Pending = true };
        await using var fixture = new PresentationFixture(language, calibration: service,
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([CalibrationReceiver]));
        await PrepareCalibrationFixture(fixture); var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            model.OpenCalibration.Execute(null); await CommandFinished(model.OpenCalibration);
            using (var setup = CaptureTrackReviewFrame(window)) SaveReviewFrame(setup, $"calibration-{language}-{width}-setup.png");
            Assert.True(model.CalibrationOpen); Assert.Equal(0, service.Runs); Assert.Equal(1, service.InputReads); Assert.True(model.CanStartCalibration);
            Assert.True(window.FindControl<Button>("StartCalibrationButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<PreparationIndicator>("CalibrationWave")!.AnimationRunning);
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
            ClickPlaylistControl(window, window.FindControl<Button>("StartCalibrationButton")!); await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Task.Delay(30);
            using (var running = CaptureTrackReviewFrame(window)) SaveReviewFrame(running, $"calibration-{language}-{width}-running.png");
            Assert.True(model.CalibrationRunning); Assert.True(model.Busy); Assert.False(model.CanApplyCalibration);
            Assert.Equal(model.Text["CalibrationListening"], model.CalibrationStatus);
            Assert.True(window.FindControl<PreparationIndicator>("CalibrationWave")!.Active);
            var close = window.FindControl<Button>("CloseCalibrationButton")!; Assert.True(close.Focus());
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await CommandFinished(model.StartCalibration); await CommandFinished(model.CloseCalibration);
            Assert.False(model.CalibrationOpen); Assert.False(model.CalibrationRunning); Assert.False(model.Busy);
            Assert.Empty(fixture.Settings.Links[0].Calibrations); Assert.Equal(0, fixture.Settings.Links[0].Speakers["test-output"].Preferences.DelayMs);
            using var closed = window.CaptureRenderedFrame(); Assert.False(window.FindControl<PreparationIndicator>("CalibrationWave")!.Active);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task CalibrationWorkflowPersistsBySourceAndSupportsFineTuningWithoutLosingTheLegacyDelay(string language, int width, int height)
    {
        var service = new CalibrationStub(); var outputs = new Dictionary<string, DelayTuningOutput>();
        await using var fixture = new PresentationFixture(language, calibration: service, outputFactory: d => outputs[d.Id] = new(d),
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([CalibrationReceiver]));
        await PrepareCalibrationFixture(fixture); var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            model.OpenCalibration.Execute(null); await CommandFinished(model.OpenCalibration);
            model.StartCalibration.Execute(null); await CommandFinished(model.StartCalibration);
            using (var result = CaptureTrackReviewFrame(window)) SaveReviewFrame(result, $"calibration-{language}-{width}-results.png");
            Assert.True(model.CanApplyCalibration); Assert.Equal(2, model.CalibrationResults.Count); Assert.Empty(fixture.Settings.Links[0].Calibrations);
            var apply = window.FindControl<Button>("ApplyCalibrationButton")!; Assert.True(apply.IsEffectivelyVisible); Assert.True(apply.IsEffectivelyEnabled);
            ClickPlaylistControl(window, apply); await CommandFinished(model.ApplyCalibration);
            using (var applied = CaptureTrackReviewFrame(window)) SaveReviewFrame(applied, $"calibration-{language}-{width}-applied.png");
            Assert.False(model.CalibrationOpen); Assert.Equal(100, fixture.Settings.Links[0].Calibrations["file"].Speakers["test-output"].DelayMs);
            var stored = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load();
            Assert.Equal(100, stored.Links[0].Calibrations["file"].Speakers["test-output"].DelayMs);
            var speaker = model.LinkedSpeakers.Single(s => s.Id == "test-output"); Assert.True(speaker.HasCalibratedDelay); Assert.Equal(100, speaker.DelayMs);
            speaker.Minus.Execute(null); await CommandFinished(speaker.Minus); Assert.Equal(90, speaker.DelayMs); Assert.Equal(0, speaker.Preferences.DelayMs);
            await model.PlayTrackAsync(fixture.First); Assert.Equal(90, outputs["test-output"].Control.DelayMs);
            model.MasterVolume = 45; await Task.Delay(20); Assert.Equal(90, outputs["test-output"].Control.DelayMs);
            model.Source = model.Sources.Single(s => s.Id == "youtube");
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (model.Busy) await Task.Delay(5, wait.Token);
            Assert.False(speaker.HasCalibratedDelay); Assert.Equal(0, speaker.DelayMs);
            model.Source = model.Sources.Single(s => s.Id == "local"); while (model.Busy) await Task.Delay(5, wait.Token);
            Assert.Equal(90, speaker.DelayMs); Assert.True(speaker.HasCalibratedDelay);
            model.ResetDelays.Execute(null); await CommandFinished(model.ResetDelays); Assert.False(speaker.HasCalibratedDelay);
            Assert.Empty(fixture.Settings.Links[0].Calibrations); Assert.Equal(0, speaker.DelayMs);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task CalibrationWorkflowUnreliableResultsOfferRetryWithoutChangingSavedConfiguration(string language)
    {
        var service = new CalibrationStub { Unreliable = true };
        await using var fixture = new PresentationFixture(language, calibration: service,
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([CalibrationReceiver]));
        await PrepareCalibrationFixture(fixture); var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            model.OpenCalibration.Execute(null); await CommandFinished(model.OpenCalibration); model.StartCalibration.Execute(null); await CommandFinished(model.StartCalibration);
            using (var failed = CaptureTrackReviewFrame(window)) SaveReviewFrame(failed, $"calibration-{language}-unreliable.png");
            Assert.False(model.CanApplyCalibration); Assert.True(model.CalibrationShowStart); Assert.Empty(fixture.Settings.Links[0].Calibrations);
            Assert.False(window.FindControl<Button>("ApplyCalibrationButton")!.IsEffectivelyVisible);
            var retry = window.FindControl<Button>("StartCalibrationButton")!; Assert.True(retry.IsEffectivelyVisible); Assert.True(retry.IsEffectivelyEnabled);
            Assert.Equal(model.Text["CalibrationRetry"], retry.Content);
            service.Unreliable = false; ClickPlaylistControl(window, retry); await CommandFinished(model.StartCalibration);
            Assert.Equal(2, service.Runs); Assert.True(model.CanApplyCalibration); Assert.Empty(fixture.Settings.Links[0].Calibrations);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task CalibrationWorkflowLeavesMusicPausedAndNextPlayResumesThePreviousPositionOnlyOnce()
    {
        var service = new CalibrationStub();
        await using var fixture = new PresentationFixture(calibration: service,
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([CalibrationReceiver]));
        await PrepareCalibrationFixture(fixture); var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12); await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(model.IsPlaying);
        model.OpenCalibration.Execute(null); await CommandFinished(model.OpenCalibration); model.StartCalibration.Execute(null); await CommandFinished(model.StartCalibration);
        Assert.False(model.IsPlaying); Assert.Equal(12, model.Position);
        model.CloseCalibration.Execute(null); await CommandFinished(model.CloseCalibration);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); Assert.True(model.IsPlaying); Assert.Equal(12, fixture.Backend.Position);
        await model.PlayTrackAsync(fixture.First); Assert.Equal(0, model.Position);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (fixture.Backend.Position != 0) await Task.Delay(5, timeout.Token);
        Assert.Equal(0, fixture.Backend.Position);
    }
    [AvaloniaFact]
    public async Task CalibrationWorkflowMissingMicrophoneAndMissingSpeakerKeepTheStartActionDisabled()
    {
        var service = new CalibrationStub { NoInputs = true };
        await using var fixture = new PresentationFixture(calibration: service, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([]));
        await fixture.Model.InitializeAsync(); var model = fixture.Model;
        model.OpenCalibration.Execute(null); await CommandFinished(model.OpenCalibration); Assert.False(model.CanStartCalibration);
        Assert.Equal(model.Text["CalibrationNoMicrophone"], model.CalibrationError); Assert.Equal(0, service.Runs);
        service.NoInputs = false; model.RefreshCalibrationInputs.Execute(null); await CommandFinished(model.RefreshCalibrationInputs);
        Assert.False(model.CanStartCalibration); Assert.Equal(model.Text["CalibrationDevicesRequired"], model.CalibrationError);
    }
}
