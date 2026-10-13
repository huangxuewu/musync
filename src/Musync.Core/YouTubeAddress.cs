using System.Text.RegularExpressions;

namespace Musync.Core;

public sealed record RadioStation(string Id, string Name, string Url, bool AutoName = false);

public sealed partial record YouTubeAddress(string VideoId)
{
    public string Url => "https://www.youtube.com/watch?v=" + VideoId;

    public static bool TryParse(string? input, out YouTubeAddress? result)
    {
        result = null;
        if (input is null || input.Length > 2048 || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (host == "youtu.be" && segments.Length == 1) id = segments[0];
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            if (segments.Length == 1 && segments[0] == "watch")
            {
                var values = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == "v").ToArray();
                if (values.Length == 1 && values[0].Length == 2) id = Uri.UnescapeDataString(values[0][1]);
            }
            else if (segments.Length == 2 && segments[0] is "live" or "shorts" or "embed") id = segments[1];
        }
        if (id is null || !VideoIdPattern().IsMatch(id)) return false;
        result = new(id); return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdPattern();
}
