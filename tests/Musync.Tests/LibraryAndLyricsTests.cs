using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class LibraryAndLyricsTests
{
    [Fact]
    public void LrcSupportsRepeatedTimestampsUnicodeAndLateOffset()
    {
        var lyrics = SyncedLyrics.Parse("[ar:Test]\n[00:01.50][00:04.500]光の中\n[00:02]一起唱\n[offset:250]");
        Assert.Equal([1.75, 2.25, 4.75], lyrics.Lines.Select(l => l.Seconds));
        Assert.Equal(new LyricWindow(1, "光の中", "一起唱", "光の中"), lyrics.At(3));
        Assert.Equal(new LyricWindow(-1, "", "", "光の中"), lyrics.At(0));
    }
    [Fact]
    public void LrcSeekCanMoveBackwardAndBlankCuesClearTheCurrentLine()
    {
        var lyrics = SyncedLyrics.Parse("[00:01]First\n[00:05]\n[00:10]Last");
        Assert.Equal("Last", lyrics.At(99).Current);
        Assert.Equal("", lyrics.At(7).Current);
        Assert.Equal("First", lyrics.At(2).Current);
    }
    [Fact]
    public void InvalidLrcDoesNotInventLyricsAndDuplicateTimesAreDeterministic()
    {
        Assert.Empty(SyncedLyrics.Parse("Words without times\n[00:99]Invalid\n[artist:Example]").Lines);
        var lyrics = SyncedLyrics.Parse("[00:01.1]Earlier\n[00:01.100]Corrected\n[offset:-2000]");
        Assert.Single(lyrics.Lines); Assert.Equal(0, lyrics.Lines[0].Seconds); Assert.Equal("Corrected", lyrics.At(0).Current);
        Assert.Empty(SyncedLyrics.Parse(new string('x', 1_048_577)).Lines);
    }
    [Fact]
    public async Task AlongsideLrcHandlesBomMissingAndOversizedFiles()
    {
        using var folder = new TestFolder();
        var audio = folder.File("夜.mp3");
        Assert.Empty((await SyncedLyrics.LoadAlongsideAsync(audio, default)).Lines);
        await System.IO.File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), "[00:02]原创测试文字", System.Text.Encoding.Unicode);
        Assert.Equal("原创测试文字", (await SyncedLyrics.LoadAlongsideAsync(audio, default)).At(2).Current);
        await System.IO.File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), new string('x', 1_048_577));
        Assert.Empty((await SyncedLyrics.LoadAlongsideAsync(audio, default)).Lines);
    }
    [Fact]
    public void NestedFoldersProduceSeparatePlaylistsAndReimportIsIdempotent()
    {
        using var folder = new TestFolder();
        folder.File("root.mp3"); folder.File("夜の音楽/child.FLAC"); folder.File("夜の音楽/deep/end.wav");
        folder.File("empty/readme.txt");
        MusicTrack Probe(string path) => new(Guid.NewGuid().ToString(), Path.GetFileNameWithoutExtension(path), "Test", path);
        var result = FolderMusicImporter.Import([folder.Path], [], new Dictionary<string, List<string>>(), new Dictionary<string, string>(), Probe);
        Assert.Equal(3, result.Tracks.Count); Assert.Equal(3, result.Playlists.Count);
        Assert.Equal(3, result.ImportedPlaylists.Count);
        Assert.All(result.Playlists.Values, ids => Assert.Single(ids));
        Assert.Contains("夜の音楽", result.Playlists.Keys); Assert.DoesNotContain("empty", result.Playlists.Keys);
        var second = FolderMusicImporter.Import([folder.Path, System.IO.Path.Combine(folder.Path, "夜の音楽")], result.Tracks,
            result.Playlists, result.Folders, _ => throw new Exception("Existing files must not be probed"));
        Assert.Empty(second.Tracks); Assert.Equal(3, second.Playlists.Count); Assert.All(second.Playlists.Values, ids => Assert.Single(ids));
        Assert.Equal(result.ImportedPlaylists, second.ImportedPlaylists);
    }
    [Fact]
    public void CollidingFoldersAndManualPlaylistRemainIndependentAndBadAudioIsSkipped()
    {
        using var folder = new TestFolder();
        folder.File("a/Music/one.mp3"); folder.File("b/Music/two.mp3"); folder.File("b/Music/bad.wav");
        var manual = new Dictionary<string, List<string>> { ["Music"] = ["manual-track"] };
        var result = FolderMusicImporter.Import([folder.Path], [], manual, new Dictionary<string, string>(), path =>
            path.EndsWith("bad.wav") ? throw new IOException("Corrupt") : new(path, "Song", "Artist", path));
        Assert.Equal(2, result.Tracks.Count); Assert.Equal(1, result.Skipped);
        Assert.Equal(["manual-track"], result.Playlists["Music"]);
        Assert.Single(result.Playlists["Music (2)"]); Assert.Single(result.Playlists["Music (3)"]);
        Assert.Single(manual); // A worker never mutates settings while scanning.
    }
    [Fact]
    public void CanceledFolderImportCannotPublishAPartialLibrary()
    {
        using var folder = new TestFolder(); folder.File("one.mp3");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => FolderMusicImporter.Import([folder.Path], [],
            new Dictionary<string, List<string>>(), new Dictionary<string, string>(), _ => throw new Exception(), cancel.Token));
    }
    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Musync-Library-Test-" + Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Path);
        public string File(string relative)
        {
            var path = System.IO.Path.Combine(Path, relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "test"); return path;
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
