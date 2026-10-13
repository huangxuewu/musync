using Musync.Core;
using Musync.Network;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Musync.Tests;

public sealed class RaopIntegrationTests
{
    [Fact]
    public async Task SessionNegotiationSendsAudioAndTearsDown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var audio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var control = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timing = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var methods = new List<string>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            while (!timeout.IsCancellationRequested)
            {
                var request = await reader.ReadLineAsync(timeout.Token);
                if (request is null) break;
                var method = request.Split(' ')[0]; methods.Add(method);
                var headers = new Dictionary<string, string>();
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                {
                    var parts = line.Split(':', 2); headers[parts[0]] = parts[1].Trim();
                }
                var length = int.Parse(headers.GetValueOrDefault("Content-Length", "0"));
                var body = new char[length]; if (length > 0) await reader.ReadBlockAsync(body, timeout.Token);
                var extra = method == "SETUP" ? $"Session: Musync-test\r\nTransport: RTP/AVP/UDP;server_port={((IPEndPoint)audio.Client.LocalEndPoint!).Port};control_port={((IPEndPoint)control.Client.LocalEndPoint!).Port};timing_port={((IPEndPoint)timing.Client.LocalEndPoint!).Port}\r\n" : "";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\nCSeq: {headers["CSeq"]}\r\n{extra}Content-Length: 0\r\n\r\n"), timeout.Token);
                if (method == "TEARDOWN") break;
            }
        }, timeout.Token);
        try
        {
            await using var output = new RaopOutput(new("test", "Test receiver", SpeakerTransport.AirPlay,
                "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port));
            output.Buffer.Write(Enumerable.Repeat(.2f, 40000).ToArray());
            await output.StartAsync(timeout.Token);
            await Task.Delay(50, timeout.Token); Assert.Null(output.Error);
            var packet = await audio.ReceiveAsync(timeout.Token);
            Assert.Equal(1424, packet.Buffer.Length); Assert.Equal(0x80, packet.Buffer[0]);
            await Task.Delay(50, timeout.Token); Assert.Null(output.Error);
            var sync = await control.ReceiveAsync(timeout.Token); Assert.Equal(0xd4, sync.Buffer[1]);
            await output.StopAsync(); await server;
            Assert.Equal(new[] { "OPTIONS", "ANNOUNCE", "SETUP", "RECORD", "TEARDOWN" }, methods);
            Assert.Null(output.Error);
        }
        finally { listener.Stop(); timeout.Cancel(); }
    }
}
