using Musync.Core;
using Musync.Network;
using Musync.Platform.Windows;
using NAudio.Wave;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Musync.Tests;

public sealed partial class FileCastOutputTests
{
    private static string CreateCastTestMusic(string folder, string extension, int rate = 44100, int channels = 2)
    {
        var wave = Path.Combine(folder, "原始音乐.wav");
        using (var writer = new WaveFileWriter(wave, new WaveFormat(rate, 16, channels)))
        {
            var data = new byte[rate * channels * 2];
            for (var frame = 0; frame < rate; frame++)
                for (var channel = 0; channel < channels; channel++)
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((frame * channels + channel) * 2, 2),
                        (short)(6000 * Math.Sin(frame * Math.Tau * 440 / rate)));
            writer.Write(data, 0, data.Length);
        }
        if (extension == ".wav") return wave;
        var encoded = Path.Combine(folder, "保留原文件" + extension);
        using (var reader = new WaveFileReader(wave)) MediaFoundationEncoder.EncodeToMp3(reader, encoded, rate < 44100 ? 96000 : 192000);
        // Valid ID3v2.4 padding exercises the metadata prefix without changing audio.
        var bytes = File.ReadAllBytes(encoded); var tag = new byte[65536 + 10];
        tag[0] = (byte)'I'; tag[1] = (byte)'D'; tag[2] = (byte)'3'; tag[3] = 4; tag[7] = 4;
        File.WriteAllBytes(encoded, [.. tag, .. bytes]); return encoded;
    }
    [Theory]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(22050, 1)]
    public async Task OriginalMp3IsSharedWithoutReencodingAndNeverDeleted(int rate, int channels)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-direct-mp3-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var source = CreateCastTestMusic(folder, ".MP3", rate, channels); var original = File.ReadAllBytes(source);
            await using var preparer = new WindowsCastMediaPreparer(Path.Combine(folder, "cache"));
            var leases = await Task.WhenAll(preparer.PrepareAsync(source, default), preparer.PrepareAsync(source, default));
            try
            {
                Assert.Equal(source, leases[0].Path); Assert.Equal(source, leases[1].Path); Assert.InRange(leases[0].Duration, .9, 1.2);
                Assert.Empty(Directory.GetFiles(Path.Combine(folder, "cache"))); Assert.Equal(original, File.ReadAllBytes(source));
                Assert.Throws<IOException>(() => File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose());
                await leases[0].DisposeAsync(); await leases[0].DisposeAsync();
                Assert.Throws<IOException>(() => File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose());
                await leases[1].DisposeAsync(); Assert.True(File.Exists(source)); Assert.Equal(original, File.ReadAllBytes(source));
                using var writable = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            finally { foreach (var lease in leases) await lease.DisposeAsync(); }
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task CastMp3TransfersOriginalBytesAndRangesWithNoPlaybackBeforeRelease()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-direct-http-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var source = CreateCastTestMusic(folder, ".mp3"); var original = File.ReadAllBytes(source);
            await using var preparer = new WindowsCastMediaPreparer(Path.Combine(folder, "cache"));
            await using var server = new LocalStreamServer(); var session = new Session();
            await using var output = new FileCastOutput(new("cast", "Synthetic receiver", SpeakerTransport.Chromecast, "127.0.0.1", 8009),
                new(source, false, 0), server, preparer, session);
            await output.StartAsync(default); Assert.Equal(0, session.Plays);
            using var http = new HttpClient(); Assert.Equal(original, await http.GetByteArrayAsync(session.Url));
            using var range = new HttpRequestMessage(HttpMethod.Get, session.Url); range.Headers.Range = new RangeHeaderValue(100, 199);
            using var response = await http.SendAsync(range); Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(original[100..200], await response.Content.ReadAsByteArrayAsync());
            using var head = new HttpRequestMessage(HttpMethod.Head, session.Url); using var headers = await http.SendAsync(head);
            Assert.Equal(HttpStatusCode.OK, headers.StatusCode); Assert.Equal(original.Length, headers.Content.Headers.ContentLength);
            Assert.Empty(await headers.Content.ReadAsByteArrayAsync());
            using var headRange = new HttpRequestMessage(HttpMethod.Head, session.Url); headRange.Headers.Range = new RangeHeaderValue(100, 199);
            using var rangeHeaders = await http.SendAsync(headRange);
            Assert.Equal(HttpStatusCode.PartialContent, rangeHeaders.StatusCode); Assert.Equal(100, rangeHeaders.Content.Headers.ContentLength);
            Assert.Empty(await rangeHeaders.Content.ReadAsByteArrayAsync());
            await output.BeginPlaybackAsync(default); Assert.Equal(1, session.Plays);
            await output.StopAsync(); using var revoked = await http.GetAsync(session.Url); Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
            Assert.Equal(original, File.ReadAllBytes(source)); Assert.Empty(Directory.GetFiles(Path.Combine(folder, "cache")));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task RenamedWaveStillUsesTheCompatibleEncodingPathInsteadOfMp3Passthrough()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-renamed-mp3-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var wave = CreateCastTestMusic(folder, ".wav"); var source = Path.ChangeExtension(wave, ".mp3"); File.Move(wave, source);
            var original = File.ReadAllBytes(source);
            await using var preparer = new WindowsCastMediaPreparer(Path.Combine(folder, "cache"));
            await using var lease = await preparer.PrepareAsync(source, default);
            Assert.NotEqual(source, lease.Path);
            using (var reader = new MediaFoundationReader(lease.Path)) Assert.InRange(reader.TotalTime.TotalSeconds, .9, 1.2);
            await lease.DisposeAsync(); Assert.False(File.Exists(lease.Path)); Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(folder, true); }
    }
}
