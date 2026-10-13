namespace Musync.Core;

public static class AudioFormat
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int SamplesPerSecond = SampleRate * Channels;
}

public enum SpeakerTransport { Windows, Bluetooth, AirPlay, Chromecast }

public sealed record SpeakerDevice(
    string Id, string Name, SpeakerTransport Transport,
    string? Host = null, int Port = 0,
    IReadOnlyDictionary<string, string>? Properties = null,
    string? UnsupportedReason = null);

public sealed class SpeakerPreferences
{
    public string Room { get; set; } = "all";
    public double Volume { get; set; } = 65;
    public int DelayMs { get; set; }
    public bool Muted { get; set; }
    public bool Selected { get; set; }
}

public sealed class OutputControl
{
    private float _gain = .65f;
    private int _delayMs;
    private int _alignmentDelayMs;
    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, Math.Clamp(value, 0, 1));
    }
    public int DelayMs
    {
        get => Volatile.Read(ref _delayMs);
        set => Volatile.Write(ref _delayMs, Math.Clamp(value, 0, 10000));
    }
    // Session-owned compensation is separate from the user's speaker tuning.
    public int AlignmentDelayMs
    {
        get => Volatile.Read(ref _alignmentDelayMs);
        set => Volatile.Write(ref _alignmentDelayMs, Math.Clamp(value, 0, 5000));
    }
    public int EffectiveDelayMs => DelayMs + AlignmentDelayMs;
}

public sealed class AudioRingBuffer
{
    private readonly object _gate = new();
    private readonly float[] _samples;
    private int _read;
    private int _count;
    private int _target;
    private bool _primed;
    private bool _paused;
    private bool _transportPaused;
    private long _dropped;
    private long _underruns;

    public AudioRingBuffer(int capacityFrames = AudioFormat.SampleRate * 16)
    {
        if (capacityFrames < 2) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _samples = new float[capacityFrames * AudioFormat.Channels];
    }

    public int BufferedFrames { get { lock (_gate) return _count / AudioFormat.Channels; } }
    public long DroppedFrames { get { lock (_gate) return _dropped; } }
    public long Underruns { get { lock (_gate) return _underruns; } }

    public void SetDelay(int milliseconds, int baseBufferMs = 300)
    {
        var target = Math.Clamp((int)((long)(Math.Clamp(milliseconds, 0, 15000) + baseBufferMs)
            * AudioFormat.SamplesPerSecond / 1000), 0, _samples.Length - 2);
        target -= target % 2;
        lock (_gate)
        {
            if (target == _target) return;
            if (_primed && target < _target)
            {
                var trim = Math.Min(_count, _target - target);
                _read = (_read + trim) % _samples.Length;
                _count -= trim;
                _dropped += trim / 2;
            }
            if (target > _target) _primed = false;
            _target = target;
        }
    }

    public void Write(ReadOnlySpan<float> input)
    {
        if (input.Length % 2 != 0) throw new ArgumentException("Whole stereo frames are required.");
        lock (_gate)
        {
            if (input.Length > _samples.Length)
            {
                _dropped += (input.Length - _samples.Length) / 2;
                input = input[^_samples.Length..];
            }
            var overflow = Math.Max(0, _count + input.Length - _samples.Length);
            _read = (_read + overflow) % _samples.Length;
            _count -= overflow;
            _dropped += overflow / 2;
            var write = (_read + _count) % _samples.Length;
            var first = Math.Min(input.Length, _samples.Length - write);
            input[..first].CopyTo(_samples.AsSpan(write));
            input[first..].CopyTo(_samples);
            _count += input.Length;
        }
    }

    public int Read(Span<float> destination, float gain = 1, bool requireFullBuffer = false)
    {
        if (destination.Length % 2 != 0) throw new ArgumentException("Whole stereo frames are required.");
        lock (_gate)
        {
            destination.Clear();
            if (_paused || _transportPaused) return 0;
            // Pull encoders can wait for a whole codec block. A partial block
            // is not a device underrun and must not trigger silence/repriming.
            if (requireFullBuffer && _count < destination.Length) return 0;
            if (!_primed)
            {
                if (_count < Math.Max(_target, destination.Length)) return 0;
                _primed = true;
            }
            var available = Math.Min(_count, destination.Length);
            var first = Math.Min(available, _samples.Length - _read);
            _samples.AsSpan(_read, first).CopyTo(destination);
            _samples.AsSpan(0, available - first).CopyTo(destination[first..]);
            _read = (_read + available) % _samples.Length;
            _count -= available;
            if (available < destination.Length)
            {
                _underruns++;
                _primed = false;
            }
            for (var i = 0; i < available; i++) destination[i] *= gain;
            return available;
        }
    }

    public void Clear()
    {
        lock (_gate) { _read = 0; _count = 0; _primed = false; }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) _paused = paused;
    }

    public void SetTransportPaused(bool paused)
    {
        // A receiver becoming ready cannot release the coordinator's barrier
        // while another receiver is still preparing its session.
        lock (_gate) _transportPaused = paused;
    }

    // Restore samples read into a transport packet/segment that was never
    // published. They must precede the source samples still in the queue.
    public void Prepend(ReadOnlySpan<float> input)
    {
        if (input.Length % AudioFormat.Channels != 0) throw new ArgumentException("Whole stereo frames are required.");
        lock (_gate)
        {
            if (input.Length > _samples.Length)
            {
                _dropped += (input.Length - _samples.Length) / AudioFormat.Channels;
                input = input[.._samples.Length];
            }
            var overflow = Math.Max(0, _count + input.Length - _samples.Length);
            _count -= overflow; _dropped += overflow / AudioFormat.Channels;
            _read = (_read - input.Length + _samples.Length) % _samples.Length;
            var first = Math.Min(input.Length, _samples.Length - _read);
            input[..first].CopyTo(_samples.AsSpan(_read));
            input[first..].CopyTo(_samples);
            _count += input.Length;
        }
    }
}

public sealed class AudioHub
{
    private readonly object _gate = new();
    private AudioRingBuffer[] _outputs = [];
    private bool _paused;
    public void SetOutputs(IEnumerable<AudioRingBuffer> outputs)
    {
        lock (_gate)
        {
            _outputs = outputs.ToArray();
            foreach (var output in _outputs) output.SetPaused(_paused);
        }
    }
    public void Publish(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_paused) return;
            foreach (var output in _outputs) output.Write(samples);
        }
    }
    public void SetPaused(bool paused)
    {
        // Retain unread music while blocking both late source publication and
        // output consumption, including during receiver resume preparation.
        lock (_gate)
        {
            _paused = paused;
            foreach (var output in _outputs) output.SetPaused(paused);
        }
    }
    public void Clear()
    {
        lock (_gate) foreach (var output in _outputs) output.Clear();
    }
}

public interface IAudioOutput : IAsyncDisposable
{
    SpeakerDevice Device { get; }
    AudioRingBuffer Buffer { get; }
    OutputControl Control { get; }
    string? Error { get; }
    // File receivers consume their prepared HTTP asset rather than the PCM hub.
    bool ReceivesPcm => true;
    // Receiver buffering is an estimate until the model has been measured.
    TimeSpan EstimatedLatency => TimeSpan.FromMilliseconds(600);
    Task StartAsync(CancellationToken cancellationToken);
    // StartAsync prepares the transport; this releases receivers that preload
    // while paused, after every remaining output has completed preparation.
    Task BeginPlaybackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task StopAsync();
    Task SetPausedAsync(bool paused) => Task.CompletedTask;
    Task PrepareResumeAsync(CancellationToken cancellationToken) => SetPausedAsync(false);
    Task UpdateControlsAsync() => Task.CompletedTask;
}

public sealed record PlaybackSource(string Path, bool IsCapture, double Position, bool IsStreaming = false);

public interface IAudioFilePreparer
{
    Task<PreparedAudioFile> PrepareAsync(string source, CancellationToken cancellationToken);
}

public sealed class PreparedAudioFile(string path, double duration, Func<ValueTask>? release = null) : IAsyncDisposable
{
    private int _disposed;
    public string Path { get; } = path;
    public double Duration { get; } = duration;
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 && release is not null ? release() : ValueTask.CompletedTask;
}

public interface IAudioBackend : IAsyncDisposable
{
    bool IsStreamingSource(string source) => false;
    Task PrepareSourceAsync(string source, CancellationToken cancellationToken) => Task.CompletedTask;
    Task ReleasePreparedSourceAsync() => Task.CompletedTask;
    IReadOnlyList<SpeakerDevice> GetDevices();
    string? DefaultDeviceId { get; }
    IAudioOutput CreateOutput(SpeakerDevice device);
    Task PlayFileAsync(string path, AudioHub hub, CancellationToken cancellationToken, double startPosition = 0);
    Task CaptureAsync(string deviceId, AudioHub hub, CancellationToken cancellationToken);
    void Pause(bool paused);
    void Seek(double seconds);
    double Position { get; }
    double Duration { get; }
    bool IsFinished { get; }
    MusicTrack ProbeFile(string path) => new("local:" + Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(path), "", path);
}
