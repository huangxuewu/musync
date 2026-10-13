using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task SourcePreparationShowsTheSelectedReceiverCountBeforeTheirHandshakesStart(string language)
    {
        await using var fixture = new PresentationFixture(language);
        fixture.Backend.SourcePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); var start = fixture.Model.PlayTrackAsync(fixture.First, 12);
        try
        {
            await PollPlayback(fixture.Model); using var frame = window.CaptureRenderedFrame();
            Assert.Equal(string.Format(fixture.Model.Text["SpeakerPreparationProgress"], 0, 1), window.FindControl<TextBlock>("PreparationCount")!.Text);
            Assert.False(fixture.Backend.Started.Task.IsCompleted); Assert.False(fixture.Model.IsPlaying); Assert.Equal(12, fixture.Model.Position);
            SaveReviewFrame(frame, $"source-preparation-{language}-1120.png");
            fixture.Model.CancelPreparation.Execute(null); await start.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(fixture.Model.HasToast); Assert.False(fixture.Model.IsPlaying);
        }
        finally { fixture.Backend.SourcePreparation.TrySetResult(); await start; window.Close(); }
    }
    private sealed class PreparingOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error => null;
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken token) => Ready.Task.WaitAsync(token);
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [AvaloniaTheory]
    [InlineData("en", 1460, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task PreparationKeepsThePlayheadStillAndCanBeCancelledInBothPlayerSizes(string language, int width, int height)
    {
        PreparingOutput? output = null;
        await using var fixture = new PresentationFixture(language, outputFactory: d => output = new(d));
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height }; window.Show(); window.ApplyMotionPreference(false);
        var start = fixture.Model.PlayTrackAsync(fixture.First, 12);
        try
        {
            Assert.True(fixture.Model.IsPreparingPlayback); Assert.False(fixture.Model.IsPlaying); Assert.False(fixture.Model.PlayEnabled); Assert.False(fixture.Model.Seekable);
            Assert.Equal(12, fixture.Model.Position); Assert.False(fixture.Backend.Started.Task.IsCompleted);
            await Task.Delay(350);
            // Activation reads the real Windows preference; override it after
            // that event to exercise the reduced-motion rendering explicitly.
            window.ApplyMotionPreference(false);
            using var frame = window.CaptureRenderedFrame();
            var notice = window.FindControl<Border>("PreparationNotice")!; var count = window.FindControl<TextBlock>("PreparationCount")!;
            Assert.True(notice.IsEffectivelyVisible); Assert.Equal(string.Format(fixture.Model.Text["SpeakerPreparationProgress"], 0, 1), count.Text);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(count));
            var point = notice.TranslatePoint(new Point(), window)!.Value;
            Assert.True(point.X >= 8 && point.X + notice.Bounds.Width <= width - 8);
            Assert.True(point.Y >= 8 && point.Y + notice.Bounds.Height <= height - 104);
            Assert.False(window.FindControl<PreparationIndicator>("FullPreparationWave")!.AnimationRunning);
            SaveReviewFrame(frame, $"preparation-{language}-{width}-full.png");
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var mini = window.CaptureRenderedFrame(); SaveReviewFrame(mini, $"preparation-{language}-mini.png");
            Assert.False(notice.IsEffectivelyVisible); Assert.Equal(12, fixture.Model.Position);
            Assert.NotNull(output);
            fixture.Model.CancelPreparation.Execute(null); await start.WaitAsync(TimeSpan.FromSeconds(2));
            for (var i = 0; i < 100 && fixture.Model.Busy; i++) await Task.Delay(10);
            Assert.False(fixture.Model.IsPreparingPlayback); Assert.False(fixture.Model.IsPlaying); Assert.False(fixture.Model.HasToast);
            Assert.False(fixture.Backend.Started.Task.IsCompleted); Assert.False(window.FindControl<PreparationIndicator>("MiniPreparationWave")!.AnimationRunning);
        }
        finally { output?.Ready.TrySetResult(); await start; window.Close(); }
    }
    [AvaloniaFact]
    public async Task ReadyPlaybackClearsTheNoticeAndMotionStopsOnMinimizeModeSwitchAndHide()
    {
        PreparingOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: d => output = new(d));
        var window = new MainWindow { DataContext = fixture.Model }; window.Show(); window.ApplyMotionPreference(true);
        var start = fixture.Model.PlayTrackAsync(fixture.First);
        try
        {
            var full = window.FindControl<PreparationIndicator>("FullPreparationWave")!; var mini = window.FindControl<PreparationIndicator>("MiniPreparationWave")!;
            Assert.True(full.AnimationRunning); Assert.False(mini.AnimationRunning);
            window.WindowState = WindowState.Minimized; Assert.False(full.AnimationRunning);
            window.WindowState = WindowState.Normal; Assert.True(full.AnimationRunning);
            window.ApplyMotionPreference(false); Assert.False(full.AnimationRunning);
            window.ApplyMotionPreference(true); Assert.True(full.AnimationRunning);
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(full.AnimationRunning); Assert.True(mini.AnimationRunning);
            window.Hide(); Assert.False(mini.AnimationRunning); window.Show(); Assert.True(mini.AnimationRunning);
            output!.Ready.SetResult(); await start; await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(fixture.Model.IsPlaying); Assert.False(fixture.Model.IsPreparingPlayback); Assert.False(full.AnimationRunning); Assert.False(mini.AnimationRunning);
            Assert.False(window.FindControl<Border>("PreparationNotice")!.IsEffectivelyVisible);
        }
        finally { output?.Ready.TrySetResult(); await start; window.Close(); }
    }
}
