using Musync.Network;
using System.Text.Json;
using Xunit;

namespace Musync.Tests;

public sealed class AirPlayBridgeReplyTests
{
    [Theory]
    [InlineData("homepod")]
    [InlineData(null)]
    public void ExpiredRequestRepliesCannotBecomeReceiverOrGlobalErrors(string? id)
    {
        var bridge = new AirPlayBridgeFeedback();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            request = "expired-request", id, error = "Old volume request rejected"
        }));
        bridge.ObserveReply(json.RootElement);
        Assert.Null(bridge.Error("homepod"));
        Assert.Null(bridge.Error("another-receiver"));
    }

    [Fact]
    public void UnsolicitedDisconnectRemainsVisibleAfterALateRequestError()
    {
        var bridge = new AirPlayBridgeFeedback();
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

    [Fact]
    public async Task AStoppedProcessOnlyFailsItsOwnRequestsAndRetainsItsReceiverFailure()
    {
        var old = new AirPlayBridgeFeedback();
        var replacement = new AirPlayBridgeFeedback();
        var oldReply = old.RegisterRequest("old-request");
        var newReply = replacement.RegisterRequest("new-request");
        old.Stop();
        await Assert.ThrowsAsync<IOException>(() => oldReply);
        Assert.False(newReply.IsCompleted);
        old.ReportError("*", "Obsolete helper diagnostic");
        Assert.Equal("AirPlay bridge stopped.", old.Error("old-receiver"));
        Assert.Null(replacement.Error("new-receiver"));
        using var reply = JsonDocument.Parse("{\"request\":\"new-request\",\"ok\":true}");
        replacement.ObserveReply(reply.RootElement);
        await newReply;
    }
}
