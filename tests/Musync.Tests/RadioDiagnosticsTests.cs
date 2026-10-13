using System.Text;
using System.IO.Pipes;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class RadioDiagnosticsTests
{
    [Theory]
    [InlineData(65537)]
    [InlineData(1024 * 1024)]
    public async Task StreamingDiagnosticsKeepDrainingUntilTheSenderCloses(int length)
    {
        const string ending = "\nfinal diagnostic: stream closed\n";
        var log = new string('x', length) + ending;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(log));
        using var reader = new StreamReader(input, Encoding.UTF8);
        var tail = await RadioTools.ReadDiagnosticTailAsync(reader, CancellationToken.None);
        Assert.InRange(tail.Length, ending.Length, 65536);
        Assert.EndsWith(ending, tail);
        Assert.Equal(input.Length, input.Position);
    }
    [Fact]
    public async Task ShortDiagnosticsKeepTheirTextAndUnicodeWhileEmptyLogsStayEmpty()
    {
        foreach (var log in new[] { "", "stream started\n设备が見つかりました 🎵\n" })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(log));
            using var reader = new StreamReader(input, Encoding.UTF8);
            Assert.Equal(log, await RadioTools.ReadDiagnosticTailAsync(reader, CancellationToken.None));
        }
    }
    [Fact]
    public async Task EvictionDoesNotRetainAnOrphanedSurrogateAtTheBeginningOfTheTail()
    {
        var recent = new string('y', 65535);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65536) + "🎵" + recent));
        using var reader = new StreamReader(input, Encoding.UTF8);
        Assert.Equal(recent, await RadioTools.ReadDiagnosticTailAsync(reader, CancellationToken.None));
    }
    [Fact]
    public async Task CancellingAPendingDiagnosticReadClosesTheTaskWithoutWaitingForMorePipeData()
    {
        var name = "Musync-radio-diagnostics-" + Guid.NewGuid().ToString("N");
        await using var sender = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var input = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        using var connection = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Task.WhenAll(sender.WaitForConnectionAsync(connection.Token), input.ConnectAsync(connection.Token));
        using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
        using var cancellation = new CancellationTokenSource();
        var drain = RadioTools.ReadDiagnosticTailAsync(reader, cancellation.Token);
        Assert.False(drain.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(TimeSpan.FromSeconds(2)));
    }
    [Fact]
    public async Task LargeNativePipeLogsDoNotLeaveTheSenderBlockedBehindTheCaptureLimit()
    {
        var name = "Musync-radio-diagnostic-flood-" + Guid.NewGuid().ToString("N");
        await using var sender = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var input = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Task.WhenAll(sender.WaitForConnectionAsync(deadline.Token), input.ConnectAsync(deadline.Token));
        using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
        var drain = RadioTools.ReadDiagnosticTailAsync(reader, deadline.Token);
        const string ending = "\nlast diagnostic after one megabyte\n";
        await sender.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024) + ending), deadline.Token);
        await sender.DisposeAsync();
        var tail = await drain.WaitAsync(deadline.Token);
        Assert.EndsWith(ending, tail); Assert.Equal(65536, tail.Length);
    }
    [Fact]
    public async Task MetadataOutputRetainsItsSeparateSizeLimit()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65537)));
        using var reader = new StreamReader(input, Encoding.UTF8);
        await Assert.ThrowsAsync<IOException>(() => RadioTools.ReadLimitedAsync(reader, CancellationToken.None));
    }
}
