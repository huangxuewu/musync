using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Musync.Core;

public sealed record CalibrationSpeaker(SpeakerDevice Device, SpeakerPreferences Preferences);
public sealed record CalibrationRequest(string LinkId, string Path, string InputId, IReadOnlyList<CalibrationSpeaker> Speakers, double MasterVolume);
public enum CalibrationPhase { PreparingSpeakers, OpeningMicrophone, StartingPlayback, Recording, Analyzing, Closing }
public sealed record CalibrationProgress(CalibrationPhase Phase, int Completed, int Total, string? SpeakerName = null);
public sealed class CalibrationSpeakerException(SpeakerDevice speaker, CalibrationPhase phase, Exception inner)
    : IOException("CalibrationSpeakerFailed: " + speaker.Name, inner)
{
    public string SpeakerId { get; } = speaker.Id;
    public string SpeakerName { get; } = speaker.Name;
    public CalibrationPhase Phase { get; } = phase;
}
public sealed record CalibrationRunResult(CalibrationRequest Request, AcousticAnalysis Analysis, IReadOnlyDictionary<string, int> Delays)
{
    public bool CanApply => Analysis.CanApply && Delays.Count == Request.Speakers.Count && Delays.Values.All(v => v is >= 0 and <= 10000);
}
public interface IAcousticCalibrationService
{
    IReadOnlyList<AcousticInputDevice> GetInputs();
    Task<CalibrationRunResult> RunAsync(CalibrationRequest request, IProgress<CalibrationProgress>? progress, CancellationToken token);
}

public sealed class AcousticCalibrationService : IAcousticCalibrationService
{
    private readonly IAcousticInputProvider _inputs;
    private readonly Func<SpeakerDevice, PlaybackSource, IAudioOutput> _factory;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<AcousticProbe, IReadOnlyList<IAudioOutput>, double, CancellationToken, Task> _emit;
    public AcousticCalibrationService(IAcousticInputProvider inputs, Func<SpeakerDevice, PlaybackSource, IAudioOutput> factory, string directory)
        : this(inputs, factory, directory, EmitAsync) { }
    internal AcousticCalibrationService(IAcousticInputProvider inputs, Func<SpeakerDevice, PlaybackSource, IAudioOutput> factory, string directory,
        Func<AcousticProbe, IReadOnlyList<IAudioOutput>, double, CancellationToken, Task> emit)
    { _inputs = inputs; _factory = factory; _directory = System.IO.Path.GetFullPath(directory); _emit = emit; }
    public IReadOnlyList<AcousticInputDevice> GetInputs() => _inputs.GetInputs();
    public async Task<CalibrationRunResult> RunAsync(CalibrationRequest request, IProgress<CalibrationProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CalibrationProfiles.IsPath(request.Path) || request.Speakers.Count is < 2 or > 32
            || request.Speakers.Any(s => s.Device.UnsupportedReason is not null || s.Preferences.Muted || s.Preferences.Volume <= 0)
            || request.Speakers.Any(s => string.IsNullOrWhiteSpace(s.Device.Id) || !double.IsFinite(s.Preferences.Volume)
                || s.Preferences.Volume > 100 || s.Preferences.DelayMs is < 0 or > 10000)
            || request.Speakers.Select(s => s.Device.Id).Distinct(StringComparer.Ordinal).Count() != request.Speakers.Count
            || !double.IsFinite(request.MasterVolume) || request.MasterVolume is <= 0 or > 100
            || ReceiverRoutes.Choose(request.Speakers.Select(s => (s.Device, s.Preferences))).Count != request.Speakers.Count)
            throw new InvalidOperationException("CalibrationDevicesRequired");
        if (!_gate.Wait(0)) throw new InvalidOperationException("CalibrationStillClosing");
        var speakers = request.Speakers.Select(s => new CalibrationSpeaker(s.Device, SpeakerLinks.Copy(s.Preferences)))
            .OrderBy(s => s.Device.Transport is SpeakerTransport.Windows or SpeakerTransport.Bluetooth ? 0 : 1).ToArray();
        request = request with { Speakers = Array.AsReadOnly(speakers) };
        var probe = new AcousticProbe(speakers.Select(s => new AcousticProbeSpeaker(s.Device.Id, s.Device.Name)));
        var folder = System.IO.Path.Combine(_directory, "probe-" + Guid.NewGuid().ToString("N"));
        var outputs = new List<IAudioOutput>(); IAcousticCaptureSession? capture = null; AcousticRecording? recording = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? failure = null;
        try
        {
            Directory.CreateDirectory(folder);
            for (var i = 0; i < speakers.Length; i++)
            {
                var speaker = speakers[i]; var wave = System.IO.Path.Combine(folder, i + ".wav");
                progress?.Report(new(CalibrationPhase.PreparingSpeakers, i, speakers.Length, speaker.Device.Name));
                if (request.Path == "file" && speaker.Device.Transport == SpeakerTransport.Chromecast)
                    await Task.Run(() => probe.WriteSpeakerWave(speaker.Device.Id, wave, lifetime.Token), lifetime.Token);
                var output = _factory(speaker.Device, new(wave, request.Path == "capture", 0, request.Path == "stream"));
                outputs.Add(output);
                output.Control.DelayMs = speaker.Preferences.DelayMs;
                output.Control.Gain = (float)Math.Min(.3, speaker.Preferences.Volume * request.MasterVolume / 10000);
                await RunReceiverAsync(output, CalibrationPhase.PreparingSpeakers, TimeSpan.FromSeconds(45), output.StartAsync, lifetime.Token);
                // FileCastOutput owns its prepared asset after StartAsync. The
                // generated WAV is no longer needed and does not accumulate.
                if (File.Exists(wave)) File.Delete(wave);
            }
            if (request.Path == "stream")
            {
                var maximum = outputs.Max(o => o.EstimatedLatency.TotalMilliseconds);
                foreach (var output in outputs)
                    output.Control.AlignmentDelayMs = (int)Math.Clamp(maximum - output.EstimatedLatency.TotalMilliseconds, 0, 5000);
            }
            foreach (var output in outputs) output.Buffer.SetDelay(output.Control.EffectiveDelayMs);
            var arrival = Math.Min(30, outputs.Max(o => o.Control.EffectiveDelayMs / 1000d + o.EstimatedLatency.TotalSeconds) + 6);
            var recordingSeconds = probe.DurationSeconds + arrival + 1;
            progress?.Report(new(CalibrationPhase.OpeningMicrophone, 0, speakers.Length));
            capture = await _inputs.StartAsync(request.InputId, TimeSpan.FromSeconds(recordingSeconds + 10), lifetime.Token);
            var origin = checked((int)capture.SamplesCaptured);
            progress?.Report(new(CalibrationPhase.StartingPlayback, 0, speakers.Length));
            await Task.WhenAll(outputs.Select(o => RunReceiverAsync(o, CalibrationPhase.StartingPlayback, TimeSpan.FromSeconds(6), o.BeginPlaybackAsync, lifetime.Token)));
            lifetime.CancelAfter(TimeSpan.FromSeconds(recordingSeconds + 8));
            progress?.Report(new(CalibrationPhase.Recording, 0, speakers.Length));
            await _emit(probe, outputs, recordingSeconds, lifetime.Token);
            foreach (var output in outputs) if (output.Error is { } error) throw new CalibrationSpeakerException(output.Device, CalibrationPhase.Recording, new IOException(error));
            recording = await capture.CompleteAsync(lifetime.Token);
            progress?.Report(new(CalibrationPhase.Analyzing, 0, speakers.Length));
            var analysis = await Task.Run(() => AcousticAnalyzer.Analyze(probe, recording, origin, arrival, lifetime.Token), lifetime.Token);
            var delays = analysis.RecommendedDelays.ToDictionary(p => p.Key,
                p => p.Value + speakers.Single(s => s.Device.Id == p.Key).Preferences.DelayMs);
            return new(request, analysis, new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(delays));
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            lifetime.Cancel(); progress?.Report(new(CalibrationPhase.Closing, 0, speakers.Length));
            Exception? cleanupError = null;
            foreach (var output in outputs) output.Buffer.SetTransportPaused(true);
            foreach (var output in outputs)
            {
                try { await output.StopAsync(); } catch (Exception ex) { cleanupError ??= ex; }
                try { await output.DisposeAsync(); } catch (Exception ex) { cleanupError ??= ex; }
            }
            if (capture is not null) try { await capture.DisposeAsync(); } catch (Exception ex) { cleanupError ??= ex; }
            if (recording is not null && MemoryMarshal.TryGetArray(recording.Samples, out var data)) data.AsSpan().Clear();
            try
            {
                if (Directory.Exists(folder) && System.IO.Path.GetFullPath(folder).StartsWith(_directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(folder, true);
            }
            catch (IOException ex) { cleanupError ??= ex; }
            catch (UnauthorizedAccessException ex) { cleanupError ??= ex; }
            _gate.Release();
            if (failure is null && cleanupError is not null) throw new IOException("CalibrationCleanupFailed", cleanupError);
        }
    }
    private static async Task RunReceiverAsync(IAudioOutput output, CalibrationPhase phase, TimeSpan timeout, Func<CancellationToken, Task> action, CancellationToken token)
    {
        try
        {
            await action(token).WaitAsync(timeout, token);
            if (output.Error is { } error) throw new IOException(error);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new CalibrationSpeakerException(output.Device, phase, ex); }
    }
    internal static async Task EmitAsync(AcousticProbe probe, IReadOnlyList<IAudioOutput> outputs, double seconds, CancellationToken token)
    {
        var sources = outputs.Where(o => o.ReceivesPcm).ToDictionary(o => o, o => probe.Markers.Where(m => m.SpeakerId == o.Device.Id)
            .Select(m => (Start: (int)Math.Round(m.TimeSeconds * AudioFormat.SampleRate), Samples: AcousticProbe.Marker(m.Pattern, AudioFormat.SampleRate))).ToArray());
        var block = new float[4096]; var clock = Stopwatch.StartNew();
        for (var first = 0; first < seconds * AudioFormat.SampleRate; first += block.Length / 2)
        {
            token.ThrowIfCancellationRequested();
            foreach (var output in outputs)
            {
                if (output.Error is { } error) throw new CalibrationSpeakerException(output.Device, CalibrationPhase.Recording, new IOException(error));
                if (!output.ReceivesPcm) continue;
                block.AsSpan().Clear();
                foreach (var signal in sources[output])
                    for (var frame = Math.Max(first, signal.Start); frame < Math.Min(first + block.Length / 2, signal.Start + signal.Samples.Length); frame++)
                    {
                        var sample = signal.Samples[frame - signal.Start]; var index = (frame - first) * 2;
                        block[index] = sample; block[index + 1] = sample;
                    }
                output.Buffer.Write(block);
            }
            var delay = (first + block.Length / 2) / (double)AudioFormat.SampleRate - clock.Elapsed.TotalSeconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token);
        }
    }
}
