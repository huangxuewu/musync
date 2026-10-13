namespace Musync.Core;

public sealed record FolderImportResult(IReadOnlyList<MusicTrack> Tracks,
    Dictionary<string, List<string>> Playlists, Dictionary<string, string> Folders, int Skipped)
{
    public IReadOnlyList<string> ImportedPlaylists { get; init; } = [];
}

public static class FolderMusicImporter
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".aac", ".m4a", ".wma", ".aiff", ".aif" };
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Each playlist contains only that folder's direct files. Child folders are
    // independent playlists; reimport adds new tracks without duplicating IDs.
    public static FolderImportResult Import(IEnumerable<string> roots, IEnumerable<MusicTrack> library,
        IReadOnlyDictionary<string, List<string>> playlists, IReadOnlyDictionary<string, string> folders,
        Func<string, MusicTrack> probe, CancellationToken token = default)
    {
        var resultPlaylists = playlists.ToDictionary(p => p.Key, p => p.Value.ToList());
        var resultFolders = new Dictionary<string, string>(folders, PathComparer);
        var tracksByPath = new Dictionary<string, MusicTrack>(PathComparer);
        foreach (var track in library.Where(t => t.FilePath is not null)) tracksByPath.TryAdd(Path.GetFullPath(track.FilePath!), track);
        var added = new List<MusicTrack>();
        var importedPlaylists = new List<string>();
        var visited = new HashSet<string>(PathComparer);
        var pending = new Stack<string>(roots.Reverse());
        var skipped = 0;
        while (pending.TryPop(out var candidate))
        {
            token.ThrowIfCancellationRequested();
            string folder;
            try { folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { skipped++; continue; }
            if (!visited.Add(folder)) continue;
            string[] entries;
            try
            {
                // Do not follow junctions/symlinks into loops or unrelated folders.
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                entries = Directory.GetFileSystemEntries(folder).Order(PathComparer).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
            var ids = new List<string>();
            var children = new List<string>();
            foreach (var path in entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { children.Add(path); continue; }
                    if (!AudioExtensions.Contains(Path.GetExtension(path))) continue;
                    if (!tracksByPath.TryGetValue(path, out var track))
                    {
                        try { track = probe(path) with { FilePath = path }; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { skipped++; continue; }
                        tracksByPath[path] = track; added.Add(track);
                    }
                    if (!ids.Contains(track.Id)) ids.Add(track.Id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            }
            foreach (var child in children.AsEnumerable().Reverse()) pending.Push(child);
            if (ids.Count == 0) continue;
            if (!resultFolders.TryGetValue(folder, out var name) || !resultPlaylists.ContainsKey(name))
            {
                var baseName = Path.GetFileName(folder);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = new DirectoryInfo(folder).Name;
                name = baseName;
                var suffix = 2;
                while (resultPlaylists.Keys.Contains(name, StringComparer.OrdinalIgnoreCase)) name = $"{baseName} ({suffix++})";
                resultFolders[folder] = name;
            }
            if (!resultPlaylists.TryGetValue(name, out var existing)) resultPlaylists[name] = existing = [];
            foreach (var id in ids) if (!existing.Contains(id)) existing.Add(id);
            if (!importedPlaylists.Contains(name)) importedPlaylists.Add(name);
        }
        return new(added, resultPlaylists, resultFolders, skipped) { ImportedPlaylists = importedPlaylists };
    }
}
