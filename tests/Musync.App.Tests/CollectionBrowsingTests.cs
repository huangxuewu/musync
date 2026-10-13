using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;
using Musync.App;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task CollectionBrowsingShowsScopeAndRecoveryWithoutInterruptingPlayback(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12);
        await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            await Task.Delay(250); window.ApplyMotionPreference(false); using var initial = window.CaptureRenderedFrame();
            var favorites = model.Playlists.Single(p => p.IsFavorites);
            var favoriteButton = window.FindControl<ItemsControl>("SidebarPlaylistList")!.GetVisualDescendants()
                .OfType<Button>().Single(b => b.Command == favorites.OpenCommand);
            ClickPlaylistControl(window, favoriteButton);
            await Task.Delay(180);
            using var liked = window.CaptureRenderedFrame();
            Assert.Equal(model.Text["Liked"], model.SearchScope); Assert.True(model.IsEmptyCollection);
            Assert.Equal(model.Text["EmptyLikedTitle"], model.EmptyCollectionTitle);
            var browse = window.FindControl<Button>("BrowseLibraryButton")!;
            Assert.True(browse.IsEffectivelyVisible); Assert.Equal(model.Text["BrowseLibrary"], AutomationProperties.GetName(browse));
            var corner = browse.TranslatePoint(new Point(browse.Bounds.Width, browse.Bounds.Height), window)!.Value;
            Assert.InRange(corner.X, 0, width - 28); Assert.InRange(corner.Y, 0, height - 104);
            SaveReviewFrame(liked, $"empty-liked-{language}-{width}.png");
            ClickPlaylistControl(window, browse);
            Assert.True(model.IsLibraryPage); Assert.False(model.IsEmptyCollection);
            Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);

            const string name = "Late nights · 私の音楽 · 夜に駆ける collection";
            model.NewPlaylist.Execute(null); model.PlaylistName = name; model.SavePlaylist.Execute(null);
            await Task.Delay(180);
            using var empty = window.CaptureRenderedFrame();
            Assert.True(model.IsEmptyCollection); Assert.Equal(name, model.SearchScope);
            Assert.Equal(model.Text["EmptyPlaylistTitle"], model.EmptyCollectionTitle);
            Assert.True(window.FindControl<Grid>("EmptyCollectionState")!.IsEffectivelyVisible);
            SaveReviewFrame(empty, $"empty-playlist-{language}-{width}.png");
            model.OpenPlaylist(fixture.First); model.PlaylistName = name; model.SavePlaylist.Execute(null);
            model.Search = "   Original test song   ";
            Assert.Equal(fixture.First.Id, Assert.Single(model.Tracks).Track.Id);
            Assert.Equal(name, model.SearchScope); Assert.False(model.IsEmptyCollection);
            model.Search = "a song outside this collection";
            await Task.Delay(180);
            using var missing = window.CaptureRenderedFrame();
            Assert.True(model.NoSearchResults); Assert.False(model.IsEmptyCollection);
            var clear = window.FindControl<Button>("EmptySearchClearButton")!;
            Assert.True(clear.IsEffectivelyVisible); Assert.Equal(model.Text["ClearSearch"], AutomationProperties.GetName(clear));
            SaveReviewFrame(missing, $"empty-search-{language}-{width}.png");
            ClickPlaylistControl(window, clear);
            Assert.False(model.HasSearchQuery); Assert.Equal(name, model.SearchScope);
            Assert.Equal(fixture.First.Id, Assert.Single(model.Tracks).Track.Id);
            Assert.Same(window.FindControl<TextBox>("MusicSearchBox"), window.FocusManager!.GetFocusedElement());

            model.Navigation.Execute("playlists"); Assert.False(model.IsMusicView);
            model.Search = "  Original test  ";
            await Task.Delay(180);
            using var results = window.CaptureRenderedFrame();
            Assert.True(model.IsLibraryPage); Assert.True(window.FindControl<ItemsControl>("TrackList")!.IsEffectivelyVisible);
            Assert.Equal(model.Text["Library"], model.SearchScope); Assert.Equal(fixture.First.Id, Assert.Single(model.Tracks).Track.Id);
            SaveReviewFrame(results, $"overview-search-{language}-{width}.png");
            window.FindControl<TextBox>("MusicSearchBox")!.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(model.HasSearchQuery); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
            Assert.Equal(fixture.First.Title, model.Title); Assert.True(model.IsLibraryPage);
        }
        finally { window.Close(); }
    }
}
