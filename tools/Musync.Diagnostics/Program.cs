using Musync.Core;
using Musync.Network;
using Musync.Platform.Windows;
using Sharpcaster;
using Sharpcaster.Models;
using System.Text.Json;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var devices = await new NetworkDiscovery().DiscoverAsync(lifetime.Token);
var report = new List<object>();
foreach (var device in devices)
{
    Console.WriteLine($"{device.Name} | {device.Transport} | {device.Properties?.GetValueOrDefault("model") ?? device.Properties?.GetValueOrDefault("md")} | modern={device.Properties?.GetValueOrDefault("ap2")}");
}
if (args.Contains("--probe-onkyo"))
{
    // Read receiver status without launching media or changing its volume.
    var target = devices.FirstOrDefault(d => d.Transport == SpeakerTransport.Chromecast && d.Name.Contains("Onkyo", StringComparison.OrdinalIgnoreCase));
    if (target is not null)
    {
        var client = new ChromecastClient();
        try
        {
            await client.ConnectChromecast(new ChromecastReceiver { Name = target.Name, DeviceUri = new Uri("http://" + target.Host), Port = target.Port }).WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            var status = await client.ReceiverChannel.GetChromecastStatusAsync().WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            report.Add(new { target.Name, target.Transport, Connected = status is not null, Level = status?.Volume?.Level });
        }
        catch (Exception ex) { report.Add(new { target.Name, Error = ex.Message }); }
        finally { await client.DisconnectAsync(); await client.Dispose(); }
    }
}
var output = Path.GetFullPath(args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ?? "network-probe.json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Discovered = devices, Probes = report }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Saved: " + output);
