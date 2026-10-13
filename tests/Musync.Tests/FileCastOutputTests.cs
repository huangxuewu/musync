using Musync.Core;
using Musync.Network;
using Musync.Platform.Windows;
using NAudio.Wave;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Musync.Tests;

public sealed partial class FileCastOutputTests
{
    private sealed class Session : ICastSession
    {
        public string? Error { get; set; }
        public bool ConnectionLost { get; set; }
        public Task? CleanupReply;
        public bool DropDuringStop;
        public Task? DisposalReply;
        public string? Url;
        public double Duration, Position;
        public int Plays, Pauses, Stops, Disconnects;
        public bool FailPreparation;
        public Queue<double?> PositionResponses = new();
        public TaskCompletionSource<double?>? PendingPosition;
        public int Seeks;
        public List<double> Volumes = [];
        public Task ConnectAsync(SpeakerDevice device) => Task.CompletedTask;
        public Task<double?> ReadVolumeAsync() => Task.FromResult<double?>(.4);
        public Task LaunchAsync() => Task.CompletedTask;
        public Task SetVolumeAsync(double level) { Volumes.Add(level); return CleanupReply ?? Task.CompletedTask; }
        public Task LoadAsync(string url) => throw new InvalidOperationException("A local file must not use live HLS.");
        public Task PrepareFileAsync(string url, double duration, double position, CancellationToken token)
        { Url = url; Duration = duration; Position = position; return FailPreparation ? Task.FromException(new IOException("Rejected preload")) : Task.CompletedTask; }
        public Task PlayPreparedAsync() { Plays++; return Task.CompletedTask; }
        public Task PausePreparedAsync() { Pauses++; return Task.CompletedTask; }
        public Task<double?> ReadPositionAsync() => PendingPosition?.Task
            ?? Task.FromResult(PositionResponses.Count > 0 ? PositionResponses.Dequeue() : (double?)Position);
        public Task SeekPreparedAsync(double position) { Seeks++; Position = position; return Task.CompletedTask; }
        public Task StopMediaAsync()
        {
            Stops++;
            if (DropDuringStop) { ConnectionLost = true; Error = "Synthetic connection lost"; return Task.CompletedTask; }
            return CleanupReply ?? Task.CompletedTask;
        }
        public Task DisconnectAsync() { Disconnects++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => DisposalReply is { } reply ? new(reply) : ValueTask.CompletedTask;
    }
    private sealed class Preparer : IAudioFilePreparer, IDisposable
    {
        public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".mp3");
        public int Leases, Releases;
        public TaskCompletionSource? Pending;
        public Preparer() => File.WriteAllBytes(Path, Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray());
        public async Task<PreparedAudioFile> PrepareAsync(string source, CancellationToken token)
        { if (Pending is not null) await Pending.Task.WaitAsync(token); Leases++; return new(Path, 60, () => { Releases++; return ValueTask.CompletedTask; }); }
        public void Dispose() => File.Delete(Path);
    }
    private static FileCastOutput Output(LocalStreamServer server, Preparer preparer, Session session, double position = 0)
        => new(new("cast", "Cast", SpeakerTransport.Chromecast, "127.0.0.1", 8009), new("song.flac", false, position), server, preparer, session);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedConnectionLossRevokesTheFileWithoutDeadControlRequests(bool duringStop)
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session();
        await using var output = Output(server, preparer, session);
        await output.StartAsync(default); await output.BeginPlaybackAsync(default);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConnectionLost = !duringStop; session.Error = duringStop ? null : "Synthetic connection lost";
        session.DropDuringStop = duringStop; session.CleanupReply = pending.Task;
        var levels = session.Volumes.Count;
        var stop = output.StopAsync();
        try
        {
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(duringStop ? 1 : 0, session.Stops); Assert.Equal(levels, session.Volumes.Count);
            Assert.Equal(1, session.Disconnects); Assert.Equal(1, preparer.Releases);
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(session.Url)).StatusCode);
            Assert.Equal(session.Error, output.Error);
        }
        finally { pending.TrySetResult(); await stop; }
    }

    [Fact]
    public async Task UnresponsiveSessionDisposalIsBoundedAfterRevokingOwnedMedia()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session();
        await using var output = Output(server, preparer, session);
        await output.StartAsync(default);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DisposalReply = pending.Task;
        var dispose = output.DisposeAsync().AsTask();
        try
        {
            var error = await Record.ExceptionAsync(() => dispose.WaitAsync(TimeSpan.FromSeconds(7)));
            Assert.True(dispose.IsCompleted, "The output's own disposal must finish rather than relying on the caller's timeout.");
            Assert.IsType<TimeoutException>(error);
            Assert.Equal(1, preparer.Releases); Assert.Equal(1, session.Disconnects);
            Assert.Equal(.4, session.Volumes.Last());
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(session.Url)).StatusCode);
        }
        finally { pending.TrySetResult(); try { await dispose; } catch (TimeoutException) { } }
    }

    [Fact]
    public async Task MediaErrorsWithAnIntactConnectionStillStopAndRestoreVolume()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer();
        var session = new Session(); await using var output = Output(server, preparer, session);
        await output.StartAsync(default); session.Error = "Synthetic media rejected";
        await output.StopAsync();
        Assert.Equal(1, session.Stops); Assert.Equal(.4, session.Volumes.Last()); Assert.Equal(1, session.Disconnects);
        Assert.Equal(1, preparer.Releases);
    }
    [Fact]
    public async Task PreloadServesRangesWithoutPlayingAndRevokesTheUrlOnStop()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session();
        await using var output = Output(server, preparer, session, 12); output.Control.Gain = .2f;
        await output.StartAsync(default);
        Assert.False(output.ReceivesPcm); Assert.Equal(0, session.Plays); Assert.Equal(12, session.Position); Assert.Equal(60, session.Duration);
        Assert.Equal(.2, session.Volumes[0], 5);
        using var http = new HttpClient(); using var request = new HttpRequestMessage(HttpMethod.Get, session.Url); request.Headers.Range = new RangeHeaderValue(100, 199);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode); Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(File.ReadAllBytes(preparer.Path)[100..200], await response.Content.ReadAsByteArrayAsync());
        await output.BeginPlaybackAsync(default); Assert.Equal(1, session.Plays);
        await output.StopAsync(); Assert.Equal(1, preparer.Releases); Assert.Equal(.4, session.Volumes.Last());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(session.Url)).StatusCode);
    }
    [Fact]
    public async Task PauseResumeReusesThePreparedFileAndLiveDelayChangesSeekPosition()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session();
        await using var output = Output(server, preparer, session); await output.StartAsync(default); await output.BeginPlaybackAsync(default);
        await output.SetPausedAsync(true); await output.SetPausedAsync(false);
        Assert.Equal(1, preparer.Leases); Assert.Equal(2, session.Plays); Assert.Equal(1, session.Pauses);
        session.Position = 10; output.Control.DelayMs = 3000; output.Control.Gain = .3f;
        await output.UpdateControlsAsync(); Assert.Equal(7, session.Position); Assert.Equal(.3, session.Volumes.Last(), 5);
        output.Control.DelayMs = 1000; await output.UpdateControlsAsync(); Assert.Equal(9, session.Position);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public async Task SynchronizationAdjustmentRetriesInvalidPositionWithoutLosingTheChange(double? firstPosition)
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer();
        var session = new Session { PositionResponses = new([firstPosition, 10d]) };
        await using var output = Output(server, preparer, session);
        await output.StartAsync(default); await output.BeginPlaybackAsync(default); session.Position = 10;
        output.Control.DelayMs = 100;
        await output.UpdateControlsAsync();
        Assert.Equal(9.9, session.Position, 5); Assert.Equal(1, session.Seeks);
        Assert.Null(output.Error); Assert.Equal(2, session.Plays); Assert.Equal(0, session.Pauses);
    }
    [Fact]
    public async Task SynchronizationAdjustmentUsesLatestDelayWhenReceiverStatusArrives()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer();
        var session = new Session { PendingPosition = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var output = Output(server, preparer, session);
        await output.StartAsync(default); await output.BeginPlaybackAsync(default); session.Position = 10;
        output.Control.DelayMs = 100; var update = output.UpdateControlsAsync();
        output.Control.DelayMs = 400; session.PendingPosition.SetResult(10);
        await update;
        Assert.Equal(9.6, session.Position, 5); Assert.Equal(1, session.Seeks);
    }
    [Fact]
    public async Task UnavailablePositionReportsFailureAndAnUnchangedRequestedDelayCanBeRetried()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer();
        var session = new Session { PositionResponses = new([null, null, null]) };
        await using var output = Output(server, preparer, session);
        await output.StartAsync(default); await output.BeginPlaybackAsync(default); session.Position = 10;
        output.Control.DelayMs = 250;
        var error = await Assert.ThrowsAsync<IOException>(() => output.UpdateControlsAsync());
        Assert.Contains("position", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, session.Seeks); Assert.Equal(10, session.Position);
        Assert.Equal(1, session.Plays);
        await output.UpdateControlsAsync();
        Assert.Equal(9.75, session.Position, 5); Assert.Equal(1, session.Seeks);
        Assert.Equal(2, session.Plays);
    }
    [Fact]
    public async Task RevertingDelayDuringStatusReadDoesNotSeekOrRestartTheReceiver()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer();
        var session = new Session { PendingPosition = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var output = Output(server, preparer, session);
        await output.StartAsync(default); await output.BeginPlaybackAsync(default); session.Position = 10;
        output.Control.DelayMs = 100; var update = output.UpdateControlsAsync();
        output.Control.DelayMs = 0; session.PendingPosition.SetResult(10);
        await update;
        Assert.Equal(0, session.Seeks); Assert.Equal(1, session.Plays); Assert.Equal(0, session.Pauses);
    }
    [Fact]
    public async Task StoppingDuringStatusReadCancelsAdjustmentWithoutALateSeek()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer();
        var session = new Session { PendingPosition = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var output = Output(server, preparer, session);
        await output.StartAsync(default); await output.BeginPlaybackAsync(default);
        output.Control.DelayMs = 100; var update = output.UpdateControlsAsync();
        await output.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        session.PendingPosition.SetResult(10);
        Assert.Equal(0, session.Seeks); Assert.Equal(1, session.Plays); Assert.Equal(1, preparer.Releases);
    }
    [Fact]
    public async Task StopCancelsDelayedPlayRatherThanPlayingAfterDisposal()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session();
        await using var output = Output(server, preparer, session); output.Control.DelayMs = 300;
        await output.StartAsync(default); await output.BeginPlaybackAsync(default); Assert.Equal(0, session.Plays);
        await output.StopAsync(); await Task.Delay(350); Assert.Equal(0, session.Plays); Assert.Equal(1, preparer.Releases);
    }
    [Fact]
    public async Task IncreasingDelayNearTheBeginningHoldsTheReceiverBeforeResuming()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session();
        await using var output = Output(server, preparer, session); await output.StartAsync(default); await output.BeginPlaybackAsync(default);
        session.Position = .05; output.Control.DelayMs = 250; await output.UpdateControlsAsync();
        Assert.Equal(0, session.Position); Assert.Equal(1, session.Pauses); Assert.Equal(1, session.Plays);
        await Task.Delay(300); Assert.Equal(2, session.Plays);
    }
    [Fact]
    public async Task RejectedPreloadRevokesTheArtifactAndRestoresReceiverVolume()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session { FailPreparation = true };
        await using var output = Output(server, preparer, session);
        await Assert.ThrowsAsync<IOException>(() => output.StartAsync(default));
        Assert.Equal(1, preparer.Releases); Assert.Equal(.4, session.Volumes.Last()); Assert.Equal(1, session.Disconnects);
        using var http = new HttpClient(); Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(session.Url)).StatusCode);
    }
    [Fact]
    public async Task CancellationDuringTranscodingDoesNotConnectOrReleaseUnownedMedia()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var session = new Session();
        await using var output = Output(server, preparer, session); using var cancel = new CancellationTokenSource();
        var start = output.StartAsync(cancel.Token); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Null(session.Url); Assert.Equal(0, preparer.Leases); Assert.Equal(0, preparer.Releases);
    }
    [Fact]
    public async Task PausedPreparationDoesNotReleaseDuringResumeAndKeepsTheInitialManualDelay()
    {
        await using var server = new LocalStreamServer(); using var preparer = new Preparer(); var session = new Session();
        await using var output = Output(server, preparer, session); output.Control.DelayMs = 200;
        await output.StartAsync(default); await output.PrepareResumeAsync(default); Assert.Equal(0, session.Plays);
        await output.BeginPlaybackAsync(default); Assert.Equal(0, session.Plays);
        await Task.Delay(300); Assert.Equal(1, session.Plays);
        await output.SetPausedAsync(true); await output.PrepareResumeAsync(default); Assert.Equal(1, session.Plays);
        await output.BeginPlaybackAsync(default); Assert.Equal(2, session.Plays);
    }
    [Fact]
    public async Task PreparedMusicSharesEncodingAndDeletesOnlyAfterTheLastLease()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-cache-test-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.wav");
        try
        {
            using (var writer = new WaveFileWriter(source, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            { var samples = Enumerable.Range(0, 48000).Select(i => (float)(.2 * Math.Sin(i * Math.Tau * 440 / 48000))).ToArray(); writer.WriteSamples(samples, 0, samples.Length); }
            await using var preparer = new WindowsCastMediaPreparer(Path.Combine(folder, "cache"));
            var leases = await Task.WhenAll(preparer.PrepareAsync(source, default), preparer.PrepareAsync(source, default));
            Assert.Equal(leases[0].Path, leases[1].Path); Assert.InRange(leases[0].Duration, .99, 1.01);
            using (var decoded = new MediaFoundationReader(leases[0].Path, new() { RequestFloatOutput = true }))
            { var samples = new float[4096]; Assert.True(decoded.ToSampleProvider().Read(samples) > 0); Assert.Contains(samples, s => Math.Abs(s) > .05); }
            await leases[0].DisposeAsync(); await leases[0].DisposeAsync(); Assert.True(File.Exists(leases[1].Path));
            await leases[1].DisposeAsync(); Assert.False(File.Exists(leases[1].Path)); Assert.True(File.Exists(source)); Assert.Empty(Directory.GetFiles(Path.Combine(folder, "cache")));
        }
        finally { Directory.Delete(folder, true); }
    }
}
