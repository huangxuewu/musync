using Avalonia.Headless.XUnit;
using Musync.App;
using Musync.Core;
using Musync.Platform.Windows;
using NAudio.Wave;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task ImportingRealFlacOpensItsPlaylistAndClearsUnrelatedFilters(string language)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-Flac-App-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(directory, "夜の音乐"); Directory.CreateDirectory(folder);
        var flac = Path.Combine(folder, "原创测试.flac");
        var samples = new byte[44100 * 2 * 4];
        using (var pcm = new MemoryStream(samples))
        using (var source = new RawSourceWaveStream(pcm, new WaveFormat(44100, 16, 2)))
            MediaFoundationEncoder.EncodeToFlac(source, flac);
        var backend = new WindowsAudioBackend();
        var settings = new AppSettings { Language = language, LinksInitialized = true };
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var model = new MainViewModel(backend, new PlaybackCoordinator(backend, backend.CreateOutput),
            Client(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)))), store, settings, directory);
        try
        {
            model.Navigation.Execute("liked"); model.Search = "unrelated search";
            await model.ImportFoldersAsync([folder]);
            var track = Assert.Single(model.Tracks).Track;
            Assert.Equal(flac, track.FilePath); Assert.Equal("原创测试", track.Title);
            Assert.Equal("", model.Search); Assert.True(model.IsMusicView); Assert.Equal("local", model.Source!.Id);
            Assert.Equal("夜の音乐", model.ListTitle);
            Assert.Equal(track.Id, Assert.Single(settings.Playlists["夜の音乐"]));
            Assert.Equal(2, track.DurationSeconds, 3);
            await model.ImportFoldersAsync([folder]);
            Assert.Equal(track.Id, Assert.Single(model.Tracks).Track.Id);
            Assert.Single(settings.Playlists["夜の音乐"]);
        }
        finally { await model.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
