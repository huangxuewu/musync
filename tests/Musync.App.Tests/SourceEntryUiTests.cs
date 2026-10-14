using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task LocalSourceInitializationShowsTheExistingStartupWaitWithoutPublishingThePreviousClock(string language)
    {
        using var ready = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new PresentationFixture(language, discoverNetwork: _ => Task.FromResult<IReadOnlyList<Musync.Core.SpeakerDevice>>([]));
        fixture.Backend.Seek(99);
        fixture.Backend.BeforePlayback = token => { entered.TrySetResult(); ready.Wait(token); };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        var start = model.PlayTrackAsync(fixture.First, 12);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await PollPlayback(model);
            Assert.True(model.IsPreparingPlayback); Assert.True(model.Busy); Assert.False(model.IsPlaying);
            Assert.False(model.PlayEnabled); Assert.False(model.Seekable); Assert.Equal(12, model.Position);
            Assert.Equal(99, fixture.Backend.Position); Assert.Equal(0, fixture.Backend.PlayStarts);
            Assert.Equal(model.Text["SpeakerWaitingForSync"], Assert.Single(model.LinkedSpeakers).Status);
            using (var initial = CaptureTrackReviewFrame(window)) SaveReviewFrame(initial, $"source-entry-{language}-incumbent.png");
            await CheckLocalSourceEntrySurface(window, model, $"source-entry-{language}-1120");
            ready.Set(); await start.WaitAsync(TimeSpan.FromSeconds(3)); await PollPlayback(model);
            Assert.True(model.IsPlaying); Assert.True(model.Seekable); Assert.False(model.Busy);
            Assert.False(model.IsPreparingPlayback); Assert.Equal(12, model.Position); Assert.Equal(12, fixture.Backend.Position);
            Assert.False(window.FindControl<PreparationIndicator>("FullPreparationWave")!.AnimationRunning);
            Assert.Equal(model.Text["Playing"], model.Status);
        }
        finally { ready.Set(); await start; window.Close(); }
    }

    private static async Task CheckLocalSourceEntrySurface(MainWindow window, MainViewModel model, string file)
    {
        window.ApplyMotionPreference(true);
        Assert.Equal(model.Text["StartingPlayback"], model.PreparationHeadline);
        Assert.Equal(model.PreparationHeadline, model.PlayLabel);
        Assert.Equal(model.PreparationHeadline, window.FindControl<TextBlock>("PreparationHeading")!.Text);
        var detail = window.FindControl<TextBlock>("PreparationCount")!;
        Assert.Equal(model.Text["StartingPlaybackDetail"], detail.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(detail));
        Assert.True(window.FindControl<Button>("CancelPreparationButton")!.IsEffectivelyVisible);
        using (var full = CaptureTrackReviewFrame(window)) SaveReviewFrame(full, file + "-full.png");
        ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); await Task.Delay(180);
        var summary = window.FindControl<TextBlock>("MiniPreparationSummary")!;
        Assert.Equal(model.PreparationHeadline, summary.Text); Assert.True(summary.IsEffectivelyVisible);
        Assert.Equal(model.PreparationDetail, ToolTip.GetTip(summary));
        Assert.False(window.FindControl<PreparationIndicator>("FullPreparationWave")!.AnimationRunning);
        Assert.True(window.FindControl<PreparationIndicator>("MiniPreparationWave")!.AnimationRunning);
        using (var mini = CaptureTrackReviewFrame(window)) SaveReviewFrame(mini, file + "-mini.png");
        ClickPlaylistControl(window, window.FindControl<Button>("RestoreFullPlayerButton")!); await Task.Delay(180);
    }

    [AvaloniaFact]
    public async Task LocalSourceInitializationCanBeCancelledFromTheMiniPlayerWithoutAPlaybackError()
    {
        using var ready = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new PresentationFixture();
        fixture.Backend.BeforePlayback = token => { entered.TrySetResult(); ready.Wait(token); };
        var model = fixture.Model; var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        var start = model.PlayTrackAsync(fixture.First, 12);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await PollPlayback(model);
            var epoch = model.ToastEpoch;
            ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); await Task.Delay(180);
            ClickPlaylistControl(window, window.FindControl<Button>("MiniCancelPreparationButton")!);
            await CommandFinished(model.CancelPreparation); await start.WaitAsync(TimeSpan.FromSeconds(3)); await PollPlayback(model);
            Assert.Equal(epoch, model.ToastEpoch); Assert.Equal(0, fixture.Backend.PlayStarts);
            Assert.False(model.IsPlaying); Assert.False(model.IsPreparingPlayback); Assert.False(model.Busy);
            Assert.Equal(model.Text["Idle"], model.Status);
            Assert.False(window.FindControl<PreparationIndicator>("MiniPreparationWave")!.AnimationRunning);
        }
        finally { ready.Set(); await start; window.Close(); }
    }

    [AvaloniaFact]
    public async Task InitialLocalSourceFailureLeavesAStoppedPlayerAndAnExplicitRetryCanStartTheMusic()
    {
        await using var fixture = new PresentationFixture(); var model = fixture.Model;
        fixture.Backend.BeforePlayback = _ => throw new IOException("Synthetic local source cannot be opened");
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); await PollPlayback(model);
        Assert.False(model.IsPlaying); Assert.False(model.IsPreparingPlayback); Assert.False(model.Busy);
        Assert.False(model.Seekable); Assert.True(model.PlayEnabled); Assert.Equal(0, fixture.Backend.PlayStarts);
        Assert.Contains("Synthetic local source cannot be opened", model.Status);
        fixture.Backend.BeforePlayback = null;
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); await PollPlayback(model);
        Assert.True(model.IsPlaying); Assert.True(model.Seekable); Assert.Equal(1, fixture.Backend.PlayStarts);
        Assert.Equal(0, model.Position); Assert.Equal(model.Text["Playing"], model.Status);
    }
}
