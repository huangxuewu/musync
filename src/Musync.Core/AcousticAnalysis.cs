namespace Musync.Core;

public enum AcousticMeasurementStatus { Reliable, MissingSignal, AmbiguousSignal, UnstableTiming, InvalidCapture, DelayOutOfRange }
public sealed record AcousticRecording(ReadOnlyMemory<float> Samples, int SampleRate = AcousticProbe.MeasurementRate, bool Continuous = true);
public sealed record AcousticSpeakerMeasurement(string Id, AcousticMeasurementStatus Status, double RelativeDelayMs, double SpreadMs, double Confidence);
public sealed record AcousticAnalysisProgress(int CompletedSpeakers, int TotalSpeakers);
public sealed record AcousticAnalysis(IReadOnlyList<AcousticSpeakerMeasurement> Speakers, IReadOnlyDictionary<string, int> RecommendedDelays)
{
    public bool CanApply => Speakers.Count >= 2 && Speakers.All(s => s.Status == AcousticMeasurementStatus.Reliable)
        && RecommendedDelays.Count == Speakers.Count;
}

public static class AcousticAnalyzer
{
    public const int MaximumCaptureSeconds = 240;
    public static AcousticAnalysis Analyze(AcousticProbe probe, AcousticRecording capture, int originSample = 0,
        double maximumArrivalSeconds = 18, CancellationToken token = default, IProgress<AcousticAnalysisProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(probe); ArgumentNullException.ThrowIfNull(capture);
        token.ThrowIfCancellationRequested();
        if (originSample < 0 || !double.IsFinite(maximumArrivalSeconds) || maximumArrivalSeconds is <= 0 or > 30)
            throw new ArgumentOutOfRangeException();
        if (capture.SampleRate != AcousticProbe.MeasurementRate || !capture.Continuous || capture.Samples.Length == 0
            || capture.Samples.Length > AcousticProbe.MeasurementRate * MaximumCaptureSeconds || originSample >= capture.Samples.Length)
            return Invalid(probe);
        // Freeze caller-owned memory before analysis. Captured audio is never
        // written to a file or included in a diagnostic/result object.
        var samples = capture.Samples.ToArray(); var clipped = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) return Invalid(probe);
            if (Math.Abs(sample) >= .999f) clipped++;
        }
        if (clipped > samples.Length * .005) return Invalid(probe);
        var rows = new List<AcousticSpeakerMeasurement>();
        var referenceStatus = AcousticMeasurementStatus.Reliable; double referenceConfidence = 1;
        for (var target = 1; target < probe.Speakers.Count; target++)
        {
            token.ThrowIfCancellationRequested();
            var lags = new List<double>(); var status = AcousticMeasurementStatus.Reliable; double confidence = 1;
            foreach (var marker in probe.Markers.Where(m => m.SpeakerId == probe.Speakers[target].Id))
            {
                var reference = probe.Markers.Single(m => m.SpeakerId == probe.ReferenceId && m.TimeSeconds == marker.TimeSeconds);
                var start = checked(originSample + (int)Math.Round(marker.TimeSeconds * AcousticProbe.MeasurementRate));
                var end = checked(start + (int)Math.Ceiling(maximumArrivalSeconds * AcousticProbe.MeasurementRate) + 1);
                var own = AcousticCorrelation.Find(samples, AcousticProbe.Marker(marker.Pattern), start, end, token);
                var basis = AcousticCorrelation.Find(samples, AcousticProbe.Marker(reference.Pattern), start, end, token);
                var ownStatus = Quality(own); var basisStatus = Quality(basis);
                referenceConfidence = Math.Min(referenceConfidence, basis.Score);
                confidence = Math.Min(confidence, Math.Min(own.Score, basis.Score));
                if (basisStatus != AcousticMeasurementStatus.Reliable) referenceStatus = basisStatus;
                if (ownStatus != AcousticMeasurementStatus.Reliable) status = ownStatus;
                if (basisStatus != AcousticMeasurementStatus.Reliable) status = basisStatus;
                lags.Add((own.Sample - basis.Sample) * 1000d / AcousticProbe.MeasurementRate);
            }
            var ordered = lags.Order().ToArray(); var spread = ordered[^1] - ordered[0];
            if (status == AcousticMeasurementStatus.Reliable && spread > 12) status = AcousticMeasurementStatus.UnstableTiming;
            rows.Add(new(probe.Speakers[target].Id, status, ordered[1], spread, confidence));
            progress?.Report(new(target, probe.Speakers.Count));
        }
        rows.Insert(0, new(probe.ReferenceId, referenceStatus, 0, 0, referenceConfidence));
        var recommendations = new Dictionary<string, int>();
        if (rows.All(r => r.Status == AcousticMeasurementStatus.Reliable))
        {
            var slowest = rows.Max(r => r.RelativeDelayMs);
            if (slowest - rows.Min(r => r.RelativeDelayMs) > 10000)
                rows = rows.Select(r => r with { Status = AcousticMeasurementStatus.DelayOutOfRange }).ToList();
            else foreach (var row in rows) recommendations[row.Id] = (int)Math.Round(slowest - row.RelativeDelayMs, MidpointRounding.AwayFromZero);
        }
        progress?.Report(new(probe.Speakers.Count, probe.Speakers.Count));
        return new(rows.AsReadOnly(), new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(recommendations));
    }
    private static AcousticMeasurementStatus Quality(AcousticCorrelation.Match match) => match.Score < .45
        ? AcousticMeasurementStatus.MissingSignal : match.Alternative > match.Score * .8
        ? AcousticMeasurementStatus.AmbiguousSignal : AcousticMeasurementStatus.Reliable;
    private static AcousticAnalysis Invalid(AcousticProbe probe) => new(
        Array.AsReadOnly(probe.Speakers.Select(s => new AcousticSpeakerMeasurement(s.Id, AcousticMeasurementStatus.InvalidCapture, 0, 0, 0)).ToArray()),
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>()));
}
