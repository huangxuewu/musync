using Musync.Core;
using Musync.Network;
using System.Buffers.Binary;
using Xunit;

namespace Musync.Tests;

public sealed class AudioClockTests
{
    [Fact]
    public void PresentationTimeContinuesWhenRtpTimestampWraps()
    {
        const ulong anchor = 0x1234567800000000;
        var before = (long)uint.MaxValue - 100;
        var after = before + AudioFormat.SampleRate;
        var first = RaopProtocol.NtpAtFrame(anchor, before);
        var next = RaopProtocol.NtpAtFrame(anchor, after);
        Assert.Equal(1ul << 32, next - first);
        var wrapped = unchecked((uint)after);
        var sync = RaopProtocol.SyncPacket(wrapped, 11025, next, false);
        Assert.Equal(wrapped, BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(16)));
        Assert.Equal(next, BinaryPrimitives.ReadUInt64BigEndian(sync.AsSpan(8)));
    }
    [Fact]
    public void FortyEightHourTimelineUsesExactFrameDuration()
    {
        const ulong anchor = 0x1234567800000000;
        var frames = (long)AudioFormat.SampleRate * 48 * 60 * 60;
        Assert.Equal(anchor + (172800ul << 32), RaopProtocol.NtpAtFrame(anchor, frames));
        Assert.Equal(anchor, RaopProtocol.NtpAtFrame(anchor, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RaopProtocol.NtpAtFrame(anchor, -1));
    }
}
