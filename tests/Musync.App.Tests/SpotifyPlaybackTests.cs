using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Musync.App;
using Musync.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Musync.Tests.SpotifyTestApplication))]
[assembly: SupportedOSPlatform("windows")]

namespace Musync.Tests;

public static class SpotifyTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Musync.App.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed partial class SpotifyPlaybackTests
{
    private const string ClientId = "0123456789abcdef0123456789abcdef";
    private const string TrackJson = "{\"id\":\"song\",\"name\":\"Song\",\"uri\":\"spotify:track:song\",\"duration_ms\":180000,\"artists\":[{\"name\":\"Artist\"}],\"album\":{\"images\":[]}}";
    private static readonly MusicTrack Song = new("song", "Song", "Artist", null, "spotify:track:song", "https://i.scdn.co/image/test", 180);
    private sealed class Vault : ITokenVault
    {
        private string? _json = JsonSerializer.Serialize(new SpotifyTokens("test-access", "test-refresh", DateTime.UtcNow.AddHours(1)));
        public string? Load() => _json;
        public void Save(string json) => _json = json;
        public void Clear() => _json = null;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Respond(request, token);
        public Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        { Calls++; return respond(request, token); }
    }
    private sealed class Backend : IAudioBackend
    {
        public IReadOnlyList<SpeakerDevice> GetDevices() => [];
        public string? DefaultDeviceId => null;
        public IAudioOutput CreateOutput(SpeakerDevice device) => throw new InvalidOperationException("No hardware may be opened in these tests.");
        public Task PlayFileAsync(string path, AudioHub hub, CancellationToken token, double startPosition = 0) => throw new InvalidOperationException("No local playback expected.");
        public Task CaptureAsync(string id, AudioHub hub, CancellationToken token) => throw new InvalidOperationException("No capture expected.");
        public void Pause(bool paused) { }
        public void Seek(double seconds) { }
        public double Position => 0;
        public double Duration => 0;
        public bool IsFinished => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Musync-Spotify-Test-" + Guid.NewGuid().ToString("N"));
        public MainViewModel Model { get; }
        public Fixture(Handler handler, string language = "en", bool observePlayback = false,
            Func<CancellationToken, Task<IReadOnlyList<SpeakerDevice>>>? discoverNetwork = null)
        {
            Directory.CreateDirectory(_directory);
            var backend = new Backend();
            var initialDevices = true;
            var client = Client(new Handler((request, token) =>
            {
                if (!observePlayback && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/me/player")
                    return Task.FromResult(Response(HttpStatusCode.NoContent));
                if (initialDevices && request.RequestUri!.AbsolutePath == "/v1/me/player/devices")
                {
                    initialDevices = false;
                    return Task.FromResult(Response(HttpStatusCode.OK,
                        "{\"devices\":[{\"id\":\"desktop\",\"name\":\"Desktop\",\"type\":\"computer\",\"is_active\":false}]}"));
                }
                return handler.Respond(request, token);
            }));
            var settings = new AppSettings { Language = language, SpotifyClientId = ClientId, LinksInitialized = true };
            Model = new(backend, new PlaybackCoordinator(backend, _ => throw new InvalidOperationException("No output expected.")),
                client, new SettingsStore(Path.Combine(_directory, "settings.json")), settings, _directory, discoverNetwork);
            Model.Source = Model.Sources.First(s => s.Id == "spotify");
        }
        public async ValueTask DisposeAsync()
        {
            await Model.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
    private static SpotifyClient Client(Handler handler) => new(new Vault(), new HttpClient(handler)) { ClientId = ClientId };
    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status)
    { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
    private static async Task CommandFinished(System.Windows.Input.ICommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!command.CanExecute(null)) await Task.Delay(10, timeout.Token);
    }

    [AvaloniaFact]
    public async Task InvalidArtworkDoesNotBlockPlaybackOrReplacePlayingStatus()
    {
        var requests = new List<string>();
        var handler = new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "api.spotify.com") return Task.FromResult(Response(HttpStatusCode.NoContent));
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Response(HttpStatusCode.OK, "not an image"));
        });
        await using var fixture = new Fixture(handler);
        await fixture.Model.PlayTrackAsync(Song);
        Assert.Equal(["api.spotify.com", "i.scdn.co"], requests);
        Assert.True(fixture.Model.IsPlaying);
        Assert.Equal(fixture.Model.Text["Playing"], fixture.Model.Status);
        Assert.Equal(180, fixture.Model.Duration);
        Assert.Null(fixture.Model.SpotifyArtwork);
    }

    [AvaloniaFact]
    public async Task ASlowArtworkRequestDoesNotDelayPlayCompletion()
    {
        var cover = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((request, _) => request.RequestUri!.Host == "api.spotify.com"
            ? Task.FromResult(Response(HttpStatusCode.NoContent)) : cover.Task);
        await using var fixture = new Fixture(handler);
        try
        {
            await fixture.Model.PlayTrackAsync(Song).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(fixture.Model.IsPlaying);
        }
        finally { cover.TrySetResult(Response(HttpStatusCode.ServiceUnavailable)); await Task.Yield(); }
    }

    [AvaloniaFact]
    public async Task FirstPlayResumesSpotifyWithoutRequiringACurrentTrack()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/v1/me/player/play", request.RequestUri!.AbsolutePath);
            Assert.Contains("device_id=desktop", request.RequestUri.Query);
            Assert.Null(request.Content);
            return Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler);
        fixture.Model.SpotifyDevice = new("desktop", "Desktop");
        await fixture.Model.PlayTrackAsync(null);
        Assert.True(fixture.Model.IsPlaying);
        Assert.Equal(1, handler.Calls);
    }

    [AvaloniaFact]
    public async Task SignedInSourceRefreshesDevicesBeforeTheFirstPlay()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Only the initial device refresh is expected."));
        await using var fixture = new Fixture(handler);
        Assert.Equal("desktop", fixture.Model.SpotifyDevice?.Id);
        Assert.Single(fixture.Model.SpotifyDevices);
        Assert.False(fixture.Model.IsPlaying);
    }

    [AvaloniaFact]
    public async Task PlayRefreshesAnEmptyDeviceSelectionBeforeSendingTheCommand()
    {
        var paths = new List<string>();
        var handler = new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.Method == HttpMethod.Get ? Response(HttpStatusCode.OK,
                "{\"devices\":[{\"id\":\"ready\",\"name\":\"Ready\",\"type\":\"computer\",\"is_active\":false}]}")
                : Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler);
        fixture.Model.SpotifyDevice = null;
        await fixture.Model.PlayTrackAsync(Song with { ImageUrl = null });
        Assert.Equal(["/v1/me/player/devices", "/v1/me/player/play"], paths);
        Assert.True(fixture.Model.IsPlaying); Assert.Equal("ready", fixture.Model.SpotifyDevice?.Id);
    }

    [AvaloniaFact]
    public async Task SearchChangingDuringARequestDiscardsLateResults()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => old.Task);
        await using var fixture = new Fixture(handler);
        fixture.Model.Search = "old"; fixture.Model.SearchCommand.Execute(null);
        Assert.True(fixture.Model.IsSearching);
        fixture.Model.Search = "new";
        old.SetResult(Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + TrackJson + "]}}"));
        await CommandFinished(fixture.Model.SearchCommand);
        Assert.Empty(fixture.Model.Tracks); Assert.False(fixture.Model.IsSearching);
        Assert.False(fixture.Model.NoSearchResults);
        Assert.Equal(fixture.Model.Text["SearchSpotifyHint"], fixture.Model.SearchNote);
    }

    [AvaloniaFact]
    public async Task SearchResultsCanBeClearedWithoutLeavingOldSpotifyRows()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + TrackJson + "]}}")));
        await using var fixture = new Fixture(handler);
        fixture.Model.Search = "song"; fixture.Model.SearchCommand.Execute(null);
        await CommandFinished(fixture.Model.SearchCommand);
        Assert.Single(fixture.Model.Tracks); Assert.False(fixture.Model.ShowHero);
        fixture.Model.ClearSearch.Execute(null);
        Assert.Empty(fixture.Model.Tracks); Assert.False(fixture.Model.HasSearchQuery); Assert.True(fixture.Model.ShowHero);
    }

    [AvaloniaFact]
    public async Task SearchErrorsAreLocalizedAndDoNotShowAnEmptyResultState()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden,
            "{\"error\":{\"reason\":\"PREMIUM_REQUIRED\"}}")));
        await using var fixture = new Fixture(handler, "zh-Hans");
        fixture.Model.Search = "song"; fixture.Model.SearchCommand.Execute(null);
        await CommandFinished(fixture.Model.SearchCommand);
        Assert.True(fixture.Model.HasSearchError); Assert.False(fixture.Model.NoSearchResults);
        Assert.Equal(fixture.Model.Text["SpotifyPremiumRequired"], fixture.Model.SearchNote);
        Assert.Contains("Premium", fixture.Model.SearchNote);
    }

    [AvaloniaFact]
    public async Task RestrictedDevicesAreNotOfferedAndRefreshPreservesTheChosenDevice()
    {
        const string devices = "{\"devices\":[{\"id\":\"active\",\"name\":\"Active\",\"type\":\"computer\",\"is_active\":true},"
            + "{\"id\":\"chosen\",\"name\":\"Chosen\",\"type\":\"speaker\",\"is_active\":false},"
            + "{\"id\":\"locked\",\"name\":\"Locked\",\"type\":\"speaker\",\"is_active\":false,\"is_restricted\":true}]}";
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, devices)));
        await using var fixture = new Fixture(handler);
        fixture.Model.SpotifyDevice = new("chosen", "Chosen"); fixture.Model.RefreshSpotifyDevices.Execute(null);
        await CommandFinished(fixture.Model.RefreshSpotifyDevices);
        Assert.Equal(2, fixture.Model.SpotifyDevices.Count);
        Assert.Equal("chosen", fixture.Model.SpotifyDevice!.Id);
        Assert.DoesNotContain(fixture.Model.SpotifyDevices, d => d.Id == "locked");
    }

    [Theory]
    [InlineData(403, "PREMIUM_REQUIRED", "SpotifyPremiumRequired")]
    [InlineData(403, "PERMISSIONS_MISSING", "SpotifyPermissionRequired")]
    [InlineData(403, "UNKNOWN", "SpotifyAccessDenied")]
    [InlineData(404, "NO_ACTIVE_DEVICE", "NoSpotifyDevices")]
    [InlineData(404, "UNKNOWN", "SpotifyUnavailable")]
    [InlineData(429, "QUOTA_EXCEEDED", "SpotifyQuotaExceeded")]
    [InlineData(500, "UNKNOWN", "SpotifyRequestFailed")]
    public async Task ApiErrorsCarryRecoveryKeysInsteadOfRawAccountData(int status, string reason, string key)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response((HttpStatusCode)status,
            "{\"error\":{\"reason\":\"" + reason + "\",\"message\":\"private test detail\"}}")));
        using var spotify = Client(handler);
        var error = await Assert.ThrowsAsync<SpotifyApiException>(() => spotify.PlayAsync(Song.SpotifyUri, null, CancellationToken.None));
        Assert.Equal(key, error.ErrorKey); Assert.DoesNotContain("private test detail", error.Message);
    }

    [Fact]
    public async Task BlankSearchDoesNotSpendApiQuota()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var spotify = Client(handler);
        Assert.Empty(await spotify.SearchAsync("   ", CancellationToken.None)); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("an incidental non-JSON response")]
    [InlineData("<html>success</html>")]
    public async Task SuccessfulPlayerCommandsDoNotParseAResponseBody(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        using var spotify = Client(handler);
        await spotify.PlayOnDeviceAsync(null, "desktop", CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("invalid-private-response")]
    [InlineData("[]")]
    public async Task InvalidReadResponsesCannotExposeTheResponseInAnError(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        using var spotify = Client(handler);
        var error = await Assert.ThrowsAsync<SpotifyApiException>(() => spotify.PlaybackAsync(CancellationToken.None));
        Assert.Equal("SpotifyRequestFailed", error.ErrorKey);
        Assert.DoesNotContain(body, error.Message);
    }

    [AvaloniaTheory]
    [InlineData("en", 1120, 720)]
    [InlineData("zh-Hans", 1460, 940)]
    [InlineData("ja", 1120, 720)]
    public async Task SearchKeyboardAndLayoutWorkAtSupportedWindowSizes(string language, int width, int height)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK,
            "{\"tracks\":{\"items\":[" + TrackJson + "]}}")));
        await using var fixture = new Fixture(handler, language);
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        try
        {
            window.Show();
            var search = window.FindControl<TextBox>("MusicSearchBox")!;
            Assert.True(search.Focus());
            window.KeyTextInput("song");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await CommandFinished(fixture.Model.SearchCommand);
            Assert.Single(fixture.Model.Tracks);
            Assert.True(window.FindControl<TextBlock>("SearchFeedback")!.IsVisible);
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                var origin = search.TranslatePoint(new Point(0, 0), window)!.Value;
                Assert.True(origin.X >= 0 && origin.Y >= 0);
                Assert.True(origin.X + search.Bounds.Width <= window.ClientSize.Width);
                Assert.Equal(46, search.Bounds.Height);
                SaveReviewFrame(frame, $"search-{language}-{width}-results.png");
            }
            Assert.True(search.IsFocused);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(fixture.Model.HasSearchQuery);
            Assert.True(fixture.Model.ShowHero);
            Assert.False(window.FindControl<TextBlock>("SearchFeedback")!.IsVisible);
            using var restored = window.CaptureRenderedFrame();
            Assert.NotNull(restored);
            SaveReviewFrame(restored, $"search-{language}-{width}-idle.png");
        }
        finally { window.Close(); }
    }

    private static void SaveReviewFrame(Avalonia.Media.Imaging.Bitmap? frame, string name)
    {
        if (Environment.GetEnvironmentVariable("MUSYNC_REVIEW_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            frame!.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    [Fact]
    public async Task AnInactiveDeviceIsTransferredOnceBeforeRetryingPlay()
    {
        var paths = new List<string>();
        var handler = new Handler(async (request, token) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (paths.Count == 1) return Response(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Player command failed: No active device found\"}}");
            if (request.RequestUri.AbsolutePath == "/v1/me/player")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("desktop", body.RootElement.GetProperty("device_ids")[0].GetString());
                Assert.False(body.RootElement.GetProperty("play").GetBoolean());
            }
            return Response(HttpStatusCode.NoContent);
        });
        using var spotify = Client(handler);
        await spotify.PlayOnDeviceAsync(null, "desktop", CancellationToken.None);
        Assert.Equal(["/v1/me/player/play", "/v1/me/player", "/v1/me/player/play"], paths);
    }

    [Fact]
    public async Task PremiumFailuresNeverTriggerDeviceTransferOrAnotherPlay()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden, "{\"error\":{\"reason\":\"PREMIUM_REQUIRED\"}}")));
        using var spotify = Client(handler);
        var error = await Assert.ThrowsAsync<SpotifyApiException>(() => spotify.PlayOnDeviceAsync(Song.SpotifyUri, "desktop", CancellationToken.None));
        Assert.Equal("SpotifyPremiumRequired", error.ErrorKey); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DeclaredOversizeArtworkIsRejected()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response(HttpStatusCode.OK, "x");
            response.Content!.Headers.ContentLength = 5_000_001;
            return Task.FromResult(response);
        });
        using var spotify = Client(handler);
        Assert.Null(await spotify.ArtworkAsync(Song.ImageUrl, CancellationToken.None));
    }

    [Fact]
    public async Task ChunkedArtworkIsBoundedWithoutContentLength()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new ForwardOnlyStream(5_000_001)) }));
        using var spotify = Client(handler);
        Assert.Null(await spotify.ArtworkAsync(Song.ImageUrl, CancellationToken.None));
    }

    private sealed class ForwardOnlyStream(int remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = Math.Min(count, remaining); Array.Fill(buffer, (byte)1, offset, read); remaining -= read; return read; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
