using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class PausedQueueTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    [InlineData(10000)]
    public void ADelayedTrackRemainsInOrderAcrossPause(int delay)
    {
        var hub = new AudioHub(); var buffer = new AudioRingBuffer(); buffer.SetDelay(delay);
        hub.SetOutputs([buffer]);
        var music = new[] { .1f, .1f, .2f, .2f, .3f, .3f };
        hub.Publish(music); hub.SetPaused(true);
        var held = buffer.BufferedFrames;
        var read = new float[2];
        for (var i = 0; i < 20; i++) { Assert.Equal(0, buffer.Read(read)); Assert.Equal(new float[2], read); }
        hub.Publish([.9f, .9f]); Assert.Equal(held, buffer.BufferedFrames);
        hub.SetPaused(false);
        // EOF silence primes even the maximum manual delay; no earlier music
        // frame is discarded by the pause or by repeated silent output reads.
        hub.Publish(new float[(delay + 300) * AudioFormat.SamplesPerSecond / 1000]);
        var received = new float[6]; Assert.Equal(6, buffer.Read(received)); Assert.Equal(music, received);
    }

    [Fact]
    public void TransportReadinessCannotReleaseTheCommonPauseBarrier()
    {
        var buffer = new AudioRingBuffer(8); var hub = new AudioHub(); hub.SetOutputs([buffer]);
        hub.Publish([1, 1, 2, 2]); hub.SetPaused(true); buffer.SetTransportPaused(true);
        buffer.SetTransportPaused(false);
        var read = new float[2]; Assert.Equal(0, buffer.Read(read)); Assert.Equal(2, buffer.BufferedFrames);
        hub.SetPaused(false); Assert.Equal(2, buffer.Read(read)); Assert.Equal(new[] { 1f, 1f }, read);
    }

    [Fact]
    public void CommonResumeCannotReleaseATransportThatIsStillPaused()
    {
        var buffer = new AudioRingBuffer(8); var hub = new AudioHub(); hub.SetOutputs([buffer]);
        hub.Publish([1, 1]); hub.SetPaused(true); buffer.SetTransportPaused(true); hub.SetPaused(false);
        var read = new float[2]; Assert.Equal(0, buffer.Read(read)); Assert.Equal(1, buffer.BufferedFrames);
        buffer.SetTransportPaused(false); Assert.Equal(2, buffer.Read(read)); Assert.Equal(new[] { 1f, 1f }, read);
    }

    [Fact]
    public void AttachingAnOutputDuringPauseFreezesItsExistingQueue()
    {
        var hub = new AudioHub(); hub.SetPaused(true);
        var buffer = new AudioRingBuffer(8); buffer.Write([.5f, .5f]); hub.SetOutputs([buffer]);
        Assert.Equal(0, buffer.Read(new float[2])); Assert.Equal(1, buffer.BufferedFrames);
        hub.SetPaused(false); var read = new float[2]; buffer.Read(read); Assert.Equal(new[] { .5f, .5f }, read);
    }

    [Fact]
    public void PrependingAnUnpublishedPacketRestoresFifoAcrossWrap()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write(Enumerable.Range(1, 8).SelectMany(n => new[] { (float)n, (float)n }).ToArray());
        buffer.Read(new float[12]); buffer.SetPaused(true);
        buffer.Prepend([5, 5, 6, 6]); buffer.Write([9, 9, 10, 10]);
        Assert.Equal(6, buffer.BufferedFrames); Assert.Equal(0, buffer.Read(new float[12]));
        buffer.SetPaused(false); var read = new float[12]; buffer.Read(read);
        Assert.Equal(Enumerable.Range(5, 6).SelectMany(n => new[] { (float)n, (float)n }).ToArray(), read);
    }

    [Fact]
    public void RestoredQueueRemainsBoundedAndPreservesTheEarliestFrames()
    {
        var buffer = new AudioRingBuffer(4); buffer.Write([3, 3, 4, 4, 5, 5, 6, 6]);
        buffer.Prepend([1, 1, 2, 2]); Assert.Equal(4, buffer.BufferedFrames); Assert.Equal(2, buffer.DroppedFrames);
        var read = new float[8]; buffer.Read(read); Assert.Equal(new[] { 1f, 1f, 2f, 2f, 3f, 3f, 4f, 4f }, read);
        buffer.Clear(); buffer.Prepend([1, 1, 2, 2, 3, 3, 4, 4, 5, 5]);
        buffer.Read(read); Assert.Equal(new[] { 1f, 1f, 2f, 2f, 3f, 3f, 4f, 4f }, read);
    }
}
