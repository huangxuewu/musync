namespace Musync.Core;

public sealed class SpeakerLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public double MasterVolume { get; set; } = 65;
    public Dictionary<string, LinkedSpeaker> Speakers { get; set; } = new();
}

public sealed class LinkedSpeaker
{
    public string Name { get; set; } = "";
    public SpeakerTransport Transport { get; set; }
    public SpeakerPreferences Preferences { get; set; } = new();
}

public static class SpeakerLinks
{
    public static SpeakerPreferences Copy(SpeakerPreferences source) => new()
    {
        Selected = source.Selected, Volume = source.Volume, DelayMs = source.DelayMs,
        Muted = source.Muted, Room = source.Room
    };

    public static SpeakerLink Draft(SpeakerLink? source) => source is null ? new() : new()
    {
        Id = source.Id, Name = source.Name, MasterVolume = source.MasterVolume,
        Speakers = source.Speakers.ToDictionary(s => s.Key, s => new LinkedSpeaker
        {
            Name = s.Value.Name, Transport = s.Value.Transport,
            Preferences = Copy(s.Value.Preferences)
        })
    };

    public static string? ValidationError(SpeakerLink link, IEnumerable<SpeakerLink> existing)
    {
        if (string.IsNullOrWhiteSpace(link.Name) || link.Name.Trim().Length > 48) return "LinkNameRequired";
        if (existing.Any(l => l.Id != link.Id && string.Equals(l.Name.Trim(), link.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "LinkNameExists";
        return link.Speakers.Count == 0 ? "LinkDevicesRequired" : null;
    }

    public static void MigrateLegacy(AppSettings settings, string name)
    {
        if (settings.LinksInitialized) return;
        settings.LinksInitialized = true;
        if (settings.Links.Count > 0) { settings.ActiveLinkId ??= settings.Links[0].Id; return; }
        var selected = settings.Speakers.Where(s => s.Value.Selected).ToArray();
        if (selected.Length == 0) return;
        var link = new SpeakerLink { Name = name, MasterVolume = settings.MasterVolume };
        foreach (var (id, preferences) in selected)
            link.Speakers[id] = new() { Name = id, Preferences = Copy(preferences) };
        settings.Links.Add(link); settings.ActiveLinkId = link.Id;
    }

    public static IReadOnlyList<(SpeakerDevice Device, SpeakerPreferences Preferences)> Resolve(
        SpeakerLink link, IEnumerable<SpeakerDevice> discovered)
    {
        // Never connect to stale saved addresses. Only current discovery can supply a route.
        var routes = discovered.Where(d => d.UnsupportedReason is null && link.Speakers.ContainsKey(d.Id))
            .Select(d => (Device: d, Preferences: Copy(link.Speakers[d.Id].Preferences))).ToArray();
        foreach (var route in routes) route.Preferences.Selected = true;
        return ReceiverRoutes.Choose(routes);
    }
}
