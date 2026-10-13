using Makaretu.Dns;
using Musync.Core;
using System.Collections.Concurrent;

namespace Musync.Network;

public sealed class NetworkDiscovery
{
    public async Task<IReadOnlyList<SpeakerDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var hosts = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var services = new ConcurrentDictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);
        var text = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        using var multicast = new MulticastService();
        using var discovery = new ServiceDiscovery(multicast);
        multicast.AnswerReceived += (_, args) =>
        {
            foreach (var record in args.Message.Answers.Concat(args.Message.AdditionalRecords))
            {
                var name = record.Name.ToString();
                if (record is ARecord address) hosts[name] = address.Address.ToString();
                if (record is SRVRecord service) services[name] = (service.Target.ToString(), service.Port);
                if (record is TXTRecord txt)
                {
                    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in txt.Strings)
                    {
                        var parts = entry.Split('=', 2);
                        properties[parts[0]] = parts.Length == 2 ? parts[1] : "";
                    }
                    text[name] = properties;
                }
            }
        };
        discovery.ServiceInstanceDiscovered += (_, args) =>
        {
            multicast.SendQuery(args.ServiceInstanceName, type: DnsType.SRV);
            multicast.SendQuery(args.ServiceInstanceName, type: DnsType.TXT);
        };
        multicast.Start();
        // Interfaces attach asynchronously. Repeat discovery after sockets have
        // settled, and recover records omitted from compact/cached replies.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            discovery.QueryServiceInstances("_raop._tcp");
            discovery.QueryServiceInstances("_airplay._tcp");
            discovery.QueryServiceInstances("_googlecast._tcp");
            await Task.Delay(1700, cancellationToken);
        }
        var devices = new List<SpeakerDevice>();
        foreach (var (name, service) in services)
        {
            var cast = name.Contains("._googlecast._tcp.", StringComparison.OrdinalIgnoreCase);
            var modern = name.Contains("._airplay._tcp.", StringComparison.OrdinalIgnoreCase);
            if (!cast && !modern && !name.Contains("._raop._tcp.", StringComparison.OrdinalIgnoreCase)) continue;
            var host = hosts.GetValueOrDefault(service.Host) ?? service.Host.TrimEnd('.');
            var properties = text.GetValueOrDefault(name) ?? new();
            var displayName = properties.GetValueOrDefault("fn") ?? name.Split("._")[0];
            if (!cast && displayName.Contains('@')) displayName = displayName.Split('@', 2)[1];
            displayName = System.Text.RegularExpressions.Regex.Replace(displayName, @"\\(\d+)", match =>
                int.TryParse(match.Groups[1].Value, out var code) && code is > 0 and <= 0x10ffff
                    ? char.ConvertFromUtf32(code) : match.Value);
            string? unsupported = null;
            if (modern)
            {
                properties["ap2"] = "true";
                properties["model"] = properties.GetValueOrDefault("model") ?? properties.GetValueOrDefault("am") ?? "";
                properties["features"] = properties.GetValueOrDefault("features") ?? properties.GetValueOrDefault("ft") ?? "0";
                properties["flags"] = properties.GetValueOrDefault("flags") ?? properties.GetValueOrDefault("sf") ?? "0";
            }
            if (!cast && !modern)
            {
                if (properties.GetValueOrDefault("pw") is "true" or "1") unsupported = "AirPlayAuthentication";
                var encryption = properties.GetValueOrDefault("et");
                if (encryption is not null && !encryption.Split(',').Contains("0")) unsupported = "AirPlayEncryption";
            }
            var hardwareId = properties.GetValueOrDefault("deviceid") ?? name.Split('@')[0];
            var id = cast ? "cast:" + (properties.GetValueOrDefault("id") ?? name)
                : "airplay:" + hardwareId.Replace(":", "").ToUpperInvariant();
            // AirPlay and RAOP advertisements from one receiver share an address.
            // Prefer the encrypted modern lane when both are advertised.
            devices.Add(new(id, displayName, cast ? SpeakerTransport.Chromecast : SpeakerTransport.AirPlay,
                host, service.Port, properties, unsupported));
        }
        return devices.GroupBy(d => (d.Transport, d.Host)).Select(g =>
            g.OrderByDescending(d => d.Properties?.GetValueOrDefault("ap2") == "true").First()).ToArray();
    }
}
