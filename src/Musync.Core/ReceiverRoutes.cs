using System.Net;

namespace Musync.Core;

public static class ReceiverRoutes
{
    private static string? NetworkHost(SpeakerDevice device)
    {
        if (device.Transport is not (SpeakerTransport.AirPlay or SpeakerTransport.Chromecast)
            || string.IsNullOrWhiteSpace(device.Host)) return null;
        var host = device.Host.Trim().TrimEnd('.');
        if (IPAddress.TryParse(host, out var address))
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        return host;
    }

    public static bool SameReceiver(SpeakerDevice left, SpeakerDevice right)
    {
        var host = NetworkHost(left);
        return host is not null && string.Equals(host, NetworkHost(right), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<(SpeakerDevice Device, SpeakerPreferences Preferences)> Choose(
        IEnumerable<(SpeakerDevice Device, SpeakerPreferences Preferences)> routes) => routes
        .Where(r => r.Device.UnsupportedReason is null)
        .GroupBy(r => NetworkHost(r.Device) is { } host ? "network:" + host : "endpoint:" + r.Device.Id,
            StringComparer.OrdinalIgnoreCase)
        // Preserve an existing choice. For a new group prefer AirPlay when both
        // transports are available; selecting the Cast row overrides that choice.
        .Select(group => group.OrderByDescending(r => r.Preferences.Selected)
            .ThenBy(r => r.Device.Transport == SpeakerTransport.AirPlay ? 0 : 1)
            .ThenBy(r => r.Device.Id, StringComparer.Ordinal).First()).ToArray();
}
