using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaFact]
    public async Task RetryingAnInterruptedLocalSongFromItsCoverPreservesTheSamePositionAsTheFooter()
    {
        var created = new List<StatusOutput>();
        await using var fixture = new PresentationFixture(outputFactory: device =>
        {
            var output = new StatusOutput(device); output.Prepared.TrySetResult(); created.Add(output); return output;
        });
        var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            await model.PlayTrackAsync(fixture.First, 12);
            created[0].Error = "Synthetic receiver disconnected"; await PollPlayback(model);
            var row = model.Tracks.Single(track => track.Track.Id == fixture.First.Id);
            using var interrupted = CaptureTrackReviewFrame(window); SaveReviewFrame(interrupted, "local-track-retry-incumbent.png");
            var cover = window.FindControl<ItemsControl>("TrackList")!.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Command == row.PlayCommand);
            ClickPlaylistControl(window, cover); await CommandFinished(row.PlayCommand); await PollPlayback(model);
            Assert.Equal(12, fixture.Backend.Position); Assert.Equal(12, model.Position); Assert.True(model.IsPlaying);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task AFailedLocalSongKeepsAnAccessibleRetryAfterItsToastExpires(string language)
    {
        const string diagnostic = "Synthetic local decoder cannot open the file";
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model; fixture.Backend.BeforePlayback = _ => throw new IOException(diagnostic);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); await PollPlayback(model);
            var row = model.Tracks.Single(track => track.Track.Id == fixture.First.Id);
            Assert.True(row.HasPlaybackError); Assert.True(row.IsCurrentTrack); Assert.False(row.HasPlaybackActivity);
            Assert.Equal("refresh-cw", row.PlayIcon); Assert.Equal(model.Text["TrackRetryHint"], row.PlaybackStatus);
            Assert.Contains(diagnostic, row.PlaybackDetails); Assert.Contains(model.Text["TechnicalDetails"], row.PlaybackDetails);
            Assert.Equal(row.PlayLabel, model.PlayLabel); Assert.True(row.CanPlay); Assert.False(model.IsPlaying);
            var epoch = model.ToastEpoch; await Task.Delay(5300);
            Assert.False(model.HasToast); Assert.Equal(epoch, model.ToastEpoch); Assert.True(row.HasPlaybackError);
            using var expired = CaptureTrackReviewFrame(window); SaveReviewFrame(expired, $"local-track-failed-{language}-1120.png");
            var list = window.FindControl<ItemsControl>("TrackList")!;
            var cover = list.GetVisualDescendants().OfType<Button>().Single(button => button.Command == row.PlayCommand);
            var grid = cover.GetVisualAncestors().OfType<Grid>().First();
            var issue = grid.GetVisualDescendants().OfType<LucideIcon>().Single(icon => icon.Kind == "triangle-alert");
            Assert.True(issue.IsEffectivelyVisible); Assert.Equal(row.PlaybackStatus, AutomationProperties.GetName(issue));
            Assert.Equal(row.PlaybackDetails, AutomationProperties.GetHelpText(issue));
            AssertIconHasContrastingPixels(expired!, window, issue, Colors.White);
            Assert.Equal(row.PlayLabel, AutomationProperties.GetName(cover));
            model.Language = model.Languages.Single(choice => choice.Id == (language == "ja" ? "en" : "ja"));
            row = model.Tracks.Single(track => track.Track.Id == fixture.First.Id);
            Assert.True(row.HasPlaybackError); Assert.Equal(model.Text["TrackRetryHint"], row.PlaybackStatus);
            Assert.False(model.HasToast); Assert.Equal(epoch, model.ToastEpoch); Assert.Contains(diagnostic, row.PlaybackDetails);
            fixture.Backend.BeforePlayback = null;
            using var translated = CaptureTrackReviewFrame(window);
            cover = list.GetVisualDescendants().OfType<Button>().Single(button => button.Command == row.PlayCommand);
            Assert.True(cover.Focus()); window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); await CommandFinished(row.PlayCommand); await PollPlayback(model);
            Assert.True(model.IsPlaying); Assert.False(row.HasPlaybackError); Assert.Equal("pause", row.PlayIcon);
            Assert.Equal(model.Text["Playing"], row.PlaybackStatus); Assert.Equal(0, model.Position);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoosingAnotherSongOrSourceClearsThePriorLocalFailure(bool changeSource)
    {
        await using var fixture = new PresentationFixture(); var model = fixture.Model;
        fixture.Backend.BeforePlayback = _ => throw new IOException("Synthetic local file failed");
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        Assert.True(model.Tracks.Single(track => track.Track.Id == fixture.First.Id).HasPlaybackError);
        fixture.Backend.BeforePlayback = null;
        if (changeSource)
        {
            model.Source = model.Sources.Single(source => source.Id == "capture"); await SourceTransitionSettled(model);
            model.Source = model.Sources.Single(source => source.Id == "local"); await SourceTransitionSettled(model);
        }
        else await model.PlayTrackAsync(model.Tracks.First(track => track.Track.Id != fixture.First.Id).Track);
        Assert.DoesNotContain(model.Tracks, track => track.HasPlaybackError);
    }

    [AvaloniaFact]
    public async Task AReceiverFailureWithHealthyPlaybackDoesNotMarkTheLocalSongAsFailed()
    {
        var receiver = new SpeakerDevice("partial-retry", "Synthetic receiver", SpeakerTransport.Chromecast, "192.0.2.42", 8009);
        var outputs = new Dictionary<string, StatusOutput>();
        await using var fixture = new PresentationFixture(outputFactory: device =>
        {
            var output = outputs[device.Id] = new(device); output.Prepared.TrySetResult(); return output;
        }, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true } };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        await model.PlayTrackAsync(fixture.First, 12);
        outputs[receiver.Id].Error = "Chromecast connection lost."; await PollPlayback(model);
        var row = model.Tracks.Single(track => track.Track.Id == fixture.First.Id);
        Assert.True(model.IsPlaying); Assert.False(row.HasPlaybackError); Assert.True(row.IsRowPlaying);
        Assert.Equal("pause", row.PlayIcon); Assert.Equal(model.Text["Playing"], row.PlaybackStatus); Assert.Equal(12, model.Position);
    }
}
