using System.Globalization;
using System.Text.RegularExpressions;

namespace Musync.Core;

public sealed record LyricLine(double Seconds, string Text);
public sealed record LyricWindow(int Index, string Previous, string Current, string Next);

public sealed partial class SyncedLyrics
{
    public static SyncedLyrics Empty { get; } = new([]);
    public IReadOnlyList<LyricLine> Lines { get; }
    public SyncedLyrics(IEnumerable<LyricLine> lines) => Lines = lines
        .Where(l => double.IsFinite(l.Seconds) && l.Seconds >= 0)
        .OrderBy(l => l.Seconds).GroupBy(l => l.Seconds).Select(g => g.Last()).Take(10000).ToArray();

    public static SyncedLyrics Parse(string text)
    {
        if (text.Length > 1_048_576) return Empty;
        var lines = new List<LyricLine>();
        var offset = 0d;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', '\uFEFF').Trim();
            var offsetMatch = Offset().Match(line);
            if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            { offset = Math.Clamp(ms / 1000, -86400, 86400); continue; }
            var timestamps = Timestamp().Matches(line);
            if (timestamps.Count == 0 || timestamps[0].Index != 0) continue;
            var end = timestamps[^1].Index + timestamps[^1].Length;
            var lyric = line[end..].Trim();
            foreach (Match timestamp in timestamps)
            {
                if (!double.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture, out var minutes)
                    || !double.TryParse(timestamp.Groups[2].Value, CultureInfo.InvariantCulture, out var seconds)
                    || seconds >= 60 || minutes > 1440) continue;
                lines.Add(new(minutes * 60 + seconds, lyric));
            }
        }
        return new(lines.Select(l => l with { Seconds = Math.Max(0, l.Seconds + offset) }));
    }

    public LyricWindow At(double seconds)
    {
        var low = 0; var high = Lines.Count - 1; var index = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (Lines[middle].Seconds <= seconds) { index = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return new(index, index > 0 ? Lines[index - 1].Text : "",
            index >= 0 ? Lines[index].Text : "", index + 1 < Lines.Count ? Lines[index + 1].Text : "");
    }

    public static async Task<SyncedLyrics> LoadAlongsideAsync(string? audioPath, CancellationToken token)
    {
        if (audioPath is null) return Empty;
        try
        {
            var file = Path.ChangeExtension(audioPath, ".lrc");
            if (!File.Exists(file) && OperatingSystem.IsWindows())
                file = Directory.EnumerateFiles(Path.GetDirectoryName(audioPath)!, "*")
                    .FirstOrDefault(p => string.Equals(Path.GetFileName(p), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) ?? file;
            if (!File.Exists(file) || new FileInfo(file).Length > 1_048_576) return Empty;
            // UTF-8 plus UTF-16/UTF-32 BOM detection, with a bounded read.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[1_048_577];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
            return count > 1_048_576 ? Empty : Parse(new string(buffer, 0, count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Empty; }
    }

    [GeneratedRegex(@"\[(\d{1,4}):([0-5]?\d(?:\.\d{1,3})?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"^\[offset:([+-]?\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
}
