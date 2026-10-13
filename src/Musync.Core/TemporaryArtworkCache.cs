using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Musync.Core;

internal sealed class TemporaryArtworkCache(string directory, TimeProvider clock)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly object _gate = new();
    private bool _maintained;
    private const long MaxBytes = 64 * 1024 * 1024;
    internal sealed record Match(string Title, string Artist, string? Album, MusicTrack? Catalog, DateTimeOffset Expires);
    internal sealed record Cover(byte[]? Data, DateTimeOffset Expires);
    public Match? ReadMatch(string key)
    {
        var entry = Read<Match>("match:" + key, 64 * 1024, e => e.Expires);
        return entry is not null && entry.Title is not null && entry.Artist is not null ? entry : null;
    }
    public Cover? ReadCover(string url) => Read<Cover>("cover:" + url, 7 * 1024 * 1024, e => e.Expires);
    public void SaveMatch(string key, Match entry) => Save("match:" + key, entry);
    public void SaveCover(string url, Cover entry) => Save("cover:" + url, entry);

    private string FileName(string key) => Path.Combine(_directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    private T? Read<T>(string key, long maximum, Func<T, DateTimeOffset> expiration) where T : class
    {
        lock (_gate)
        {
            try
            {
                if (!_maintained)
                {
                    if (Directory.Exists(_directory)) Prune();
                    _maintained = true;
                }
                var path = FileName(key); var file = new FileInfo(path);
                if (!file.Exists) return null;
                if (file.Length > maximum) { File.Delete(path); return null; }
                var entry = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path));
                var now = clock.GetUtcNow();
                if (entry is null || expiration(entry) <= now || expiration(entry) > now.AddHours(1))
                { File.Delete(path); return null; }
                return entry;
            }
            catch (Exception error) when (Optional(error)) { return null; }
        }
    }
    private void Save<T>(string key, T entry)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = FileName(key); var temporary = path + ".writing";
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(entry));
                File.Move(temporary, path, overwrite: true);
                File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
                Prune();
            }
            catch (Exception error) when (Optional(error)) { /* Cache failure must not become an audio error. */ }
        }
    }
    private IEnumerable<FileInfo> Files() => new DirectoryInfo(_directory).EnumerateFiles()
        .Where(f => f.Name.Length is 69 or 77 && f.Name[..64].All(char.IsAsciiHexDigit)
            && (f.Name.EndsWith(".json", StringComparison.Ordinal) || f.Name.EndsWith(".json.writing", StringComparison.Ordinal)));
    private void Prune()
    {
        var files = Files().OrderBy(f => f.LastWriteTimeUtc).ToList();
        var bytes = files.Sum(f => f.Length); var count = files.Count;
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= clock.GetUtcNow().UtcDateTime.AddHours(-1) && bytes <= MaxBytes && count <= 256) break;
            bytes -= file.Length; count--; file.Delete();
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            try { if (Directory.Exists(_directory)) foreach (var file in Files()) file.Delete(); }
            catch (Exception error) when (Optional(error)) { }
        }
    }
    private static bool Optional(Exception error) => error is IOException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException;
}
