using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static async Task CheckRadioPreparationSurface(MainWindow window, MainViewModel model, string headline, string detail, string summary, string file, bool includeMini = true)
    {
        await PollPlayback(model); await Task.Delay(300); window.ApplyMotionPreference(true);
        Assert.Equal(headline, model.PreparationHeadline); Assert.Equal(headline, model.PlayLabel);
        Assert.Equal(headline, model.RadioStations[0].StatusCaption);
        Assert.Equal(headline, window.FindControl<TextBlock>("PreparationHeading")!.Text);
        Assert.Equal(detail, window.FindControl<TextBlock>("PreparationCount")!.Text);
        Assert.Equal(summary, model.PreparationSummary);
        var notice = window.FindControl<Border>("PreparationNotice")!;
        Assert.True(notice.IsEffectivelyVisible); Assert.True(notice.Bounds.Width <= 400);
        var count = window.FindControl<TextBlock>("PreparationCount")!;
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(count));
        var cancel = window.FindControl<Button>("CancelPreparationButton")!;
        Assert.True(cancel.IsEffectivelyVisible); Assert.True(cancel.IsEnabled);
        Assert.Equal(model.Text["CancelPreparation"], AutomationProperties.GetName(cancel));
        using (var full = CaptureTrackReviewFrame(window)) { Assert.NotNull(full); SaveReviewFrame(full, file + "-full.png"); }
        if (!includeMini) return;
        ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); await Task.Delay(180);
        var compact = window.FindControl<TextBlock>("MiniPreparationSummary")!;
        Assert.Equal(summary, compact.Text); Assert.True(compact.IsEffectivelyVisible);
        Assert.Equal(detail, ToolTip.GetTip(compact));
        Assert.False(window.FindControl<PreparationIndicator>("FullPreparationWave")!.AnimationRunning);
        Assert.True(window.FindControl<PreparationIndicator>("MiniPreparationWave")!.AnimationRunning);
        using (var mini = CaptureTrackReviewFrame(window)) { Assert.NotNull(mini); SaveReviewFrame(mini, file + "-mini.png"); }
        ClickPlaylistControl(window, window.FindControl<Button>("RestoreFullPlayerButton")!); await Task.Delay(180);
    }

    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task RadioPreparationReleasesMetadataLoadingBeforeReportingRealReceiverReadiness(string language, int width, int height)
    {
        var outputs = new Dictionary<string, PreparingOutput>();
        await using var fixture = new RadioFixture(language, d => outputs[d.Id] = new(d), receiverCount: 2);
        var model = fixture.Model; SaveStation(model, "Evening radio · 晚风電台");
        fixture.Backend.ResolveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.SourcePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        var start = model.PlayRadioAsync(model.RadioStations[0].Station);
        try
        {
            await fixture.Backend.Resolving.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(model.RadioLoading); Assert.False(model.IsPlaying);
            await CheckRadioPreparationSurface(window, model, model.Text["RadioLoadingTitle"], model.Text["RadioLoadingDetail"], model.Text["RadioLoadingTitle"], $"radio-phase-loading-{language}-{width}");
            fixture.Backend.ResolveGate.TrySetResult();
            await fixture.Backend.PreparingSource.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(model.RadioLoading);
            await CheckRadioPreparationSurface(window, model, model.Text["RadioBuffering"], model.Text["RadioBufferingDetail"], model.Text["RadioBuffering"], $"radio-phase-audio-{language}-{width}");
            fixture.Backend.SourcePreparation.TrySetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (outputs.Count != 2) await Task.Delay(5, deadline.Token);
            await PollPlayback(model);
            Assert.Equal(string.Format(model.Text["SpeakerPreparationProgress"], 0, 2), window.FindControl<TextBlock>("PreparationCount")!.Text);
            Assert.Equal(model.Text["PreparingSpeakers"], model.RadioStations[0].StatusCaption);
            var none = string.Format(model.Text["SpeakerPreparationProgress"], 0, 2);
            await CheckRadioPreparationSurface(window, model, model.Text["PreparingSpeakers"], none, none, $"radio-phase-receivers-{language}-{width}");
            outputs["test-output"].Ready.TrySetResult();
            while (fixture.Player.ReadyOutputs != 1) await Task.Delay(5, deadline.Token);
            await PollPlayback(model);
            Assert.Equal(string.Format(model.Text["SpeakerPreparationProgress"], 1, 2), model.PreparationDetail);
            var one = string.Format(model.Text["SpeakerPreparationProgress"], 1, 2);
            await CheckRadioPreparationSurface(window, model, model.Text["PreparingSpeakers"], one, one, $"radio-phase-partial-{language}-{width}", includeMini: false);
            Assert.False(model.IsPlaying); Assert.Equal(0, fixture.Backend.Plays);
            outputs["test-output-2"].Ready.TrySetResult(); await start;
            await fixture.Backend.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(model.IsPlaying); Assert.False(model.IsPreparingPlayback); Assert.Equal(1, fixture.Backend.Plays);
        }
        finally
        {
            if (model.IsPreparingPlayback) { model.CancelPreparation.Execute(null); await CommandFinished(model.CancelPreparation); }
            fixture.Backend.ResolveGate.TrySetResult(); fixture.Backend.SourcePreparation.TrySetResult();
            foreach (var output in outputs.Values) output.Ready.TrySetResult();
            await start; window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("zh-Hans", false)]
    [InlineData("ja", false)]
    [InlineData("en", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("ja", true)]
    public async Task RadioMetadataAndAudioPreparationCanBeCancelledFromTheMiniPlayerKeyboard(string language, bool audioStage)
    {
        await using var fixture = new RadioFixture(language); var model = fixture.Model; SaveStation(model);
        var savedToastEpoch = model.ToastEpoch;
        fixture.Backend.ResolveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.SourcePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        var start = model.PlayRadioAsync(model.RadioStations[0].Station);
        try
        {
            await fixture.Backend.Resolving.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (audioStage) { fixture.Backend.ResolveGate.TrySetResult(); await fixture.Backend.PreparingSource.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            await Task.Delay(250); window.ApplyMotionPreference(true);
            ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); await Task.Delay(180);
            var button = window.FindControl<Button>("MiniCancelPreparationButton")!;
            Assert.True(button.IsEffectivelyVisible); Assert.True(button.IsEnabled);
            Assert.Equal(model.Text["CancelPreparation"], AutomationProperties.GetName(button));
            button.Focus(); window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await CommandFinished(model.CancelPreparation); await start.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(model.IsPreparingPlayback); Assert.False(model.RadioLoading); Assert.False(model.IsPlaying);
            Assert.Equal(savedToastEpoch, model.ToastEpoch); Assert.False(model.HasRadioError); Assert.Equal(0, fixture.Backend.Plays);
            Assert.False(window.FindControl<PreparationIndicator>("MiniPreparationWave")!.AnimationRunning);
            Assert.Equal(model.RadioStations[0].Url, model.RadioStations[0].StatusCaption);
        }
        finally
        {
            if (model.IsPreparingPlayback) { model.CancelPreparation.Execute(null); await CommandFinished(model.CancelPreparation); }
            fixture.Backend.ResolveGate.TrySetResult(); fixture.Backend.SourcePreparation.TrySetResult(); await start; window.Close();
        }
    }
}
