using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Musync.App;
using Musync.Core;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static Handler LocalCacheHandler() => new((request, _) => Task.FromResult(request.RequestUri!.Host == "api.spotify.com"
        ? Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + ListTrack(0).Replace("\"Song\"", "\"Original test song\"").Replace("\"Artist\"", "\"Synthetic test artist\"") + "]}}")
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ListCover()) }));
    private static int IndexedFiles(PresentationFixture fixture)
    {
        var path = Path.Combine(fixture.DirectoryPath, "artwork-cache", "cache-index.json");
        if (!File.Exists(path)) return 0;
        using var json = JsonDocument.Parse(File.ReadAllBytes(path)); return json.RootElement.EnumerateObject().Count();
    }

    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task CacheCapacitySettingIsReachableLocalizedAndPersists(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        fixture.Model.SettingsOpen = true;
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        try
        {
            window.Show(); await Task.Delay(650); using var initial = window.CaptureRenderedFrame();
            var capacity = window.FindControl<NumericUpDown>("ArtworkCacheLimitInput")!;
            Assert.Equal(500, capacity.Value); Assert.Equal(fixture.Model.Text["ArtworkCacheLimit"], AutomationProperties.GetName(capacity));
            var origin = capacity.TranslatePoint(default, window)!.Value;
            Assert.InRange(origin.Y, 0, height - capacity.Bounds.Height); Assert.Equal(140, capacity.Bounds.Width);
            Assert.Equal(ScrollBarVisibility.Hidden, capacity.GetVisualAncestors().OfType<ScrollViewer>().First().VerticalScrollBarVisibility);
            Assert.True(capacity.GetVisualDescendants().OfType<TextBox>().Single().Focus()); capacity.Value = 750;
            Assert.Equal(750, fixture.Model.ArtworkCacheLimitMb);
            using var changed = window.CaptureRenderedFrame(); SaveReviewFrame(changed, $"artwork-cache-settings-{language}-{width}.png");
            fixture.Model.CloseSettings.Execute(null); await CommandFinished(fixture.Model.CloseSettings);
            var settings = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")).Load();
            Assert.Equal(750, settings.ArtworkCacheLimitMb); Assert.False(fixture.Model.SettingsOpen); Assert.False(fixture.Model.IsPlaying);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RemovingLibrarySongDropsAllPlaylistReferencesAndItsCacheWhileKeepingAudio()
    {
        await using var fixture = new PresentationFixture(spotifyHandler: LocalCacheHandler());
        fixture.Settings.Playlists["Collection"] = [fixture.First.Id]; fixture.Model.Navigation.Execute("library");
        var row = fixture.Model.Tracks.Single(t => t.Track.Id == fixture.First.Id); await row.EnsureArtworkAsync();
        Assert.True(row.HasArtwork); Assert.Equal(3, IndexedFiles(fixture));
        await fixture.Model.RemoveTrackAsync(fixture.First);
        Assert.DoesNotContain(fixture.Model.Tracks, t => t.Track.Id == fixture.First.Id);
        Assert.DoesNotContain(fixture.Settings.Library, t => t.Id == fixture.First.Id);
        Assert.Empty(fixture.Settings.Playlists["Collection"]); Assert.Equal(0, IndexedFiles(fixture));
        Assert.Equal("mock audio", File.ReadAllText(fixture.First.FilePath!));
    }

    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task SongRemoveButtonFitsTheRowAndCleansCacheAfterClick(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language, spotifyHandler: LocalCacheHandler());
        fixture.Model.Navigation.Execute("library");
        var row = fixture.Model.Tracks.Single(t => t.Track.Id == fixture.First.Id); await row.EnsureArtworkAsync();
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        try
        {
            window.Show(); await Task.Delay(650); using var initial = window.CaptureRenderedFrame();
            var remove = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == row.RemoveCommand);
            Assert.Equal(36, remove.Bounds.Width); Assert.Equal(row.RemoveLabel, AutomationProperties.GetName(remove));
            var position = remove.TranslatePoint(default, window)!.Value;
            Assert.InRange(position.X, 0, width - 36); Assert.InRange(position.Y, 0, height - 36);
            SaveReviewFrame(initial, $"artwork-cache-row-{language}-{width}.png");
            ClickPlaylistControl(window, remove); await Until(() => IndexedFiles(fixture) == 0);
            Assert.DoesNotContain(fixture.Model.Tracks, t => t.Track.Id == fixture.First.Id); Assert.True(File.Exists(fixture.First.FilePath));
            Assert.False(fixture.Model.IsPlaying);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RemovingPlaylistRetainsSharedTrackCacheUntilTheLastPlaylistIsRemoved()
    {
        await using var fixture = new PresentationFixture(spotifyHandler: LocalCacheHandler());
        fixture.Model.OpenPlaylist(fixture.First); fixture.Model.PlaylistName = "First"; fixture.Model.SavePlaylist.Execute(null);
        fixture.Model.OpenPlaylist(fixture.First); fixture.Model.PlaylistName = "Second"; fixture.Model.SavePlaylist.Execute(null);
        await fixture.Model.Tracks.Single(t => t.Track.Id == fixture.First.Id).EnsureArtworkAsync();
        Assert.Equal(3, IndexedFiles(fixture));
        var first = fixture.Model.Playlists.Single(p => p.Name == "First"); first.RemoveCommand.Execute(null); await CommandFinished(first.RemoveCommand);
        Assert.Equal(3, IndexedFiles(fixture));
        var second = fixture.Model.Playlists.Single(p => p.Name == "Second"); second.OpenCommand.Execute(null);
        second.RemoveCommand.Execute(null); await CommandFinished(second.RemoveCommand);
        Assert.Equal(0, IndexedFiles(fixture)); Assert.True(fixture.Model.IsLibraryPage);
        await fixture.Model.Tracks.Single(t => t.Track.Id == fixture.First.Id).EnsureArtworkAsync();
        Assert.Equal(0, IndexedFiles(fixture)); Assert.True(File.Exists(fixture.First.FilePath));
    }

    [AvaloniaFact]
    public async Task RemovingSongFromOnePlaylistKeepsTheOtherPlaylistAndThenCleansItsLastReference()
    {
        await using var fixture = new PresentationFixture(spotifyHandler: LocalCacheHandler());
        foreach (var name in new[] { "First", "Second" })
        { fixture.Model.OpenPlaylist(fixture.First); fixture.Model.PlaylistName = name; fixture.Model.SavePlaylist.Execute(null); }
        var first = fixture.Model.Playlists.Single(p => p.Name == "First"); first.OpenCommand.Execute(null);
        var row = Assert.Single(fixture.Model.Tracks); await row.EnsureArtworkAsync();
        Assert.Equal(string.Format(fixture.Model.Text["RemoveTrackFromPlaylist"], fixture.First.Title), row.RemoveLabel);
        await fixture.Model.RemoveTrackAsync(fixture.First);
        Assert.Empty(fixture.Settings.Playlists["First"]); Assert.Single(fixture.Settings.Playlists["Second"]); Assert.Equal(3, IndexedFiles(fixture));
        fixture.Model.Playlists.Single(p => p.Name == "Second").OpenCommand.Execute(null);
        await fixture.Model.RemoveTrackAsync(fixture.First);
        Assert.Equal(0, IndexedFiles(fixture)); Assert.True(File.Exists(fixture.First.FilePath));
    }
}
