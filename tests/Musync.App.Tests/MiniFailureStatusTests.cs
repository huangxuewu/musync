using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Musync.App;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", "zh-Hans")]
    [InlineData("zh-Hans", "ja")]
    [InlineData("ja", "en")]
    public async Task MiniPlayerKeepsTheActualLocalFailureAfterLanguageChanges(string initialLanguage, string nextLanguage)
    {
        await using var fixture = new PresentationFixture(initialLanguage);
        var model = fixture.Model; fixture.Backend.BeforePlayback = _ => throw new IOException("Synthetic unavailable local audio");
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
            model.Language = model.Languages.Single(language => language.Id == nextLanguage);
            ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); await Task.Delay(180);
            using var frame = CaptureTrackReviewFrame(window); SaveReviewFrame(frame, $"mini-failure-{nextLanguage}-incumbent.png");
            Assert.False(model.IsPlaying);
            Assert.True(model.Tracks.Single(track => track.Track.Id == fixture.First.Id).HasPlaybackError);
            Assert.Contains(window.FindControl<Border>("MiniPlayerView")!.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == model.Text["TrackRetryHint"]);
            var status = window.FindControl<TextBlock>("MiniPlaybackStatus")!;
            Assert.Equal(model.PlayerStatusDetails, ToolTip.GetTip(status));
            Assert.Equal(model.PlayerStatusDetails, AutomationProperties.GetHelpText(status));
            Assert.Contains("Synthetic unavailable local audio", model.PlayerStatusDetails);
            Assert.Equal(Color.Parse("#FFC970"), Assert.IsAssignableFrom<ISolidColorBrush>(status.Foreground).Color);
            var epoch = model.ToastEpoch; await Task.Delay(5300); Assert.False(model.HasToast);
            Assert.Equal(model.Text["TrackRetryHint"], status.Text); Assert.Equal(epoch, model.ToastEpoch);
            using var expired = CaptureTrackReviewFrame(window); SaveReviewFrame(expired, $"mini-failure-{nextLanguage}-expired.png");
            fixture.Backend.BeforePlayback = null;
            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); await PollPlayback(model);
            Assert.True(model.IsPlaying); Assert.Equal(model.Text["Playing"], status.Text);
            Assert.Equal(model.Status, model.PlayerStatusDetails);
            Assert.Equal(Color.Parse("#E5DCEC"), Assert.IsAssignableFrom<ISolidColorBrush>(status.Foreground).Color);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ANewNoticeDoesNotReplaceTheStoppedSongStateOrItsOriginalDiagnosticInTheMiniPlayer()
    {
        await using var fixture = new PresentationFixture(); var model = fixture.Model;
        fixture.Backend.BeforePlayback = _ => throw new IOException("Synthetic original audio failure");
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
            model.ShowError(new InvalidOperationException("Synthetic independent notice"));
            var notice = model.Status;
            ClickPlaylistControl(window, window.FindControl<Button>("MiniPlayerButton")!); await Task.Delay(180);
            using var frame = CaptureTrackReviewFrame(window);
            var status = window.FindControl<TextBlock>("MiniPlaybackStatus")!;
            Assert.Contains("Synthetic independent notice", notice); Assert.Equal(notice, model.Status);
            Assert.Equal(model.Text["TrackRetryHint"], status.Text);
            Assert.Contains("Synthetic original audio failure", model.PlayerStatusDetails);
            Assert.DoesNotContain("Synthetic independent notice", model.PlayerStatusDetails);
            Assert.Equal(notice, model.ToastMessage);
            model.Source = model.Sources.Single(source => source.Id == "capture"); await SourceTransitionSettled(model);
            Assert.Equal(model.Status, status.Text); Assert.Equal(model.Status, model.PlayerStatusDetails);
            Assert.Equal(Color.Parse("#E5DCEC"), Assert.IsAssignableFrom<ISolidColorBrush>(status.Foreground).Color);
        }
        finally { window.Close(); }
    }
}
