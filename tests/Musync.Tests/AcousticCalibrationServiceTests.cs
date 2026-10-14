using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class AcousticCalibrationServiceTests
{
    private sealed class Capture : IAcousticCaptureSession
    {
        public long SamplesCaptured => 64;
        public float[] Samples = [];
        public bool Completed, Disposed;
        public Task<AcousticRecording> CompleteAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Completed = true; return Task.FromResult(new AcousticRecording(Samples)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Inputs : IAcousticInputProvider
    {
        public Capture Capture { get; } = new();
        public int Opens;
        public IReadOnlyList<AcousticInputDevice> GetInputs() => [new("mic", "Synthetic microphone")];
        public Task<IAcousticCaptureSession> StartAsync(string id, TimeSpan duration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Opens++; return Task.FromResult<IAcousticCaptureSession>(Capture); }
    }
    private sealed class Output(SpeakerDevice device, PlaybackSource source) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error { get; set; }
        public bool ReceivesPcm => device.Transport != SpeakerTransport.Chromecast || source.IsStreaming || source.IsCapture;
        public TimeSpan EstimatedLatency => TimeSpan.FromMilliseconds(device.Transport == SpeakerTransport.Chromecast ? 100 : 0);
        public TaskCompletionSource? Preparation, Cleanup;
        public Exception? PreparationFailure, ActivationFailure;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Began, Stopped, Disposed, ReadPreparedWave;
        public async Task StartAsync(CancellationToken token)
        {
            Entered.TrySetResult(); if (Preparation is not null) await Preparation.Task.WaitAsync(token);
            if (PreparationFailure is not null) throw PreparationFailure;
            if (!ReceivesPcm) { Assert.True(File.Exists(source.Path)); ReadPreparedWave = true; }
        }
        public Task BeginPlaybackAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); if (ActivationFailure is not null) throw ActivationFailure; Began = true; return Task.CompletedTask; }
        public async Task StopAsync() { Stopped = true; if (Cleanup is not null) await Cleanup.Task; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static CalibrationRequest Request(string path = "file") => new("link", path, "mic",
        [new(new("pc", "PC", SpeakerTransport.Windows), new() { Volume = 80, DelayMs = 20 }),
         new(new("cast", "Onkyo test", SpeakerTransport.Chromecast, "192.0.2.1", 8009), new() { Volume = 90 })], 70);
    [Fact]
    public async Task ProductionPcmDriverDeliversDifferentPairedSignalsOnOnePacedTimeline()
    {
        var probe = new AcousticProbe([new("one", "One"), new("two", "Two")]);
        var outputs = new[] { new Output(new("one", "One", SpeakerTransport.Windows), new("", false, 0)),
            new Output(new("two", "Two", SpeakerTransport.Windows), new("", false, 0)) };
        await AcousticCalibrationService.EmitAsync(probe, outputs, 2.5, default);
        foreach (var output in outputs)
        {
            var count = output.Buffer.BufferedFrames; Assert.InRange(count, 110000, 112000);
            var samples = new float[count * 2]; output.Buffer.Read(samples, 1);
            var expected = probe.RenderMono(output.Device.Id, AudioFormat.SampleRate);
            var marker = probe.Markers.First(m => m.SpeakerId == output.Device.Id); var first = (int)Math.Round(marker.TimeSeconds * AudioFormat.SampleRate);
            Assert.All(samples.Take(first * 2), sample => Assert.Equal(0, sample));
            for (var i = first; i < first + 5000; i++) { Assert.Equal(expected[i], samples[i * 2]); Assert.Equal(samples[i * 2], samples[i * 2 + 1]); }
        }
        Assert.NotEqual(probe.Markers[0].Pattern, probe.Markers[1].Pattern);
    }
    private static AcousticCalibrationService Service(string folder, Inputs inputs, List<Output> outputs,
        Func<SpeakerDevice, PlaybackSource, Output>? create = null, bool failEmission = false)
        => new(inputs, (device, source) => { var output = create?.Invoke(device, source) ?? new(device, source); outputs.Add(output); return output; }, folder,
            (probe, sinks, seconds, token) =>
            {
                Assert.All(sinks.Cast<Output>(), o => Assert.True(o.Began));
                Assert.All(sinks, o => Assert.InRange(o.Control.Gain, .001f, .3f));
                if (failEmission) throw new IOException("Synthetic receiver failed");
                var samples = new float[64 + (int)((probe.DurationSeconds + 2) * AcousticProbe.MeasurementRate)];
                foreach (var marker in probe.Markers)
                {
                    var output = sinks.Single(o => o.Device.Id == marker.SpeakerId);
                    var arrival = marker.TimeSeconds + output.Control.EffectiveDelayMs / 1000d + output.EstimatedLatency.TotalSeconds;
                    var first = 64 + (int)Math.Round(arrival * AcousticProbe.MeasurementRate);
                    var signal = AcousticProbe.Marker(marker.Pattern);
                    for (var i = 0; i < signal.Length; i++) samples[first + i] += signal[i];
                }
                inputs.Capture.Samples = samples; return Task.CompletedTask;
            });
    [Theory]
    [InlineData("file")]
    [InlineData("stream")]
    [InlineData("capture")]
    public async Task CalibrationUsesTheRequestedTransportAndAddsCorrectionsToTheMeasuredDelay(string path)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Cal-Service-" + Guid.NewGuid().ToString("N"));
        var inputs = new Inputs(); var outputs = new List<Output>(); var request = Request(path);
        try
        {
            var result = await Service(folder, inputs, outputs).RunAsync(request, null, default);
            Assert.True(result.CanApply); Assert.Equal(1, inputs.Opens); Assert.True(inputs.Capture.Completed); Assert.True(inputs.Capture.Disposed);
            Assert.All(outputs, o => { Assert.True(o.Stopped); Assert.True(o.Disposed); });
            Assert.All(inputs.Capture.Samples, sample => Assert.Equal(0, sample));
            Assert.Equal(path == "file", outputs.Single(o => o.Device.Id == "cast").ReadPreparedWave);
            Assert.Equal(path == "stream" ? 20 : 100, result.Delays["pc"]); Assert.Equal(path == "stream" ? 20 : 0, result.Delays["cast"]);
            Assert.Equal(20, request.Speakers[0].Preferences.DelayMs); Assert.Equal(80, request.Speakers[0].Preferences.Volume);
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task CancellationDuringPreparationNeverOpensTheMicrophoneAndClosesEveryCreatedOutput()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Cal-Service-" + Guid.NewGuid().ToString("N"));
        var inputs = new Inputs(); var outputs = new List<Output>(); var preparation = new TaskCompletionSource(); using var token = new CancellationTokenSource();
        try
        {
            var service = Service(folder, inputs, outputs, (device, source) => new(device, source) { Preparation = preparation });
            var run = service.RunAsync(Request(), null, token.Token); await outputs[0].Entered.Task; token.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(0, inputs.Opens);
            Assert.All(outputs, o => { Assert.True(o.Stopped); Assert.True(o.Disposed); Assert.False(o.Began); });
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        }
        finally { preparation.TrySetResult(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("prepare")]
    [InlineData("reported")]
    [InlineData("activate")]
    public async Task ReceiverFailureIdentifiesTheSpeakerAndClosesPreparedOutputs(string stage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Cal-Service-" + Guid.NewGuid().ToString("N")); var inputs = new Inputs(); var outputs = new List<Output>();
        try
        {
            var service = Service(folder, inputs, outputs, (device, source) => new(device, source)
            {
                PreparationFailure = device.Id == "cast" && stage == "prepare" ? new IOException("RAW_RECEIVER_FAILURE") : null,
                ActivationFailure = device.Id == "cast" && stage == "activate" ? new IOException("RAW_RECEIVER_FAILURE") : null,
                Error = device.Id == "cast" && stage == "reported" ? "RAW_RECEIVER_FAILURE" : null
            });
            var error = await Assert.ThrowsAnyAsync<IOException>(() => service.RunAsync(Request(), null, default));
            Assert.All(outputs, o => { Assert.True(o.Stopped); Assert.True(o.Disposed); });
            Assert.Equal(stage == "activate" ? 1 : 0, inputs.Opens);
            if (inputs.Opens > 0) Assert.True(inputs.Capture.Disposed);
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
            Assert.Contains("Onkyo test", error.Message);
            var receiver = Assert.IsType<CalibrationSpeakerException>(error); Assert.Equal("cast", receiver.SpeakerId);
            Assert.Equal(stage == "activate" ? CalibrationPhase.StartingPlayback : CalibrationPhase.PreparingSpeakers, receiver.Phase);
            Assert.Equal("RAW_RECEIVER_FAILURE", receiver.InnerException!.Message);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task PcmReceiverFailureIdentifiesTheSpeakerAndPreventsARecommendation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Cal-Service-" + Guid.NewGuid().ToString("N")); var inputs = new Inputs(); var outputs = new List<Output>();
        var service = new AcousticCalibrationService(inputs, (device, source) => { var output = new Output(device, source); outputs.Add(output); return output; }, folder,
            async (probe, sinks, seconds, token) => { outputs.Single(o => o.Device.Id == "cast").Error = "RAW_RECEIVER_FAILURE"; await AcousticCalibrationService.EmitAsync(probe, sinks, .01, token); });
        try
        {
            var error = await Assert.ThrowsAnyAsync<IOException>(() => service.RunAsync(Request("stream"), null, default));
            Assert.True(inputs.Capture.Disposed); Assert.All(outputs, o => Assert.True(o.Disposed));
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder)); Assert.Contains("Onkyo test", error.Message);
            var receiver = Assert.IsType<CalibrationSpeakerException>(error); Assert.Equal("cast", receiver.SpeakerId); Assert.Equal(CalibrationPhase.Recording, receiver.Phase);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task FailedEmissionClosesTheMicrophoneAndDoesNotReturnARecommendation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Cal-Service-" + Guid.NewGuid().ToString("N")); var inputs = new Inputs(); var outputs = new List<Output>();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Service(folder, inputs, outputs, failEmission: true).RunAsync(Request(), null, default));
            Assert.True(inputs.Capture.Disposed); Assert.All(outputs, o => Assert.True(o.Disposed));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task PendingOutputCleanupPreventsAnOverlappingCalibration()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Cal-Service-" + Guid.NewGuid().ToString("N")); var inputs = new Inputs(); var outputs = new List<Output>();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var service = Service(folder, inputs, outputs, (device, source) => new(device, source) { Cleanup = cleanup });
            var run = service.RunAsync(Request(), null, default);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!outputs.Any(o => o.Stopped)) await Task.Delay(5, wait.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Request(), null, default));
            cleanup.TrySetResult(); Assert.True((await run).CanApply);
        }
        finally { cleanup.TrySetResult(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task PersistedProfilesAreIndependentAndOnlyApplyToTheMeasuredMembershipAndProtocol()
    {
        var request = Request(); var link = new SpeakerLink { Id = "link", Speakers = request.Speakers.ToDictionary(s => s.Device.Id,
            s => new LinkedSpeaker { Name = s.Device.Name, Transport = s.Device.Transport, Preferences = SpeakerLinks.Copy(s.Preferences) }) };
        CalibrationProfile Profile(int value) => new() { Speakers = request.Speakers.ToDictionary(s => s.Device.Id,
            s => new CalibratedSpeaker { Transport = s.Device.Transport, Protocol = CalibrationProfiles.Protocol(s.Device), DelayMs = value }) };
        link.Calibrations["file"] = Profile(100); link.Calibrations["stream"] = Profile(250);
        var devices = request.Speakers.Select(s => s.Device).ToArray();
        Assert.All(SpeakerLinks.Resolve(link, devices, "file"), route => Assert.Equal(100, route.Preferences.DelayMs));
        Assert.All(SpeakerLinks.Resolve(link, devices, "stream"), route => Assert.Equal(250, route.Preferences.DelayMs));
        var draft = SpeakerLinks.Draft(link); draft.Calibrations["file"].Speakers["pc"].DelayMs = 140;
        Assert.Equal(100, link.Calibrations["file"].Speakers["pc"].DelayMs);
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Cal-Store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json")); await store.SaveAsync(new() { Links = [link] });
            var restored = Assert.Single(store.Load().Links); Assert.Equal(250, restored.Calibrations["stream"].Speakers["pc"].DelayMs);
            restored.Speakers.Remove("cast"); Assert.Null(CalibrationProfiles.Active(restored, "file", devices));
            link.Calibrations["file"].Speakers["cast"].Protocol = "wrong"; Assert.Null(CalibrationProfiles.Active(link, "file", devices));
            Assert.Equal(20, link.Speakers["pc"].Preferences.DelayMs);
        }
        finally { Directory.Delete(folder, true); }
    }
}
