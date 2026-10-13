using Avalonia;
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
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task CurrentRadioRowTogglesWithoutRepeatingLookupOrRestartingItsTimeline(string language, int width, int height)
    {
        await using var fixture = new RadioFixture(language);
        var model = fixture.Model;
        var name = language == "ja" ? new string('音', 70) + " · 晚风電台" : "Evening radio · 晚风電台";
        SaveStation(model, name); SaveStation(model, "Second station", "https://youtu.be/abcdefghijk");
        fixture.Backend.PositionValue = 43;
        await model.PlayRadioAsync(model.RadioStations[0].Station); await PollPlayback(model);
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(250); window.ApplyMotionPreference(true);
            var page = window.FindControl<RadioView>("RadioPage")!;
            var row = model.RadioStations[0];
            var button = page.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext == row && b.Classes.Contains("radioRow"));
            var wave = button.GetVisualDescendants().OfType<PreparationIndicator>().Single();
            var action = button.GetVisualDescendants().OfType<LucideIcon>().Single(icon => icon.Classes.Contains("radioAction"));
            Assert.True(row.IsActive); Assert.Equal(43, model.Position);
            Assert.True(wave.AnimationRunning); Assert.Equal("pause", action.Kind);
            Assert.Equal(model.Text["Playing"], row.StatusCaption);
            Assert.Equal(string.Format(model.Text["RadioPauseNamed"], name), AutomationProperties.GetName(button));
            using (var playing = CaptureTrackReviewFrame(window))
            {
                Assert.NotNull(playing); SaveReviewFrame(playing, $"radio-playing-{language}-{width}.png");
                AssertIconHasContrastingPixels(playing!, window, action, Avalonia.Media.Color.Parse("#FDE8EC"));
            }
            ClickPlaylistControl(window, button); await CommandFinished(row.Play);
            await Task.Delay(180);
            Assert.False(model.IsPlaying); Assert.True(fixture.Backend.Paused);
            Assert.Equal(1, fixture.Backend.Lookups); Assert.Equal(1, fixture.Backend.Plays); Assert.Equal(43, model.Position);
            Assert.False(wave.AnimationRunning); Assert.False(wave.IsVisible); Assert.Equal("play", action.Kind);
            Assert.Equal(model.Text["Paused"], row.StatusCaption);
            Assert.Equal(string.Format(model.Text["RadioPlayNamed"], name), AutomationProperties.GetName(button));
            using (var paused = CaptureTrackReviewFrame(window)) { Assert.NotNull(paused); SaveReviewFrame(paused, $"radio-paused-{language}-{width}.png"); }
            button.Focus(); window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); await CommandFinished(row.Play);
            Assert.True(model.IsPlaying); Assert.False(fixture.Backend.Paused);
            Assert.Equal(1, fixture.Backend.Lookups); Assert.Equal(1, fixture.Backend.Plays); Assert.Equal(43, model.Position);
            Assert.True(wave.AnimationRunning);
            window.ApplyMotionPreference(false); Assert.False(wave.AnimationRunning);
            window.ApplyMotionPreference(true); Assert.True(wave.AnimationRunning);
            window.WindowState = WindowState.Minimized; Assert.False(wave.AnimationRunning);
            window.WindowState = WindowState.Normal; Assert.True(wave.AnimationRunning);
            ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); Assert.False(wave.AnimationRunning);
            ClickPlaylistControl(window, window.FindControl<Button>("RestoreFullPlayerButton")!); Assert.True(wave.AnimationRunning);
            window.Hide(); Assert.False(wave.AnimationRunning); window.Show(); Assert.True(wave.AnimationRunning);
            model.Navigation.Execute("home"); Assert.False(wave.AnimationRunning);
            model.Navigation.Execute("radio"); Assert.True(wave.AnimationRunning);
            var second = model.RadioStations[1]; second.Play.Execute(null); await CommandFinished(second.Play);
            Assert.True(model.IsPlaying); Assert.True(second.IsActive); Assert.False(row.IsActive);
            Assert.Equal(2, fixture.Backend.Lookups); Assert.Equal(2, fixture.Backend.Plays);
            Assert.False(wave.AnimationRunning);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task PreparingRadioRowDisablesDuplicateRequestsAndClearsItsActivityOnSourceChange(string language)
    {
        await using var fixture = new RadioFixture(language);
        var model = fixture.Model; SaveStation(model);
        fixture.Backend.ResolveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = model.RadioStations[0]; row.Play.Execute(null);
        await fixture.Backend.Resolving.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            await Task.Delay(250); window.ApplyMotionPreference(true);
            var page = window.FindControl<RadioView>("RadioPage")!;
            var button = page.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext == row && b.Classes.Contains("radioRow"));
            var wave = button.GetVisualDescendants().OfType<PreparationIndicator>().Single();
            Assert.True(row.IsPreparing); Assert.True(wave.AnimationRunning); Assert.False(button.IsEnabled);
            Assert.Equal(1, button.Opacity);
            Assert.Equal(model.Text["RadioLoadingTitle"], row.StatusCaption);
            Assert.Equal(string.Format(model.Text["RadioPreparingNamed"], row.Name), AutomationProperties.GetName(button));
            Assert.False(button.GetVisualDescendants().OfType<LucideIcon>().Single(icon => icon.Classes.Contains("radioAction")).IsVisible);
            using (var waiting = CaptureTrackReviewFrame(window)) { Assert.NotNull(waiting); SaveReviewFrame(waiting, $"radio-preparing-{language}-1120.png"); }
            row.Play.Execute(null); Assert.Equal(1, fixture.Backend.Lookups);
            model.Source = model.Sources.First(source => source.Id == "local");
            await CommandFinished(row.Play); await SourceTransitionSettled(model);
            Assert.False(row.IsPreparing); Assert.False(row.HasPlaybackActivity); Assert.False(wave.AnimationRunning);
            Assert.Equal(row.Url, row.StatusCaption); Assert.True(button.IsEnabled); Assert.Equal(1, fixture.Backend.Cancellations);
            Assert.False(model.IsPlaying); Assert.False(model.HasRadioError);
        }
        finally { window.Close(); }
    }
}
