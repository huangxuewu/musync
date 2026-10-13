using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static async Task InterruptRadio(RadioFixture fixture, Exception? failure = null)
    {
        await fixture.Backend.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Backend.StreamFailure!.TrySetException(failure ?? new IOException("Synthetic decoder connection lost"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (fixture.Player.IsPlaying) await Task.Delay(5, deadline.Token);
        await PollPlayback(fixture.Model);
    }

    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task InterruptedRadioKeepsAnInlineRetryAfterItsToastExpiresAndRestartsOnlyOnActivation(string language, int width, int height)
    {
        await using var fixture = new RadioFixture(language);
        var model = fixture.Model;
        var name = language == "ja" ? new string('音', 70) + " · 晚风電台" : "Evening radio · 晚风電台";
        SaveStation(model, name); SaveStation(model, "Other station", "https://youtu.be/abcdefghijk");
        fixture.Backend.StreamFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await model.PlayRadioAsync(model.RadioStations[0].Station);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            await Task.Delay(250); window.ApplyMotionPreference(true);
            await InterruptRadio(fixture);
            var row = model.RadioStations[0];
            Assert.False(model.IsPlaying); Assert.Empty(fixture.Player.Outputs);
            Assert.Equal(model.Text["RadioRetryHint"], row.StatusCaption);
            Assert.Equal("refresh-cw", row.PlayIcon);
            Assert.True(row.HasPlaybackError);
            Assert.Equal(model.Text["RadioPlaybackInterrupted"], row.CaptionTooltip);
            Assert.Equal(model.RadioStations[1].Url, model.RadioStations[1].StatusCaption);
            Assert.Equal(1, fixture.Backend.Lookups); Assert.Equal(1, fixture.Backend.Plays);
            Assert.False(model.HasRadioError); // Playback failures do not belong to the Add-station form.
            Assert.Equal(model.Text["RadioPlaybackInterrupted"], model.Status);
            Assert.True(model.HasToast);
            await Task.Delay(5300); Assert.False(model.HasToast);
            Assert.Equal(model.Text["RadioRetryHint"], row.StatusCaption);
            var page = window.FindControl<RadioView>("RadioPage")!;
            var button = page.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext == row && b.Classes.Contains("radioRow"));
            var wave = button.GetVisualDescendants().OfType<PreparationIndicator>().Single();
            var action = button.GetVisualDescendants().OfType<LucideIcon>().Single(i => i.Classes.Contains("radioAction"));
            Assert.False(wave.AnimationRunning); Assert.False(wave.IsVisible);
            Assert.Equal(string.Format(model.Text["RadioRetryNamed"], name), AutomationProperties.GetName(button));
            Assert.True(button.IsEnabled); Assert.True(button.Bounds.Height >= 44);
            Assert.Contains("failed", button.Classes);
            Assert.Equal(12, button.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("radioCaption")).FontSize);
            using (var failed = CaptureTrackReviewFrame(window))
            {
                Assert.NotNull(failed); SaveReviewFrame(failed, $"radio-interrupted-{language}-{width}.png");
                AssertIconHasContrastingPixels(failed!, window, action, Avalonia.Media.Color.Parse("#FDE8EC"));
            }
            fixture.Backend.StreamFailure = null;
            if (language == "en") ClickPlaylistControl(window, button);
            else { button.Focus(); window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); }
            await CommandFinished(row.Play); await PollPlayback(model); await Task.Delay(180);
            Assert.True(model.IsPlaying); Assert.Equal(2, fixture.Backend.Lookups); Assert.Equal(2, fixture.Backend.Plays);
            Assert.Equal(model.Text["Playing"], row.StatusCaption); Assert.Equal("pause", row.PlayIcon);
            Assert.False(row.HasPlaybackError); Assert.DoesNotContain("failed", button.Classes);
            Assert.True(wave.AnimationRunning); Assert.False(model.HasToast);
            using var recovered = CaptureTrackReviewFrame(window);
            Assert.NotNull(recovered); SaveReviewFrame(recovered, $"radio-retried-{language}-{width}.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("zh-Hans", false)]
    [InlineData("ja", true)]
    public async Task StartupRadioFailureStaysOnItsStationAcrossFormEditsAndOtherPlaybackUntilRetryOrRemoval(string language, bool missingTools)
    {
        await using var fixture = new RadioFixture(language);
        var model = fixture.Model;
        SaveStation(model, "Failed station"); SaveStation(model, "Healthy station", "https://youtu.be/abcdefghijk");
        fixture.Backend.ResolveError = missingTools ? new FileNotFoundException("Synthetic missing helper") : new IOException("Synthetic unavailable video");
        var failed = model.RadioStations[0];
        await model.PlayRadioAsync(failed.Station);
        Assert.False(model.HasRadioError); Assert.True(failed.HasPlaybackError);
        Assert.Equal(model.Text[missingTools ? "RadioToolsMissing" : "RadioPlaybackFailed"], failed.CaptionTooltip);
        model.RadioUrl = "https://youtu.be/0123456789a"; model.RadioName = "Another station";
        Assert.True(failed.HasPlaybackError); Assert.Equal(model.Text["RadioRetryHint"], failed.StatusCaption);
        fixture.Backend.ResolveError = null;
        await model.PlayRadioAsync(model.RadioStations[1].Station);
        Assert.True(model.IsPlaying); Assert.True(failed.HasPlaybackError); Assert.False(failed.IsActive);
        Assert.Equal(model.Text["RadioRetryHint"], failed.StatusCaption);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            await Task.Delay(250); window.ApplyMotionPreference(false);
            var page = window.FindControl<RadioView>("RadioPage")!;
            var button = page.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext == failed && b.Classes.Contains("radioRow"));
            var action = button.GetVisualDescendants().OfType<LucideIcon>().Single(i => i.Classes.Contains("radioAction"));
            Assert.Contains("failed", button.Classes); Assert.DoesNotContain("active", button.Classes);
            using var frame = CaptureTrackReviewFrame(window); Assert.NotNull(frame);
            AssertIconHasContrastingPixels(frame!, window, action, Avalonia.Media.Colors.White);
            SaveReviewFrame(frame, $"radio-unavailable-inactive-{language}-1120.png");
        }
        finally { window.Close(); }
        failed.Play.Execute(null); await CommandFinished(failed.Play);
        Assert.True(model.IsPlaying); Assert.False(failed.HasPlaybackError); Assert.False(model.HasToast);
        Assert.Equal(model.Text["Playing"], failed.StatusCaption);
        fixture.Backend.ResolveError = new IOException("Synthetic repeat failure");
        await model.PlayRadioAsync(failed.Station); Assert.True(failed.HasPlaybackError);
        failed.Remove.Execute(null); await CommandFinished(failed.Remove);
        SaveStation(model, "Re-added station");
        Assert.False(model.RadioStations.Last().HasPlaybackError);
        Assert.Equal(model.RadioStations.Last().Url, model.RadioStations.Last().StatusCaption);
    }

    [AvaloniaFact]
    public async Task RadioVolumeControlFailureDoesNotOfferRetryOrRestartAHealthyStream()
    {
        RecoverableControlOutput? output = null;
        await using var fixture = new RadioFixture(outputFactory: d => output = new(d));
        var model = fixture.Model; SaveStation(model);
        await model.PlayRadioAsync(model.RadioStations[0].Station);
        await fixture.Backend.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await FailControl(model, output!); await PollPlayback(model);
        var row = model.RadioStations[0];
        Assert.True(model.IsPlaying); Assert.False(row.HasPlaybackError);
        Assert.Equal(model.Text["Playing"], row.StatusCaption); Assert.Equal("pause", row.PlayIcon);
        row.Play.Execute(null); await CommandFinished(row.Play);
        Assert.Equal(1, fixture.Backend.Plays); Assert.Equal(1, fixture.Backend.Lookups);
        Assert.False(row.HasPlaybackError); Assert.Equal(model.Text["Paused"], row.StatusCaption);
    }

    [AvaloniaFact]
    public async Task RadioRetryClearsOnlyItsOwnFailureToast()
    {
        await using var fixture = new RadioFixture(); var model = fixture.Model; SaveStation(model);
        fixture.Backend.StreamFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await model.PlayRadioAsync(model.RadioStations[0].Station); await InterruptRadio(fixture);
        model.ShowError(new IOException("Newer unrelated notification"));
        var message = model.ToastMessage; var epoch = model.ToastEpoch;
        fixture.Backend.StreamFailure = null;
        var row = model.RadioStations[0]; row.Play.Execute(null); await CommandFinished(row.Play);
        Assert.True(model.IsPlaying); Assert.False(row.HasPlaybackError);
        Assert.Equal(message, model.ToastMessage); Assert.Equal(epoch, model.ToastEpoch);
    }

    [AvaloniaFact]
    public async Task AFailedRadioPollCannotApplyRetryStateAfterTheUserChangesSource()
    {
        DisconnectingOutput? output = null;
        await using var fixture = new RadioFixture(outputFactory: d => output = new(d));
        var model = fixture.Model; SaveStation(model);
        await model.PlayRadioAsync(model.RadioStations[0].Station);
        await fixture.Backend.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var message = model.ToastMessage; var epoch = model.ToastEpoch;
        output!.Error = "Synthetic receiver disconnected";
        var poll = PollPlayback(model);
        await output.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            model.Source = model.Sources.First(s => s.Id == "local");
            output.CleanupReady.TrySetResult();
            await poll.WaitAsync(TimeSpan.FromSeconds(3)); await SourceTransitionSettled(model);
            var row = model.RadioStations[0];
            Assert.False(row.HasPlaybackError); Assert.Equal(row.Url, row.StatusCaption);
            Assert.Equal(message, model.ToastMessage); Assert.Equal(epoch, model.ToastEpoch);
            Assert.False(model.HasRadioError); Assert.False(model.IsPlaying);
        }
        finally { output.CleanupReady.TrySetResult(); await poll; await SourceTransitionSettled(model); }
    }

    [AvaloniaFact]
    public async Task DecoderReadTimeoutOffersRetryWithoutChangingSourceOrRemovingTheSavedStation()
    {
        await using var fixture = new RadioFixture("zh-Hans"); var model = fixture.Model; SaveStation(model);
        var station = model.RadioStations[0]; var saved = station.Station;
        fixture.Backend.StreamFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await model.PlayRadioAsync(saved);
        await InterruptRadio(fixture, new TimeoutException("Synthetic decoder frame deadline"));
        Assert.Equal("youtube", model.Source!.Id); Assert.Equal(saved, Assert.Single(model.RadioStations).Station);
        Assert.Empty(fixture.Player.Outputs); Assert.False(model.IsPlaying);
        Assert.Equal(model.Text["RadioRetryHint"], station.StatusCaption);
        Assert.Equal(model.Text["RadioPlaybackInterrupted"], model.ToastMessage);
        Assert.False(model.HasRadioError);
        fixture.Backend.StreamFailure = null;
        station.Play.Execute(null); await CommandFinished(station.Play);
        await fixture.Backend.StreamStarted.Task;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (fixture.Backend.Plays != 2) await Task.Delay(5, deadline.Token);
        Assert.Equal(2, fixture.Backend.Lookups); Assert.True(model.IsPlaying);
        Assert.False(station.HasPlaybackError); Assert.False(model.HasToast);
    }
}
