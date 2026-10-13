using Musync.Network;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Musync.Tests;

public sealed class RtspConnectionTests
{
    public static IEnumerable<object[]> InvalidReplies => new[]
    {
        "RTSP/1.0 2000 Invalid\r\nCSeq: 1\r\n\r\n",
        "RTSP/1.0 200OK\r\nCSeq: 1\r\n\r\n",
        "RTSP/1.0 200 OK\r\n\r\n",
        "RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n",
        "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: -1\r\n\r\n",
        "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 65537\r\n\r\n",
        "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: invalid\r\n\r\n",
        "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 0\r\ncontent-length: 0\r\n\r\n",
        "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 3\r\n\r\nxy",
        "RTSP/1.0 200 OK\r\nCSeq: 1\r\nLong: " + new string('x', 16384) + "\r\n\r\n"
    }.Select(reply => new object[] { reply });

    [Theory]
    [MemberData(nameof(InvalidReplies))]
    public async Task MalformedOrPartialReplyClosesConnection(string reply)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), deadline.Token);
        });
        try
        {
            using var connection = new RaopRtspConnection();
            await connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            await Assert.ThrowsAnyAsync<IOException>(() => connection.RequestAsync("OPTIONS", "*", "", null, deadline.Token));
            Assert.False(connection.CanRequest);
            await server;
        }
        finally { deadline.Cancel(); listener.Stop(); }
    }

    [Fact]
    public async Task ConcurrentRequestsRemainOrderedAndConsumeResponseBodies()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var sequences = new List<int>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            for (var request = 0; request < 2; request++)
            {
                Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
                var headers = new Dictionary<string, string>();
                while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
                {
                    var parts = line.Split(':', 2); headers[parts[0]] = parts[1].Trim();
                }
                var sequence = int.Parse(headers["CSeq"]); sequences.Add(sequence);
                Assert.Equal("test-session", headers["Session"]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\ncseq: {sequence}\r\nContent-Length: 2\r\n\r\nOK"), deadline.Token);
            }
        });
        try
        {
            using var connection = new RaopRtspConnection { Session = "test-session" };
            await connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            var first = connection.RequestAsync("GET_PARAMETER", "rtsp://test/session", "", null, deadline.Token);
            var second = connection.RequestAsync("OPTIONS", "*", "", null, deadline.Token);
            var replies = await Task.WhenAll(first, second);
            Assert.All(replies, r => Assert.Equal(200, r.Status));
            await server; Assert.Equal(new[] { 1, 2 }, sequences);
        }
        finally { deadline.Cancel(); listener.Stop(); }
    }
}
