using Musync.Core;
using Musync.Platform.Windows;
using Musync.Network;
using NAudio.Wave;
using System.Buffers.Binary;
using System.Globalization;
using Xunit;

namespace Musync.Tests;

public sealed class CastStreamTests
{
    [Fact]
    public void PullEncoderWaitsForWholeBlocksWithoutManufacturingAnUnderrun()
    {
        var buffer = new AudioRingBuffer(100); buffer.Write([.25f, .25f]);
        var block = new float[4];
        Assert.Equal(0, buffer.Read(block, requireFullBuffer: true)); Assert.Equal(1, buffer.BufferedFrames);
        Assert.Equal(0, buffer.Underruns);
        buffer.Write([-.25f, -.25f]); Assert.Equal(4, buffer.Read(block, requireFullBuffer: true));
        Assert.Equal(new[] { .25f, .25f, -.25f, -.25f }, block);
        Assert.Equal(0, buffer.Read(block, requireFullBuffer: true)); Assert.Equal(0, buffer.Underruns);
    }

    [Theory]
    [InlineData(8589934592L)]
    [InlineData(8589934592L + 90000)]
    public void PackedTimestampUsesTheRequired33BitClockWrap(long timestamp)
    {
        var packet = CastOutput.PackedAudio([255, 251], timestamp);
        Assert.Equal((ulong)(timestamp % 8589934592L), BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(65, 8)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(8192)]
    public void ArbitrarilySplitWritesKeepWholeFramesAndExactTimestamps(int writeSize)
    {
        // CBR MPEG-1, 192 kbps, 44.1 kHz: include alternating padding lengths.
        var frames = new List<byte[]>();
        for (var i = 0; i < 60; i++)
        {
            var frame = new byte[626 + i % 2];
            frame[0] = 255; frame[1] = 251; frame[2] = (byte)(176 + (i % 2) * 2);
            frames.Add(frame);
        }
        var source = frames.SelectMany(f => f).ToArray();
        var segments = new List<(byte[] Bytes, long Start, int Count)>();
        var segmenter = new Mp3Segmenter((bytes, start, count) => segments.Add((bytes, start, count)));
        for (var offset = 0; offset < source.Length; offset += writeSize)
            segmenter.Push(source.AsSpan(offset, Math.Min(writeSize, source.Length - offset)));
        Assert.Equal(3, segments.Count);
        Assert.Equal(source, segments.SelectMany(s => s.Bytes).ToArray());
        for (var i = 0; i < segments.Count; i++)
        {
            Assert.Equal(i * 23040L, segments[i].Start); Assert.Equal(23040, segments[i].Count);
            var packed = CastOutput.PackedAudio(segments[i].Bytes, segments[i].Start * 90000 / AudioFormat.SampleRate);
            Assert.Equal((ulong)(i * 23040L * 90000 / 44100), BinaryPrimitives.ReadUInt64BigEndian(packed.AsSpan(65, 8)));
        }
    }

    [Fact]
    public void HlsDurationFollowsSamplesWithoutAdvertisingAnUnverifiedWallClock()
    {
        var stream = new HlsStream();
        for (var i = 0; i < 8; i++) stream.Add([1]);
        var lines = stream.Playlist().Split('\n');
        Assert.Contains("#EXT-X-TARGETDURATION:1", lines);
        var durations = lines.Where(l => l.StartsWith("#EXTINF:")).Select(l => double.Parse(l[8..^1], CultureInfo.InvariantCulture)).ToArray();
        Assert.All(durations, d => Assert.Equal(23040d / 44100, d, 8));
        Assert.True(durations.Sum() >= 3);
        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-PROGRAM-DATE-TIME:"));
        Assert.DoesNotContain("#EXT-X-ENDLIST", lines);
        Assert.Equal(8, durations.Length);
    }

    [Fact]
    public void SixtyFourSegmentsKeepSampleAccurateClockWithoutCumulativeRounding()
    {
        var stream = new HlsStream();
        for (var i = 0; i < 10000; i++) stream.Add([1]);
        var lines = stream.Playlist().Split('\n');
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:9992", lines);
        Assert.Null(stream.Get(9935)); Assert.NotNull(stream.Get(9936));
        var durations = lines.Where(l => l.StartsWith("#EXTINF:")).Select(l => double.Parse(l[8..^1], CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(8 * 23040d / 44100, durations.Sum(), 8);
        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-PROGRAM-DATE-TIME:"));
    }

    [Fact]
    public void RealContinuousPackedAudioDecodesAcrossEverySegmentWithoutSilentGaps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-cast-continuity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "continuous.mp3");
        try
        {
            var segments = new List<byte[]>(); var position = 0;
            var segmenter = new Mp3Segmenter((bytes, start, count) => segments.Add(CastOutput.PackedAudio(bytes, start * 90000 / AudioFormat.SampleRate)));
            new Mp3StreamEncoder().EncodeMp3(samples =>
            {
                if (position >= AudioFormat.SampleRate * 7) return 0;
                for (var i = 0; i < samples.Length; i++) samples[i] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * (position + i / 2) / 44100));
                position += samples.Length / 2; return samples.Length;
            }, bytes => segmenter.Push(bytes.Span), CancellationToken.None);
            Assert.True(segments.Count >= 12);
            File.WriteAllBytes(path, segments.SelectMany(s => s).ToArray());
            using var reader = new MediaFoundationReader(path, new() { RequestFloatOutput = true });
            var provider = reader.ToSampleProvider(); var chunk = new float[8192]; var decoded = new List<float>(); int read;
            while ((read = provider.Read(chunk)) > 0) decoded.AddRange(chunk[..read]);
            Assert.True(decoded.Count >= 44100 * 2 * 6);
            for (var boundary = 23040; boundary + 1152 < decoded.Count / 2; boundary += 23040)
            {
                var window = decoded.Skip((boundary - 576) * 2).Take(1152 * 2).ToArray();
                var rms = Math.Sqrt(window.Select(v => (double)v * v).Average());
                Assert.InRange(rms, .11, .17);
                for (var frame = boundary - 576; frame < boundary + 576; frame++)
                    Assert.InRange(Math.Abs(decoded[frame * 2] - decoded[(frame - 1) * 2]), 0, .035);
            }
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public void ContinuousEncoderEmitsAudioBeforeTheInputEnds()
    {
        var inputFrames = 0;
        var bytesBeforeEof = 0;
        var ended = false;
        using var output = new MemoryStream();
        new Mp3StreamEncoder().EncodeMp3(samples =>
        {
            if (inputFrames >= AudioFormat.SampleRate * 3) { ended = true; return 0; }
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * (inputFrames + i / 2) / AudioFormat.SampleRate));
            inputFrames += samples.Length / 2;
            return samples.Length;
        }, bytes =>
        {
            output.Write(bytes.Span);
            if (!ended) bytesBeforeEof += bytes.Length;
        }, CancellationToken.None);
        Assert.True(bytesBeforeEof > 40000, $"Only {bytesBeforeEof} bytes were emitted before EOF.");
        Assert.True(output.Length > 40000);
    }
}
