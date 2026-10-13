using Musync.Network;
using Musync.Core;
using Makaretu.Dns;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed class NetworkDiscoveryTests
{
    [Fact]
    public void SplitRepliesResolveReceiverAddressesWithBoundedRetries()
    {
        var records = new NetworkDiscoveryRecords();
        var service = "Onkyo._googlecast._tcp.local";
        var details = new Message();
        details.Answers.Add(new SRVRecord { Name = service, Target = "receiver.local", Port = 8009 });
        details.AdditionalRecords.Add(new TXTRecord { Name = service, Strings = ["fn=Onkyo", "id=receiver-id"] });
        records.Observe(details);
        var host = Assert.Single(records.TakeAddressQueries());
        Assert.Equal("receiver.local", host.TrimEnd('.'));
        Assert.Empty(records.TakeAddressQueries());

        // A repeated service reply must not cause an address-query storm.
        records.Observe(details);
        Assert.Empty(records.TakeAddressQueries());
        records.BeginQueryRound();
        Assert.Equal(host, Assert.Single(records.TakeAddressQueries()));

        var address = new Message();
        address.Answers.Add(new ARecord { Name = "RECEIVER.local", Address = IPAddress.Parse("192.0.2.44") });
        records.Observe(address);
        records.BeginQueryRound();
        Assert.Empty(records.TakeAddressQueries());
        var receiver = Assert.Single(records.Devices());
        Assert.Equal("cast:receiver-id", receiver.Id);
        Assert.Equal("Onkyo", receiver.Name);
        Assert.Equal("192.0.2.44", receiver.Host);
        Assert.Equal(8009, receiver.Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BundledAddressRecordsAvoidRedundantQueriesRegardlessOfRecordOrder(bool addressFirst)
    {
        var records = new NetworkDiscoveryRecords();
        var reply = new Message();
        var address = new ARecord { Name = "receiver.local", Address = IPAddress.Parse("192.0.2.45") };
        var service = new SRVRecord { Name = "Receiver._googlecast._tcp.local", Target = "receiver.local", Port = 8009 };
        if (addressFirst) { reply.Answers.Add(address); reply.AdditionalRecords.Add(service); }
        else { reply.Answers.Add(service); reply.AdditionalRecords.Add(address); }
        records.Observe(reply);
        Assert.Empty(records.TakeAddressQueries());
        Assert.Equal("192.0.2.45", Assert.Single(records.Devices()).Host);
    }

    [Fact]
    public void SharedAirPlayHostIsQueriedOnceWithoutResolvingUnrelatedLanServices()
    {
        var records = new NetworkDiscoveryRecords();
        var reply = new Message();
        reply.Answers.Add(new SRVRecord { Name = "HomePod._airplay._tcp.local", Target = "homepod.local", Port = 7000 });
        reply.Answers.Add(new SRVRecord { Name = "AABBCCDDEEFF@HomePod._raop._tcp.local", Target = "homepod.local", Port = 5000 });
        reply.Answers.Add(new SRVRecord { Name = "Printer._ipp._tcp.local", Target = "printer.local", Port = 631 });
        records.Observe(reply);
        Assert.Equal("homepod.local", Assert.Single(records.TakeAddressQueries()).TrimEnd('.'));
        var address = new Message();
        address.AdditionalRecords.Add(new ARecord { Name = "homepod.local", Address = IPAddress.Parse("192.0.2.46") });
        records.Observe(address);
        var receiver = Assert.Single(records.Devices());
        Assert.Equal(SpeakerTransport.AirPlay, receiver.Transport);
        Assert.Equal("192.0.2.46", receiver.Host);
        Assert.Equal(7000, receiver.Port);
        Assert.Equal("true", receiver.Properties!["ap2"]);
    }

    [Fact]
    public void UpdatedServiceTargetResolvesTheNewHostWithoutLosingReceiverIdentity()
    {
        var records = new NetworkDiscoveryRecords();
        var service = "Receiver._googlecast._tcp.local";
        var initial = new Message();
        initial.Answers.Add(new SRVRecord { Name = service, Target = "old.local", Port = 8009 });
        initial.AdditionalRecords.Add(new TXTRecord { Name = service, Strings = ["id=stable-id", "fn=Living room"] });
        initial.AdditionalRecords.Add(new ARecord { Name = "old.local", Address = IPAddress.Parse("192.0.2.47") });
        records.Observe(initial);
        Assert.Empty(records.TakeAddressQueries());
        var changed = new Message();
        changed.Answers.Add(new SRVRecord { Name = service, Target = "new.local", Port = 8009 });
        records.Observe(changed);
        Assert.Equal("new.local", Assert.Single(records.TakeAddressQueries()).TrimEnd('.'));
        var address = new Message();
        address.Answers.Add(new ARecord { Name = "new.local", Address = IPAddress.Parse("192.0.2.48") });
        records.Observe(address);
        var receiver = Assert.Single(records.Devices());
        Assert.Equal("cast:stable-id", receiver.Id);
        Assert.Equal("Living room", receiver.Name);
        Assert.Equal("192.0.2.48", receiver.Host);
    }

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
