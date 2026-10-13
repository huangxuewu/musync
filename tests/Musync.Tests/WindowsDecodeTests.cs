using Musync.Core;
using Musync.Platform.Windows;
using NAudio.Wave;
using Xunit;

namespace Musync.Tests;

public sealed class WindowsDecodeTests
{
    private static string Wave(int rate, int channels, double duration = 3)
    {
        var path = Path.Combine(Path.GetTempPath(), "Musync-decode-" + Guid.NewGuid().ToString("N") + ".wav");
        var samples = Enumerable.Range(0, (int)(rate * duration) * channels)
            .Select(index => index / channels < rate ? .25f : -.25f).ToArray();
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        writer.WriteSamples(samples, 0, samples.Length); return path;
    }
    private static async Task Finish(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    [Theory]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(96000, 2)]
    public async Task PositionTracksPublishedFramesRatherThanDecoderReadAhead(int rate, int channels)
    {
        var path = Wave(rate, channels);
        try
        {
            await using var backend = new WindowsAudioBackend(); using var lifetime = new CancellationTokenSource();
            var hub = new AudioHub(); var buffer = new AudioRingBuffer(); hub.SetOutputs([buffer]);
            var source = backend.PlayFileAsync(path, hub, lifetime.Token);
            backend.Pause(true);
            try
            {
                Assert.True(buffer.BufferedFrames > 0);
                Assert.Equal(buffer.BufferedFrames / (double)AudioFormat.SampleRate, backend.Position, 6);
                var paused = backend.Position; await Task.Delay(50); Assert.Equal(paused, backend.Position);
                Assert.False(backend.IsFinished);
            }
            finally { lifetime.Cancel(); await Finish(source); }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task SeekingWhilePausedResetsResamplerAndDoesNotPublishOldSamples()
    {
        var path = Wave(48000, 1);
        try
        {
            await using var backend = new WindowsAudioBackend(); using var lifetime = new CancellationTokenSource();
            var hub = new AudioHub(); var buffer = new AudioRingBuffer(); hub.SetOutputs([buffer]);
            var source = backend.PlayFileAsync(path, hub, lifetime.Token); backend.Pause(true);
            try
            {
                hub.Clear(); backend.Seek(1.5); Assert.Equal(1.5, backend.Position, 6);
                await Task.Delay(50); Assert.Equal(0, buffer.BufferedFrames);
                backend.Pause(false); await Until(() => buffer.BufferedFrames >= 441); backend.Pause(true);
                var decoded = new float[882]; Assert.Equal(882, buffer.Read(decoded));
                Assert.All(decoded, value => Assert.True(value < .02f)); Assert.True(decoded.Min() < -.1f);
                Assert.True(backend.Position > 1.5);
            }
            finally { lifetime.Cancel(); await Finish(source); }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task CancellationRetainsPublishedPositionIncludingStartOffset()
    {
        var path = Wave(48000, 1);
        try
        {
            await using var backend = new WindowsAudioBackend(); using var lifetime = new CancellationTokenSource();
            var hub = new AudioHub(); var buffer = new AudioRingBuffer(); hub.SetOutputs([buffer]);
            var source = backend.PlayFileAsync(path, hub, lifetime.Token, 1.2); backend.Pause(true);
            var position = backend.Position;
            lifetime.Cancel(); await Finish(source);
            Assert.Equal(position, backend.Position); Assert.Equal(1.2 + buffer.BufferedFrames / (double)AudioFormat.SampleRate, position, 6);
            Assert.False(backend.IsFinished);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task ResampledShortFileReportsEofAndCompleteDuration()
    {
        var path = Wave(48000, 1, .05);
        try
        {
            await using var backend = new WindowsAudioBackend(); var hub = new AudioHub();
            var buffer = new AudioRingBuffer(); hub.SetOutputs([buffer]);
            await backend.PlayFileAsync(path, hub, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(backend.IsFinished); Assert.Equal(.05, backend.Duration, 6); Assert.Equal(.05, backend.Position, 6);
            Assert.InRange(buffer.BufferedFrames, 2000, 2300);
        }
        finally { File.Delete(path); }
    }
}
