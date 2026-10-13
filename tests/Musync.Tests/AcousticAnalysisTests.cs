using Musync.Core;
using Musync.Platform.Windows;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace Musync.Tests;

public sealed class AcousticAnalysisTests
{
    private static AcousticProbe Probe(int count = 2) => new(Enumerable.Range(0, count).Select(i => new AcousticProbeSpeaker("speaker-" + i, "Test speaker " + i)));
    private static float[] Recording(AcousticProbe probe, Func<AcousticProbeMarker, double> delayMs,
        double gain = 1, int origin = 0, double noise = .001, Func<AcousticProbeMarker, double>? markerGain = null)
    {
        var result = new float[origin + (int)((probe.DurationSeconds + 20) * AcousticProbe.MeasurementRate)];
        var random = new Random(612);
        for (var i = 0; i < result.Length; i++) result[i] = (float)(.02 + (random.NextDouble() * 2 - 1) * noise);
        foreach (var marker in probe.Markers)
        {
            var signal = AcousticProbe.Marker(marker.Pattern);
            var first = origin + (int)Math.Round((marker.TimeSeconds + delayMs(marker) / 1000) * AcousticProbe.MeasurementRate);
            for (var i = 0; i < signal.Length; i++) result[first + i] += (float)(signal[i] * gain * (markerGain?.Invoke(marker) ?? 1));
        }
        return result;
    }
    [Theory]
    [InlineData(100)]
    [InlineData(-37)]
    [InlineData(3075)]
    public void KnownOffsetsAreRecoveredFromNoisyPairedMarkersWithoutPeriodicAlias(double offset)
    {
        var probe = Probe(); var origin = 1370;
        var samples = Recording(probe, m => 180 + (m.SpeakerId == probe.ReferenceId ? 0 : offset), origin: origin);
        var before = (float[])samples.Clone();
        var result = AcousticAnalyzer.Analyze(probe, new(samples), origin);
        Assert.True(result.CanApply, string.Join(", ", result.Speakers.Select(r => r.Status)));
        Assert.InRange(result.Speakers[1].RelativeDelayMs, offset - .5, offset + .5);
        Assert.InRange(result.Speakers[1].SpreadMs, 0, .5);
        Assert.Equal((int)Math.Max(0, offset), result.RecommendedDelays[probe.ReferenceId]);
        Assert.Equal((int)Math.Max(0, -offset), result.RecommendedDelays[probe.Speakers[1].Id]);
        Assert.Equal(before, samples);
    }
    [Fact]
    public void PairedReferenceRemovesCommonCaptureOffsetsAcrossDifferentSlots()
    {
        var probe = Probe(3);
        var samples = Recording(probe, m => 200 + m.TimeSeconds * 8 + (m.SpeakerId switch { "speaker-1" => -60, "speaker-2" => 350, _ => 0 }));
        var result = AcousticAnalyzer.Analyze(probe, new(samples));
        Assert.True(result.CanApply);
        Assert.Equal(350, result.RecommendedDelays["speaker-0"]); Assert.Equal(410, result.RecommendedDelays["speaker-1"]);
        Assert.Equal(0, result.RecommendedDelays["speaker-2"]);
    }
    [Fact]
    public void UnstableOffsetsCannotProduceAnApplicableConfiguration()
    {
        var probe = Probe();
        var samples = Recording(probe, m => 200 + (m.SpeakerId == probe.ReferenceId ? 0 : m.Pattern == 1 ? 100 : m.Pattern == 3 ? 160 : 220));
        var result = AcousticAnalyzer.Analyze(probe, new(samples));
        Assert.False(result.CanApply); Assert.Empty(result.RecommendedDelays);
        Assert.Equal(AcousticMeasurementStatus.UnstableTiming, result.Speakers[1].Status);
    }
    [Fact]
    public void MissingAndWeakSpeakersCannotBeSilentlyDroppedFromTheResult()
    {
        var probe = Probe(3); var samples = Recording(probe, _ => 180, markerGain: m => m.SpeakerId == "speaker-2" ? 0 : 1);
        var result = AcousticAnalyzer.Analyze(probe, new(samples));
        Assert.False(result.CanApply); Assert.Equal(3, result.Speakers.Count); Assert.Empty(result.RecommendedDelays);
        Assert.Equal(AcousticMeasurementStatus.MissingSignal, result.Speakers[2].Status);
    }
    [Fact]
    public void AWeakTargetBesideALoudReferenceCannotBecomeACalibrationResult()
    {
        var probe = Probe(); var samples = Recording(probe, _ => 200, markerGain: m => m.SpeakerId == probe.ReferenceId ? 1 : .02);
        var result = AcousticAnalyzer.Analyze(probe, new(samples));
        Assert.False(result.CanApply); Assert.Empty(result.RecommendedDelays);
    }
    [Fact]
    public void ReferenceOnlyAudioCannotBeMistakenForAnyOfThe32DeviceTargetCodes()
    {
        var probe = Probe(32);
        foreach (var marker in probe.Markers.Where(m => m.SpeakerId != probe.ReferenceId))
        {
            var reference = probe.Markers.Single(m => m.SpeakerId == probe.ReferenceId && m.TimeSeconds == marker.TimeSeconds);
            var signal = AcousticProbe.Marker(reference.Pattern); var samples = new float[signal.Length * 3]; signal.CopyTo(samples, signal.Length);
            var match = AcousticCorrelation.Find(samples, AcousticProbe.Marker(marker.Pattern), 0, samples.Length, default);
            Assert.True(match.Score < .45, $"Reference pattern {reference.Pattern} resembles absent target {marker.Pattern}: {match.Score:0.000}");
        }
    }
    [Fact]
    public void TwoEquallyStrongArrivalsAreReportedAsAmbiguous()
    {
        var probe = Probe(); var samples = Recording(probe, _ => 200);
        foreach (var marker in probe.Markers.Where(m => m.SpeakerId != probe.ReferenceId))
        {
            var signal = AcousticProbe.Marker(marker.Pattern);
            var first = (int)Math.Round((marker.TimeSeconds + .6) * AcousticProbe.MeasurementRate);
            for (var i = 0; i < signal.Length; i++) samples[first + i] += signal[i];
        }
        var result = AcousticAnalyzer.Analyze(probe, new(samples));
        Assert.False(result.CanApply); Assert.Equal(AcousticMeasurementStatus.AmbiguousSignal, result.Speakers[1].Status);
    }
    [Fact]
    public void AQuietCleanEchoCannotReplaceTheStrongerArrivalMixedWithTheReference()
    {
        var probe = Probe(); var samples = Recording(probe, m => m.SpeakerId == probe.ReferenceId ? 200 : 300);
        foreach (var marker in probe.Markers.Where(m => m.SpeakerId != probe.ReferenceId))
        {
            var signal = AcousticProbe.Marker(marker.Pattern);
            var first = (int)Math.Round((marker.TimeSeconds + .7) * AcousticProbe.MeasurementRate);
            for (var i = 0; i < signal.Length; i++) samples[first + i] += signal[i] * .12f;
        }
        var result = AcousticAnalyzer.Analyze(probe, new(samples));
        Assert.True(result.CanApply); Assert.Equal(100, result.RecommendedDelays[probe.ReferenceId]);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidCaptureNeverReturnsACompensation(int defect)
    {
        var probe = Probe(); var samples = Recording(probe, _ => 100);
        var recording = defect switch
        {
            0 => new AcousticRecording(samples, 44100),
            1 => new AcousticRecording(samples, Continuous: false),
            2 => new AcousticRecording(samples.Select((s, i) => i == 4 ? float.NaN : s).ToArray()),
            _ => new AcousticRecording(Enumerable.Repeat(1f, samples.Length).ToArray())
        };
        var result = AcousticAnalyzer.Analyze(probe, recording);
        Assert.False(result.CanApply); Assert.Empty(result.RecommendedDelays);
        Assert.All(result.Speakers, r => Assert.Equal(AcousticMeasurementStatus.InvalidCapture, r.Status));
    }
    [Fact]
    public void CancellationAndDelayOutsideTheControlRangeDoNotBecomeSuccess()
    {
        var probe = Probe(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => AcousticAnalyzer.Analyze(probe, new(new float[8000]), token: cancelled.Token));
        var result = AcousticAnalyzer.Analyze(probe, new(Recording(probe, m => m.SpeakerId == probe.ReferenceId ? 100 : 10700)));
        Assert.False(result.CanApply); Assert.Empty(result.RecommendedDelays);
        Assert.All(result.Speakers, r => Assert.Equal(AcousticMeasurementStatus.DelayOutOfRange, r.Status));
    }
    [Fact]
    public void ProbeSupportsAll32DevicesAndRejectsDuplicateOrUnknownRoutes()
    {
        var probe = Probe(32); Assert.Equal(186, probe.Markers.Count);
        Assert.InRange(probe.DurationSeconds, 140, 144);
        Assert.All(probe.Markers, marker => Assert.InRange(AcousticProbe.Marker(marker.Pattern).Max(Math.Abs), 0, .081));
        Assert.Throws<ArgumentException>(() => new AcousticProbe([new("same", "One"), new("same", "Two")]));
        Assert.Throws<ArgumentException>(() => probe.RenderMono("unknown"));
        Assert.Throws<ArgumentException>(() => Probe(33));
    }
    [Fact]
    public void WaveGenerationDoesNotOverwriteExistingFilesOrWriteWhenCanceled()
    {
        var probe = Probe(); var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            File.WriteAllText(path, "existing file"); Assert.Throws<IOException>(() => probe.WriteSpeakerWave(probe.ReferenceId, path));
            Assert.Equal("existing file", File.ReadAllText(path)); File.Delete(path);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => probe.WriteSpeakerWave(probe.ReferenceId, path, cancelled.Token));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task TheActualWindowsCastMp3PreparationRetainsMarkerOffsetsWithinTwoMilliseconds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-Acoustic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var probe = Probe(); var decoded = new List<float[]>();
            await using var preparer = new WindowsCastMediaPreparer(Path.Combine(directory, "cache"));
            foreach (var speaker in probe.Speakers)
            {
                var wave = Path.Combine(directory, speaker.Id + ".wav"); probe.WriteSpeakerWave(speaker.Id, wave);
                await using var lease = await preparer.PrepareAsync(wave, default);
                using var reader = new MediaFoundationReader(lease.Path, new() { RequestFloatOutput = true });
                ISampleProvider mono = new StereoToMonoSampleProvider(reader.ToSampleProvider());
                if (mono.WaveFormat.SampleRate != AcousticProbe.MeasurementRate) mono = new WdlResamplingSampleProvider(mono, AcousticProbe.MeasurementRate);
                var data = new List<float>(); var block = new float[2048]; int read;
                while ((read = mono.Read(block)) > 0) data.AddRange(block.Take(read));
                decoded.Add(data.ToArray());
            }
            var samples = new float[decoded.Max(d => d.Length) + 8000];
            for (var speaker = 0; speaker < decoded.Count; speaker++)
                for (var i = 0; i < decoded[speaker].Length; i++) samples[i + 800 + speaker * 800] += decoded[speaker][i];
            var result = AcousticAnalyzer.Analyze(probe, new(samples));
            Assert.True(result.CanApply, string.Join(", ", result.Speakers.Select(r => $"{r.Status}/{r.Confidence:0.000}")));
            Assert.InRange(result.Speakers[1].RelativeDelayMs, 98, 102); Assert.InRange(result.RecommendedDelays[probe.ReferenceId], 98, 102);
        }
        finally { Directory.Delete(directory, true); }
    }
}
