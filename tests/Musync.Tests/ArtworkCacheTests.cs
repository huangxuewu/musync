using System.Text.Json;
using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class ArtworkCacheTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(1);
    }
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Musync-IndexedCache-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public TemporaryArtworkCache Cache { get; }
        public Fixture() { Directory.CreateDirectory(DirectoryPath); Cache = Restart(); }
        public TemporaryArtworkCache Restart() => new(DirectoryPath, Clock, SpotifyArtworkResolver.DefaultCacheBytes);
        public TemporaryArtworkCache.Match Match() => new("Title", "Artist", "Album", null, Clock.GetUtcNow().AddHours(1));
        public TemporaryArtworkCache.Cover Cover(int bytes = 100) => new(new byte[bytes], Clock.GetUtcNow().AddHours(1));
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
    [Fact]
    public async Task ExistingSettingsDefaultTo500MbAndChangedCapacitySurvivesRestart()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.DirectoryPath, "settings.json");
        File.WriteAllText(path, "{\"Language\":\"zh-Hans\"}"); var store = new SettingsStore(path);
        var settings = store.Load(); Assert.Equal(500, settings.ArtworkCacheLimitMb);
        settings.ArtworkCacheLimitMb = 750; await store.SaveAsync(settings);
        Assert.Equal(750, new SettingsStore(path).Load().ArtworkCacheLimitMb);
    }
    [Fact]
    public void CapacityReplacesTheOld256FileLimitAndIndexSurvivesRestart()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 300; i++) fixture.Cache.SaveMatch("track" + i, fixture.Match(), "spotify:" + i);
        var restarted = fixture.Restart(); var stats = restarted.Maintain(SpotifyArtworkResolver.DefaultCacheBytes);
        Assert.Equal(300, stats.Files); Assert.InRange(stats.Bytes, 1, stats.LimitBytes);
        Assert.NotNull(restarted.ReadMatch("track0", "spotify:0"));
        Assert.NotNull(restarted.ReadMatch("track299", "spotify:299"));
    }
    [Fact]
    public void LoweringCapacityEvictsLeastRecentlyUsedFilesAndCountsIndexSpace()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "a", "b", "c" }) { fixture.Cache.SaveCover(name, fixture.Cover(5000), name); fixture.Clock.Advance(); }
        Assert.NotNull(fixture.Cache.ReadCover("a", "a"));
        var stats = fixture.Cache.Maintain(SpotifyArtworkResolver.DefaultCacheBytes);
        var coverBytes = JsonSerializer.SerializeToUtf8Bytes(fixture.Cover(5000)).LongLength;
        var reduced = fixture.Cache.Maintain(stats.Bytes - coverBytes);
        Assert.Equal(2, reduced.Files); Assert.True(reduced.Bytes <= reduced.LimitBytes);
        var restarted = fixture.Restart();
        Assert.Null(restarted.ReadCover("b", "b")); Assert.NotNull(restarted.ReadCover("a", "a")); Assert.NotNull(restarted.ReadCover("c", "c"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheHitsPersistLastUseBeforeRestartAndCapacityReduction(bool memoryHit)
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "a", "b", "c" })
        { fixture.Cache.SaveCover(name, fixture.Cover(5000), name); fixture.Clock.Advance(); }
        var stats = fixture.Cache.Maintain(SpotifyArtworkResolver.DefaultCacheBytes);
        if (memoryHit) Assert.True(fixture.Cache.AssociateCover("a", "a"));
        else Assert.NotNull(fixture.Cache.ReadCover("a", "a"));
        // Restart without another maintenance pass: the hit itself must persist recency.
        var restarted = fixture.Restart();
        var coverBytes = JsonSerializer.SerializeToUtf8Bytes(fixture.Cover(5000)).LongLength;
        var reduced = restarted.Maintain(stats.Bytes - coverBytes);
        Assert.Equal(2, reduced.Files); Assert.True(reduced.Bytes <= reduced.LimitBytes);
        Assert.Null(restarted.ReadCover("b", "b"));
        Assert.NotNull(restarted.ReadCover("a", "a")); Assert.NotNull(restarted.ReadCover("c", "c"));
    }
    [Fact]
    public void RemovingTracksDeletesTheirMatchesButKeepsSharedCoverUntilItsLastOwnerIsRemoved()
    {
        using var fixture = new Fixture();
        fixture.Cache.SaveMatch("a", fixture.Match(), "a"); fixture.Cache.SaveMatch("b", fixture.Match(), "b");
        fixture.Cache.SaveCover("album", fixture.Cover(), "a"); fixture.Cache.AssociateCover("album", "b");
        var restarted = fixture.Restart(); restarted.RemoveOwners(new HashSet<string> { "a" });
        Assert.Null(restarted.ReadMatch("a", "a")); Assert.NotNull(restarted.ReadCover("album", "b"));
        Assert.Equal(2, restarted.Maintain(SpotifyArtworkResolver.DefaultCacheBytes).Files);
        restarted.RemoveOwners(new HashSet<string> { "b" });
        Assert.Equal(0, restarted.Maintain(SpotifyArtworkResolver.DefaultCacheBytes).Files);
    }
    [Fact]
    public void LocalTagResultsOutliveSpotifyExpiryAndReplaceOlderFileVersions()
    {
        using var fixture = new Fixture(); var entry = new TemporaryArtworkCache.LocalMetadata("歌", "歌手", "专辑");
        fixture.Cache.SaveLocal("stamp1", entry, "local:a");
        for (var i = 0; i < 4000; i++) fixture.Clock.Advance();
        Assert.Equal(entry, fixture.Restart().ReadLocal("stamp1", "local:a"));
        fixture.Cache.SaveLocal("stamp2", entry with { Title = "New tags" }, "local:a");
        Assert.Null(fixture.Cache.ReadLocal("stamp1", "local:a")); Assert.Equal("New tags", fixture.Cache.ReadLocal("stamp2", "local:a")!.Title);
    }
    [Fact]
    public void MaintenanceRemovesMissingLocalOwnersAndExpiredSpotifyFilesWithoutTouchingSourceAudio()
    {
        using var fixture = new Fixture(); var audio = Path.Combine(fixture.DirectoryPath, "original.flac"); File.WriteAllText(audio, "original audio");
        fixture.Cache.SaveLocal("a", new("Song", "Artist", null), "local:a");
        fixture.Cache.SaveCover("album", fixture.Cover(), "local:a");
        fixture.Cache.Maintain(SpotifyArtworkResolver.DefaultCacheBytes, new HashSet<string>());
        Assert.Equal(0, fixture.Cache.Maintain(SpotifyArtworkResolver.DefaultCacheBytes).Files); Assert.Equal("original audio", File.ReadAllText(audio));
        fixture.Cache.SaveMatch("b", fixture.Match(), "spotify:b");
        for (var i = 0; i < 3601; i++) fixture.Clock.Advance();
        Assert.Equal(0, fixture.Cache.Maintain(SpotifyArtworkResolver.DefaultCacheBytes).Files);
    }
    [Fact]
    public void CorruptIndexIsRebuiltAndUnsafeNamesNeverDeleteFilesOutsideTheCache()
    {
        using var fixture = new Fixture(); fixture.Cache.SaveCover("album", fixture.Cover(), "a");
        var index = Path.Combine(fixture.DirectoryPath, "cache-index.json"); File.WriteAllText(index, "broken index");
        Assert.NotNull(fixture.Restart().ReadCover("album", "a"));
        File.WriteAllText(index, "{\"../original.flac\":{\"Kind\":\"local\",\"Owners\":[\"a\"]}}");
        var original = Path.Combine(fixture.DirectoryPath, "original.flac"); File.WriteAllText(original, "untouched");
        fixture.Restart().Clear(); Assert.Equal("untouched", File.ReadAllText(original));
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath));
    }

    [Fact]
    public void DisconnectPurgesSpotifyDataButPreservesIndependentLocalTags()
    {
        using var fixture = new Fixture();
        fixture.Cache.SaveLocal("a", new("Song", "Artist", null), "local:a");
        fixture.Cache.SaveMatch("a", fixture.Match(), "local:a"); fixture.Cache.SaveCover("album", fixture.Cover(), "local:a");
        fixture.Cache.Clear(includeLocalMetadata: false);
        Assert.NotNull(fixture.Restart().ReadLocal("a", "local:a")); Assert.Null(fixture.Cache.ReadMatch("a", "local:a"));
        Assert.Null(fixture.Cache.ReadCover("album", "local:a")); Assert.Equal(1, fixture.Cache.Maintain(SpotifyArtworkResolver.DefaultCacheBytes).Files);
    }
}
