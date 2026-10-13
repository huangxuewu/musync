using Musync.Core;

namespace Musync.Network;

// Split one continuous MPEG-1 Layer III stream only at complete frame boundaries.
// The receiver keeps its decoder/bit reservoir across adjacent HLS segments.
internal sealed class Mp3Segmenter(Action<byte[], long, int> publish)
{
    public const int SamplesPerFrame = 1152;
    public const int FramesPerSegment = 20;
    public const int SamplesPerSegment = SamplesPerFrame * FramesPerSegment;
    private static readonly int[] Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private readonly List<byte> _pending = [];
    private readonly MemoryStream _segment = new();
    private bool _beginning = true;
    private int _frameCount;
    private long _samplePosition;

    public void Push(ReadOnlySpan<byte> bytes)
    {
        _pending.AddRange(bytes.ToArray());
        var offset = 0;
        while (_pending.Count - offset >= 4)
        {
            if (_beginning && _pending[offset] == 'I' && _pending[offset + 1] == 'D' && _pending[offset + 2] == '3')
            {
                if (_pending.Count - offset < 10) break;
                var size = 10;
                for (var i = 6; i < 10; i++)
                {
                    if ((_pending[offset + i] & 128) != 0) throw new IOException("Invalid MP3 ID3 header.");
                }
                size += (_pending[offset + 6] << 21) | (_pending[offset + 7] << 14) | (_pending[offset + 8] << 7) | _pending[offset + 9];
                if ((_pending[offset + 5] & 16) != 0) size += 10;
                if (size > 65536) throw new IOException("MP3 metadata exceeds the live stream limit.");
                if (_pending.Count - offset < size) break;
                offset += size; _beginning = false;
                continue;
            }
            var b0 = _pending[offset]; var b1 = _pending[offset + 1]; var b2 = _pending[offset + 2];
            var bitrate = b2 >> 4;
            if (b0 != 255 || (b1 & 254) != 250 || ((b2 >> 2) & 3) != 0 || bitrate is 0 or 15)
                throw new IOException("Expected 44.1 kHz MPEG-1 Layer III frames from the live encoder.");
            var length = 144000 * Bitrates[bitrate] / AudioFormat.SampleRate + ((b2 >> 1) & 1);
            if (_pending.Count - offset < length) break;
            _beginning = false;
            for (var i = 0; i < length; i++) _segment.WriteByte(_pending[offset + i]);
            offset += length;
            if (++_frameCount == FramesPerSegment)
            {
                publish(_segment.ToArray(), _samplePosition, SamplesPerSegment);
                _samplePosition += SamplesPerSegment;
                _segment.SetLength(0); _frameCount = 0;
            }
        }
        if (offset > 0) _pending.RemoveRange(0, offset);
        if (_pending.Count > 65536) throw new IOException("Unbounded MP3 frame buffer.");
    }
}
