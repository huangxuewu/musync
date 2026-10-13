using Musync.Core;
using System.Diagnostics;

namespace Musync.Network;

public sealed class CastOutput : IAudioOutput
{
    private readonly LocalStreamServer _server;
    private readonly IAudioStreamEncoder _encoder;
    private readonly ICastSession _session;
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _streamLifetime;
    private ManualResetEventSlim? _readReady;
    private Task? _worker;
    private string? _key;
    private bool _connected;
    private bool _mediaLoaded;
    private bool _paused;
    private volatile bool _preservePending;
    private int _disposed;
    private double? _originalLevel;
    private float? _appliedLevel;
    private string? _error;
    public CastOutput(SpeakerDevice device, LocalStreamServer server, IAudioStreamEncoder encoder)
        : this(device, server, encoder, new CastSession()) { }
    internal CastOutput(SpeakerDevice device, LocalStreamServer server, IAudioStreamEncoder encoder, ICastSession session)
    { Device = device; _server = server; _encoder = encoder; _session = session; Buffer.SetDelay(0); }
    public SpeakerDevice Device { get; }
    public AudioRingBuffer Buffer { get; } = new();
    public OutputControl Control { get; } = new();
    public string? Error => _error ?? (_connected ? _session.Error : null);
    // HLS receiver buffering still varies by model. This is a conservative
    // short-segment tail estimate, not a hardware synchronization measurement.
    public TimeSpan EstimatedLatency => TimeSpan.FromSeconds(5);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_lifetime is not null) await StopInternal();
            Buffer.SetTransportPaused(true);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _lifetime.Token;
            _error = null; _paused = false;
            await _server.StartAsync(token);
            await _session.ConnectAsync(Device).WaitAsync(TimeSpan.FromSeconds(10), token);
            _connected = true;
            _originalLevel = await _session.ReadVolumeAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            await _session.LaunchAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
            // Apply the receiver level before LOAD can make any audio audible.
            await ApplyVolume(token);
            await BeginStream(token);
            Buffer.SetTransportPaused(false);
        }
        catch { await StopInternal(); throw; }
        finally { _gate.Release(); }
    }
    private async Task BeginStream(CancellationToken token, bool preserveBuffer = false)
    {
        if (!preserveBuffer) Buffer.Clear();
        _preservePending = false;
        var stream = _server.CreateStream();
        _key = stream.Key;
        _streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var primed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReady = _readReady = new ManualResetEventSlim();
        var streamToken = _streamLifetime.Token;
        _worker = Task.Factory.StartNew(() => EncodeStream(stream.Stream, primed, readReady, streamToken),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // Six frame-aligned short segments provide >= 3 target durations as
        // required by HLS. Encode this lead-in immediately, rather than spending
        // six real seconds playing silence before LOAD.
        await primed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        token.ThrowIfCancellationRequested();
        _mediaLoaded = true;
        await _session.LoadAsync(_server.Url(Device.Host!, stream.Key)).WaitAsync(TimeSpan.FromSeconds(15), token);
        readReady.Set();
    }
    private async Task EndStream(bool preserveBuffer = false)
    {
        _preservePending = preserveBuffer;
        _streamLifetime?.Cancel();
        if (_worker is not null) await _worker;
        _worker = null;
        _readReady?.Dispose(); _readReady = null;
        if (_key is not null) _server.RemoveStream(_key);
        _key = null;
        _streamLifetime?.Dispose(); _streamLifetime = null;
        if (!preserveBuffer) Buffer.Clear();
    }
    private void EncodeStream(HlsStream stream, TaskCompletionSource primed, ManualResetEventSlim readReady, CancellationToken token)
    {
        var pending = new PendingMusic();
        long inputFrames = 0;
        var musicClock = new Stopwatch();
        long musicFrames = 0;
        try
        {
            var segmenter = new Mp3Segmenter((bytes, startFrame, frameCount) =>
            {
                if (token.IsCancellationRequested) return;
                stream.Add(PackedAudio(bytes, startFrame * 90000L / AudioFormat.SampleRate), frameCount);
                // Retain a small conservative encoder lookahead on Pause. This
                // prevents codec-buffered, unpublished music from being lost.
                pending.CommitThrough(startFrame + frameCount - 4L * Mp3Segmenter.SamplesPerFrame);
                if (stream.SegmentCount >= 6) primed.TrySetResult();
            });
            _encoder.EncodeMp3(samples =>
            {
                if (token.IsCancellationRequested) return 0;
                if (!primed.Task.IsCompleted && inputFrames < 6L * Mp3Segmenter.SamplesPerSegment + 4L * Mp3Segmenter.SamplesPerFrame)
                {
                    samples.Clear(); inputFrames += samples.Length / AudioFormat.Channels;
                    return samples.Length;
                }
                try { primed.Task.Wait(token); }
                catch (OperationCanceledException) { return 0; }
                try { readReady.Wait(token); }
                catch (OperationCanceledException) { return 0; }
                var wait = musicFrames / (double)AudioFormat.SampleRate - musicClock.Elapsed.TotalSeconds;
                if (wait > 0 && token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) return 0;
                var starved = Stopwatch.StartNew();
                while (!token.IsCancellationRequested)
                {
                    Buffer.SetDelay(Control.DelayMs);
                    var count = Buffer.Read(samples, requireFullBuffer: true);
                    if (count > 0)
                    {
                        if (!musicClock.IsRunning || starved.Elapsed.TotalSeconds > .1)
                        {
                            // A producer stall shifts wall time, not PCM time.
                            // Resume steadily rather than sending a catch-up burst.
                            musicClock.Restart(); musicFrames = 0;
                        }
                        musicFrames += count / AudioFormat.Channels;
                        pending.Add(inputFrames, samples[..count]); inputFrames += count / AudioFormat.Channels;
                        return count;
                    }
                    // The producer already paces PCM. Wait for it instead of
                    // inserting zeros and resetting the music at an underrun.
                    if (token.WaitHandle.WaitOne(5)) return 0;
                }
                return 0;
            }, bytes => segmenter.Push(bytes.Span), token);
            if (!token.IsCancellationRequested) throw new IOException("Chromecast audio encoder stopped unexpectedly.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = ex.Message; primed.TrySetException(ex); }
        finally
        {
            primed.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
            if (_preservePending) Buffer.Prepend(pending.Remaining());
        }
    }
    private sealed class PendingMusic
    {
        private readonly object _gate = new();
        private readonly Queue<(long Start, float[] Samples)> _chunks = new();
        private long _committed;
        public void Add(long start, ReadOnlySpan<float> samples)
        { lock (_gate) _chunks.Enqueue((start, samples.ToArray())); }
        public void CommitThrough(long frame)
        {
            lock (_gate)
            {
                _committed = Math.Max(_committed, frame);
                while (_chunks.TryPeek(out var chunk) && chunk.Start + chunk.Samples.Length / AudioFormat.Channels <= _committed)
                    _chunks.Dequeue();
            }
        }
        public float[] Remaining()
        {
            lock (_gate) return _chunks.SelectMany(c => c.Samples.Skip((int)Math.Clamp((_committed - c.Start) * AudioFormat.Channels, 0, c.Samples.Length))).ToArray();
        }
    }
    public static byte[] PackedAudio(byte[] mp3, long timestamp)
    {
        // HLS packed audio carries a 90 kHz MPEG timestamp in an ID3 PRIV frame.
        var owner = System.Text.Encoding.ASCII.GetBytes("com.apple.streaming.transportStreamTimestamp\0");
        var payload = new byte[owner.Length + 8];
        owner.CopyTo(payload, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(owner.Length), (ulong)timestamp & ((1UL << 33) - 1));
        var tag = new byte[20 + payload.Length];
        "ID3"u8.CopyTo(tag); tag[3] = 4;
        Synchsafe(tag.AsSpan(6, 4), payload.Length + 10);
        "PRIV"u8.CopyTo(tag.AsSpan(10));
        Synchsafe(tag.AsSpan(14, 4), payload.Length);
        payload.CopyTo(tag, 20);
        var offset = 0;
        if (mp3.Length >= 10 && mp3.AsSpan(0, 3).SequenceEqual("ID3"u8))
            offset = 10 + ((mp3[6] & 127) << 21) + ((mp3[7] & 127) << 14) + ((mp3[8] & 127) << 7) + (mp3[9] & 127);
        if (offset > mp3.Length) offset = 0;
        var result = new byte[tag.Length + mp3.Length - offset];
        tag.CopyTo(result, 0); mp3.AsSpan(offset).CopyTo(result.AsSpan(tag.Length));
        return result;
    }
    private static void Synchsafe(Span<byte> output, int value)
    {
        output[0] = (byte)((value >> 21) & 127); output[1] = (byte)((value >> 14) & 127);
        output[2] = (byte)((value >> 7) & 127); output[3] = (byte)(value & 127);
    }
    public async Task SetPausedAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_connected || paused == _paused) return;
            Buffer.SetTransportPaused(true);
            var token = _lifetime!.Token;
            if (paused)
            {
                // STOP discards the receiver's old HLS queue. PLAY on a paused live
                // playlist can otherwise replay old music or expired segments.
                await EndStream(preserveBuffer: true);
                await _session.StopMediaAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
                _mediaLoaded = false;
                _paused = true;
            }
            else
            {
                await ApplyVolume(token);
                await BeginStream(token, preserveBuffer: true);
                _paused = false;
                Buffer.SetTransportPaused(false);
            }
        }
        finally { _gate.Release(); }
    }
    public async Task UpdateControlsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connected) await ApplyVolume(_lifetime!.Token);
        }
        finally { _gate.Release(); }
    }
    private async Task ApplyVolume(CancellationToken token)
    {
        // All updates read the latest slider value under the lifecycle gate.
        // Queued calls skip values already applied by an earlier update.
        while (_appliedLevel != Control.Gain)
        {
            var level = Control.Gain;
            await _session.SetVolumeAsync(level).WaitAsync(TimeSpan.FromSeconds(5), token);
            _appliedLevel = level;
        }
    }
    public async Task StopAsync()
    {
        _lifetime?.Cancel();
        await _gate.WaitAsync();
        try { await StopInternal(); }
        finally { _gate.Release(); }
    }
    private async Task StopInternal()
    {
        _lifetime?.Cancel();
        await EndStream();
        if (_mediaLoaded && !_session.ConnectionLost) try { await _session.StopMediaAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        _mediaLoaded = false;
        if (_originalLevel is { } level && !_session.ConnectionLost)
            try { await _session.SetVolumeAsync(level).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        _originalLevel = null; _appliedLevel = null;
        if (_session.ConnectionLost) _error ??= _session.Error;
        if (_connected) try { await _session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _connected = false; _paused = false;
        _lifetime?.Dispose(); _lifetime = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync();
        await _session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
