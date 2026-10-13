using System.Diagnostics;
using System.Threading.Channels;

namespace Musync.Core;

public sealed class FfmpegRadioDecoder(RadioTools tools) : IRadioPcmDecoder
{
    private static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(35);
    internal static async ValueTask<int> ReadFrameAsync(Stream pipe, Memory<byte> frame, TimeSpan timeout, CancellationToken token)
    {
        // Apply one deadline to the complete PCM block, including partial reads.
        // This scope ends before the bounded queue can wait for a paused consumer.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var count = 0;
        try
        {
            while (count < frame.Length)
            {
                var read = await pipe.ReadAsync(frame[count..], deadline.Token);
                if (read == 0) break;
                count += read;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("The radio stream stopped providing audio. Check your connection and retry.");
        }
        return count;
    }

    public async Task<IRadioPcmStream> PrepareAsync(RadioAudioInfo info, CancellationToken token)
    {
        var stream = new PipeStream(tools, info, token);
        try { stream.Start(); await stream.WaitUntilReadyAsync(token); return stream; }
        catch { await stream.DisposeAsync(); throw; }
    }

    private sealed class PipeStream : IRadioPcmStream
    {
        private const int ChunkSamples = AudioFormat.SamplesPerSecond / 50;
        private const int PrebufferChunks = 150;
        private readonly Channel<float[]> _queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(400)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _lifetime;
        private readonly Process _download, _decode;
        private Task _pump = Task.CompletedTask, _producer = Task.CompletedTask, _closing = Task.CompletedTask,
            _downloadDiagnostics = Task.CompletedTask, _decodeDiagnostics = Task.CompletedTask;
        private int _chunks, _disposed;

        public PipeStream(RadioTools tools, RadioAudioInfo info, CancellationToken token)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _download = tools.CreateYtDlp("-f", info.FormatId, "-o", "-", info.Address.Url);
            _decode = RadioTools.Create(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", "pipe:0", "-vn", "-ac", "2", "-ar", "44100", "-f", "f32le", "pipe:1"], input: true);
        }
        public void Start()
        {
            _decode.Start(); _download.Start();
            _downloadDiagnostics = RadioTools.ReadDiagnosticTailAsync(_download.StandardError, _lifetime.Token);
            _decodeDiagnostics = RadioTools.ReadDiagnosticTailAsync(_decode.StandardError, _lifetime.Token);
            _pump = PumpAsync(); _producer = ProduceAsync();
        }
        public async Task WaitUntilReadyAsync(CancellationToken token)
        {
            try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(40), token); }
            catch (TimeoutException) { throw new TimeoutException("The radio stream did not finish buffering. Try again."); }
        }
        private async Task PumpAsync()
        {
            try { await _download.StandardOutput.BaseStream.CopyToAsync(_decode.StandardInput.BaseStream, _lifetime.Token); }
            finally { try { _decode.StandardInput.BaseStream.Close(); } catch (ObjectDisposedException) { } }
        }
        private async Task ProduceAsync()
        {
            Exception? failure = null;
            try
            {
                while (true)
                {
                    var bytes = new byte[ChunkSamples * sizeof(float)];
                    var count = await ReadFrameAsync(_decode.StandardOutput.BaseStream, bytes, FrameReadTimeout, _lifetime.Token);
                    if (count == 0) break;
                    if (count % (sizeof(float) * AudioFormat.Channels) != 0) throw new IOException("The radio stream ended in an incomplete frame.");
                    var samples = new float[count / sizeof(float)]; Buffer.BlockCopy(bytes, 0, samples, 0, count);
                    for (var i = 0; i < samples.Length; i++) if (!float.IsFinite(samples[i])) samples[i] = 0;
                    await _queue.Writer.WriteAsync(samples, _lifetime.Token);
                    if (++_chunks >= PrebufferChunks) _ready.TrySetResult();
                    if (count < bytes.Length) break;
                }
                // EOF must not leave the source waiting forever for a helper
                // that still owns a pipe. Disposal observes all closing tasks.
                _closing = Task.WhenAll(_pump, _decode.WaitForExitAsync(_lifetime.Token), _download.WaitForExitAsync(_lifetime.Token));
                await _closing.WaitAsync(FrameReadTimeout, _lifetime.Token);
                if (_download.ExitCode != 0 || _decode.ExitCode != 0 || _chunks == 0)
                    throw new IOException("YouTube audio stopped loading. Check your connection and retry.");
                _ready.TrySetResult();
            }
            catch (Exception ex) { failure = ex; _ready.TrySetException(ex); }
            finally { _queue.Writer.TryComplete(failure); }
        }
        public async ValueTask<float[]?> ReadAsync(CancellationToken token)
        {
            if (await _queue.Reader.WaitToReadAsync(token)) return await _queue.Reader.ReadAsync(token);
            return null;
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            await RadioTools.StopOwnedAsync(_decode); await RadioTools.StopOwnedAsync(_download);
            try { await Task.WhenAll(_producer, _pump, _closing, _downloadDiagnostics, _decodeDiagnostics); }
            catch (Exception) { /* Cancellation can close an in-flight pipe read. */ }
            _decode.Dispose(); _download.Dispose(); _lifetime.Dispose();
        }
    }
}
