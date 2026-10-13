using Musync.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.Versioning;

namespace Musync.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsAcousticInputProvider : IAcousticInputProvider
{
    private readonly SemaphoreSlim _lease = new(1, 1);
    private readonly Func<string, IAcousticRecorder> _factory;
    private readonly TimeSpan _shutdownTimeout;
    public WindowsAcousticInputProvider() : this(CreateNative, TimeSpan.FromSeconds(3)) { }
    internal WindowsAcousticInputProvider(Func<string, IAcousticRecorder> factory, TimeSpan shutdownTimeout)
    { _factory = factory; _shutdownTimeout = shutdownTimeout; }
    public IReadOnlyList<AcousticInputDevice> GetInputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AcousticInputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (device) result.Add(new(device.ID, device.FriendlyName));
        return result;
    }
    public async Task<IAcousticCaptureSession> StartAsync(string inputId, TimeSpan maximumDuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromSeconds(AcousticAnalyzer.MaximumCaptureSeconds))
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        if (!_lease.Wait(0)) throw new IOException("The previous microphone recording is still closing.");
        Session? session = null; IAcousticRecorder? recorder = null;
        try
        {
            recorder = _factory(inputId);
            session = new(recorder, maximumDuration, _shutdownTimeout, () => _lease.Release()); recorder = null;
            session.Start(token);
            await session.Ready.WaitAsync(TimeSpan.FromSeconds(3), token);
            token.ThrowIfCancellationRequested(); return session;
        }
        catch
        {
            if (session is not null)
            {
                ObserveFailure(session.Ready);
                try { await session.DisposeAsync(); } catch (TimeoutException) { }
            }
            else if (recorder is not null)
            {
                var cleanup = ReleaseUnusedAsync(recorder);
                ObserveFailure(cleanup);
                try { await cleanup.WaitAsync(_shutdownTimeout); } catch (TimeoutException) { }
            }
            else _lease.Release();
            throw;
        }
    }
    private async Task ReleaseUnusedAsync(IAcousticRecorder recorder)
    { try { await recorder.DisposeAsync(); } finally { _lease.Release(); } }
    private static void ObserveFailure(Task work) => _ = work.ContinueWith(failed => { _ = failed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static IAcousticRecorder CreateNative(string inputId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(inputId);
        try
        {
            if (device.DataFlow != DataFlow.Capture) throw new ArgumentException("Select a microphone input, not a speaker loopback endpoint.", nameof(inputId));
            return new NativeRecorder(device, new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().Build());
        }
        catch { device.Dispose(); throw; }
    }
    private sealed class NativeRecorder(MMDevice device, WasapiRecorder recorder) : IAcousticRecorder
    {
        public WaveFormat WaveFormat => recorder.WaveFormat;
        public event CaptureDataAvailableHandler DataAvailable { add => recorder.DataAvailable += value; remove => recorder.DataAvailable -= value; }
        public event EventHandler<StoppedEventArgs> RecordingStopped { add => recorder.RecordingStopped += value; remove => recorder.RecordingStopped -= value; }
        public void StartRecording() => recorder.StartRecording();
        public void StopRecording() => recorder.StopRecording();
        public async ValueTask DisposeAsync() { try { await recorder.DisposeAsync(); } finally { device.Dispose(); } }
    }
    private sealed class Session : IAcousticCaptureSession
    {
        private readonly IAcousticRecorder _recorder;
        private readonly AcousticCaptureAccumulator _samples;
        private readonly Action _release;
        private readonly Timer _limit;
        private readonly TimeSpan _shutdownTimeout;
        private CancellationTokenRegistration _cancellation;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private Exception? _error;
        private int _stopRequested;
        private Task? _cleanup;
        private bool _started;
        public Session(IAcousticRecorder recorder, TimeSpan duration, TimeSpan shutdownTimeout, Action release)
        {
            _release = release; _recorder = recorder; _shutdownTimeout = shutdownTimeout;
            _samples = new(_recorder.WaveFormat, duration);
            _recorder.DataAvailable += (data, flags, _, _) =>
            {
                if (Volatile.Read(ref _stopRequested) != 0) { _recorder.StopRecording(); return; }
                _samples.Append(data, flags);
                if (_samples.Error is { } error) { Fail(error); Stop(); }
                else if (_samples.Count > 0) _ready.TrySetResult();
            };
            _recorder.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null) Fail(args.Exception);
                else if (Volatile.Read(ref _stopRequested) == 0) Fail(new IOException("The microphone stopped unexpectedly."));
                _ready.TrySetException(new IOException("The microphone stopped before audio was available."));
                _stopped.TrySetResult();
            };
            _limit = new(_ => { Fail(new TimeoutException("The microphone test exceeded its duration limit.")); Stop(); }, null, duration, Timeout.InfiniteTimeSpan);
        }
        public Task Ready => _ready.Task;
        public long SamplesCaptured => _samples.Count;
        public void Start(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); _recorder.StartRecording(); _started = true;
            _cancellation = token.Register(Stop);
        }
        private void Fail(Exception error) { Interlocked.CompareExchange(ref _error, error, null); _ready.TrySetException(error); }
        private void Stop() { Interlocked.Exchange(ref _stopRequested, 1); _recorder.StopRecording(); }
        public async Task<AcousticRecording> CompleteAsync(CancellationToken token)
        {
            Stop();
            await _stopped.Task.WaitAsync(_shutdownTimeout, token);
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _error) is { } error) throw new IOException("The microphone test did not complete reliably.", error);
            lock (_gate) { ObjectDisposedException.ThrowIf(_cleanup is not null, this); return _samples.Complete(); }
        }
        public ValueTask DisposeAsync()
        {
            Task work;
            lock (_gate) work = _cleanup ??= CleanupAsync();
            ObserveFailure(work);
            return new(work.WaitAsync(_shutdownTimeout));
        }
        private async Task CleanupAsync()
        {
            Stop(); _limit.Dispose(); _cancellation.Dispose(); _samples.Clear();
            try
            {
                if (_started) await _stopped.Task;
                await _recorder.DisposeAsync();
            }
            finally { _release(); }
        }
    }
}

[SupportedOSPlatform("windows")]
internal interface IAcousticRecorder : IAsyncDisposable
{
    WaveFormat WaveFormat { get; }
    event CaptureDataAvailableHandler DataAvailable;
    event EventHandler<StoppedEventArgs> RecordingStopped;
    void StartRecording();
    void StopRecording();
}
