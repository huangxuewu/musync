using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Musync.App;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1460, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task ImportedFolderPlaylistsAppearInTheSidebarAndOpenTheirOwnTracks(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var rootName = "清晨音乐 Morning collection with a long folder name";
        var childName = "夜の音楽 — Long evening collection";
        var folder = Path.Combine(fixture.DirectoryPath, rootName);
        var child = Path.Combine(folder, childName); Directory.CreateDirectory(child);
        var rootFile = Path.Combine(folder, "Morning.mp3"); var childFile = Path.Combine(child, "Evening.flac");
        File.WriteAllText(rootFile, "mock"); File.WriteAllText(childFile, "mock");
        await fixture.Model.ImportFoldersAsync([folder]);
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(650); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var list = window.FindControl<ItemsControl>("SidebarPlaylistList")!;
            Assert.Same(fixture.Model.Playlists, list.ItemsSource);
            var root = Assert.Single(fixture.Model.Playlists, p => p.Name == rootName);
            var nested = Assert.Single(fixture.Model.Playlists, p => p.Name == childName);
            Assert.True(root.IsActive); Assert.Equal(1, root.Count); Assert.Equal(1, nested.Count);
            var button = list.GetVisualDescendants().OfType<Button>().Single(b => b.Command == nested.OpenCommand);
            Assert.Equal(childName, Avalonia.Automation.AutomationProperties.GetName(button));
            Assert.Equal(childName, ToolTip.GetTip(button)); Assert.True(button.Bounds.Height >= 44);
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == childName);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, label.TextTrimming);
            Assert.True(label.Bounds.Right <= button.Bounds.Width);
            // The redesigned rail keeps its header and settings fixed. At
            // minimum height, scroll the target into the viewport before clicking.
            button.BringIntoView(); using var reachable = window.CaptureRenderedFrame();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Equal(childName, fixture.Model.ListTitle);
            Assert.Equal(childFile, Assert.Single(fixture.Model.Tracks).Track.FilePath);
            Assert.True(nested.IsActive); Assert.False(root.IsActive); Assert.Contains("active", button.Classes);
            Assert.False(fixture.Model.IsPlaying);
            using var selected = window.CaptureRenderedFrame(); SaveReviewFrame(selected, $"sidebar-playlists-{language}-{width}.png");
            fixture.Model.Navigation.Execute("library"); Assert.DoesNotContain(fixture.Model.Playlists, p => p.IsActive);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ManySidebarPlaylistsRemainScrollableWithoutVisibleBars()
    {
        await using var fixture = new PresentationFixture("ja");
        var folder = Path.Combine(fixture.DirectoryPath, "Collection");
        for (var i = 0; i < 24; i++)
        {
            var child = Path.Combine(folder, $"音楽 {i:00}"); Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "Song.flac"), "mock");
        }
        await fixture.Model.ImportFoldersAsync([folder]);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            await Task.Delay(650); using var frame = window.CaptureRenderedFrame();
            var scroll = window.FindControl<ScrollViewer>("SidebarScroll")!;
            Assert.Equal(ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.DoesNotContain(scroll.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.IsVisible);
            var list = window.FindControl<ItemsControl>("SidebarPlaylistList")!;
            var settings = window.FindControl<Button>("SettingsNav")!;
            var add = window.FindControl<Button>("SidebarAddPlaylistButton")!;
            Assert.DoesNotContain(scroll, settings.GetVisualAncestors());
            Assert.DoesNotContain(scroll, add.GetVisualAncestors());
            var settingsY = settings.TranslatePoint(default, window)!.Value.Y;
            var addY = add.TranslatePoint(default, window)!.Value.Y;
            var last = list.GetVisualDescendants().OfType<Button>().Last();
            last.Focus(); last.BringIntoView();
            using var scrolled = window.CaptureRenderedFrame();
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(settingsY, settings.TranslatePoint(default, window)!.Value.Y);
            Assert.Equal(addY, add.TranslatePoint(default, window)!.Value.Y);
            Assert.True(settings.IsEffectivelyVisible);
            SaveReviewFrame(scrolled, "sidebar-playlists-ja-many-1120.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RemovingAllUserPlaylistsRetainsOnlyThePinnedFavoritesPlaylist()
    {
        await using var fixture = new PresentationFixture();
        foreach (var playlist in fixture.Model.Playlists.Where(p => !p.IsFavorites).ToArray()) playlist.RemoveCommand.Execute(null);
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            await Task.Delay(650); using var frame = window.CaptureRenderedFrame();
            var list = window.FindControl<ItemsControl>("SidebarPlaylistList")!;
            var favorites = Assert.Single(fixture.Model.Playlists);
            Assert.True(favorites.IsFavorites); Assert.Equal(0, favorites.Count);
            Assert.Single(list.GetVisualDescendants().OfType<Button>(), b => b.IsVisible);
            Assert.True(list.Bounds.Height >= 44);
            SaveReviewFrame(frame, "sidebar-playlists-en-only-favorites-1120.png");
        }
        finally { window.Close(); }
    }
}
