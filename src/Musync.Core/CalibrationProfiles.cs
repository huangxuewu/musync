namespace Musync.Core;

public sealed class CalibrationProfile
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, CalibratedSpeaker> Speakers { get; set; } = new();
}
public sealed class CalibratedSpeaker
{
    public SpeakerTransport Transport { get; set; }
    public string Protocol { get; set; } = "";
    public int DelayMs { get; set; }
}
public static class CalibrationProfiles
{
    public static bool IsPath(string path) => path is "file" or "stream" or "capture";
    public static string Protocol(SpeakerDevice device) => device.Transport == SpeakerTransport.AirPlay
        ? device.Properties?.GetValueOrDefault("ap2") == "true" ? "airplay2" : "raop" : device.Transport.ToString();
    public static CalibrationProfile? Active(SpeakerLink link, string path, IEnumerable<SpeakerDevice> discovered)
    {
        if (!IsPath(path) || link.Calibrations is null || !link.Calibrations.TryGetValue(path, out var profile) || profile is null || profile.Version != 1
            || profile.Speakers is null || profile.Speakers.Count != link.Speakers.Count || link.Speakers.Any(m => !profile.Speakers.TryGetValue(m.Key, out var s)
                || s is null || s.Transport != m.Value.Transport || s.DelayMs is < 0 or > 10000)) return null;
        foreach (var device in discovered.Where(d => profile.Speakers.ContainsKey(d.Id)))
            if (profile.Speakers[device.Id].Protocol != Protocol(device)) return null;
        return profile;
    }
    public static Dictionary<string, CalibrationProfile> Copy(Dictionary<string, CalibrationProfile> profiles) => (profiles ?? new()).Where(p => p.Value?.Speakers is not null && p.Value.Speakers.Values.All(s => s is not null)).ToDictionary(p => p.Key,
        p => new CalibrationProfile { Version = p.Value.Version, CreatedAt = p.Value.CreatedAt, Speakers = p.Value.Speakers.ToDictionary(s => s.Key,
            s => new CalibratedSpeaker { Transport = s.Value.Transport, Protocol = s.Value.Protocol, DelayMs = s.Value.DelayMs }) });
}
