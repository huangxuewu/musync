using System.Text.Json;

namespace Musync.Core;

public sealed record MusicTrack(string Id, string Title, string Artist, string? FilePath,
    string? SpotifyUri = null, string? ImageUrl = null, double DurationSeconds = 0, bool IsDemo = false, string? Album = null);

public sealed class AppSettings
{
    public string Language { get; set; } = "";
    public double MasterVolume { get; set; } = 65;
    public string SpotifyClientId { get; set; } = "";
    public int ArtworkCacheLimitMb { get; set; } = 500;
    public string? CaptureDeviceId { get; set; }
    public Dictionary<string, SpeakerPreferences> Speakers { get; set; } = new();
    public List<SpeakerLink> Links { get; set; } = [];
    public string? ActiveLinkId { get; set; }
    public bool LinksInitialized { get; set; }
    public List<MusicTrack> Library { get; set; } = [];
    public Dictionary<string, List<string>> Playlists { get; set; } = new();
    public bool PlaylistsInitialized { get; set; }
    public bool PlaylistsExpanded { get; set; } = true;
    public Dictionary<string, string> ImportedPlaylistFolders { get; set; } = new();
    public HashSet<string> LikedTracks { get; set; } = [];
    public List<RadioStation> RadioStations { get; set; } = [];
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public SettingsStore(string path) => _path = path;
    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new(); }
        catch (JsonException)
        {
            File.Copy(_path, _path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false);
            return new();
        }
    }
    public async Task SaveAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, Options);
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".writing";
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }
}

public static class PcmEncoding
{
    public static byte[] ToInt16(ReadOnlySpan<float> samples)
    {
        var result = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = float.IsNaN(samples[i]) ? 0 : Math.Clamp(samples[i], -1, 1);
            var value = sample <= -1 ? short.MinValue : (short)Math.Round(sample * 32767);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), value);
        }
        return result;
    }
}
