using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task FavoritesArePinnedInPlaylistsAndFollowLikeSelectionAndCollapse(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model;
        var favorites = Assert.Single(model.Playlists, p => p.IsFavorites);
        Assert.Same(favorites, model.Playlists[0]); Assert.Equal(model.Text["Liked"], favorites.Name);
        Assert.False(favorites.CanRemove); Assert.False(favorites.RemoveCommand.CanExecute(null));
        model.ToggleLike(fixture.First.Id); Assert.Equal(1, favorites.Count);
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            await Task.Delay(250); using var home = window.CaptureRenderedFrame();
            window.ApplyMotionPreference(false);
            Assert.Null(window.FindControl<Button>("LikedNav"));
            var list = window.FindControl<ItemsControl>("SidebarPlaylistList")!;
            var open = list.GetVisualDescendants().OfType<Button>().Single(b => b.Command == favorites.OpenCommand);
            var remove = list.GetVisualDescendants().OfType<Button>().Single(b => b.Command == favorites.RemoveCommand);
            Assert.Equal(favorites.Name, AutomationProperties.GetName(open)); Assert.True(open.Bounds.Height >= 44);
            Assert.False(remove.IsVisible); Assert.Equal("heart", open.GetVisualDescendants().OfType<LucideIcon>().Single().Kind);
            var header = window.FindControl<Button>("PlaylistsNav")!;
            Assert.True(open.TranslatePoint(default, window)!.Value.Y > header.TranslatePoint(default, window)!.Value.Y);
            var ordinary = model.Playlists.First(p => !p.IsFavorites);
            var next = list.GetVisualDescendants().OfType<Button>().Single(b => b.Command == ordinary.OpenCommand);
            Assert.True(open.TranslatePoint(default, window)!.Value.Y < next.TranslatePoint(default, window)!.Value.Y);
            var count = open.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == favorites.Count.ToString());
            var countRight = count.TranslatePoint(new Point(count.Bounds.Width, 0), window)!.Value.X;
            var scroll = window.FindControl<ScrollViewer>("SidebarScroll")!;
            var viewportRight = scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), window)!.Value.X;
            Assert.True(countRight <= viewportRight - 8);
            if (language == "zh-Hans" && width == 1280) SaveReviewFrame(home, "favorites-zh-Hans-1280-home.png");

            model.Search = "unmatched";
            ClickPlaylistControl(window, open);
            Assert.Equal("", model.Search); Assert.True(model.IsLikedPage); Assert.True(favorites.IsActive);
            Assert.Contains("active", open.Classes); Assert.False(model.IsHomePage);
            Assert.Equal(fixture.First.Id, Assert.Single(model.Tracks).Track.Id);
            Assert.Equal(model.Text["Liked"], model.ListTitle); Assert.False(model.IsPlaying);
            window.MouseMove(new Point(width / 2d, 100), RawInputModifiers.None);
            await Task.Delay(650); using var selected = window.CaptureRenderedFrame();
            SaveReviewFrame(selected, $"favorites-{language}-{width}-selected.png");
            model.ToggleLike(fixture.First.Id);
            Assert.Equal(0, favorites.Count); Assert.Empty(model.Tracks); Assert.True(favorites.IsActive);
            favorites.RemoveCommand.Execute(null); Assert.Same(favorites, model.Playlists[0]);
            model.TogglePlaylists.Execute(null); Assert.False(list.IsVisible);
            model.TogglePlaylists.Execute(null); Assert.True(list.IsVisible);
            if (language == "zh-Hans" && width == 1280)
            {
                model.Navigation.Execute("playlists"); await Task.Delay(650);
                var workspace = window.FindControl<ItemsControl>("WorkspacePlaylistList")!;
                var workspaceOpen = workspace.GetVisualDescendants().OfType<Button>().Single(b => b.Command == favorites.OpenCommand);
                var workspaceRemove = workspace.GetVisualDescendants().OfType<Button>().Single(b => b.Command == favorites.RemoveCommand);
                Assert.False(workspaceRemove.IsVisible); Assert.Equal(favorites.Name, AutomationProperties.GetName(workspaceOpen));
                using var workspaceFrame = window.CaptureRenderedFrame();
                SaveReviewFrame(workspaceFrame, "favorites-zh-Hans-1280-workspace.png");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FavoritesStayDistinctFromSameNamedUserPlaylistsAndLocalizeWithoutLosingLikes()
    {
        await using var fixture = new PresentationFixture("zh-Hans");
        var model = fixture.Model;
        model.ToggleLike(fixture.First.Id);
        model.NewPlaylist.Execute(null); model.PlaylistName = model.Text["Liked"]; model.SavePlaylist.Execute(null);
        var favorites = Assert.Single(model.Playlists, p => p.IsFavorites);
        var named = Assert.Single(model.Playlists, p => !p.IsFavorites && p.Name == favorites.Name);
        Assert.Same(favorites, model.Playlists[0]); Assert.True(named.IsActive); Assert.False(favorites.IsActive);
        named.RemoveCommand.Execute(null);
        Assert.Contains(favorites, model.Playlists); Assert.DoesNotContain(named, model.Playlists);
        Assert.Contains(fixture.First.Id, fixture.Settings.LikedTracks); Assert.Equal(1, favorites.Count);
        favorites.OpenCommand.Execute(null);
        model.Text.Switch("ja"); Assert.Equal(model.Text["Liked"], favorites.Name);
        Assert.Equal(model.Text["Liked"], model.ListTitle); Assert.True(favorites.IsActive);
        Assert.Equal(fixture.First.Id, Assert.Single(model.Tracks).Track.Id);
        Assert.DoesNotContain(fixture.Settings.Playlists.Keys, n => n == favorites.Name);
    }
}
