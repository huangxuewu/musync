namespace Musync.Core;

public sealed record AcousticProbeSpeaker(string Id, string Name);
public sealed record AcousticProbeMarker(string SpeakerId, int Pattern, double TimeSeconds);

// The reference emits a different marker alongside each target marker. Their
// relative arrival is measured in the same microphone recording and time slot.
public sealed class AcousticProbe
{
    public const int MeasurementRate = 8000;
    public const double MarkerSeconds = .18;
    public const int MaximumSpeakers = 32;
    private readonly AcousticProbeMarker[] _markers;
    public AcousticProbe(IEnumerable<AcousticProbeSpeaker> speakers)
    {
        Speakers = Array.AsReadOnly(speakers.ToArray());
        if (Speakers.Count is < 2 or > MaximumSpeakers
            || Speakers.Any(s => string.IsNullOrWhiteSpace(s.Id))
            || Speakers.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Speakers.Count)
            throw new ArgumentException("A probe needs 2–32 different speakers.", nameof(speakers));
        var markers = new List<AcousticProbeMarker>();
        for (var target = 1; target < Speakers.Count; target++)
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var time = 2 + (target - 1) * 4.5 + new[] { .2, 1.3, 2.9 }[repeat];
                var pattern = (target - 1) * 6 + repeat * 2;
                markers.Add(new(ReferenceId, pattern, time));
                markers.Add(new(Speakers[target].Id, pattern + 1, time));
            }
        _markers = markers.ToArray();
        Markers = Array.AsReadOnly(_markers);
        DurationSeconds = _markers[^1].TimeSeconds + MarkerSeconds + 2;
    }
    public IReadOnlyList<AcousticProbeSpeaker> Speakers { get; }
    public string ReferenceId => Speakers[0].Id;
    public IReadOnlyList<AcousticProbeMarker> Markers { get; }
    public double DurationSeconds { get; }

    public static float[] Marker(int pattern, int rate = MeasurementRate)
    {
        if (pattern is < 0 or >= (MaximumSpeakers - 1) * 6 || rate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException();
        var bits = new bool[45];
        uint state = unchecked(0x9e3779b9u * (uint)(pattern + 1) + 0x85ebca6bu);
        for (var i = 0; i < bits.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            bits[i] = (state & 1) != 0;
        }
        var result = new float[(int)Math.Round(MarkerSeconds * rate)];
        var carrier = 1000 + pattern % 16 * 150;
        for (var frame = 0; frame < result.Length; frame++)
        {
            var chipPosition = frame / (rate * .004);
            var chip = Math.Min(bits.Length - 1, (int)chipPosition);
            var envelope = .5 - .5 * Math.Cos(Math.Tau * (chipPosition - chip));
            result[frame] = (float)(.08 * envelope * Math.Sin(Math.Tau * carrier * frame / rate) * (bits[chip] ? 1 : -1));
        }
        return result;
    }

    public float[] RenderMono(string speakerId, int rate = MeasurementRate)
    {
        ValidateSpeaker(speakerId);
        if (rate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(rate));
        var result = new float[(int)Math.Ceiling(DurationSeconds * rate)];
        foreach (var marker in _markers.Where(m => m.SpeakerId == speakerId))
            Marker(marker.Pattern, rate).CopyTo(result, (int)Math.Round(marker.TimeSeconds * rate));
        return result;
    }
    public void WriteSpeakerWave(string speakerId, string path, CancellationToken token = default)
    {
        ValidateSpeaker(speakerId); token.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var created = false;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            created = true;
            using var writer = new BinaryWriter(stream);
            var frames = (int)Math.Ceiling(DurationSeconds * AudioFormat.SampleRate);
            var bytes = checked(frames * 4);
            writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(AudioFormat.SampleRate);
            writer.Write(AudioFormat.SampleRate * 4); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(bytes);
            var signals = _markers.Where(m => m.SpeakerId == speakerId).Select(m =>
                (Start: (int)Math.Round(m.TimeSeconds * AudioFormat.SampleRate), Samples: Marker(m.Pattern, AudioFormat.SampleRate))).ToArray();
            var block = new byte[4096 * 4];
            for (var first = 0; first < frames; first += 4096)
            {
                token.ThrowIfCancellationRequested(); block.AsSpan().Clear();
                var count = Math.Min(4096, frames - first);
                foreach (var signal in signals)
                    for (var frame = Math.Max(first, signal.Start); frame < Math.Min(first + count, signal.Start + signal.Samples.Length); frame++)
                    {
                        var value = (short)(signal.Samples[frame - signal.Start] * short.MaxValue);
                        var offset = (frame - first) * 4;
                        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(offset, 2), value);
                        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(offset + 2, 2), value);
                    }
                writer.Write(block, 0, count * 4);
            }
        }
        catch
        {
            if (created) { try { File.Delete(fullPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
    }
    private void ValidateSpeaker(string id)
    {
        if (!Speakers.Any(s => s.Id == id)) throw new ArgumentException("The speaker is not in this probe.", nameof(id));
    }
}
