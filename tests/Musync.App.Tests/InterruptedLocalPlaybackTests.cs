using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Musync.App;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private sealed class RecoveryOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnDispose { get; set; }
        public Task PauseUpdates { get; set; } = Task.CompletedTask;
        public Task StartAsync(CancellationToken token) => Ready.Task.WaitAsync(token);
        public Task StopAsync() => Task.CompletedTask;
        public Task SetPausedAsync(bool paused) => PauseUpdates;
        public ValueTask DisposeAsync() { OnDispose?.Invoke(); return ValueTask.CompletedTask; }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task LosingEveryReceiverPreservesLocalProgressAndWaitsForExplicitPlaybackAfterRescan(string language)
    {
        var receiver = new SpeakerDevice("recovery-cast", "Onkyo · Synthetic receiver", SpeakerTransport.Chromecast, "192.0.2.40", 8009);
        var created = new List<RecoveryOutput>(); var holdPreparation = false;
        await using var fixture = new PresentationFixture(language, outputFactory: device =>
        {
            var output = new RecoveryOutput(device); created.Add(output);
            if (!holdPreparation) output.Ready.TrySetResult(); return output;
        }, discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        var link = fixture.Settings.Links[0]; link.Speakers.Remove("test-output");
        link.Speakers[receiver.Id] = new() { Name = receiver.Name, Transport = receiver.Transport,
            Preferences = new() { Selected = true, Volume = 70, DelayMs = 110 } };
        var model = fixture.Model; model.Speakers.Clear(); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        Task? pendingPlay = null;
        try
        {
            using var initial = CaptureTrackReviewFrame(window); window.ApplyMotionPreference(false);
            await model.PlayTrackAsync(fixture.First, 12); await fixture.Backend.Started.Task;
            Assert.Single(created);
            fixture.Backend.Seek(12.34); await PollPlayback(model);
            var visiblePositions = new List<double>();
            model.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(model.Position)) visiblePositions.Add(model.Position); };
            created[0].Error = "Chromecast connection lost.";
            // The producer may advance while failed transports are closing.
            // The unheard cleanup interval must not become the resume position.
            created[0].OnDispose = () => fixture.Backend.Seek(17.34);
            await PollPlayback(model);
            Assert.False(model.IsPlaying); Assert.False(model.Seekable); Assert.True(model.HasLinkIssue);
            Assert.Equal(12.34, model.Position); Assert.Equal(180, model.Duration);
            Assert.DoesNotContain(17.34, visiblePositions);
            Assert.Equal("此刻一起听见音乐", model.LyricCurrent);
            using (var interrupted = CaptureTrackReviewFrame(window)) SaveReviewFrame(interrupted, $"local-interrupted-{language}-1120.png");
            model.ScanCommand.Execute(null); await CommandFinished(model.ScanCommand);
            Assert.Single(created); Assert.False(model.IsPlaying); Assert.Equal(12.34, model.Position);
            holdPreparation = true;
            ClickPlaylistControl(window, window.FindControl<Button>("PlayerPlayButton")!);
            await Until(() => created.Count == 2);
            pendingPlay = CommandFinished(model.PlayCommand);
            Assert.True(model.IsPreparingPlayback); Assert.False(model.IsPlaying); Assert.Equal(12.34, model.Position);
            created[1].Ready.TrySetResult(); await pendingPlay;
            await Until(() => fixture.Backend.PlayStarts == 2); await PollPlayback(model);
            Assert.True(model.IsPlaying); Assert.True(model.Seekable); Assert.False(model.HasLinkIssue);
            Assert.Equal(12.34, fixture.Backend.Position); Assert.Equal(12.34, model.Position);
            Assert.Equal(model.Text["Playing"], model.Status); Assert.Equal("此刻一起听见音乐", model.LyricCurrent);
            Assert.Equal(link.Id, fixture.Settings.ActiveLinkId); Assert.Single(fixture.Settings.Links);
            Assert.Equal(70, link.Speakers[receiver.Id].Preferences.Volume); Assert.Equal(110, link.Speakers[receiver.Id].Preferences.DelayMs);
            using var resumed = CaptureTrackReviewFrame(window); SaveReviewFrame(resumed, $"local-resumed-{language}-1120.png");
        }
        finally
        {
            foreach (var output in created) output.Ready.TrySetResult();
            if (pendingPlay is not null) await pendingPlay;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AFailedLocalResumeAttemptKeepsItsPositionForTheNextExplicitRetry()
    {
        var created = new List<RecoveryOutput>();
        await using var fixture = new PresentationFixture(outputFactory: device =>
        {
            var output = new RecoveryOutput(device); created.Add(output);
            if (created.Count == 2) output.Ready.TrySetException(new IOException("Synthetic receiver still unavailable"));
            else output.Ready.TrySetResult();
            return output;
        });
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12); await fixture.Backend.Started.Task;
        created[0].Error = "Synthetic receiver disconnected"; await PollPlayback(model);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        Assert.False(model.IsPlaying); Assert.Equal(12, model.Position);
        Assert.Contains("Synthetic receiver still unavailable", model.Status); Assert.Equal(2, created.Count);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        await Until(() => fixture.Backend.PlayStarts == 2); await PollPlayback(model);
        Assert.True(model.IsPlaying); Assert.Equal(3, created.Count); Assert.Equal(12, fixture.Backend.Position);
        Assert.Equal(model.Text["Playing"], model.Status);
    }

    [AvaloniaTheory]
    [InlineData("restart-current")]
    [InlineData("different-track")]
    [InlineData("different-source")]
    public async Task ExplicitLocalPlaybackChoicesDiscardAnEarlierInterruptedPosition(string choice)
    {
        RecoveryOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: device =>
        {
            output = new(device); output.Ready.TrySetResult(); return output;
        });
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12); await fixture.Backend.Started.Task;
        output!.Error = "Synthetic receiver disconnected"; await PollPlayback(model);
        Assert.Equal(12, model.Position); Assert.False(model.IsPlaying);
        if (choice == "different-source")
        {
            model.Source = model.Sources.Single(source => source.Id == "capture"); await SourceTransitionSettled(model);
            model.Source = model.Sources.Single(source => source.Id == "local"); await SourceTransitionSettled(model);
            model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        }
        else await model.PlayTrackAsync(choice == "different-track" ? fixture.First with { Id = "local:another-track", Title = "Another song" } : fixture.First);
        await Until(() => fixture.Backend.PlayStarts == 2);
        await PollPlayback(model);
        Assert.True(model.IsPlaying); Assert.Equal(0, fixture.Backend.Position); Assert.Equal(0, model.Position);
        // A later ordinary pause/resume must keep the new song's own position.
        fixture.Backend.Seek(3); await PollPlayback(model);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        Assert.True(model.IsPlaying); Assert.Equal(3, model.Position);
    }

    [AvaloniaFact]
    public async Task AReceiverLostWhilePausedStillResumesTheSameLocalSongPosition()
    {
        RecoveryOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: device =>
        {
            output = new(device); output.Ready.TrySetResult(); return output;
        });
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12); await fixture.Backend.Started.Task;
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        Assert.False(model.IsPlaying);
        output!.Error = "Synthetic paused receiver disconnected"; await PollPlayback(model);
        Assert.Equal(12, model.Position); Assert.False(model.Seekable);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        await Until(() => fixture.Backend.PlayStarts == 2); await PollPlayback(model);
        Assert.True(model.IsPlaying); Assert.True(model.Seekable); Assert.Equal(12, fixture.Backend.Position);
    }

    [AvaloniaFact]
    public async Task CancellingPreparationDiscardsTheInterruptedLocalRetry()
    {
        var created = new List<RecoveryOutput>();
        await using var fixture = new PresentationFixture(outputFactory: device =>
        {
            var output = new RecoveryOutput(device); created.Add(output);
            if (created.Count != 2) output.Ready.TrySetResult(); return output;
        });
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12); await fixture.Backend.Started.Task;
        created[0].Error = "Synthetic receiver disconnected"; await PollPlayback(model);
        model.PlayCommand.Execute(null); await Until(() => created.Count == 2);
        model.CancelPreparation.Execute(null);
        await CommandFinished(model.CancelPreparation); await CommandFinished(model.PlayCommand);
        Assert.False(model.IsPlaying); Assert.False(model.IsPreparingPlayback);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        await Until(() => fixture.Backend.PlayStarts == 2); await PollPlayback(model);
        Assert.True(model.IsPlaying); Assert.Equal(0, fixture.Backend.Position);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LosingAllReceiversDuringPauseOrResumeRetainsTheLocalPosition(bool resuming)
    {
        RecoveryOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: device =>
        {
            output = new(device); output.Ready.TrySetResult(); return output;
        });
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12); await fixture.Backend.Started.Task;
        if (resuming) { model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); Assert.False(model.IsPlaying); }
        output!.PauseUpdates = Task.FromException(new IOException("Synthetic receiver lost during pause/resume"));
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        Assert.False(model.IsPlaying); Assert.False(model.Seekable); Assert.Equal(12, model.Position);
        Assert.Contains("Synthetic receiver lost during pause/resume", model.Status);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        await Until(() => fixture.Backend.PlayStarts == 2); await PollPlayback(model);
        Assert.True(model.IsPlaying); Assert.Equal(12, fixture.Backend.Position); Assert.Equal(model.Text["Playing"], model.Status);
    }
}
