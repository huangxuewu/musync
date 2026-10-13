using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Musync.Core;

public sealed record ArtworkCacheStats(long Bytes, int Files, long LimitBytes);

// The index owns hashed cache files only, never source audio.
internal sealed class TemporaryArtworkCache(string directory, TimeProvider clock, long limitBytes)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly object _gate = new();
    private Dictionary<string, FileEntry>? _files;
    private long _limitBytes = Math.Max(0, limitBytes);
    private const string IndexName = "cache-index.json";
    internal sealed record Match(string Title, string Artist, string? Album, MusicTrack? Catalog, DateTimeOffset Expires);
    internal sealed record Cover(byte[]? Data, DateTimeOffset Expires);
    internal sealed record LocalMetadata(string Title, string Artist, string? Album);
    private sealed class FileEntry
    {
        public string Kind { get; set; } = "";
        public long Bytes { get; set; }
        public DateTimeOffset LastAccess { get; set; }
        public DateTimeOffset? Expires { get; set; }
        public HashSet<string> Owners { get; set; } = new(StringComparer.Ordinal);
    }
    private static string Name(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json";
    internal static string Owner(MusicTrack track) => track.FilePath is null ? "spotify:" + Name(track.Id)[..64]
        : "local:" + Name(OperatingSystem.IsWindows() ? Path.GetFullPath(track.FilePath).ToUpperInvariant() : Path.GetFullPath(track.FilePath))[..64];
    private static bool OwnedFile(string name) => name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal) && name[..64].All(char.IsAsciiHexDigit);
    private string PathFor(string name) => Path.Combine(_directory, name);
    public Match? ReadMatch(string key, string owner) => Read<Match>("match:" + key, "match", owner, 64 * 1024);
    public Cover? ReadCover(string url, string owner) => Read<Cover>("cover:" + url, "cover", owner, 7 * 1024 * 1024);
    public LocalMetadata? ReadLocal(string key, string owner) => Read<LocalMetadata>("local:" + key, "local", owner, 64 * 1024);
    public void SaveMatch(string key, Match entry, string owner) => Save("match:" + key, "match", entry, entry.Expires, owner);
    public void SaveCover(string url, Cover entry, string owner) => Save("cover:" + url, "cover", entry, entry.Expires, owner);
    public void SaveLocal(string key, LocalMetadata entry, string owner) => Save("local:" + key, "local", entry, null, owner);
    private T? Read<T>(string key, string kind, string owner, long maximum) where T : class
    {
        lock (_gate)
        {
            try
            {
                LoadIndex(); var name = Name(key);
                if (!_files!.TryGetValue(name, out var file) || file.Kind != kind) return null;
                if (file.Expires <= clock.GetUtcNow()) { Delete(name); WriteIndex(); return null; }
                var info = new FileInfo(PathFor(name));
                if (!info.Exists || info.Length > maximum) { Delete(name); WriteIndex(); return null; }
                var entry = JsonSerializer.Deserialize<T>(File.ReadAllBytes(info.FullName));
                if (entry is null) { Delete(name); WriteIndex(); return null; }
                var expiration = entry is Match match ? match.Expires : entry is Cover cover ? cover.Expires : (DateTimeOffset?)null;
                if (kind != "local" && (expiration is null || expiration <= clock.GetUtcNow() || expiration > clock.GetUtcNow().AddHours(1)))
                { Delete(name); WriteIndex(); return null; }
                file.LastAccess = clock.GetUtcNow(); file.Bytes = info.Length;
                file.Owners.Add(owner);
                PersistAccess();
                return entry;
            }
            catch (Exception error) when (Optional(error)) { return null; }
        }
    }
    private void Save<T>(string key, string kind, T entry, DateTimeOffset? expires, string owner)
    {
        lock (_gate)
        {
            try
            {
                LoadIndex(); Directory.CreateDirectory(_directory);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
                if (bytes.LongLength > _limitBytes) return;
                var name = Name(key); var path = PathFor(name); var temporary = path + ".writing";
                File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true);
                if (kind == "local")
                    foreach (var old in _files!.Where(p => p.Key != name && p.Value.Kind == kind && p.Value.Owners.Contains(owner)).Select(p => p.Key).ToArray()) Delete(old);
                var owners = _files!.TryGetValue(name, out var previous) ? previous.Owners : new HashSet<string>(StringComparer.Ordinal);
                owners.Add(owner);
                _files[name] = new() { Kind = kind, Bytes = bytes.LongLength, Expires = expires, LastAccess = clock.GetUtcNow(), Owners = owners };
                Prune();
            }
            catch (Exception error) when (Optional(error)) { /* Optional caches must never interrupt audio. */ }
        }
    }
    public bool AssociateCover(string url, string owner)
    {
        lock (_gate)
        {
            try
            {
                LoadIndex();
                if (_files!.TryGetValue(Name("cover:" + url), out var entry))
                { entry.LastAccess = clock.GetUtcNow(); entry.Owners.Add(owner); PersistAccess(); return _files.ContainsKey(Name("cover:" + url)); }
            }
            catch (Exception error) when (Optional(error)) { }
            return false;
        }
    }
    private void LoadIndex()
    {
        if (_files is not null) return;
        _files = new(StringComparer.Ordinal);
        if (!Directory.Exists(_directory)) return;
        try
        {
            var indexPath = PathFor(IndexName);
            if (File.Exists(indexPath) && new FileInfo(indexPath).Length <= 32 * 1024 * 1024)
                _files = JsonSerializer.Deserialize<Dictionary<string, FileEntry>>(File.ReadAllBytes(indexPath)) ?? _files;
        }
        catch (Exception error) when (Optional(error)) { }
        foreach (var name in _files.Keys.Where(n => !OwnedFile(n)).ToArray()) _files.Remove(name);
        // Migrate the old cache and recover an interrupted index update.
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
        {
            if (!OwnedFile(file.Name)) continue;
            if (_files.TryGetValue(file.Name, out var existing) && existing is not null && existing.Owners is not null)
            { existing.Bytes = file.Length; continue; }
            try
            {
                if (file.Length > 7 * 1024 * 1024) { file.Delete(); continue; }
                using var json = JsonDocument.Parse(File.ReadAllBytes(file.FullName)); var root = json.RootElement;
                var kind = root.TryGetProperty("Data", out _) ? "cover" : root.TryGetProperty("Catalog", out _) ? "match" : "local";
                var expires = root.TryGetProperty("Expires", out var expiration) ? expiration.GetDateTimeOffset() : (DateTimeOffset?)null;
                if (kind != "local" && expires is null) { file.Delete(); continue; }
                _files[file.Name] = new() { Kind = kind, Bytes = file.Length, Expires = expires, LastAccess = file.LastWriteTimeUtc };
            }
            catch (Exception error) when (Optional(error) || error is FormatException or InvalidOperationException) { file.Delete(); }
        }
        foreach (var name in _files.Keys.Where(n => !File.Exists(PathFor(n))).ToArray()) _files.Remove(name);
        Prune();
    }
    private byte[] IndexBytes() => JsonSerializer.SerializeToUtf8Bytes(_files);
    private void PersistAccess()
    {
        // Persist hits as well as new owners so a restart retains actual LRU order.
        // Normal hits need no directory scan; capacity/expiry maintenance runs separately.
        var bytes = IndexBytes();
        if (_files!.Values.Sum(f => f.Bytes) + bytes.LongLength > _limitBytes) Prune();
        else WriteIndex(bytes);
    }
    private void WriteIndex(byte[]? bytes = null)
    {
        if (!Directory.Exists(_directory)) { if (_files!.Count == 0) return; Directory.CreateDirectory(_directory); }
        var temporary = PathFor(IndexName + ".writing");
        File.WriteAllBytes(temporary, bytes ?? IndexBytes()); File.Move(temporary, PathFor(IndexName), overwrite: true);
    }
    private void Delete(string name) { if (!OwnedFile(name)) return; File.Delete(PathFor(name)); _files!.Remove(name); }
    private void Prune()
    {
        if (Directory.Exists(_directory))
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
                if (file.LastWriteTimeUtc < clock.GetUtcNow().UtcDateTime.AddMinutes(-5) && file.Name.EndsWith(".writing", StringComparison.Ordinal)
                    && (file.Name == IndexName + ".writing" || OwnedFile(file.Name[..^8]))) file.Delete();
        foreach (var name in _files!.Where(p => p.Value.Expires <= clock.GetUtcNow() || !File.Exists(PathFor(p.Key))).Select(p => p.Key).ToArray()) Delete(name);
        var bytes = _files!.Values.Sum(f => f.Bytes); var indexBytes = IndexBytes();
        foreach (var name in _files.OrderBy(p => p.Value.LastAccess).Select(p => p.Key).ToArray())
        {
            if (bytes + indexBytes.LongLength <= _limitBytes) break;
            bytes -= _files[name].Bytes; Delete(name); indexBytes = IndexBytes();
        }
        WriteIndex(indexBytes);
    }
    public ArtworkCacheStats Maintain(long maximum, IReadOnlySet<string>? localOwners = null)
    {
        lock (_gate)
        {
            _limitBytes = Math.Max(0, maximum);
            try
            {
                LoadIndex();
                if (localOwners is not null)
                    RemoveOwnersCore(_files!.Values.SelectMany(f => f.Owners).Where(o => o.StartsWith("local:", StringComparison.Ordinal) && !localOwners.Contains(o)).ToHashSet(StringComparer.Ordinal));
                Prune();
                return new(_files!.Values.Sum(f => f.Bytes) + (File.Exists(PathFor(IndexName)) ? new FileInfo(PathFor(IndexName)).Length : 0), _files.Count, _limitBytes);
            }
            catch (Exception error) when (Optional(error)) { return new(0, 0, _limitBytes); }
        }
    }
    private void RemoveOwnersCore(IReadOnlySet<string> owners)
    {
        foreach (var (name, entry) in _files!.ToArray())
            if (entry.Owners.RemoveWhere(owners.Contains) > 0 && entry.Owners.Count == 0) Delete(name);
    }
    public void RemoveOwners(IReadOnlySet<string> owners)
    {
        lock (_gate)
        {
            try { LoadIndex(); RemoveOwnersCore(owners); Prune(); }
            catch (Exception error) when (Optional(error)) { }
        }
    }
    public void Clear(bool includeLocalMetadata = true)
    {
        lock (_gate)
        {
            try
            {
                LoadIndex(); foreach (var name in _files!.Where(p => includeLocalMetadata || p.Value.Kind != "local").Select(p => p.Key).ToArray()) Delete(name);
                if (Directory.Exists(_directory))
                    foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
                        if (file.Name is IndexName or "cache-index.json.writing" || file.Name.EndsWith(".writing", StringComparison.Ordinal) && OwnedFile(file.Name[..^8])) file.Delete();
                if (_files!.Count > 0) WriteIndex();
            }
            catch (Exception error) when (Optional(error)) { }
        }
    }
    private static bool Optional(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
