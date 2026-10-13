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
    private sealed class ConfirmingOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error => null;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Confirmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task BeginPlaybackAsync(CancellationToken token) { Entered.TrySetResult(); return Confirmed.Task.WaitAsync(token); }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task PlaybackConfirmationKeepsTheTimelineFrozenAndExplainsTheWaitInFullAndMiniPlayers(string language, int width, int height)
    {
        var outputs = new Dictionary<string, ConfirmingOutput>();
        await using var fixture = new RadioFixture(language, d => outputs[d.Id] = new(d), receiverCount: 2);
        var model = fixture.Model; SaveStation(model, "Evening radio · 晚风電台");
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        var start = model.PlayRadioAsync(model.RadioStations[0].Station);
        try
        {
            for (var i = 0; i < 200 && outputs.Count != 2; i++) await Task.Delay(5);
            Assert.Equal(2, outputs.Count);
            foreach (var output in outputs.Values) await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.Player.IsActivating); Assert.Equal(2, fixture.Player.ReadyOutputs);
            Assert.True(fixture.Backend.Paused); Assert.False(model.IsPlaying); Assert.Equal(0, model.Position);
            await CheckRadioPreparationSurface(window, model, model.Text["StartingPlayback"], model.Text["StartingPlaybackDetail"],
                model.Text["StartingPlayback"], $"activation-{language}-{width}");
            Assert.NotEqual(model.Text["PreparingSpeakers"], model.PreparationHeadline);
            foreach (var output in outputs.Values) output.Confirmed.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(3)); await PollPlayback(model);
            Assert.False(fixture.Player.IsActivating); Assert.False(fixture.Backend.Paused); Assert.True(model.IsPlaying);
            Assert.False(window.FindControl<Border>("PreparationNotice")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<PreparationIndicator>("FullPreparationWave")!.AnimationRunning);
            using var settled = CaptureTrackReviewFrame(window); SaveReviewFrame(settled, $"activation-{language}-{width}-settled.png");
        }
        finally
        {
            foreach (var output in outputs.Values) output.Confirmed.TrySetResult(); await start; window.Close();
        }
    }
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task PlaybackConfirmationCanBeCanceledFromTheMiniPlayerWithoutAFalseFailure(string language)
    {
        ConfirmingOutput? output = null;
        await using var fixture = new RadioFixture(language, d => output = new(d));
        var model = fixture.Model; SaveStation(model, "Evening radio");
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        var start = model.PlayRadioAsync(model.RadioStations[0].Station);
        try
        {
            for (var i = 0; i < 200 && output is null; i++) await Task.Delay(5);
            Assert.NotNull(output); await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await PollPlayback(model);
            var toast = model.ToastEpoch;
            ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); await Task.Delay(200);
            var cancel = window.FindControl<Button>("MiniCancelPreparationButton")!; Assert.True(cancel.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await start.WaitAsync(TimeSpan.FromSeconds(3)); await CommandFinished(model.CancelPreparation); await PollPlayback(model);
            output.Confirmed.TrySetResult(); await Task.Delay(100);
            Assert.False(fixture.Player.IsActivating); Assert.False(model.IsPreparingPlayback); Assert.False(model.IsPlaying);
            Assert.False(model.HasRadioError); Assert.Equal(toast, model.ToastEpoch);
            Assert.False(window.FindControl<PreparationIndicator>("MiniPreparationWave")!.AnimationRunning);
        }
        finally { output?.Confirmed.TrySetResult(); await start; window.Close(); }
    }
}
