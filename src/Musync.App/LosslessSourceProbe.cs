using NAudio.Wave;

namespace Musync.App;

// Source-file information only: receiver encoding and Spotify catalogue quality
// are separate. Read headers off the UI thread, never open an audio output.
internal sealed class LosslessSourceProbe
{
    private sealed record Entry(long Length, long Modified, string? Codec);
    private readonly Dictionary<string, Entry> _cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _gate = new();
    internal string? Read(string? path)
    {
        if (path is null) return null;
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".flac" or ".wav" or ".aiff" or ".aif" or ".m4a" or ".wma")) return null;
            var file = new FileInfo(path);
            if (!file.Exists) { lock (_gate) _cache.Remove(file.FullName); return null; }
            var length = file.Length; var modified = file.LastWriteTimeUtc.Ticks;
            lock (_gate)
                if (_cache.TryGetValue(file.FullName, out var saved) && saved.Length == length && saved.Modified == modified) return saved.Codec;
            var codec = Inspect(file.FullName, extension);
            file.Refresh();
            // Do not retain a result if the file was replaced during inspection.
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != modified) return null;
            lock (_gate)
            {
                if (_cache.Count >= 256) _cache.Clear();
                _cache[file.FullName] = new(length, modified, codec);
            }
            return codec;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
            or TagLib.UnsupportedFormatException or ArgumentException or NotSupportedException or InvalidOperationException or FormatException)
        { return null; }
    }

    private static string? Inspect(string path, string extension)
    {
        if (extension == ".wav")
        {
            using var wave = new WaveFileReader(path);
            // File readers expose extensible headers as WaveFormatExtraData;
            // NAudio normalizes both representations using the actual subtype GUID.
            var format = wave.WaveFormat.AsStandardWaveFormat();
            if (format.Encoding == WaveFormatEncoding.Pcm) return "WAV / PCM";
            if (format.Encoding == WaveFormatEncoding.IeeeFloat) return "WAV / Float";
            return null;
        }
        if (extension is ".aiff" or ".aif")
        {
            // AIFF-C may carry a lossy codec. Only ordinary uncompressed AIFF qualifies here.
            using var header = System.IO.File.OpenRead(path);
            Span<byte> signature = stackalloc byte[12]; header.ReadExactly(signature);
            if (!signature[..4].SequenceEqual("FORM"u8) || !signature[8..].SequenceEqual("AIFF"u8)) return null;
            using var aiff = new AiffFileReader(path);
            return aiff.WaveFormat.Encoding == WaveFormatEncoding.Pcm ? "AIFF / PCM" : null;
        }
        using var tags = TagLib.File.Create(path, TagLib.ReadStyle.Average);
        var codecs = tags.Properties?.Codecs.OfType<TagLib.IAudioCodec>().ToArray();
        if (codecs is not { Length: 1 }) return null;
        return codecs[0] switch
        {
            TagLib.Flac.StreamHeader => "FLAC",
            TagLib.Mpeg4.IsoAudioSampleEntry entry when entry.BoxType.ToString() == "alac" => "ALAC",
            TagLib.Riff.WaveFormatEx waveCodec when waveCodec.FormatTag == 0x0163 => "WMA Lossless",
            _ => null
        };
    }
}
