using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Musync.App;
using Musync.Core;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private const string UserRadioUrl = "https://www.youtube.com/live/eZyI7epSb_w?is=DbH3UTcIZl6IAoJ6";
    private static void SaveStation(MainViewModel model, string name = "晚风電台", string url = UserRadioUrl)
    { model.RadioUrl = url; model.RadioName = name; model.SaveRadio.Execute(null); }

    [AvaloniaFact]
    public async Task RadioSaveRejectsInvalidAndDuplicateUrlsAndPersistsRemoval()
    {
        await using var fixture = new PresentationFixture(); var model = fixture.Model;
        model.Navigation.Execute("radio"); SaveStation(model, url: "https://example.com/audio");
        Assert.True(model.HasRadioError); Assert.Empty(model.RadioStations);
        SaveStation(model); Assert.False(model.HasRadioError);
        Assert.Equal("https://www.youtube.com/watch?v=eZyI7epSb_w", Assert.Single(model.RadioStations).Url);
        Assert.Equal("晚风電台", Assert.Single(fixture.Settings.RadioStations).Name);
        Assert.Empty(model.RadioUrl); SaveStation(model); Assert.True(model.HasRadioError); Assert.Single(model.RadioStations);
        await Task.Delay(400);
        var store = new SettingsStore(Path.Combine(fixture.DirectoryPath, "settings.json")); Assert.Single(store.Load().RadioStations);
        var remove = model.RadioStations[0].Remove; remove.Execute(null); await CommandFinished(remove);
        await Task.Delay(400); Assert.Empty(store.Load().RadioStations); Assert.True(model.NoRadioStations);
    }
    [AvaloniaFact]
    public async Task BrowsingRadioPreservesLocalPlaybackAndPlaylistSelectionLeavesRadioPage()
    {
        await using var fixture = new PresentationFixture(); var model = fixture.Model;
        await model.PlayTrackAsync(fixture.First, 12); Assert.True(model.IsPlaying);
        model.Navigation.Execute("radio"); Assert.True(model.IsRadioNav); Assert.True(model.IsRadioPage);
        Assert.True(model.IsPlaying); Assert.Equal("local", model.Source!.Id); SaveStation(model);
        Assert.True(model.IsPlaying); Assert.Equal(12, model.Position);
        fixture.Settings.Playlists["Synthetic playlist"] = [fixture.First.Id];
        // Import refreshes the list through the real folder workflow.
        var folder = Path.Combine(fixture.DirectoryPath, "Synthetic playlist"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Song.mp3"), "mock"); await model.ImportFoldersAsync([folder]);
        model.Navigation.Execute("radio");
        model.Playlists.First(p => p.Name == "Synthetic playlist").OpenCommand.Execute(null);
        Assert.False(model.IsRadioPage); Assert.False(model.IsRadioNav); Assert.True(model.IsMusicView);
    }
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task RadioControlsFitMinimumViewportWithAccessibleLabelsAndHiddenScrollbars(string language)
    {
        await using var fixture = new PresentationFixture(language); var model = fixture.Model;
        SaveStation(model); model.Navigation.Execute("radio");
        var window = new MainWindow { DataContext = model, Width = 1120, Height = 720 }; window.Show();
        try
        {
            await Task.Delay(500); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var page = window.FindControl<RadioView>("RadioPage")!;
            foreach (var name in new[] { "RadioUrlInput", "RadioNameInput", "SaveRadioButton" })
            {
                var control = page.FindControl<Control>(name)!; var point = control.TranslatePoint(default, window)!.Value;
                Assert.True(control.IsEffectivelyVisible); Assert.True(control.Bounds.Height >= 44);
                Assert.InRange(point.X, 0, window.Bounds.Width - control.Bounds.Width);
                Assert.InRange(point.Y, 0, window.Bounds.Height - control.Bounds.Height);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)));
            }
            var navigation = window.FindControl<Button>("RadioNav")!; Assert.Contains("active", navigation.Classes);
            var scroll = page.FindControl<ScrollViewer>("RadioScroll")!;
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b)?.Contains("晚风電台") == true);
        }
        finally { window.Close(); }
    }

    private sealed class RadioTestBackend : IAudioBackend, IYouTubeRadioBackend
    {
        public TaskCompletionSource? ResolveGate;
        public Exception? ResolveError;
        public TaskCompletionSource? StreamFailure;
        public readonly TaskCompletionSource StreamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resolving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Plays, Cancellations, Lookups; public double PositionValue; public bool Paused;
        public double Position => PositionValue; public double Duration => 180; public bool IsFinished => false;
        public string? DefaultDeviceId => "test-output";
        public IReadOnlyList<SpeakerDevice> GetDevices() => [new("test-output", "Test output", SpeakerTransport.Windows)];
        public IAudioOutput CreateOutput(SpeakerDevice device) => new PresentationOutput(device);
        public bool IsStreamingSource(string source) => YouTubeAddress.TryParse(source, out _);
        public async Task<MusicTrack> ResolveRadioAsync(YouTubeAddress address, CancellationToken token)
        {
            Lookups++;
            Resolving.TrySetResult();
            try { if (ResolveGate is not null) await ResolveGate.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancellations++; throw; }
            if (ResolveError is not null) throw ResolveError;
            return new("youtube:" + address.VideoId, "Synthetic resolved title", "Synthetic channel", address.Url, DurationSeconds: 180);
        }
        public async Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0)
        {
            Plays++; StreamStarted.TrySetResult();
            if (StreamFailure is { } failure) await failure.Task.WaitAsync(token);
            else await Task.Delay(Timeout.Infinite, token);
        }
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public void Pause(bool paused) => Paused = paused; public void Seek(double seconds) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RadioFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Musync-Radio-UI-" + Guid.NewGuid());
        public RadioTestBackend Backend { get; } = new(); public MainViewModel Model { get; }
        public PlaybackCoordinator Player { get; }
        public RadioFixture(string language = "en", Func<SpeakerDevice, IAudioOutput>? outputFactory = null)
        {
            Directory.CreateDirectory(_directory);
            var preferences = new SpeakerPreferences { Selected = true };
            var link = new SpeakerLink { Id = "test-link", Name = "Test link", Speakers = new() { ["test-output"] = new() { Name = "Test output", Preferences = preferences } } };
            var settings = new AppSettings { Language = language, LinksInitialized = true, ActiveLinkId = link.Id, Links = [link] };
            Player = new(Backend, outputFactory ?? Backend.CreateOutput);
            Model = new(Backend, Player,
                Client(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)))),
                new SettingsStore(Path.Combine(_directory, "settings.json")), settings, _directory);
            Model.Speakers.Add(new(Backend.GetDevices()[0], preferences, Model.Text, (_, _) => { }));
        }
        public async ValueTask DisposeAsync() { await Model.DisposeAsync(); Directory.Delete(_directory, true); }
    }
    [AvaloniaFact]
    public async Task RadioPlaybackHooksSharedControlsAndSourceSwitchCancelsMetadataLoading()
    {
        await using var fixture = new RadioFixture(); var model = fixture.Model;
        model.Source = model.Sources.First(s => s.Id == "youtube"); await SourceTransitionSettled(model);
        Assert.False(model.PlayEnabled);
        SaveStation(model, name: ""); var station = model.RadioStations[0].Station;
        Assert.True(model.PlayEnabled); model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand);
        Assert.True(model.YouTubeMode); Assert.True(model.IsPlaying); Assert.False(model.Seekable);
        Assert.False(model.CanLikeCurrent); model.LikeCurrent.Execute(null); Assert.False(model.CurrentLiked);
        Assert.Equal("Synthetic resolved title", model.Title); Assert.Equal(180, model.Duration);
        Assert.Equal("Synthetic resolved title", Assert.Single(model.RadioStations).Name);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); Assert.False(model.IsPlaying);
        model.PlayCommand.Execute(null); await CommandFinished(model.PlayCommand); Assert.True(model.IsPlaying);
        model.Source = model.Sources.First(s => s.Id == "local"); await SourceTransitionSettled(model);
        fixture.Backend.ResolveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = model.PlayRadioAsync(station);
        for (var i = 0; i < 200 && !model.RadioLoading; i++) await Task.Delay(5);
        Assert.True(model.RadioLoading); Assert.True(model.IsPreparingPlayback);
        model.Source = model.Sources.First(s => s.Id == "local"); await loading; await SourceTransitionSettled(model);
        Assert.Equal(1, fixture.Backend.Cancellations); Assert.False(model.RadioLoading); Assert.False(model.IsPlaying);
        Assert.False(model.IsPreparingPlayback); Assert.False(model.HasRadioError); Assert.Equal("local", model.Source!.Id);
    }
    [AvaloniaFact]
    public async Task ShutdownCancelsReceiverPreparationBeforeWaitingForThePlaybackGate()
    {
        PreparingOutput? output = null;
        var fixture = new PresentationFixture(outputFactory: device => output = new(device));
        var start = fixture.Model.PlayTrackAsync(fixture.First);
        Assert.True(fixture.Model.IsPreparingPlayback); Assert.NotNull(output);
        await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await start.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(fixture.Model.IsPlaying);
    }
}
