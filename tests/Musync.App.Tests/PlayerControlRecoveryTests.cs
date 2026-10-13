using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Musync.App;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private sealed class RecoverableControlOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error => null;
        public Task Controls = Task.CompletedTask;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task UpdateControlsAsync() => Controls;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task WaitForControlStatus(MainViewModel model, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (model.Status != expected) await Task.Delay(10, deadline.Token);
    }
    private static async Task FailControl(MainViewModel model, RecoverableControlOutput output)
    {
        output.Controls = Task.FromException(new IOException("Synthetic volume failure"));
        model.MasterVolume = 25;
        await WaitForControlStatus(model, output.Device.Name + ": Synthetic volume failure");
    }
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task SuccessfulControlRetryRestoresPlayingAndClearsOnlyItsErrorToast(string language)
    {
        RecoverableControlOutput? output = null;
        await using var fixture = new PresentationFixture(language, outputFactory: d => output = new(d));
        var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            await model.PlayTrackAsync(fixture.First);
            await FailControl(model, output!); Assert.True(model.HasToast);
            output!.Controls = Task.CompletedTask; model.MasterVolume = 35;
            await WaitForControlStatus(model, model.Text["Playing"]);
            Assert.False(model.HasToast); Assert.True(model.IsPlaying);
            using var full = window.CaptureRenderedFrame();
            Assert.False(window.FindControl<Border>("StatusToast")!.IsEffectivelyVisible);
            SaveReviewFrame(full, $"control-recovered-{language}-1120.png");
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var mini = window.CaptureRenderedFrame();
            Assert.Equal(model.Text["Playing"], model.Status);
            SaveReviewFrame(mini, $"control-recovered-{language}-mini.png");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task SuccessfulControlRetryRestoresPausedStatus()
    {
        RecoverableControlOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: d => output = new(d));
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await FailControl(model, output!);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        Assert.False(model.IsPlaying);
        output!.Controls = Task.CompletedTask; model.MasterVolume = 35;
        await WaitForControlStatus(model, model.Text["Paused"]);
        Assert.False(model.HasToast); Assert.False(model.IsPlaying);
    }
    [AvaloniaFact]
    public async Task ControlRecoveryPreservesANewerUnrelatedNotification()
    {
        RecoverableControlOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: d => output = new(d));
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await FailControl(model, output!);
        model.ShowError(new IOException("Unrelated notification"));
        var newer = model.ToastMessage;
        output!.Controls = Task.CompletedTask; model.MasterVolume = 35;
        await Task.Delay(450);
        Assert.Equal(newer, model.ToastMessage); Assert.Equal(newer, model.Status);
        Assert.True(model.HasToast); Assert.True(model.IsPlaying);
    }
    [AvaloniaFact]
    public async Task SpotifyPlaybackDoesNotReuseAnEarlierLocalControlError()
    {
        RecoverableControlOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: d => output = new(d));
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await FailControl(model, output!);
        model.Source = model.Sources.Single(s => s.Id == "spotify");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (model.IsPlaying || model.Busy) await Task.Delay(10, deadline.Token);
        model.SpotifyDevice = new("synthetic-spotify-output", "Synthetic Spotify device");
        await model.PlayTrackAsync(null);
        Assert.True(model.SpotifyMode); Assert.True(model.IsPlaying);
        Assert.Equal(model.Text["Playing"], model.Status);
        Assert.DoesNotContain("Synthetic volume failure", model.ToastMessage);
    }
}
