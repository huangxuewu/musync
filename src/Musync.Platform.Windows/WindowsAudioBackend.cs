using Musync.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Musync.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioBackend : IAudioBackend
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private WaveStream? _reader;
    private WasapiRecorder? _capture;
    private volatile bool _paused;
    private readonly object _readerGate = new();
    private double _lastPosition;
    private double _lastDuration;
    private int _readerRevision;
    private volatile bool _finished;
    public string? DefaultDeviceId
    {
        get
        {
            try { using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); return device.ID; }
            catch (Exception) { return null; }
        }
    }
    public double Position { get { lock (_readerGate) return _lastPosition; } }
    public double Duration { get { lock (_readerGate) return _reader?.TotalTime.TotalSeconds ?? _lastDuration; } }
    public bool IsFinished => _finished;
    public IReadOnlyList<SpeakerDevice> GetDevices()
    {
        var devices = new List<SpeakerDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var name = device.FriendlyName;
                var bluetooth = name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                    || device.DeviceFriendlyName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
                devices.Add(new(device.ID, name, bluetooth ? SpeakerTransport.Bluetooth : SpeakerTransport.Windows));
            }
        }
        return devices;
    }
    public IAudioOutput CreateOutput(SpeakerDevice device) =>
        new WindowsOutput(device, _enumerator.GetDevice(device.Id));
    public MusicTrack ProbeFile(string path)
    {
        using var reader = OpenFile(path);
        return LocalTrackMetadata.Read(new("local:" + Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(path), "", path, DurationSeconds: reader.TotalTime.TotalSeconds));
    }
    internal static WaveStream OpenFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aif", StringComparison.OrdinalIgnoreCase)) return new AudioFileReader(path);
        // The portable NAudio build does not dispatch compressed files to MF,
        // even when NAudio.Wasapi is referenced. Use the Windows reader directly.
        return new MediaFoundationReader(path, new() { RequestFloatOutput = true });
    }
    public void Pause(bool paused) { lock (_readerGate) _paused = paused; }
    public void Seek(double seconds)
    {
        lock (_readerGate)
        {
            if (_reader is null) return;
            _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _reader.TotalTime.TotalSeconds));
            _lastPosition = _reader.CurrentTime.TotalSeconds; _readerRevision++; _finished = false;
        }
    }
    public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken cancellationToken, double startPosition = 0)
    {
        using var reader = OpenFile(path);
        reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(startPosition, 0, reader.TotalTime.TotalSeconds));
        lock (_readerGate) { _reader = reader; _lastPosition = reader.CurrentTime.TotalSeconds; _lastDuration = reader.TotalTime.TotalSeconds; _finished = false; }
        try
        {
            var provider = Normalize(reader.ToSampleProvider());
            var revision = _readerRevision;
            var origin = _lastPosition;
            long frames = 0;
            var chunk = new float[882];
            var clock = Stopwatch.StartNew();
            double next = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_paused) { await Task.Delay(25, cancellationToken); next = clock.Elapsed.TotalSeconds; continue; }
                int read;
                lock (_readerGate)
                {
                    if (_paused) continue;
                    if (revision != _readerRevision)
                    {
                        // A resampler can retain decoded samples from before
                        // Seek. Recreate it at the reader's new file position.
                        provider = Normalize(reader.ToSampleProvider()); revision = _readerRevision;
                        origin = _lastPosition; frames = 0; next = clock.Elapsed.TotalSeconds;
                    }
                    read = provider.Read(chunk); read -= read % AudioFormat.Channels;
                    if (read == 0) { _finished = true; _lastPosition = _lastDuration; return; }
                    frames += read / AudioFormat.Channels;
                    _lastPosition = Math.Min(_lastDuration, origin + frames / (double)AudioFormat.SampleRate);
                    hub.Publish(chunk.AsSpan(0, read));
                }
                next += (double)read / AudioFormat.SamplesPerSecond;
                var wait = next - clock.Elapsed.TotalSeconds;
                if (wait > .001) await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
                if (wait < -.1) next = clock.Elapsed.TotalSeconds;
            }
        }
        finally { lock (_readerGate) _reader = null; }
    }
    public async Task CaptureAsync(string deviceId, AudioHub hub, CancellationToken cancellationToken)
    {
        using var device = _enumerator.GetDevice(deviceId);
        using var capture = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().Build();
        _capture = capture;
        _finished = false;
        var provider = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true, ReadFully = false
        };
        var normalized = Normalize(provider.ToSampleProvider());
        var chunk = new float[4096];
        Exception? captureError = null;
        capture.DataAvailable += (data, flags, devicePosition, qpcPosition) =>
        {
            if (_paused) return;
            provider.AddSamples(data);
            int count;
            while ((count = normalized.Read(chunk)) > 0)
                hub.Publish(chunk.AsSpan(0, count - count % 2));
        };
        capture.RecordingStopped += (_, args) => captureError = args.Exception;
        capture.StartRecording();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
                if (captureError is not null) throw new IOException("Windows audio capture stopped.", captureError);
            }
        }
        finally { capture.StopRecording(); _capture = null; }
    }
    internal static ISampleProvider Normalize(ISampleProvider provider)
    {
        if (provider.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
        else if (provider.WaveFormat.Channels != 2)
            throw new NotSupportedException("The selected audio source must provide mono or stereo audio.");
        if (provider.WaveFormat.SampleRate != AudioFormat.SampleRate)
            provider = new WdlResamplingSampleProvider(provider, AudioFormat.SampleRate);
        return provider;
    }
    public ValueTask DisposeAsync() { _capture?.StopRecording(); _enumerator.Dispose(); return ValueTask.CompletedTask; }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsOutput : IAudioOutput, ISampleProvider
{
    private readonly MMDevice _device;
    private WasapiPlayer? _player;
    public WindowsOutput(SpeakerDevice device, MMDevice endpoint) { Device = device; _device = endpoint; Buffer.SetDelay(0); }
    public SpeakerDevice Device { get; }
    public AudioRingBuffer Buffer { get; } = new();
    public OutputControl Control { get; } = new();
    public string? Error { get; private set; }
    public TimeSpan EstimatedLatency => TimeSpan.FromMilliseconds(400);
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels);
    public int Read(Span<float> buffer)
    {
        Buffer.SetDelay(Control.EffectiveDelayMs);
        Buffer.Read(buffer, Control.Gain);
        return buffer.Length;
    }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Buffer.SetTransportPaused(false);
        _player = new WasapiPlayerBuilder().WithDevice(_device).WithSharedMode().WithEventSync().WithLatency(100).Build();
        _player.PlaybackStopped += (_, args) => { if (args.Exception is not null) Error = args.Exception.Message; };
        _player.Init(new SampleToWaveProvider(this));
        _player.Play();
        return Task.CompletedTask;
    }
    public Task StopAsync() { _player?.Stop(); _player?.Dispose(); _player = null; Buffer.Clear(); return Task.CompletedTask; }
    public Task SetPausedAsync(bool paused) { Buffer.SetTransportPaused(paused); return Task.CompletedTask; }
    public async ValueTask DisposeAsync() { await StopAsync(); _device.Dispose(); }
}
