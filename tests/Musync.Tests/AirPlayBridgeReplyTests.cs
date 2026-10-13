using Musync.Network;
using System.Text.Json;
using Xunit;

namespace Musync.Tests;

public sealed class AirPlayBridgeReplyTests
{
    [Theory]
    [InlineData("homepod")]
    [InlineData(null)]
    public async Task ExpiredRequestRepliesCannotBecomeReceiverOrGlobalErrors(string? id)
    {
        await using var bridge = new AirPlayBridge("unused", "unused");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            request = "expired-request", id, error = "Old volume request rejected"
        }));
        bridge.ObserveReply(json.RootElement);
        Assert.Null(bridge.Error("homepod"));
        Assert.Null(bridge.Error("another-receiver"));
    }

    [Fact]
    public async Task UnsolicitedDisconnectRemainsVisibleAfterALateRequestError()
    {
        await using var bridge = new AirPlayBridge("unused", "unused");
        using var disconnect = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            request = (string?)null, id = "homepod", error = "Control connection dropped"
        }));
        bridge.ObserveReply(disconnect.RootElement);
        using var late = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            request = "expired-request", id = "homepod", error = "Old volume request rejected"
        }));
        bridge.ObserveReply(late.RootElement);
        Assert.Equal("Control connection dropped", bridge.Error("homepod"));
        Assert.Null(bridge.Error("another-receiver"));
    }
}
