using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using System.Net;
using System.Reflection;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private sealed class DisconnectingOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error { get; set; }
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync() { CleanupStarted.TrySetResult(); await CleanupReady.Task; }
    }

    private static Task PollPlayback(MainViewModel model) => (Task)typeof(MainViewModel)
        .GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, null)!;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADisconnectedReceiverPollCannotWriteIntoAnotherSource(bool returnToLocal)
    {
        DisconnectingOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: device => output = new(device));
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12);
        output!.Error = "Synthetic receiver disconnected";
        var poll = PollPlayback(model);
        await output.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var staleWrites = new List<string>();
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(model.Position) && model.Position > 0) staleWrites.Add("progress");
            if (change.PropertyName == nameof(model.Status) && model.Status.Contains("Synthetic receiver disconnected")) staleWrites.Add("error");
        };
        try
        {
            model.Source = model.Sources.First(s => s.Id == "capture");
            if (returnToLocal) model.Source = model.Sources.First(s => s.Id == "local");
            Assert.True(model.Busy); Assert.Equal(0, model.Position);
            output.CleanupReady.TrySetResult();
            await poll.WaitAsync(TimeSpan.FromSeconds(2)); await SourceTransitionSettled(model);
            Assert.Empty(staleWrites);
            Assert.Equal(0, model.Position);
            Assert.Equal(returnToLocal ? fixture.First.DurationSeconds : 0, model.Duration);
            Assert.False(model.HasToast); Assert.False(model.IsPlaying);
            Assert.Equal(model.Text["Idle"], model.Status);
        }
        finally { output.CleanupReady.TrySetResult(); await poll; await SourceTransitionSettled(model); }
    }

    [AvaloniaFact]
    public async Task ADisconnectedReceiverPollCannotRestoreAToastAfterShutdown()
    {
        DisconnectingOutput? output = null;
        var fixture = new PresentationFixture(outputFactory: device => output = new(device));
        Task? dispose = null;
        Task? poll = null;
        try
        {
            await fixture.Model.PlayTrackAsync(fixture.First, 12);
            output!.Error = "Synthetic receiver disconnected";
            poll = PollPlayback(fixture.Model);
            await output.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var position = fixture.Model.Position;
            dispose = fixture.DisposeAsync().AsTask();
            fixture.Backend.Seek(65);
            output.CleanupReady.TrySetResult();
            await Task.WhenAll(poll, dispose).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(position, fixture.Model.Position);
            Assert.False(fixture.Model.HasToast);
        }
        finally
        {
            output?.CleanupReady.TrySetResult();
            if (poll is not null) await poll;
            if (dispose is not null) await dispose; else await fixture.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ACurrentReceiverDisconnectStillReportsItsErrorAndCanRetryPlayback()
    {
        DisconnectingOutput? failed = null;
        await using var fixture = new PresentationFixture(outputFactory: device =>
            failed is null ? failed = new(device) : new PresentationOutput(device));
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12);
        failed!.Error = "Synthetic receiver disconnected";
        var poll = PollPlayback(model);
        try
        {
            await failed.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            failed.CleanupReady.TrySetResult(); await poll.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(model.IsPlaying); Assert.True(model.HasToast);
            Assert.Contains("Synthetic receiver disconnected", model.Status);
            Assert.Contains("Synthetic receiver disconnected", model.Speakers[0].Details);
            await model.PlayTrackAsync(fixture.First);
            Assert.True(model.IsPlaying); Assert.False(model.HasToast);
            Assert.Equal(model.Text["Playing"], model.Status);
            Assert.DoesNotContain("Synthetic receiver disconnected", model.Speakers[0].Details);
        }
        finally { failed.CleanupReady.TrySetResult(); await poll; }
    }

    [AvaloniaFact]
    public async Task SlowReceiverCleanupDoesNotFreezeHealthyPlaybackProgressAndLyrics()
    {
        var receiver = new SpeakerDevice("disconnected-cast", "Synthetic Cast", SpeakerTransport.Chromecast, "192.0.2.10", 8009);
        DisconnectingOutput? failed = null;
        await using var fixture = new PresentationFixture(outputFactory: device => device.Id == receiver.Id
            ? failed = new(device) : new PresentationOutput(device),
            discoverNetwork: _ => Task.FromResult<IReadOnlyList<SpeakerDevice>>([receiver]));
        fixture.Settings.Links[0].Speakers[receiver.Id] = new()
        { Name = receiver.Name, Transport = receiver.Transport, Preferences = new() { Selected = true } };
        var model = fixture.Model;
        await model.InitializeAsync(); await model.PlayTrackAsync(fixture.First, 12); await WaitForLyrics(model);
        failed!.Error = "Synthetic receiver disconnected";
        var poll = PollPlayback(model);
        try
        {
            await failed.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Backend.Seek(25);
            await PollPlayback(model);
            Assert.Equal(25, model.Position);
            Assert.Equal("Next original test line", model.LyricCurrent);
            Assert.True(model.IsPlaying); Assert.False(model.Busy); Assert.True(model.PlayEnabled);
            Assert.Equal(model.Text["Playing"], model.Speakers.Single(s => s.Id == "test-output").Status);
            failed.CleanupReady.TrySetResult(); await poll.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(model.IsPlaying); Assert.Equal(25, model.Position);
            Assert.Equal(model.Text["DeviceError"], model.Speakers.Single(s => s.Id == receiver.Id).Status);
        }
        finally { failed.CleanupReady.TrySetResult(); await poll; }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task SlowSpotifyStopCannotAdmitAndThenStopANewLocalSession(string language)
    {
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/pause")
            { pauseEntered.TrySetResult(); await pause.Task.WaitAsync(token); }
            return request.Method == HttpMethod.Get ? SourceReadResponse(request) : Response(HttpStatusCode.NoContent);
        });
        await using var fixture = new PresentationFixture(language, spotifyHandler: handler);
        var model = fixture.Model;
        model.Source = model.Sources.First(s => s.Id == "spotify");
        await Task.Delay(50);
        await model.PlayTrackAsync(Song);
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            model.Source = model.Sources.First(s => s.Id == "local");
            await pauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(model.Busy);
            Assert.False(model.PlayEnabled); Assert.False(model.IsPlaying); Assert.False(model.Seekable);
            Assert.Equal(0, model.Position);
            using var waiting = window.CaptureRenderedFrame();
            Assert.False(window.FindControl<Button>("PlayerPlayButton")!.IsEnabled);
            var wave = window.FindControl<PreparationIndicator>("MusicSourceWave")!;
            Assert.True(wave.IsEffectivelyVisible);
            Assert.Equal(model.Text["SwitchingSource"], AutomationProperties.GetName(window.FindControl<ComboBox>("MusicSourceSelector")!));
            window.ApplyMotionPreference(false); Assert.False(wave.AnimationRunning);
            window.ApplyMotionPreference(true); Assert.True(wave.AnimationRunning);
            window.WindowState = WindowState.Minimized; Assert.False(wave.AnimationRunning);
            window.WindowState = WindowState.Normal; window.ApplyMotionPreference(true); Assert.True(wave.AnimationRunning);
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var mini = window.CaptureRenderedFrame();
            Assert.False(wave.AnimationRunning); Assert.False(wave.IsEffectivelyVisible);
            SaveReviewFrame(mini, $"source-transition-{language}-mini.png");
            window.FindControl<Button>("RestoreFullPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var restored = window.CaptureRenderedFrame();
            window.ApplyMotionPreference(true); Assert.True(wave.AnimationRunning);
            window.Hide(); Assert.False(wave.AnimationRunning);
            await Task.Delay(30); window.Show(); using var shown = window.CaptureRenderedFrame();
            window.ApplyMotionPreference(true); Assert.True(wave.AnimationRunning);
            await model.PlayTrackAsync(fixture.First);
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
            pause.TrySetResult();
            await SourceTransitionSettled(model);
            Assert.True(model.PlayEnabled); Assert.False(model.HasToast);
            Assert.False(wave.AnimationRunning); Assert.False(wave.IsEffectivelyVisible);
            await model.PlayTrackAsync(fixture.First);
            await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(model.IsPlaying);
            SaveReviewFrame(waiting, $"source-transition-{language}-1120.png");
        }
        finally { pause.TrySetResult(); await SourceTransitionSettled(model); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ADeviceReadFromAnOldSourceCannotReplaceTheNewLibraryStatus()
    {
        var devices = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/devices")
            { entered.TrySetResult(); return devices.Task.WaitAsync(token); }
            return Task.FromResult(SourceReadResponse(request));
        });
        await using var fixture = new PresentationFixture(spotifyHandler: handler);
        var model = fixture.Model;
        model.Source = model.Sources.First(s => s.Id == "spotify");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        model.Source = model.Sources.First(s => s.Id == "local");
        devices.SetResult(Response(HttpStatusCode.OK, "{\"devices\":[{\"id\":\"late\",\"name\":\"Old source device\",\"type\":\"computer\",\"is_active\":false,\"is_restricted\":false}]}"));
        await Task.Delay(80); await SourceTransitionSettled(model);
        Assert.Empty(model.SpotifyDevices);
        Assert.Equal(model.Text["Idle"], model.Status);
        Assert.False(model.HasToast); Assert.False(model.IsPlaying);
        Assert.Contains(model.Tracks, row => row.Track.Id == fixture.First.Id);
    }

    [AvaloniaFact]
    public async Task ChangingSourceCancelsReceiverPreparationWithoutOldProgressOrPlayback()
    {
        PreparingOutput? output = null;
        await using var fixture = new PresentationFixture(outputFactory: device => output = new(device));
        var model = fixture.Model;
        var start = model.PlayTrackAsync(fixture.First, 12);
        Assert.True(model.IsPreparingPlayback);
        model.Source = model.Sources.First(s => s.Id == "capture");
        try
        {
            var failure = await Record.ExceptionAsync(async () => await start.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(failure);
            await SourceTransitionSettled(model);
            Assert.Equal(0, model.Position); Assert.Equal(0, model.Duration);
            Assert.Equal(model.Text["CaptureTitle"], model.Title);
            Assert.False(model.IsPreparingPlayback); Assert.False(model.Busy); Assert.False(model.IsPlaying);
            Assert.False(fixture.Backend.Started.Task.IsCompleted);
            Assert.False(model.HasToast);
        }
        finally { output?.Ready.TrySetResult(); try { await start; } catch (OperationCanceledException) { } }
    }

    [AvaloniaFact]
    public async Task ReturningToLocalCancelsAnObsoleteSpotifyDeviceRead()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/devices")
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return SourceReadResponse(request);
        });
        await using var fixture = new PresentationFixture(spotifyHandler: handler);
        var model = fixture.Model;
        model.Source = model.Sources.First(s => s.Id == "spotify");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(model.Text["SwitchingSource"], model.Status);
        model.Source = model.Sources.First(s => s.Id == "local");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await SourceTransitionSettled(model);
        Assert.Empty(model.SpotifyDevices); Assert.False(model.HasToast);
        await model.PlayTrackAsync(fixture.First);
        await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(model.IsPlaying);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOldSpotifyPlayResponseCannotRestorePlaybackOrErrorsInTheNewSource(bool fail)
    {
        var play = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauses = 0;
        var handler = new Handler((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/play") return play.Task.WaitAsync(token);
            if (request.RequestUri.AbsolutePath == "/v1/me/player/pause") pauses++;
            return Task.FromResult(request.Method == HttpMethod.Get ? SourceReadResponse(request) : Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new PresentationFixture(spotifyHandler: handler);
        var model = fixture.Model;
        model.Source = model.Sources.First(s => s.Id == "spotify");
        await SourceTransitionSettled(model);
        var start = model.PlayTrackAsync(Song);
        model.Source = model.Sources.First(s => s.Id == "local");
        Assert.True(model.Busy); Assert.False(model.IsPlaying);
        play.SetResult(fail ? Response(HttpStatusCode.Forbidden, "{\"error\":{\"reason\":\"PREMIUM_REQUIRED\"}}") : Response(HttpStatusCode.NoContent));
        await start.WaitAsync(TimeSpan.FromSeconds(2)); await SourceTransitionSettled(model);
        Assert.Equal(1, pauses); Assert.False(model.IsPlaying); Assert.False(model.HasToast);
        Assert.Equal(fixture.First.Title, model.Title); Assert.Equal(0, model.Position);
        await model.PlayTrackAsync(fixture.First);
        await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(model.IsPlaying);
    }

    [AvaloniaFact]
    public async Task RapidSourceChoicesKeepTheLatestChoiceBusyUntilTheOldStopFinishes()
    {
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauses = 0;
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/pause")
            { pauses++; await pause.Task.WaitAsync(token); }
            return request.Method == HttpMethod.Get ? SourceReadResponse(request) : Response(HttpStatusCode.NoContent);
        });
        await using var fixture = new PresentationFixture(spotifyHandler: handler);
        var model = fixture.Model;
        model.Source = model.Sources.First(s => s.Id == "spotify");
        await SourceTransitionSettled(model); await model.PlayTrackAsync(Song);
        model.Source = model.Sources.First(s => s.Id == "local");
        model.Source = model.Sources.First(s => s.Id == "capture");
        model.Source = model.Sources.First(s => s.Id == "local");
        try
        {
            Assert.Equal(1, pauses); Assert.True(model.Busy); Assert.False(model.PlayEnabled);
            Assert.Equal("local", model.Source?.Id); Assert.False(model.IsPlaying);
            pause.TrySetResult(); await SourceTransitionSettled(model);
            Assert.Equal(1, pauses); Assert.Equal(model.Text["Idle"], model.Status);
            Assert.Equal(fixture.First.Title, model.Title); Assert.False(model.HasToast);
            await model.PlayTrackAsync(fixture.First);
            await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(model.IsPlaying);
        }
        finally { pause.TrySetResult(); await SourceTransitionSettled(model); }
    }

    [AvaloniaFact]
    public async Task ClosingThePlayerCancelsAPendingSourceStopAndSettlesItsWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/pause")
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
            return request.Method == HttpMethod.Get ? SourceReadResponse(request) : Response(HttpStatusCode.NoContent);
        });
        var fixture = new PresentationFixture(spotifyHandler: handler);
        Task? dispose = null;
        try
        {
            fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "spotify");
            await SourceTransitionSettled(fixture.Model); await fixture.Model.PlayTrackAsync(Song);
            fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "local");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            dispose = fixture.DisposeAsync().AsTask(); await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(cancelled); Assert.False(fixture.Model.Busy);
            Assert.False(fixture.Model.IsSwitchingSource); Assert.False(fixture.Model.HasToast);
        }
        finally { if (dispose is not null) await dispose; else await fixture.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task CompletingASourceStopPreservesANewerUnrelatedNotification()
    {
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/me/player/pause") await pause.Task.WaitAsync(token);
            return request.Method == HttpMethod.Get ? SourceReadResponse(request) : Response(HttpStatusCode.NoContent);
        });
        await using var fixture = new PresentationFixture(spotifyHandler: handler);
        var model = fixture.Model;
        model.Source = model.Sources.First(s => s.Id == "spotify");
        await SourceTransitionSettled(model); await model.PlayTrackAsync(Song);
        model.Source = model.Sources.First(s => s.Id == "local");
        try
        {
            model.ShowError(new IOException("Unrelated notification"));
            var message = model.ToastMessage; var epoch = model.ToastEpoch;
            pause.TrySetResult(); await SourceTransitionSettled(model);
            Assert.Equal(message, model.Status); Assert.Equal(message, model.ToastMessage);
            Assert.Equal(epoch, model.ToastEpoch); Assert.True(model.HasToast); Assert.False(model.Busy);
        }
        finally { pause.TrySetResult(); await SourceTransitionSettled(model); }
    }

    private static HttpResponseMessage SourceReadResponse(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/v1/me/player/devices"
        ? Response(HttpStatusCode.OK, "{\"devices\":[{\"id\":\"desktop\",\"name\":\"Synthetic desktop\",\"type\":\"computer\",\"is_active\":true}]}")
        : Response(HttpStatusCode.OK, PlaybackWithVolume());

    private static async Task SourceTransitionSettled(MainViewModel model)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (model.Busy) await Task.Delay(10, timeout.Token);
    }
}
