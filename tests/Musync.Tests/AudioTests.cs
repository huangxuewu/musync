using Musync.Core;
using Musync.Network;
using Musync.Platform.Windows;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using Xunit;

[assembly: SupportedOSPlatform("windows")]
namespace Musync.Tests;

public sealed class AudioTests
{
    [Fact]
    public void PausedHubRetainsUnreadMusicAndDropsLateSourceFrames()
    {
        var hub = new AudioHub(); var output = new AudioRingBuffer(20);
        hub.SetOutputs([output]); hub.Publish([.1f, .1f]);
        hub.SetPaused(true); hub.Publish([.2f, .2f]);
        Assert.Equal(1, output.BufferedFrames);
        var samples = new float[2]; Assert.Equal(0, output.Read(samples)); Assert.Equal(new float[2], samples);
        hub.SetPaused(false); hub.Publish([.3f, .3f]);
        output.Read(samples); Assert.Equal(new[] { .1f, .1f }, samples); output.Read(samples);
        Assert.Equal(new[] { .3f, .3f }, samples);
    }
    [Fact]
    public void PausedHubDoesNotFeedNewlyAttachedOutputs()
    {
        var hub = new AudioHub(); var output = new AudioRingBuffer(20);
        hub.SetPaused(true); hub.SetOutputs([output]); hub.Publish([.1f, .1f]);
        Assert.Equal(0, output.BufferedFrames);
        hub.SetPaused(false); hub.Publish([.2f, .2f]);
        Assert.Equal(1, output.BufferedFrames);
    }
    [Fact]
    public void GainAndMuteDoNotChangeAnotherOutput()
    {
        var hub = new AudioHub(); var left = new AudioRingBuffer(20); var right = new AudioRingBuffer(20);
        hub.SetOutputs([left, right]); hub.Publish([1, -1, .5f, -.5f]);
        var quiet = new float[4]; var loud = new float[4];
        Assert.Equal(4, left.Read(quiet, 0)); Assert.Equal(4, right.Read(loud, .5f));
        Assert.Equal(new float[4], quiet); Assert.Equal(new[] { .5f, -.5f, .25f, -.25f }, loud);
    }
    [Fact]
    public void DelayPrimesAndUnderflowRebuffers()
    {
        var buffer = new AudioRingBuffer(1000); buffer.SetDelay(10, 0); var output = new float[100];
        buffer.Write(new float[800]); Assert.Equal(0, buffer.Read(output));
        buffer.Write(Enumerable.Repeat(.5f, 100).ToArray()); Assert.Equal(100, buffer.Read(output));
        buffer.Read(new float[1000]); Assert.Equal(1, buffer.Underruns);
        buffer.Write(new float[200]); Assert.Equal(0, buffer.Read(output));
    }
    [Fact]
    public void OverflowKeepsLatestWholeFrames()
    {
        var buffer = new AudioRingBuffer(4); buffer.Write(Enumerable.Range(1, 12).Select(n => (float)n).ToArray());
        var output = new float[8]; Assert.Equal(8, buffer.Read(output));
        Assert.Equal(Enumerable.Range(5, 8).Select(n => (float)n), output); Assert.Equal(2, buffer.DroppedFrames);
    }
    [Fact]
    public void ReducingDelayDropsOnlyItsDelta()
    {
        var buffer = new AudioRingBuffer(2000); buffer.SetDelay(20, 0);
        buffer.Write(new float[3000]); buffer.Read(new float[100]); var before = buffer.BufferedFrames;
        buffer.SetDelay(10, 0); Assert.Equal(before - 441, buffer.BufferedFrames);
    }
    [Fact]
    public void PcmClipsWithoutWrappingAndPreservesPolarity()
    {
        var bytes = PcmEncoding.ToInt16([-2, 2, -.5f, .5f]);
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(bytes));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2)));
        Assert.True(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(4)) < 0);
        var invalid = PcmEncoding.ToInt16([float.NaN, float.PositiveInfinity]);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(invalid));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(invalid.AsSpan(2)));
    }
    [Fact]
    public void PkceMatchesRfc7636Vector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            SpotifyClient.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }
    [Fact]
    public async Task PreferencesRoundTripWithoutTokenStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new() { Language = "ja", MasterVolume = 72, Speakers = new() { ["a"] = new() { Volume = 29, DelayMs = 215, Muted = true, Room = "living" } } });
            var restored = store.Load(); Assert.Equal("ja", restored.Language); Assert.Equal(215, restored.Speakers["a"].DelayMs);
            Assert.True(restored.Speakers["a"].Muted); Assert.DoesNotContain("AccessToken", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
    }
    [Fact]
    public void RaopRtpUsesNetworkOrderAndSequenceWrap()
    {
        var packet = RaopProtocol.AudioPacket([1, 2], ushort.MaxValue, 0x12345678, 0x87654321, true);
        Assert.Equal(0xe0, packet[1]); Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4))); Assert.Equal(new byte[] {1, 2}, packet[12..]);
        Assert.Equal(0x60, RaopProtocol.AudioPacket([], 0, 0, 0, false)[1]);
    }
    [Fact]
    public void SyncAnchorsHaveExpectedFields()
    {
        var packet = RaopProtocol.SyncPacket(44100, 11025, 0x1122334455667788, true);
        Assert.Equal(20, packet.Length); Assert.Equal(0xd4, packet[1]);
        Assert.Equal(33075u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(0x1122334455667788ul, BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8)));
    }
    [Fact]
    public void AlacEncodesWholeStereoPackets()
    {
        Assert.Equal(1412, RaopProtocol.EncodeAlac(new byte[1408]).Length);
        Assert.Throws<ArgumentException>(() => RaopProtocol.EncodeAlac(new byte[4]));
        Assert.Throws<ArgumentException>(() => new AudioRingBuffer().Write(new float[3]));
    }
    [Fact]
    public void HlsKeepsBoundedWindowAndChronologicalSequence()
    {
        var stream = new HlsStream(); for (var i = 0; i < 12; i++) stream.Add([(byte)i]);
        Assert.Equal(8, stream.SegmentCount); Assert.Equal(new byte[] { 3 }, stream.Get(3)); Assert.Equal(new byte[] { 4 }, stream.Get(4));
        var playlist = stream.Playlist(); Assert.Contains("#EXT-X-MEDIA-SEQUENCE:4", playlist);
        Assert.DoesNotContain("\n3.mp3", playlist); Assert.Contains("\n11.mp3", playlist);
        for (var i = 12; i < 68; i++) stream.Add([(byte)i]);
        Assert.Null(stream.Get(3)); Assert.NotNull(stream.Get(4));
    }
    [Fact]
    public void HlsPackedAudioHasTimestampAndStripsPriorId3()
    {
        var mp3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0, 0xff, 0xfb };
        var packet = CastOutput.PackedAudio(mp3, 180000);
        Assert.Equal("ID3", System.Text.Encoding.ASCII.GetString(packet, 0, 3));
        Assert.Equal(180000ul, BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(packet.Length - 10, 8)));
        Assert.Equal(new byte[] { 0xff, 0xfb }, packet[^2..]);
    }
    [Fact]
    public void WindowsMediaFoundationEncodesRealAudio()
    {
        var position = 0;
        using var output = new MemoryStream();
        new Mp3StreamEncoder().EncodeMp3(samples =>
        {
            if (position >= AudioFormat.SampleRate * 2) return 0;
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * (position + i / 2) / AudioFormat.SampleRate));
            position += samples.Length / 2; return samples.Length;
        }, bytes => output.Write(bytes.Span), CancellationToken.None);
        var encoded = output.ToArray();
        Assert.True(encoded.Length > 20000);
        Assert.True(encoded.AsSpan().IndexOf(new byte[] { 0xff, 0xfb }) >= 0);
    }
    [Fact]
    public async Task HlsServerRequiresStreamTokenAndRevokesOnStop()
    {
        await using var server = new LocalStreamServer(); await server.StartAsync(CancellationToken.None);
        var stream = server.CreateStream(); stream.Stream.Add([1, 2, 3]);
        using var http = new HttpClient(); var root = $"http://127.0.0.1:{server.Port}/s/";
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(root + "invalid/index.m3u8")).StatusCode);
        var manifest = await http.GetStringAsync(root + stream.Key + "/index.m3u8");
        Assert.Contains("0.mp3", manifest);
        Assert.DoesNotContain("#EXT-X-PROGRAM-DATE-TIME", manifest);
        Assert.Equal(new byte[] { 1, 2, 3 }, await http.GetByteArrayAsync(root + stream.Key + "/0.mp3"));
        server.RemoveStream(stream.Key);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(root + stream.Key + "/0.mp3")).StatusCode);
    }
}
