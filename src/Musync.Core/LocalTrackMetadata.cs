using System.Text.RegularExpressions;

namespace Musync.Core;

public static partial class LocalTrackMetadata
{
    // Reading tags never opens an audio output and never saves changes to the file.
    public static MusicTrack Read(MusicTrack track)
    {
        if (track.IsDemo || track.FilePath is null) return track;
        var title = track.Title; var artist = track.Artist; var album = track.Album; var taggedTitle = false;
        try
        {
            using var file = TagLib.File.Create(track.FilePath, TagLib.ReadStyle.None);
            if (!string.IsNullOrWhiteSpace(file.Tag.Title)) { title = file.Tag.Title.Trim(); taggedTitle = true; }
            var performers = file.Tag.Performers.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (performers.Length > 0) artist = string.Join(", ", performers);
            if (!string.IsNullOrWhiteSpace(file.Tag.Album)) album = file.Tag.Album.Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
            or TagLib.UnsupportedFormatException or ArgumentException or NotSupportedException)
        { /* Damaged/missing tags do not make a decodable audio file unplayable. */ }
        if (!taggedTitle && string.IsNullOrWhiteSpace(artist)
            && track.Title == Path.GetFileNameWithoutExtension(track.FilePath))
        {
            var filename = TrackNumber().Replace(title, "");
            var separator = filename.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0 && separator + 3 < filename.Length)
            { artist = filename[..separator].Trim(); title = filename[(separator + 3)..].Trim(); }
            else title = filename;
        }
        return track with { Title = title, Artist = artist, Album = album };
    }

    [GeneratedRegex(@"^\d{1,3}[ ._-]+")]
    private static partial Regex TrackNumber();
}
