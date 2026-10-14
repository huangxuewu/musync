using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Musync.App;
using Musync.Core;
using NAudio.Wave;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static string LosslessFixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lossless", name);
    private static string MakeSourceQualityFile(string folder, string type)
    {
        var path = Path.Combine(folder, type + (type == "alac" || type == "aac" ? ".m4a" : type == "aiff" ? ".aiff" : type == "flac" ? ".FLAC" : ".wav"));
        if (type is "alac" or "adpcm")
        { File.Copy(LosslessFixture(type == "alac" ? "tone-alac.m4a" : "tone-adpcm.wav"), path); return path; }
        var wave = Path.Combine(folder, type + "-source.wav");
        var format = type == "float" ? WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)
            : type == "extensible" ? new WaveFormatExtensible(44100, 24, 2)
            : type == "extensible-float" ? new WaveFormatExtensible(44100, 32, 2) : new WaveFormat(44100, 16, 1);
        using (var writer = new WaveFileWriter(wave, format)) writer.Write(new byte[format.BlockAlign * 4410], 0, format.BlockAlign * 4410);
        if (type is "flac" or "aac")
        {
            using var source = new WaveFileReader(wave);
            if (type == "flac") MediaFoundationEncoder.EncodeToFlac(source, path);
            else MediaFoundationEncoder.EncodeToAac(source, path);
        }
        else if (type == "aiff")
        {
            using var source = new WaveFileReader(wave);
            AiffFileWriter.CreateAiffFile(path, source);
        }
        else File.Copy(wave, path);
        return path;
    }

    [AvaloniaTheory]
    [InlineData("pcm", "WAV / PCM")]
    [InlineData("float", "WAV / Float")]
    [InlineData("extensible", "WAV / PCM")]
    [InlineData("extensible-float", "WAV / Float")]
    [InlineData("flac", "FLAC")]
    [InlineData("alac", "ALAC")]
    [InlineData("aiff", "AIFF / PCM")]
    [InlineData("aac", null)]
    [InlineData("adpcm", null)]
    public void LosslessBadgeClassificationReadsRealAudioHeaders(string type, string? expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Quality-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = MakeSourceQualityFile(folder, type);
            Assert.Equal(expected, new LosslessSourceProbe().Read(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public void LosslessBadgeClassificationRejectsDamagedFilesAndInvalidatesReplacedOrRemovedFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Musync-Quality-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var probe = new LosslessSourceProbe(); var path = MakeSourceQualityFile(folder, "pcm");
            Assert.Equal("WAV / PCM", probe.Read(path));
            File.Copy(LosslessFixture("tone-adpcm.wav"), path, overwrite: true);
            Assert.Null(probe.Read(path)); File.Delete(path); Assert.Null(probe.Read(path));
            foreach (var extension in new[] { ".flac", ".wav", ".aiff", ".m4a", ".wma", ".mp3", ".aac" })
            {
                var corrupt = Path.Combine(folder, "damaged" + extension); File.WriteAllText(corrupt, "unrecognized audio");
                Assert.Null(probe.Read(corrupt));
            }
            Assert.Null(probe.Read(null));
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaTheory]
    [InlineData("en", 1280, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task LosslessBadgeFitsLongSongInformationAndDoesNotLabelLossyOrSpotifyRows(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var flac = MakeSourceQualityFile(fixture.DirectoryPath, "flac");
        var aac = MakeSourceQualityFile(fixture.DirectoryPath, "aac");
        var alac = MakeSourceQualityFile(fixture.DirectoryPath, "alac");
        foreach (var row in fixture.Model.Tracks) row.Dispose(); fixture.Model.Tracks.Clear();
        var source = new TrackViewModel(new("quality:flac", "夜に駆ける · 清晨音乐 · A very long lossless music collection title", new string('音', 80), flac, DurationSeconds: 180), fixture.Model);
        var lossy = new TrackViewModel(new("quality:aac", "AAC music", "Synthetic artist", aac, DurationSeconds: 180), fixture.Model);
        var shortArtist = new TrackViewModel(new("quality:alac", "ALAC music", "Original artist", alac, DurationSeconds: 180), fixture.Model);
        var spotify = new TrackViewModel(new("quality:spotify", "Spotify music", "Synthetic artist", null, SpotifyUri: "spotify:track:quality", DurationSeconds: 180), fixture.Model);
        fixture.Model.Tracks.Add(source); fixture.Model.Tracks.Add(shortArtist); fixture.Model.Tracks.Add(lossy); fixture.Model.Tracks.Add(spotify);
        await Task.WhenAll(source.EnsureArtworkAsync(), shortArtist.EnsureArtworkAsync(), lossy.EnsureArtworkAsync(), spotify.EnsureArtworkAsync());
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        window.Show();
        try
        {
            using var ready = window.CaptureRenderedFrame(); window.ApplyMotionPreference(false);
            var list = window.FindControl<ItemsControl>("TrackList")!;
            var scroll = window.FindControl<ScrollViewer>("MusicScroll")!;
            scroll.Offset = new Vector(0, 330); using var frame = window.CaptureRenderedFrame();
            Assert.True(source.HasLosslessSource); Assert.False(lossy.HasLosslessSource); Assert.False(spotify.HasLosslessSource);
            var badge = list.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("losslessBadge") && b.DataContext == source);
            Assert.True(badge.IsEffectivelyVisible); Assert.Equal(20, badge.Bounds.Height);
            Assert.Contains("FLAC", AutomationProperties.GetName(badge)); Assert.Equal(source.LosslessDetails, ToolTip.GetTip(badge));
            Assert.Equal(fixture.Model.Text["Lossless"], badge.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            var line = (Grid)badge.Parent!;
            var artist = line.Children.OfType<TextBlock>().Single();
            Assert.True(artist.Bounds.Right + 8 <= badge.Bounds.Left + .01);
            Assert.True(badge.Bounds.Right <= line.Bounds.Width + .01);
            Assert.True(line.Bounds.Height <= 20);
            var inlineBadge = list.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("losslessBadge") && b.DataContext == shortArtist);
            var inlineLine = (Grid)inlineBadge.Parent!;
            Assert.True(inlineBadge.IsEffectivelyVisible); Assert.Contains("ALAC", shortArtist.LosslessDetails);
            Assert.Equal(inlineLine.Children.OfType<TextBlock>().Single().Bounds.Right + 8, inlineBadge.Bounds.Left, 2);
            SaveReviewFrame(frame, $"lossless-badge-{language}-{width}.png");
            Assert.False(fixture.Model.IsPlaying);
        }
        finally { window.Close(); }
    }
}
