using Makaretu.Dns;
using Musync.Core;
using System.Collections.Concurrent;

namespace Musync.Network;

public sealed class NetworkDiscovery
{
    public async Task<IReadOnlyList<SpeakerDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var records = new NetworkDiscoveryRecords();
        using var multicast = new MulticastService();
        using var discovery = new ServiceDiscovery(multicast);
        void QueryMissingAddresses()
        {
            foreach (var host in records.TakeAddressQueries())
                multicast.SendQuery(host, type: DnsType.A);
        }
        multicast.AnswerReceived += (_, args) =>
        {
            records.Observe(args.Message);
            // Some receivers send SRV/TXT without address records. Ask the SRV
            // target directly instead of relying on Windows to resolve .local.
            QueryMissingAddresses();
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
            // One address query per host in each bounded discovery round.
            // Retry a lost reply without querying on every unrelated packet.
            records.BeginQueryRound();
            QueryMissingAddresses();
            discovery.QueryServiceInstances("_raop._tcp");
            discovery.QueryServiceInstances("_airplay._tcp");
            discovery.QueryServiceInstances("_googlecast._tcp");
            await Task.Delay(1700, cancellationToken);
        }
        return records.Devices();
    }

    internal static string DecodeDisplayName(string displayName) =>
        System.Text.RegularExpressions.Regex.Replace(displayName, @"\\(\d+)", match =>
            // Surrogate code points are within the numeric range but are not
            // Unicode scalars. Preserve them instead of failing the whole scan.
            int.TryParse(match.Groups[1].Value, out var code) && code > 0 && System.Text.Rune.IsValid(code)
                ? char.ConvertFromUtf32(code) : match.Value);
}

// Records can arrive in separate answers or in the additional section. Keep
// their assembly independent of the multicast sockets for deterministic checks.
internal sealed class NetworkDiscoveryRecords
{
    private readonly ConcurrentDictionary<string, string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Host, int Port)> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _text = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queriedHosts = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(Message message)
    {
        foreach (var record in message.Answers.Concat(message.AdditionalRecords))
        {
            var name = record.Name.ToString();
            if (record is ARecord address) _hosts[name] = address.Address.ToString();
            if (record is SRVRecord service) _services[name] = (service.Target.ToString(), service.Port);
            if (record is TXTRecord txt)
            {
                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in txt.Strings)
                {
                    var parts = entry.Split('=', 2);
                    properties[parts[0]] = parts.Length == 2 ? parts[1] : "";
                }
                _text[name] = properties;
            }
        }
    }

    public void BeginQueryRound() => _queriedHosts.Clear();

    public IReadOnlyList<string> TakeAddressQueries() => _services
        .Where(service => IsAudioService(service.Key))
        .Select(service => service.Value.Host)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(host => !_hosts.ContainsKey(host) && _queriedHosts.TryAdd(host, 0)).ToArray();

    private static bool IsAudioService(string name) =>
        name.Contains("._googlecast._tcp.", StringComparison.OrdinalIgnoreCase)
        || name.Contains("._airplay._tcp.", StringComparison.OrdinalIgnoreCase)
        || name.Contains("._raop._tcp.", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SpeakerDevice> Devices()
    {
        var devices = new List<SpeakerDevice>();
        foreach (var (name, service) in _services)
        {
            var cast = name.Contains("._googlecast._tcp.", StringComparison.OrdinalIgnoreCase);
            var modern = name.Contains("._airplay._tcp.", StringComparison.OrdinalIgnoreCase);
            if (!cast && !modern && !name.Contains("._raop._tcp.", StringComparison.OrdinalIgnoreCase)) continue;
            var host = _hosts.GetValueOrDefault(service.Host) ?? service.Host.TrimEnd('.');
            var properties = new Dictionary<string, string>(_text.GetValueOrDefault(name) ?? new(), StringComparer.OrdinalIgnoreCase);
            var displayName = properties.GetValueOrDefault("fn") ?? name.Split("._")[0];
            if (!cast && displayName.Contains('@')) displayName = displayName.Split('@', 2)[1];
            displayName = NetworkDiscovery.DecodeDisplayName(displayName);
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
