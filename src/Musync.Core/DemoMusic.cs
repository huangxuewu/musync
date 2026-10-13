namespace Musync.Core;

public static class DemoMusic
{
    public static IReadOnlyList<MusicTrack> EnsureLibrary(string directory)
    {
        Directory.CreateDirectory(directory);
        var names = new[] { "Afterglow", "Slow Sunday", "Golden Hour" };
        var tracks = new List<MusicTrack>();
        for (var song = 0; song < names.Length; song++)
        {
            var path = Path.Combine(directory, names[song] + ".wav");
            if (!File.Exists(path)) Write(path, song, 45);
            tracks.Add(new("demo:" + song, names[song], "Musync · Original demo", path, DurationSeconds: 45, IsDemo: true));
        }
        return tracks;
    }
    public static string Calibration(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "calibration.wav");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        Header(writer, 16);
        for (var frame = 0; frame < AudioFormat.SampleRate * 16; frame++)
        {
            var time = frame / (double)AudioFormat.SampleRate;
            var phase = time % 1;
            var tone = phase < .09 ? Math.Sin(2 * Math.PI * 1000 * time) * .25 * Math.Sin(Math.PI * phase / .09) : 0;
            var value = (short)(tone * 32767);
            writer.Write(value); writer.Write(value);
        }
        return path;
    }
    private static void Write(string path, int song, int seconds)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        Header(writer, seconds);
        var chords = new[] { 220.0, 174.614, 261.626, 195.998 };
        var shift = Math.Pow(2, song / 12.0);
        for (var frame = 0; frame < AudioFormat.SampleRate * seconds; frame++)
        {
            var time = frame / (double)AudioFormat.SampleRate;
            var chord = chords[(int)(time / 4) % chords.Length] * shift;
            var pad = .075 * (Math.Sin(time * 2 * Math.PI * chord) +
                .6 * Math.Sin(time * 2 * Math.PI * chord * 1.25) + .4 * Math.Sin(time * 2 * Math.PI * chord * 1.5));
            var beat = time % .5;
            var pluck = .12 * Math.Exp(-beat * 12) * Math.Sin(time * 2 * Math.PI * chord * 2);
            var bass = .09 * Math.Sin(time * 2 * Math.PI * chord / 2);
            var envelope = Math.Min(1, time / 2) * Math.Min(1, (seconds - time) / 2);
            var left = (short)(Math.Clamp((pad + pluck + bass) * envelope, -1, 1) * 32767);
            var right = (short)(Math.Clamp((pad + pluck * .85 + bass) * envelope, -1, 1) * 32767);
            writer.Write(left); writer.Write(right);
        }
    }
    private static void Header(BinaryWriter writer, int seconds)
    {
        var length = AudioFormat.SampleRate * seconds * 4;
        writer.Write("RIFF"u8); writer.Write(length + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(AudioFormat.SampleRate);
        writer.Write(AudioFormat.SampleRate * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(length);
    }
}
