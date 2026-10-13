using System.Buffers.Binary;
using Musync.Core;

namespace Musync.Network;

public static class RaopProtocol
{
    public const int FramesPerPacket = 352;
    public static byte[] EncodeAlac(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length != FramesPerPacket * 4) throw new ArgumentException("RAOP requires 352 stereo frames.");
        var output = new byte[pcm.Length + 4];
        var bit = 0;
        void Write(uint value, int count)
        {
            for (var n = count - 1; n >= 0; n--, bit++)
                output[bit / 8] |= (byte)(((value >> n) & 1) << (7 - bit % 8));
        }
        Write(1, 3);
        Write(0, 4);
        Write(0, 12);
        Write(0, 1);
        Write(0, 2);
        Write(1, 1);
        for (var i = 0; i < pcm.Length; i += 2) Write(BinaryPrimitives.ReadUInt16LittleEndian(pcm[i..]), 16);
        Write(7, 3);
        return output;
    }

    public static byte[] AudioPacket(ReadOnlySpan<byte> alac, ushort sequence, uint timestamp, uint ssrc, bool first)
    {
        var result = new byte[12 + alac.Length];
        result[0] = 0x80; result[1] = (byte)(first ? 0xe0 : 0x60);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), ssrc);
        alac.CopyTo(result.AsSpan(12));
        return result;
    }

    public static ulong NtpNow()
    {
        var ticks = DateTime.UtcNow.Ticks - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        return ((ulong)(ticks / TimeSpan.TicksPerSecond) << 32)
            | (ulong)((ticks % TimeSpan.TicksPerSecond) * (double)(1L << 32) / TimeSpan.TicksPerSecond);
    }

    public static byte[] SyncPacket(uint timestamp, uint latency, ulong ntp, bool first)
    {
        var result = new byte[20];
        result[0] = first ? (byte)0x90 : (byte)0x80; result[1] = 0xd4;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), unchecked(timestamp - latency));
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), timestamp);
        return result;
    }
    public static ulong NtpAtFrame(ulong anchor, long frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        // RTP wraps at 32 bits; the presentation clock must continue from the
        // full frame count. Integer arithmetic also avoids accumulated rounding.
        var offset = ((UInt128)(ulong)frames << 32) / AudioFormat.SampleRate;
        return unchecked(anchor + (ulong)offset);
    }
}
