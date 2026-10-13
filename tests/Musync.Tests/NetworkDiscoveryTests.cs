using Musync.Network;
using Xunit;

namespace Musync.Tests;

public sealed class NetworkDiscoveryTests
{
    [Theory]
    [InlineData(@"Room \55296")]
    [InlineData(@"Room \56320")]
    [InlineData(@"Room \57343")]
    [InlineData(@"Room \55357\56842")]
    public void InvalidCharacterEscapesCannotInterruptOtherReceiverNames(string invalidName)
    {
        // These strings can arrive as TXT friendly names. Keep an invalid
        // escape visible rather than abandoning discovery for every receiver.
        var names = new[] { @"Living \23460", invalidName, @"Studio \128522" };
        var decoded = names.Select(NetworkDiscovery.DecodeDisplayName).ToArray();
        Assert.Equal(new[] { "Living 室", invalidName, "Studio 😊" }, decoded);
    }

    [Theory]
    [InlineData("客厅のスピーカー", "客厅のスピーカー")]
    [InlineData(@"Room \0\1114112\999999999999999999", @"Room \0\1114112\999999999999999999")]
    [InlineData(@"Kitchen\32Speaker", "Kitchen Speaker")]
    public void FriendlyNamesRetainUnicodeAndUnrecognizedEscapes(string name, string expected) =>
        Assert.Equal(expected, NetworkDiscovery.DecodeDisplayName(name));
}
