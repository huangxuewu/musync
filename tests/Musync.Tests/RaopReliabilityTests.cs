using Musync.Core;
using Musync.Network;
using System.Buffers.Binary;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed class RaopReliabilityTests
{
    [Fact]
    public async Task PauseTearsDownAndResumeNegotiatesAFreshRtpTimeline()
    {
        await using var receiver = new FakeRaopReceiver(2);
        await using var output = Output(receiver);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        await receiver.Audio.ReceiveAsync(deadline.Token);
        output.Buffer.Write(Enumerable.Repeat(.25f, 40000).ToArray());
        await output.SetPausedAsync(true);
        var held = output.Buffer.BufferedFrames;
        Assert.True(held > 0); Assert.Equal(0, output.Buffer.Read(new float[704])); Assert.Equal(held, output.Buffer.BufferedFrames);
        Assert.Contains("TEARDOWN", receiver.Methods); Assert.Null(output.Error);
        while (receiver.Audio.Available > 0) await receiver.Audio.ReceiveAsync(deadline.Token);
        using (var silence = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await receiver.Audio.ReceiveAsync(silence.Token));
        await output.SetPausedAsync(false);
        var fresh = (await receiver.Audio.ReceiveAsync(deadline.Token)).Buffer;
        Assert.Equal(0xe0, fresh[1]); Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(fresh.AsSpan(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(fresh.AsSpan(4)));
        Assert.Equal(2, receiver.Methods.Count(m => m == "SETUP"));
        await output.StopAsync(); Assert.Equal(2, receiver.Methods.Count(m => m == "TEARDOWN"));
        Assert.Equal(0, output.Buffer.BufferedFrames);
    }
    private static RaopOutput Output(FakeRaopReceiver receiver) => new(new("test", "Loopback AirPlay", SpeakerTransport.AirPlay, "127.0.0.1", receiver.Port));
    [Theory]
    [InlineData(null, false)]
    [InlineData("OPTIONS, ANNOUNCE, SETUP, RECORD, TEARDOWN", false)]
    [InlineData(null, true)]
    public async Task KeepAliveRetainsSessionAndFallsBackToSupportedOptions(string? advertised, bool reject)
    {
        await using var receiver = new FakeRaopReceiver { PublicMethods = advertised, RejectGetParameter = reject };
        await using var output = Output(receiver);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        await receiver.PingAccepted.Task.WaitAsync(deadline.Token);
        Assert.Null(output.Error); Assert.All(receiver.PingSessions, session => Assert.Equal("test-session", session));
        if (advertised is not null) Assert.DoesNotContain("GET_PARAMETER", receiver.Methods);
        else Assert.Contains("GET_PARAMETER", receiver.Methods);
        if (reject) Assert.Equal(2, receiver.Methods.Count(m => m == "OPTIONS"));
        await output.StopAsync(); Assert.Null(output.Error);
    }
    [Fact]
    public async Task DroppedControlConnectionReportsFailure()
    {
        await using var receiver = new FakeRaopReceiver { DropOnPing = true };
        await using var output = Output(receiver);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        await receiver.PingSeen.Task.WaitAsync(deadline.Token);
        while (output.Error is null) await Task.Delay(10, deadline.Token);
        Assert.Contains("connection closed", output.Error, StringComparison.OrdinalIgnoreCase);
        await output.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task StopCancelsAnUnansweredKeepAliveWithoutReusingPartialConnection()
    {
        await using var receiver = new FakeRaopReceiver { HoldPing = true };
        await using var output = Output(receiver);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        await receiver.PingSeen.Task.WaitAsync(deadline.Token);
        await output.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(output.Error); Assert.DoesNotContain("TEARDOWN", receiver.Methods);
    }
    [Fact]
    public async Task TimingRepliesPreserveOriginAndIgnoreWrongPacketType()
    {
        await using var receiver = new FakeRaopReceiver();
        await using var output = Output(receiver);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.ClientTimingPort);
        var request = new byte[32]; request[0] = 0x80; request[1] = 0xd5;
        await receiver.Timing.SendAsync(request, endpoint, deadline.Token);
        using (var silence = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await receiver.Timing.ReceiveAsync(silence.Token));
        request[1] = 0xd2; request[2] = 0x12; request[3] = 0x34;
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), 0x1234567887654321);
        await receiver.Timing.SendAsync(request, endpoint, deadline.Token);
        var reply = (await receiver.Timing.ReceiveAsync(deadline.Token)).Buffer;
        Assert.Equal(0xd3, reply[1]); Assert.Equal(request[2..4], reply[2..4]);
        Assert.Equal(0x1234567887654321ul, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8)));
        Assert.True(BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(24)) >= BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(16)));
    }
    [Fact]
    public async Task RetransmissionReturnsOriginalCachedPacketAndRequestSequence()
    {
        await using var receiver = new FakeRaopReceiver();
        await using var output = Output(receiver);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.StartAsync(deadline.Token);
        var original = (await receiver.Audio.ReceiveAsync(deadline.Token)).Buffer;
        var request = new byte[8]; request[0] = 0x80; request[1] = 0xd5; request[2] = 0x45; request[3] = 0x67;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(2)));
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), 1);
        await receiver.Control.SendAsync(request, new IPEndPoint(IPAddress.Loopback, receiver.ClientControlPort), deadline.Token);
        byte[] reply;
        do { reply = (await receiver.Control.ReceiveAsync(deadline.Token)).Buffer; } while (reply[1] != 0xd6);
        Assert.Equal(request[2..4], reply[2..4]); Assert.Equal(original, reply[4..]);
    }
}
