using System.IO.Pipes;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class RadioPipeTimeoutTests
{
    private sealed class NativePipe : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(5));
        public NamedPipeServerStream Sender { get; }
        public NamedPipeClientStream Reader { get; }
        private NativePipe(string name)
        {
            Sender = new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Reader = new(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        }
        public static async Task<NativePipe> ConnectAsync()
        {
            var pipe = new NativePipe("Musync-radio-audio-" + Guid.NewGuid().ToString("N"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await Task.WhenAll(pipe.Sender.WaitForConnectionAsync(deadline.Token), pipe.Reader.ConnectAsync(deadline.Token));
                return pipe;
            }
            catch { await pipe.DisposeAsync(); throw; }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => Sender.WriteAsync(bytes, _lifetime.Token);
        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            await Sender.DisposeAsync(); await Reader.DisposeAsync(); _lifetime.Dispose();
        }
    }

    [Fact]
    public async Task AConnectedNativePipeThatStopsSendingAudioCompletesWithTimeoutInsteadOfWaitingForever()
    {
        await using var pipe = await NativePipe.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var read = FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, new byte[1024], TimeSpan.FromMilliseconds(400), cancellation.Token).AsTask();
        try
        {
            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(read, first);
            await Assert.ThrowsAsync<TimeoutException>(() => read);
            Assert.False(cancellation.IsCancellationRequested);
        }
        finally
        {
            cancellation.Cancel();
            try { await read; } catch (OperationCanceledException) { } catch (TimeoutException) { }
        }
    }

    [Fact]
    public async Task PartialNativeReadsRetainByteOrderAndEofDoesNotBecomeATimeout()
    {
        await using var pipe = await NativePipe.ConnectAsync();
        var expected = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        var actual = new byte[128];
        var read = FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, actual, TimeSpan.FromSeconds(2), CancellationToken.None).AsTask();
        var offset = 0;
        foreach (var size in new[] { 1, 2, 7, 53, 65 })
        {
            await pipe.WriteAsync(expected.AsMemory(offset, size)); offset += size;
            await Task.Delay(10);
        }
        Assert.Equal(128, await read.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(expected, actual);
        var tail = new byte[8];
        var finalRead = FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, tail, TimeSpan.FromSeconds(2), CancellationToken.None).AsTask();
        await pipe.WriteAsync(new byte[] { 17, 29, 31 });
        await pipe.Sender.DisposeAsync();
        Assert.Equal(3, await finalRead.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new byte[] { 17, 29, 31 }, tail[..3]);
        Assert.Equal(0, await FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, tail, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task ATrickleOfPartialBytesCannotKeepAnIncompleteAudioFrameAliveForever()
    {
        await using var pipe = await NativePipe.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var bytes = new byte[65536];
        var read = FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, bytes, TimeSpan.FromMilliseconds(400), cancellation.Token).AsTask();
        var written = 0;
        async Task Trickle()
        {
            while (true)
            {
                await pipe.Sender.WriteAsync(new byte[] { 42 }, cancellation.Token);
                Interlocked.Increment(ref written);
                await Task.Delay(30, cancellation.Token);
            }
        }
        var writer = Trickle();
        try
        {
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2))));
            await Assert.ThrowsAsync<TimeoutException>(() => read);
            Assert.False(cancellation.IsCancellationRequested);
            Assert.True(Volatile.Read(ref written) > 1); Assert.Equal(42, bytes[0]);
        }
        finally
        {
            cancellation.Cancel();
            try { await writer; } catch (OperationCanceledException) { }
            try { await read; } catch (OperationCanceledException) { } catch (TimeoutException) { }
        }
    }

    [Fact]
    public async Task UserCancellationInterruptsANativeReadWithoutBeingReportedAsNetworkTimeout()
    {
        await using var pipe = await NativePipe.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var read = FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, new byte[1024], TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
        Assert.False(read.IsCompleted); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ConsumerIdleTimeDoesNotCarryAnExpiredDeadlineIntoTheNextAudioBlock()
    {
        await using var pipe = await NativePipe.ConnectAsync();
        var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var firstWrite = pipe.WriteAsync(expected).AsTask();
        var actual = new byte[8];
        Assert.Equal(8, await FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, actual, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        await firstWrite;
        await Task.Delay(500); // Longer than the completed block's deadline.
        var nextWrite = pipe.WriteAsync(expected).AsTask();
        Assert.Equal(8, await FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, actual, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        await nextWrite;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ATimedOutNativeReadCannotStealBytesFromALaterRead()
    {
        await using var pipe = await NativePipe.ConnectAsync();
        var old = FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, new byte[8], TimeSpan.FromMilliseconds(100), CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => old.WaitAsync(TimeSpan.FromSeconds(2)));
        var expected = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 }; var actual = new byte[8];
        var write = pipe.WriteAsync(expected).AsTask();
        Assert.Equal(8, await FfmpegRadioDecoder.ReadFrameAsync(pipe.Reader, actual, TimeSpan.FromSeconds(1), CancellationToken.None));
        await write;
        Assert.Equal(expected, actual);
    }
}
