using Musync.Core;
using NAudio.Wave;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Musync.Tests;

public sealed class SpotifyArtworkTests
{
    private sealed class Vault : ITokenVault
    {
        public string? Load() => JsonSerializer.Serialize(new SpotifyTokens("test-access", "test-refresh", DateTime.UtcNow.AddHours(2)));
        public void Save(string json) { }
        public void Clear() { }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return respond(request, token); }
    }
    private static MusicTrack Local(string title = "Song", string artist = "Artist", double duration = 180, string? album = null)
        => new("local:test", title, artist, "missing-file.mp3", DurationSeconds: duration, Album: album);
    private static MusicTrack Catalog(string title = "Song", string artist = "Artist", double duration = 180, string? album = null)
        => new("catalog", title, artist, null, "spotify:track:catalog", "https://i.scdn.co/image/test", duration, Album: album);
    private static string SearchJson => JsonSerializer.Serialize(new { tracks = new { items = new[] { new
    {
        id = "catalog", name = "Song", uri = "spotify:track:catalog", duration_ms = 180000,
        artists = new[] { new { name = "Artist" } }, album = new { name = "Album", images = new[] { new { url = "https://i.scdn.co/image/test" } } }
    } } } });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Theory]
    [InlineData("Different", "Artist", 180, "Album")]
    [InlineData("Song", "Cover artist", 180, "Album")]
    [InlineData("Song", "Artist", 184, "Album")]
    [InlineData("Song (Live)", "Artist", 180, "Live album")]
    public void MatchingRejectsWrongSongArtistAndVersion(string title, string artist, double duration, string? album)
        => Assert.Null(SpotifyArtworkResolver.Match(Local(album: "Album"), [Catalog(title, artist, duration, album)]));

    [Theory]
    [InlineData("夜に駆ける", "YOASOBI")]
    [InlineData("青花瓷", "周杰伦")]
    [InlineData("Song", "Artist")]
    public void MatchingKeepsIdentityAndAcceptsSmallDurationDifference(string title, string artist)
    {
        var candidate = Catalog(title, artist, 182);
        Assert.Same(candidate, SpotifyArtworkResolver.Match(Local(title, artist), [candidate]));
        var ambiguous = new[] { candidate, Catalog(title, "Other artist") };
        Assert.Null(SpotifyArtworkResolver.Match(Local(title, ""), ambiguous));
    }

    [Fact]
    public void DuetTagsAndDownloadSourceAlbumMatchTheSameRecording()
    {
        var local = Local("惊鸿一面", "许嵩 & 黄龄", 256, "kuwo");
        var candidate = Catalog("惊鸿一面", "黄龄, 许嵩", 256, "不如吃茶去");
        Assert.Same(candidate, SpotifyArtworkResolver.Match(local, [candidate]));
        Assert.Null(SpotifyArtworkResolver.Match(local, [candidate with { Artist = "翻唱歌手" }]));
    }

    [Fact]
    public void AlbumTagsPreferTheOriginalReleaseOverCompilationArtwork()
    {
        var release = Catalog(album: "Original album"); var compilation = Catalog(album: "Compilation");
        Assert.Same(release, SpotifyArtworkResolver.Match(Local(album: "Original album"), [compilation, release]));
    }

    [Theory]
    [InlineData("Stuck with U (with Justin Bieber)")]
    [InlineData("Stuck with U (feat. Justin Bieber)")]
    public void FeaturedArtistSuffixDoesNotHideTheSameRecording(string title)
    {
        var local = Local("Stuck with U", "Ariana Grande", 228);
        var candidate = Catalog(title, "Ariana Grande, Justin Bieber", 228.5);
        Assert.Same(candidate, SpotifyArtworkResolver.Match(local, [candidate]));
        Assert.Null(SpotifyArtworkResolver.Match(local, [candidate with { Title = "Stuck with U (Live)" }]));
    }

    [Fact]
    public async Task DuetLookupFiltersByAnIndividualPerformer()
    {
        var handler = new Handler((request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.Contains("artist:\"许嵩\"", query); Assert.DoesNotContain("artist:\"许嵩 & 黄龄\"", query);
            return Task.FromResult(Json("{\"tracks\":{\"items\":[]}}"));
        });
        using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
        await new SpotifyArtworkResolver(client).ResolveAsync(Local("惊鸿一面", "许嵩 & 黄龄"), CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentRowsShareSearchAndCoverWithoutChangingLocalPlaybackIdentity()
    {
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.spotify.com")
            {
                Assert.Equal("test-access", request.Headers.Authorization?.Parameter);
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains("track:\"Song\" artist:\"Artist\"", query);
                await Task.Delay(30, token); return Json(SearchJson);
            }
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
        var resolver = new SpotifyArtworkResolver(client);
        var source = Local();
        var results = await Task.WhenAll(resolver.ResolveAsync(source, CancellationToken.None), resolver.ResolveAsync(source, CancellationToken.None));
        Assert.Equal(2, handler.Calls);
        foreach (var result in results)
        {
            Assert.Equal(source.Id, result.Metadata.Id); Assert.Equal(source.FilePath, result.Metadata.FilePath);
            Assert.Null(result.Metadata.SpotifyUri); Assert.Null(result.Metadata.ImageUrl);
            Assert.Equal("spotify:track:catalog", result.SpotifyUri); Assert.Equal(new byte[] { 1, 2, 3 }, result.Image);
        }
    }

    [Fact]
    public async Task CoverCacheSharesAlbumsExpiresAndIsClearedAtDisconnect()
    {
        var clock = new Clock();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }));
        using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
        var resolver = new SpotifyArtworkResolver(client, clock);
        await resolver.ResolveAsync(Catalog(), CancellationToken.None);
        await resolver.ResolveAsync(Catalog() with { Id = "other", SpotifyUri = "spotify:track:other" }, CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(61));
        await resolver.ResolveAsync(Catalog(), CancellationToken.None); Assert.Equal(2, handler.Calls);
        resolver.Clear(); client.Disconnect();
        Assert.Null((await resolver.ResolveAsync(Catalog(), CancellationToken.None)).Image);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RateLimitStopsBackgroundLookupsForTheFullServerRetryAfter()
    {
        var clock = new Clock(); var limited = true;
        var handler = new Handler((_, _) =>
        {
            if (!limited) return Task.FromResult(Json("{\"tracks\":{\"items\":[]}}"));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"message\":\"Slow down\"}}") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(40));
            return Task.FromResult(response);
        });
        using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
        var resolver = new SpotifyArtworkResolver(client, clock);
        Assert.Null((await resolver.ResolveAsync(Local(), CancellationToken.None)).Image);
        clock.Advance(TimeSpan.FromSeconds(39)); limited = false;
        Assert.Null((await resolver.ResolveAsync(Local(), CancellationToken.None)).Image); Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(2));
        await resolver.ResolveAsync(Local(), CancellationToken.None); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellingQueuedRowsDoesNotSpendQuotaAndClearDiscardsLateCover()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => { entered.SetResult(); return finish.Task; });
        using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
        var resolver = new SpotifyArtworkResolver(client);
        var first = resolver.ResolveAsync(Catalog(), CancellationToken.None); await entered.Task;
        using var cancel = new CancellationTokenSource();
        var queued = resolver.ResolveAsync(Local(), cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        resolver.Clear(); finish.SetResult(new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        Assert.Null((await first).Image); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NumericFilenamesAndOriginalDemosNeverMatchPopularUnrelatedSongs()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("No lookup expected."));
        using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
        var resolver = new SpotifyArtworkResolver(client);
        Assert.Null((await resolver.ResolveAsync(Local("2704026311", ""), CancellationToken.None)).Image);
        Assert.Null((await resolver.ResolveAsync(Local() with { IsDemo = true }, CancellationToken.None)).Image);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DiskResultsAndImagesSurviveRestartWithoutAnySpotifyRequest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-CoverCache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.spotify.com"
                ? Json(SearchJson) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
            using (var client = new SpotifyClient(new Vault(), new HttpClient(handler)))
            {
                var resolver = new SpotifyArtworkResolver(client, directory);
                Assert.NotNull((await resolver.ResolveAsync(Local(), CancellationToken.None)).Image);
                resolver.ReleaseMemory();
            }
            var offline = new Handler((_, _) => throw new InvalidOperationException("Cached results must work without network requests."));
            using var restarted = new SpotifyClient(new Vault(), new HttpClient(offline));
            var cached = await new SpotifyArtworkResolver(restarted, directory).ResolveAsync(Local() with { Id = "local:other" }, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, cached.Image); Assert.Equal("spotify:track:catalog", cached.SpotifyUri);
            Assert.Equal("local:other", cached.Metadata.Id); Assert.Null(cached.Metadata.SpotifyUri); Assert.Equal(0, offline.Calls);
            Assert.Equal(2, handler.Calls);
            foreach (var path in Directory.GetFiles(directory)) Assert.DoesNotContain("test-access", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RemovingATrackWaitsForItsPendingLookupThenRemovesDiskAndMemoryResults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-PendingCache-" + Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var handler = new Handler((_, _) => { entered.TrySetResult(); return finish.Task; });
            using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
            var resolver = new SpotifyArtworkResolver(client, directory);
            var loading = resolver.ResolveAsync(Catalog(), CancellationToken.None); await entered.Task;
            var removing = resolver.ForgetTracksAsync([Catalog()]); Assert.False(removing.IsCompleted);
            finish.SetResult(new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
            await loading; await removing;
            Assert.Equal(0, (await resolver.MaintainAsync(SpotifyArtworkResolver.DefaultCacheBytes)).Files);
            await resolver.ResolveAsync(Catalog(), CancellationToken.None); Assert.Equal(2, handler.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LegacyUnindexedMatchesAndCoversRemainReusableDuringUpgrade()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-LegacyCache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var audio = Path.Combine(directory, "original.mp3"); File.WriteAllText(audio, "original");
            var source = Local() with { FilePath = audio }; var catalog = Catalog(); var expires = DateTimeOffset.UtcNow.AddMinutes(30);
            string CachePath(string key) => Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".json");
            var key = "local:" + audio + ":" + File.GetLastWriteTimeUtc(audio).Ticks;
            File.WriteAllBytes(CachePath("match:" + key), JsonSerializer.SerializeToUtf8Bytes(new TemporaryArtworkCache.Match(source.Title, source.Artist, source.Album, catalog, expires)));
            File.WriteAllBytes(CachePath("cover:" + catalog.ImageUrl), JsonSerializer.SerializeToUtf8Bytes(new TemporaryArtworkCache.Cover([1], expires)));
            var handler = new Handler((_, _) => throw new InvalidOperationException("Upgrade should reuse the cache."));
            using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
            var result = await new SpotifyArtworkResolver(client, directory).ResolveAsync(source, CancellationToken.None);
            Assert.Equal(new byte[] { 1 }, result.Image); Assert.Equal(0, handler.Calls); Assert.Equal("original", File.ReadAllText(audio));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DiskMissesExpireAfterTenMinutesAndDisconnectDeletesOnlyOwnedCacheFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-CoverCache-" + Guid.NewGuid().ToString("N")); var clock = new Clock();
        try
        {
            var handler = new Handler((_, _) => Task.FromResult(Json("{\"tracks\":{\"items\":[]}}")));
            using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
            await new SpotifyArtworkResolver(client, clock, directory).ResolveAsync(Local(), CancellationToken.None);
            await new SpotifyArtworkResolver(client, clock, directory).ResolveAsync(Local(), CancellationToken.None);
            Assert.Equal(1, handler.Calls);
            clock.Advance(TimeSpan.FromMinutes(11));
            var resolver = new SpotifyArtworkResolver(client, clock, directory);
            await resolver.ResolveAsync(Local(), CancellationToken.None); Assert.Equal(2, handler.Calls);
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "Unrelated file");
            resolver.Clear(); Assert.Single(Directory.GetFiles(directory)); Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DiskCoverExpiresAndCorruptCacheFallsBackToANewDownload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-CoverCache-" + Guid.NewGuid().ToString("N")); var clock = new Clock();
        try
        {
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }));
            using var client = new SpotifyClient(new Vault(), new HttpClient(handler));
            await new SpotifyArtworkResolver(client, clock, directory).ResolveAsync(Catalog(), CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(61));
            await new SpotifyArtworkResolver(client, clock, directory).ResolveAsync(Catalog(), CancellationToken.None); Assert.Equal(2, handler.Calls);
            foreach (var path in Directory.GetFiles(directory)) File.WriteAllText(path, "broken cache");
            Assert.NotNull((await new SpotifyArtworkResolver(client, clock, directory).ResolveAsync(Catalog(), CancellationToken.None)).Image);
            Assert.Equal(3, handler.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    public void RealAudioTagsIdentifyNumberedFilesAndReadingNeverWritesTheAudio(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-Tags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var wave = Path.Combine(directory, "source.wav");
            using (var writer = new WaveFileWriter(wave, new WaveFormat(44100, 16, 2))) writer.Write(new byte[176400], 0, 176400);
            var path = Path.Combine(directory, "2704026311" + extension);
            using (var source = new WaveFileReader(wave))
                if (extension == ".flac") MediaFoundationEncoder.EncodeToFlac(source, path);
                else if (extension == ".mp3") MediaFoundationEncoder.EncodeToMp3(source, path, 192000);
                else if (extension == ".m4a") MediaFoundationEncoder.EncodeToAac(source, path);
                else File.Copy(wave, path);
            using (var tags = TagLib.File.Create(path))
            { tags.Tag.Title = "青花瓷"; tags.Tag.Performers = ["周杰伦"]; tags.Tag.Album = "我很忙"; tags.Save(); }
            var before = SHA256.HashData(File.ReadAllBytes(path));
            var metadata = LocalTrackMetadata.Read(Local("2704026311", "") with { FilePath = path });
            Assert.Equal("青花瓷", metadata.Title); Assert.Equal("周杰伦", metadata.Artist); Assert.Equal("我很忙", metadata.Album);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
