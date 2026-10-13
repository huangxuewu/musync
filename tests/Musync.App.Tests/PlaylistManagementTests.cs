using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static void ClickPlaylistControl(MainWindow window, Button button)
    {
        button.BringIntoView(); using var reachable = window.CaptureRenderedFrame();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    [AvaloniaTheory]
    [InlineData("en", 1460, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task SidebarPlaylistControlsCreateCollapseAndRemoveWithoutInterruptingMusic(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First); await model.SeekAsync(12);
        var title = model.Title;
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(250); using var initial = window.CaptureRenderedFrame();
            var header = window.FindControl<Button>("PlaylistsNav")!;
            var add = window.FindControl<Button>("SidebarAddPlaylistButton")!;
            var list = window.FindControl<ItemsControl>("SidebarPlaylistList")!;
            var settings = window.FindControl<Button>("SettingsNav")!;
            Assert.Equal(model.Text["CreatePlaylist"], AutomationProperties.GetName(add));
            Assert.Equal(36, add.Bounds.Width); Assert.Equal(36, add.Bounds.Height);
            Assert.Equal("plus", add.GetVisualDescendants().OfType<LucideIcon>().Single().Kind);
            var headerRight = header.TranslatePoint(new Point(header.Bounds.Width, 0), window)!.Value.X;
            var addLeft = add.TranslatePoint(default, window)!.Value.X;
            Assert.True(headerRight <= addLeft);
            var expandedSettingsTop = settings.TranslatePoint(default, window)!.Value.Y;
            ClickPlaylistControl(window, header);
            Assert.False(list.IsVisible); Assert.False(model.PlaylistsExpanded);
            Assert.Equal(model.Text["ExpandPlaylists"], AutomationProperties.GetName(header));
            using var collapsed = window.CaptureRenderedFrame();
            Assert.Equal(expandedSettingsTop, settings.TranslatePoint(default, window)!.Value.Y);
            SaveReviewFrame(collapsed, $"playlist-controls-{language}-{width}-collapsed.png");

            ClickPlaylistControl(window, add); await Task.Delay(250);
            var name = window.FindControl<TextBox>("PlaylistNameInput")!;
            Assert.True(model.PlaylistOpen); Assert.True(name.IsFocused);
            var playlistName = "清晨音乐 — Morning collection with a long name";
            window.KeyTextInput(playlistName);
            using var dialog = window.CaptureRenderedFrame();
            SaveReviewFrame(dialog, $"playlist-controls-{language}-{width}-create.png");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.False(model.PlaylistOpen); Assert.True(model.PlaylistsExpanded);
            var created = Assert.Single(model.Playlists, p => p.Name == playlistName);
            Assert.Empty(fixture.Settings.Playlists[playlistName]); Assert.True(created.IsActive);
            Assert.Equal(title, model.Title); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
            window.MouseMove(new Point(width / 2d, 110), RawInputModifiers.None);
            await Task.Delay(250); using var expanded = window.CaptureRenderedFrame();
            var open = list.GetVisualDescendants().OfType<Button>().Single(b => b.Command == created.OpenCommand);
            var remove = list.GetVisualDescendants().OfType<Button>().Single(b => b.Command == created.RemoveCommand);
            Assert.Equal(created.RemoveLabel, AutomationProperties.GetName(remove)); Assert.Equal(created.RemoveLabel, ToolTip.GetTip(remove));
            Assert.Equal("minus", remove.GetVisualDescendants().OfType<LucideIcon>().Single().Kind);
            Assert.Equal(36, remove.Bounds.Width); Assert.Equal(36, remove.Bounds.Height); Assert.True(open.Bounds.Height >= 44);
            Assert.True(open.TranslatePoint(new Point(open.Bounds.Width, 0), window)!.Value.X <= remove.TranslatePoint(default, window)!.Value.X);
            Assert.True(remove.TranslatePoint(new Point(remove.Bounds.Width, remove.Bounds.Height), window)!.Value.X < addLeft + 37);
            Assert.Equal(TextTrimming.CharacterEllipsis, open.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == playlistName).TextTrimming);
            Assert.True(add.IsFocused);
            SaveReviewFrame(expanded, $"playlist-controls-{language}-{width}-expanded.png");
            ClickPlaylistControl(window, remove); await Task.Delay(50);
            Assert.DoesNotContain(model.Playlists, p => p.Name == playlistName); Assert.True(header.IsFocused);
            Assert.True(model.IsLibraryPage); Assert.Equal(title, model.Title); Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
            Assert.True(File.Exists(fixture.First.FilePath)); Assert.Contains(model.Tracks, t => t.Track.Id == fixture.First.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RemovingImportedPlaylistRetainsFilesAndOtherFolderPlaylistsAndAllowsReimport()
    {
        await using var fixture = new PresentationFixture();
        var model = fixture.Model;
        var folder = Path.Combine(fixture.DirectoryPath, "Folder playlist");
        var child = Path.Combine(folder, "Nested playlist"); Directory.CreateDirectory(child);
        var song = Path.Combine(folder, "Morning.flac"); File.WriteAllText(song, "mock");
        var nestedSong = Path.Combine(child, "Evening.mp3"); File.WriteAllText(nestedSong, "mock");
        await model.ImportFoldersAsync([folder]);
        var root = Assert.Single(model.Playlists, p => p.Name == "Folder playlist");
        var nested = Assert.Single(model.Playlists, p => p.Name == "Nested playlist");
        nested.OpenCommand.Execute(null);
        await model.PlayTrackAsync(Assert.Single(model.Tracks).Track); await model.SeekAsync(12);
        root.RemoveCommand.Execute(null);
        Assert.True(nested.IsActive); Assert.Equal("Nested playlist", model.ListTitle);
        Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
        Assert.False(fixture.Settings.ImportedPlaylistFolders.ContainsKey(Path.GetFullPath(folder)));
        Assert.Equal("Nested playlist", fixture.Settings.ImportedPlaylistFolders[Path.GetFullPath(child)]);
        Assert.True(File.Exists(song)); Assert.True(File.Exists(nestedSong));
        Assert.Contains(fixture.Settings.Library, t => t.FilePath == song);
        await model.ImportFoldersAsync([folder]);
        Assert.Single(model.Playlists, p => p.Name == "Folder playlist");
        Assert.Single(model.Playlists, p => p.Name == "Nested playlist");
        Assert.Single(fixture.Settings.Library, t => t.FilePath == song);
    }

    [AvaloniaFact]
    public async Task EmptyPlaylistCollectionAndCollapsedStateSurviveRestartWithoutDemoReseeding()
    {
        await using var fixture = new PresentationFixture();
        fixture.Model.ToggleLike(fixture.First.Id);
        foreach (var playlist in fixture.Model.Playlists.ToArray()) playlist.RemoveCommand.Execute(null);
        fixture.Model.TogglePlaylists.Execute(null);
        var store = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json"));
        await Task.Delay(450);
        var saved = store.Load();
        Assert.Empty(saved.Playlists); Assert.True(saved.PlaylistsInitialized); Assert.False(saved.PlaylistsExpanded);
        var backend = new PresentationBackend();
        await using var restarted = new MainViewModel(backend, new PlaybackCoordinator(backend, backend.CreateOutput),
            Client(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)))), store, saved, fixture.DirectoryPath);
        var favorites = Assert.Single(restarted.Playlists);
        Assert.True(favorites.IsFavorites); Assert.Equal(1, favorites.Count); Assert.False(restarted.PlaylistsExpanded);
        favorites.OpenCommand.Execute(null);
        Assert.Equal(fixture.First.Id, Assert.Single(restarted.Tracks).Track.Id);
        restarted.NewPlaylist.Execute(null); restarted.PlaylistName = "New collection"; restarted.SavePlaylist.Execute(null);
        Assert.True(restarted.PlaylistsExpanded); Assert.Single(restarted.Playlists, p => !p.IsFavorites);
        Assert.True(restarted.Playlists[0].IsFavorites); Assert.Equal(1, restarted.Playlists[0].Count); Assert.False(restarted.IsPlaying);
    }

    [AvaloniaFact]
    public async Task PlaylistDialogValidatesNamesSupportsEscapeAndStillAddsTracksToExistingLists()
    {
        await using var fixture = new PresentationFixture("zh-Hans");
        var model = fixture.Model;
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 };
        window.Show();
        try
        {
            model.NewPlaylist.Execute(null); await Task.Delay(150);
            model.SavePlaylist.Execute(null);
            Assert.True(model.PlaylistOpen); Assert.Equal(model.Text["PlaylistNameRequired"], model.PlaylistError);
            model.PlaylistName = " sunday SLOW "; model.SavePlaylist.Execute(null);
            Assert.True(model.PlaylistOpen); Assert.Equal(model.Text["PlaylistNameExists"], model.PlaylistError);
            model.PlaylistName = "Fresh"; Assert.False(model.HasPlaylistError);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(model.PlaylistOpen); Assert.False(fixture.Settings.Playlists.ContainsKey("Fresh"));
            await Task.Delay(50); Assert.True(window.FindControl<Button>("SidebarAddPlaylistButton")!.IsFocused);
            model.OpenPlaylist(fixture.First); model.PlaylistName = "Sunday slow"; model.SavePlaylist.Execute(null);
            Assert.Contains(fixture.First.Id, fixture.Settings.Playlists["Sunday slow"]);
            model.OpenPlaylist(fixture.First); model.PlaylistName = "Sunday slow"; model.SavePlaylist.Execute(null);
            Assert.Single(fixture.Settings.Playlists["Sunday slow"], id => id == fixture.First.Id);
            model.Text.Switch("ja");
            Assert.All(model.Playlists, p => Assert.Equal(string.Format(model.Text["RemovePlaylistNamed"], p.Name), p.RemoveLabel));
            Assert.Equal(model.Text["CollapsePlaylists"], AutomationProperties.GetName(window.FindControl<Button>("PlaylistsNav")!));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WorkspaceRemoveButtonUsesSamePlaylistActionAndReducedMotionCollapseSettlesImmediately()
    {
        await using var fixture = new PresentationFixture();
        var model = fixture.Model;
        model.Navigation.Execute("playlists");
        var window = new MainWindow { DataContext = model, Width = 1460, Height = 940 };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            await Task.Delay(100); using var initial = window.CaptureRenderedFrame();
            // Activation reads Windows' setting; explicitly override it after that event.
            window.ApplyMotionPreference(false);
            var item = model.Playlists.First(p => !p.IsFavorites);
            var workspace = window.FindControl<ItemsControl>("WorkspacePlaylistList")!;
            var remove = workspace.GetVisualDescendants().OfType<Button>().Single(b => b.Command == item.RemoveCommand);
            Assert.Equal(item.RemoveLabel, AutomationProperties.GetName(remove));
            ClickPlaylistControl(window, remove); await Task.Delay(50);
            Assert.DoesNotContain(model.Playlists, p => p.Name == item.Name);
            model.TogglePlaylists.Execute(null);
            Assert.Null(window.FindControl<LucideIcon>("PlaylistChevron")!.Transitions);
            Assert.False(window.FindControl<ItemsControl>("SidebarPlaylistList")!.IsVisible);
            model.TogglePlaylists.Execute(null); await Task.Delay(50);
            var list = window.FindControl<ItemsControl>("SidebarPlaylistList")!;
            Assert.True(list.IsVisible); Assert.Equal(1, list.Opacity); Assert.Null(list.Transitions);
        }
        finally { window.Close(); }
    }
}
