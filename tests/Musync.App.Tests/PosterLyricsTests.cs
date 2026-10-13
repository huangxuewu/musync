using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private sealed class PresentationBackend : IAudioBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double Position { get; private set; }
        public double Duration => 180;
        public bool IsFinished => false;
        public string? DefaultDeviceId => "test-output";
        public IReadOnlyList<SpeakerDevice> GetDevices() => [new("test-output", "Test output", SpeakerTransport.Windows)];
        public IAudioOutput CreateOutput(SpeakerDevice device) => new PresentationOutput(device);
        public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        { Position = startPosition; Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => throw new InvalidOperationException();
        public void Pause(bool paused) { }
        public void Seek(double seconds) => Position = seconds;
        public MusicTrack ProbeFile(string path) => new("local:" + Path.GetFileName(path), Path.GetFileNameWithoutExtension(path), "Test artist", path, DurationSeconds: 180);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class PresentationOutput(SpeakerDevice device) : IAudioOutput
    {
        public SpeakerDevice Device => device;
        public AudioRingBuffer Buffer { get; } = new();
        public OutputControl Control { get; } = new();
        public string? Error => null;
        public TimeSpan EstimatedLatency => TimeSpan.Zero;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class PresentationFixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Musync-Poster-Test-" + Guid.NewGuid().ToString("N"));
        public MainViewModel Model { get; }
        public AppSettings Settings { get; }
        public MusicTrack First { get; }
        public PresentationBackend Backend { get; } = new();
        public PresentationFixture(string language = "en", int palette = 0, Func<SpeakerDevice, IAudioOutput>? outputFactory = null,
            Func<CancellationToken, Task<IReadOnlyList<SpeakerDevice>>>? discoverNetwork = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "Original test.mp3"); File.WriteAllText(path, "mock audio");
            File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:01]First original test line\n[00:10]此刻一起听见音乐\n[00:20]Next original test line");
            var id = Enumerable.Range(0, 100).Select(i => "local:poster" + i).First(id => PaletteFor(id) == palette);
            First = new(id, "Original test song", "Synthetic test artist", path, DurationSeconds: 180);
            var preferences = new SpeakerPreferences { Selected = true };
            var link = new SpeakerLink { Id = "test-link", Name = "Test link", Speakers = new()
                { ["test-output"] = new() { Name = "Test output", Preferences = preferences } } };
            Settings = new() { Language = language, LinksInitialized = true, ActiveLinkId = link.Id, Links = [link], Library = [First] };
            var backend = Backend;
            Model = new(backend, new PlaybackCoordinator(backend, outputFactory ?? backend.CreateOutput),
                Client(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)))),
                new SettingsStore(Path.Combine(DirectoryPath, "settings.json")), Settings, DirectoryPath, discoverNetwork);
            Model.Speakers.Add(new(backend.GetDevices()[0], preferences, Model.Text, (_, _) => { }));
        }
        private static int PaletteFor(string id)
        { uint hash = 2166136261; foreach (var c in id) hash = unchecked((hash ^ c) * 16777619); return (int)(hash % 3); }
        public async ValueTask DisposeAsync() { await Model.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
    private static async Task WaitForLyrics(MainViewModel model)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (model.LyricCurrent == model.Text["LoadingLyrics"]) await Task.Delay(10, timeout.Token);
    }

    [AvaloniaFact]
    public async Task LyricsFollowLocalSeekPauseAndMissingTrackWithoutStaleLines()
    {
        await using var fixture = new PresentationFixture();
        await WaitForLyrics(fixture.Model); Assert.True(fixture.Model.HasSyncedLyrics);
        await fixture.Model.PlayTrackAsync(fixture.First); await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Model.SeekAsync(12);
        Assert.Equal("First original test line", fixture.Model.LyricPrevious);
        Assert.Equal("此刻一起听见音乐", fixture.Model.LyricCurrent);
        Assert.Equal("Next original test line", fixture.Model.LyricNext);
        fixture.Model.PlayCommand.Execute(null); await CommandFinished(fixture.Model.PlayCommand);
        Assert.False(fixture.Model.IsPlaying); Assert.Equal("此刻一起听见音乐", fixture.Model.LyricCurrent);
        await fixture.Model.SeekAsync(3); Assert.Equal("First original test line", fixture.Model.LyricCurrent);
        var next = fixture.First with { Id = "missing", FilePath = Path.Combine(fixture.DirectoryPath, "Missing.mp3") };
        File.WriteAllText(next.FilePath!, "mock audio");
        await fixture.Model.PlayTrackAsync(next); await WaitForLyrics(fixture.Model);
        Assert.False(fixture.Model.HasSyncedLyrics); Assert.Equal(fixture.Model.Text["NoSyncedLyrics"], fixture.Model.LyricCurrent);
        Assert.Empty(fixture.Model.LyricPrevious); Assert.Empty(fixture.Model.LyricNext);
    }

    [AvaloniaFact]
    public async Task FolderImportCreatesUnicodePlaylistsAndPreservesTrackIdentityOnReimport()
    {
        await using var fixture = new PresentationFixture();
        var root = Path.Combine(fixture.DirectoryPath, "日本語歌单"); Directory.CreateDirectory(Path.Combine(root, "子歌单"));
        File.WriteAllText(Path.Combine(root, "One.mp3"), "mock"); File.WriteAllText(Path.Combine(root, "子歌单", "Two.wav"), "mock");
        await fixture.Model.ImportFoldersAsync([root]);
        Assert.Single(fixture.Settings.Playlists["日本語歌单"]); Assert.Single(fixture.Settings.Playlists["子歌单"]);
        var ids = fixture.Settings.Library.Select(t => t.Id).ToArray();
        await fixture.Model.ImportFoldersAsync([root]); Assert.Equal(ids, fixture.Settings.Library.Select(t => t.Id));
        Assert.Contains(fixture.Model.Playlists, p => p.Name == "日本語歌单"); Assert.Equal("local", fixture.Model.Source!.Id);
    }

    [AvaloniaTheory]
    [InlineData("en", 1460, 940, 0)]
    [InlineData("zh-Hans", 1120, 720, 1)]
    [InlineData("ja", 1120, 720, 2)]
    public async Task PosterLayersAndThreeLyricsFitTheirViewport(string language, int width, int height, int palette)
    {
        await using var fixture = new PresentationFixture(language, palette);
        await fixture.Model.PlayTrackAsync(fixture.First); await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitForLyrics(fixture.Model); await fixture.Model.SeekAsync(12);
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(650); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var scene = window.FindControl<Grid>("HeroScene")!;
            var backdrop = window.FindControl<PosterBackdrop>("HeroSurface")!;
            var subject = window.FindControl<CrossfadeImage>("HeroArtwork")!;
            var lyrics = window.FindControl<Grid>("HeroLyrics")!;
            Assert.Equal(10, backdrop.ZIndex); Assert.Equal(20, subject.ZIndex);
            Assert.True(subject.Bounds.Top < backdrop.Bounds.Top); Assert.NotNull(subject.Source);
            Assert.Equal(3, lyrics.Children.Count); Assert.Equal("此刻一起听见音乐", window.FindControl<TextBlock>("CurrentLyric")!.Text);
            Assert.Empty(scene.Children.OfType<Button>());
            var point = lyrics.TranslatePoint(default, scene)!.Value;
            Assert.True(point.X >= 0 && point.Y >= 0 && point.Y + lyrics.Bounds.Height <= scene.Bounds.Height);
            SaveReviewFrame(frame, $"poster-{language}-{width}.png");
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var mini = window.CaptureRenderedFrame(); SaveReviewFrame(mini, $"poster-mini-{language}.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReducedMotionImageChangesDoNotKeepPreviousArt()
    {
        var image = new CrossfadeImage { MotionEnabled = false, Source = PosterAssets.Get(0) };
        image.Source = PosterAssets.Get(1);
        Assert.False(image.HasPreviousImage); Assert.Equal(1, image.Blend); Assert.Null(image.Transitions);
        var backdrop = new PosterBackdrop { MotionEnabled = false, Palette = 2 };
        Assert.Equal(1, backdrop.Blend); Assert.Null(backdrop.Transitions);
    }

    [AvaloniaFact]
    public async Task RapidCoverChangesSettleOnLatestImageAndReducedMotionCancelsTheFade()
    {
        var image = new CrossfadeImage { Width = 240, Height = 240, Source = PosterAssets.Get(0) };
        var window = new Window { Width = 280, Height = 280, Content = image };
        window.Show();
        try
        {
            using var initial = window.CaptureRenderedFrame();
            image.Source = PosterAssets.Get(1);
            await Task.Delay(60); using var midway = window.CaptureRenderedFrame();
            Assert.InRange(image.Blend, .001, .999); Assert.True(image.HasPreviousImage);
            image.Source = PosterAssets.Get(0);
            await Task.Delay(400); using var final = window.CaptureRenderedFrame();
            Assert.Same(PosterAssets.Get(0), image.Source); Assert.Equal(1, image.Blend); Assert.False(image.HasPreviousImage);
            image.Source = PosterAssets.Get(1); image.MotionEnabled = false;
            Assert.Equal(1, image.Blend); Assert.False(image.HasPreviousImage);
        }
        finally { window.Close(); }
    }
}
