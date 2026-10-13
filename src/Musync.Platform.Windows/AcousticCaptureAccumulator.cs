using Musync.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Musync.Platform.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AcousticCaptureAccumulator
{
    private readonly object _gate = new();
    private readonly BufferedWaveProvider _input;
    private readonly ISampleProvider _normalized;
    private readonly float[] _chunk = new float[2048];
    private readonly List<float> _samples = new();
    private readonly int _maximum;
    private bool _seenPacket, _closed;
    private Exception? _error;
    public AcousticCaptureAccumulator(WaveFormat format, TimeSpan maximumDuration)
    {
        if (format.Channels is not (1 or 2)) throw new NotSupportedException("The microphone must provide mono or stereo audio.");
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromSeconds(AcousticAnalyzer.MaximumCaptureSeconds))
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _maximum = (int)Math.Ceiling(maximumDuration.TotalSeconds * AcousticProbe.MeasurementRate);
        _input = new(format, TimeSpan.FromSeconds(2)) { ReadFully = false, DiscardOnBufferOverflow = false };
        ISampleProvider source = _input.ToSampleProvider();
        if (format.Channels == 2) source = new StereoToMonoSampleProvider(source) { LeftVolume = .5f, RightVolume = .5f };
        _normalized = format.SampleRate == AcousticProbe.MeasurementRate ? source : new WdlResamplingSampleProvider(source, AcousticProbe.MeasurementRate);
    }
    public long Count { get { lock (_gate) return _samples.Count; } }
    public Exception? Error { get { lock (_gate) return _error; } }
    public void Append(ReadOnlySpan<byte> packet, AudioClientBufferFlags flags)
    {
        lock (_gate)
        {
            if (_closed || _error is not null || packet.IsEmpty) return;
            try
            {
                // WASAPI can flag its first packet as a discontinuity. Once
                // recording has begun, a gap invalidates the timing measurement.
                if ((_seenPacket && flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) || flags.HasFlag(AudioClientBufferFlags.TimestampError))
                    throw new IOException("Microphone timing was interrupted. Repeat the test.");
                _seenPacket = true;
                if (flags.HasFlag(AudioClientBufferFlags.Silent))
                {
                    var silence = new byte[packet.Length]; _input.AddSamples(silence);
                }
                else _input.AddSamples(packet);
                int read;
                while ((read = _normalized.Read(_chunk)) > 0)
                {
                    if (_samples.Count + read > _maximum) throw new IOException("The microphone recording reached its duration limit.");
                    for (var i = 0; i < read; i++)
                    {
                        if (!float.IsFinite(_chunk[i])) throw new IOException("The microphone returned invalid audio samples.");
                        _samples.Add(_chunk[i]);
                    }
                }
            }
            catch (Exception ex) { _error = ex; }
        }
    }
    public AcousticRecording Complete()
    {
        lock (_gate)
        {
            _closed = true;
            if (_error is not null) throw new IOException("Microphone recording is unsuitable for calibration.", _error);
            return new(_samples.ToArray());
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            _closed = true;
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_samples).Clear(); _samples.Clear();
            _chunk.AsSpan().Clear(); _input.ClearBuffer();
        }
    }
}
