using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class ReceiverRouteTests
{
    [Fact]
    public void RoomGroupKeepsOneRoutePerHostAndEveryIndependentEndpoint()
    {
        var devices = new[]
        {
            new SpeakerDevice("airplay", "Onkyo", SpeakerTransport.AirPlay, "192.0.2.1", 7000),
            new("cast", "Onkyo", SpeakerTransport.Chromecast, "192.0.2.1", 8009),
            new("homepod", "HomePod", SpeakerTransport.AirPlay, "192.0.2.2", 7000),
            new("windows", "Speakers", SpeakerTransport.Windows),
            new("bluetooth", "Headphones", SpeakerTransport.Bluetooth)
        };
        var routes = ReceiverRoutes.Choose(devices.Select(d => (d, new SpeakerPreferences())));
        Assert.Equal(4, routes.Count); Assert.Contains(routes, r => r.Device.Id == "airplay");
        Assert.DoesNotContain(routes, r => r.Device.Id == "cast");
        Assert.Contains(routes, r => r.Device.Id == "windows"); Assert.Contains(routes, r => r.Device.Id == "bluetooth");
    }
    [Fact]
    public void PreviouslySelectedCastRouteTakesPrecedence()
    {
        var airplay = new SpeakerDevice("airplay", "Onkyo", SpeakerTransport.AirPlay, "192.0.2.1", 7000);
        var cast = new SpeakerDevice("cast", "Onkyo", SpeakerTransport.Chromecast, "192.0.2.1", 8009);
        var routes = ReceiverRoutes.Choose([(airplay, new()), (cast, new() { Selected = true })]);
        Assert.Equal("cast", Assert.Single(routes).Device.Id);
    }
    [Fact]
    public void UnsupportedAirPlayDoesNotHideSupportedCastRoute()
    {
        var airplay = new SpeakerDevice("airplay", "Receiver", SpeakerTransport.AirPlay, "192.0.2.1", 7000, UnsupportedReason: "AirPlayAuthentication");
        var cast = new SpeakerDevice("cast", "Receiver", SpeakerTransport.Chromecast, "192.0.2.1", 8009);
        Assert.Equal("cast", Assert.Single(ReceiverRoutes.Choose([(airplay, new() { Selected = true }), (cast, new())])).Device.Id);
    }
    [Fact]
    public void EqualNamesDoNotMergeDifferentHostsOrWindowsEndpoints()
    {
        var devices = new[]
        {
            new SpeakerDevice("a", "Speakers", SpeakerTransport.AirPlay, "192.0.2.1", 7000),
            new("b", "Speakers", SpeakerTransport.Chromecast, "192.0.2.2", 8009),
            new("c", "Speakers", SpeakerTransport.Windows),
            new("d", "Speakers", SpeakerTransport.Windows)
        };
        Assert.Equal(4, ReceiverRoutes.Choose(devices.Select(d => (d, new SpeakerPreferences()))).Count);
        Assert.False(ReceiverRoutes.SameReceiver(devices[0], devices[1]));
        Assert.False(ReceiverRoutes.SameReceiver(devices[2], devices[3]));
    }
    [Fact]
    public void HostNormalizationRecognizesMappedAddressesAndDnsCase()
    {
        Assert.True(ReceiverRoutes.SameReceiver(new("a", "A", SpeakerTransport.AirPlay, "::ffff:192.0.2.1"),
            new("b", "B", SpeakerTransport.Chromecast, "192.0.2.1")));
        Assert.True(ReceiverRoutes.SameReceiver(new("a", "A", SpeakerTransport.AirPlay, "Receiver.LOCAL."),
            new("b", "B", SpeakerTransport.Chromecast, "receiver.local")));
        Assert.False(ReceiverRoutes.SameReceiver(new("a", "A", SpeakerTransport.AirPlay),
            new("b", "B", SpeakerTransport.Chromecast)));
    }
}
