using Musync.Core;
using Musync.Platform.Windows;
using NAudio.Wave;
using Xunit;

namespace Musync.Tests;

public sealed class CompressedAudioTests
{
    private sealed class AudioFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Musync-Compressed-Test-" + Guid.NewGuid().ToString("N"));
        public AudioFolder() => Directory.CreateDirectory(Path);
        public string Encode(string extension, int rate, int bits, int channels, double duration = 2)
        {
            var wave = System.IO.Path.Combine(Path, "Original test.wav");
            var frames = (int)(rate * duration);
            using (var writer = new WaveFileWriter(wave, new WaveFormat(rate, bits, channels)))
            {
                var data = new byte[frames * channels * (bits / 8)];
                for (var frame = 0; frame < frames; frame++)
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var sample = frame < rate ? (1 << (bits - 3)) + (bits == 24 ? 3 : 0) : -(1 << (bits - 3));
                        for (var b = 0; b < bits / 8; b++) data[(frame * channels + channel) * (bits / 8) + b] = (byte)(sample >> (8 * b));
                    }
                writer.Write(data, 0, data.Length);
            }
            var encoded = System.IO.Path.Combine(Path, "原创测试" + extension);
            using (var source = new WaveFileReader(wave))
            {
                if (extension == ".flac") MediaFoundationEncoder.EncodeToFlac(source, encoded);
                else if (extension == ".mp3") MediaFoundationEncoder.EncodeToMp3(source, encoded, 192000);
                else MediaFoundationEncoder.EncodeToAac(source, encoded);
            }
            File.Delete(wave);
            return encoded;
        }
        public void Dispose() => Directory.Delete(Path, true);
    }

    [Theory]
    [InlineData(".flac", 44100, 16, 2)]
    [InlineData(".flac", 48000, 24, 1)]
    [InlineData(".flac", 96000, 24, 2)]
    [InlineData(".mp3", 44100, 16, 2)]
    [InlineData(".m4a", 44100, 16, 2)]
    public async Task RealCompressedFilesImportIntoTheirFolderPlaylist(string extension, int rate, int bits, int channels)
    {
        using var folder = new AudioFolder(); var path = folder.Encode(extension, rate, bits, channels);
        await using var backend = new WindowsAudioBackend();
        var result = await Task.Run(() => FolderMusicImporter.Import([folder.Path], [],
            new Dictionary<string, List<string>>(), new Dictionary<string, string>(), backend.ProbeFile));
        var track = Assert.Single(result.Tracks);
        Assert.Equal(path, track.FilePath); Assert.Equal("原创测试", track.Title);
        Assert.InRange(track.DurationSeconds, 1.9, 2.2); Assert.Equal(0, result.Skipped);
        Assert.Equal(track.Id, Assert.Single(Assert.Single(result.Playlists).Value));
    }

    [Theory]
    [InlineData(44100, 16, 2)]
    [InlineData(48000, 24, 1)]
    [InlineData(96000, 24, 2)]
    public async Task FlacPlaybackPauseAndSeekPublishSamplesFromTheRequestedPosition(int rate, int bits, int channels)
    {
        using var folder = new AudioFolder(); var path = folder.Encode(".flac", rate, bits, channels);
        await using var backend = new WindowsAudioBackend(); using var lifetime = new CancellationTokenSource();
        var hub = new AudioHub(); var buffer = new AudioRingBuffer(); hub.SetOutputs([buffer]);
        var playback = backend.PlayFileAsync(path, hub, lifetime.Token);
        try
        {
            backend.Pause(true); Assert.False(playback.IsFaulted);
            Assert.True(buffer.BufferedFrames > 0); var paused = backend.Position;
            await Task.Delay(50); Assert.Equal(paused, backend.Position);
            hub.Clear(); backend.Seek(1.5); Assert.Equal(1.5, backend.Position, 5);
            backend.Pause(false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (buffer.BufferedFrames < 441 && !playback.IsCompleted) await Task.Delay(5, deadline.Token);
            backend.Pause(true); if (playback.IsFaulted) await playback;
            var samples = new float[882]; Assert.Equal(882, buffer.Read(samples));
            Assert.True(samples.Min() < -.1f); Assert.All(samples, sample => Assert.True(sample < .02f));
            Assert.True(backend.Position > 1.5);
        }
        finally
        {
            lifetime.Cancel();
            try { await playback; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Flac24BitDecodeRetainsPrecisionBelow16BitSamplesAndReachesEof()
    {
        using var folder = new AudioFolder(); var path = folder.Encode(".flac", 44100, 24, 2);
        await using var backend = new WindowsAudioBackend();
        var hub = new AudioHub(); var buffer = new AudioRingBuffer(); hub.SetOutputs([buffer]);
        var playback = backend.PlayFileAsync(path, hub, CancellationToken.None);
        var samples = new float[882]; Assert.Equal(882, buffer.Read(samples));
        var expected = .25f + 3f / 8388608;
        Assert.All(samples, sample => Assert.InRange(sample, expected - 1e-8f, expected + 1e-8f));
        await playback.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(backend.IsFinished); Assert.Equal(2, backend.Duration, 5); Assert.Equal(2, backend.Position, 5);
    }
}
