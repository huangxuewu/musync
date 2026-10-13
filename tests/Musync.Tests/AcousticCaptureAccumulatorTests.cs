using Musync.Core;
using Musync.Platform.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace Musync.Tests;

[SupportedOSPlatform("windows")]
public sealed class AcousticCaptureAccumulatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Native48000HzPacketsBecomeOneContinuous8000HzMonoRecording(int channels)
    {
        var accumulator = new AcousticCaptureAccumulator(WaveFormat.CreateIeeeFloatWaveFormat(48000, channels), TimeSpan.FromSeconds(2));
        for (var packet = 0; packet < 100; packet++)
        {
            var data = new float[480 * channels];
            for (var frame = 0; frame < 480; frame++)
                for (var channel = 0; channel < channels; channel++) data[frame * channels + channel] = (float)(.1 * Math.Sin(Math.Tau * 1000 * (packet * 480 + frame) / 48000));
            accumulator.Append(MemoryMarshal.AsBytes(data.AsSpan()), packet == 0 ? AudioClientBufferFlags.DataDiscontinuity : AudioClientBufferFlags.None);
        }
        var recording = accumulator.Complete(); Assert.Equal(8000, recording.SampleRate);
        Assert.InRange(recording.Samples.Length, 7950, 8050); Assert.True(recording.Continuous);
        Assert.True(recording.Samples.Span.ToArray().Max(Math.Abs) > .05);
        var length = recording.Samples.Length; accumulator.Clear(); Assert.Equal(0, accumulator.Count); Assert.Equal(length, recording.Samples.Length);
    }
    [Fact]
    public void SilentFlagsDoNotExposePayloadAndLaterDiscontinuityCannotBecomeAValidRecording()
    {
        var accumulator = new AcousticCaptureAccumulator(WaveFormat.CreateIeeeFloatWaveFormat(8000, 1), TimeSpan.FromSeconds(1));
        var data = Enumerable.Repeat(.5f, 800).ToArray();
        accumulator.Append(MemoryMarshal.AsBytes(data.AsSpan()), AudioClientBufferFlags.Silent);
        Assert.All(accumulator.Complete().Samples.ToArray(), value => Assert.Equal(0, value)); accumulator.Clear();
        var broken = new AcousticCaptureAccumulator(WaveFormat.CreateIeeeFloatWaveFormat(8000, 1), TimeSpan.FromSeconds(1));
        broken.Append(MemoryMarshal.AsBytes(data.AsSpan()), AudioClientBufferFlags.None);
        broken.Append(MemoryMarshal.AsBytes(data.AsSpan()), AudioClientBufferFlags.DataDiscontinuity);
        Assert.Throws<IOException>(() => broken.Complete()); broken.Clear();
    }
    [Fact]
    public void DurationLimitBadTimestampsAndNonFinitePcmDoNotSilentlyDropSamples()
    {
        foreach (var defect in new[] { 0, 1, 2 })
        {
            var accumulator = new AcousticCaptureAccumulator(WaveFormat.CreateIeeeFloatWaveFormat(8000, 1), TimeSpan.FromMilliseconds(50));
            var data = Enumerable.Repeat(defect == 2 ? float.NaN : .1f, 800).ToArray();
            accumulator.Append(MemoryMarshal.AsBytes(data.AsSpan()), defect == 1 ? AudioClientBufferFlags.TimestampError : AudioClientBufferFlags.None);
            Assert.NotNull(accumulator.Error); Assert.Throws<IOException>(() => accumulator.Complete()); accumulator.Clear();
        }
    }
}
