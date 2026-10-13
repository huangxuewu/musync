using Musync.Core;
using NAudio.MediaFoundation;
using NAudio.Wave;
using System.Runtime.Versioning;

namespace Musync.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class Mp3StreamEncoder : IAudioStreamEncoder
{
    public void EncodeMp3(AudioSampleReader read, Action<ReadOnlyMemory<byte>> write, CancellationToken token)
    {
        var input = new PcmSource(read, token);
        using var output = new AppendStream(write);
        using var mediaType = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, input.WaveFormat, 192000)
            ?? throw new NotSupportedException("Windows MP3 encoder is unavailable.");
        using var encoder = new MediaFoundationEncoder(mediaType) { DefaultReadBufferSize = 1152 * AudioFormat.Channels * 2 };
        encoder.Encode(output, input, TranscodeContainerTypes.MFTranscodeContainerType_MP3);
    }

    private sealed class PcmSource(AudioSampleReader read, CancellationToken token) : IWaveProvider
    {
        private readonly float[] _samples = new float[1152 * AudioFormat.Channels];
        public WaveFormat WaveFormat { get; } = new(AudioFormat.SampleRate, 16, AudioFormat.Channels);
        public int Read(Span<byte> buffer)
        {
            if (token.IsCancellationRequested) return 0;
            var count = read(_samples.AsSpan(0, Math.Min(_samples.Length, buffer.Length / 4 * 2)));
            if (count == 0) return 0;
            var bytes = PcmEncoding.ToInt16(_samples.AsSpan(0, count));
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
    }

    private sealed class AppendStream(Action<ReadOnlyMemory<byte>> write) : Stream
    {
        private long _position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            write(buffer.AsMemory(offset, count)); _position += count;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            write(buffer.ToArray()); _position += buffer.Length;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
