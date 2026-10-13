using System.Diagnostics;
using System.Text.Json;

namespace Musync.Core;

public sealed record RadioTools(string YtDlp, string Ffmpeg, string Node)
{
    public static RadioTools Locate()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "RadioTools");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        var folder = Directory.Exists(bundled) ? bundled : Path.Combine(root?.FullName ?? AppContext.BaseDirectory, ".tools", "radio");
        var ffmpeg = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        var node = Path.Combine(folder, "node.exe");
        var bridgeNode = Path.Combine(AppContext.BaseDirectory, "AirPlayBridge", "node.exe");
        if (!File.Exists(node) && File.Exists(bridgeNode)) node = bridgeNode;
        else if (!File.Exists(node) && root is not null)
            node = Directory.Exists(Path.Combine(root.FullName, ".tools", "node"))
                ? Directory.EnumerateFiles(Path.Combine(root.FullName, ".tools", "node"), "node.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "node"
                : "node";
        return new(Path.Combine(folder, "yt-dlp.exe"), ffmpeg ?? Path.Combine(folder, "ffmpeg.exe"), node);
    }
    public void Validate()
    {
        if (!File.Exists(YtDlp) || !File.Exists(Ffmpeg)) throw new FileNotFoundException("Radio tools are missing. Run tools/Prepare-RadioTools.ps1.");
    }
    internal Process CreateYtDlp(params string[] arguments)
    {
        Validate();
        return Create(YtDlp, ["--ignore-config", "--no-plugin-dirs", "--no-playlist", "--no-progress",
            "--socket-timeout", "15", "--retries", "1", "--fragment-retries", "1", "--js-runtimes", "node:" + Node, .. arguments]);
    }
    internal static Process Create(string file, string[] arguments, bool input = false)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new() { StartInfo = start };
    }
    internal static async Task StopOwnedAsync(Process process)
    {
        // Only a process created and retained by this stream can reach here.
        // Close its pipes first; both tools normally exit on EOF/broken pipe.
        try { _ = process.Id; } catch (InvalidOperationException) { return; }
        try { process.StandardOutput.BaseStream.Close(); } catch (InvalidOperationException) { }
        if (process.StartInfo.RedirectStandardInput) try { process.StandardInput.BaseStream.Close(); } catch (InvalidOperationException) { }
        if (process.HasExited) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    internal static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[2048]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + read > 65536) throw new IOException("Radio tool returned too much diagnostic data.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
    internal static async Task<string> ReadDiagnosticTailAsync(StreamReader reader, CancellationToken token)
    {
        // Long-lived stderr must keep draining, even when a helper repeats an
        // error. A fixed ring retains the latest diagnostics without allowing
        // an output-size exception to leave the native pipe undrained.
        var tail = new char[65536]; var buffer = new char[2048];
        var offset = 0; var stored = 0; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            var first = Math.Min(read, tail.Length - offset);
            buffer.AsSpan(0, first).CopyTo(tail.AsSpan(offset));
            buffer.AsSpan(first, read - first).CopyTo(tail);
            offset = (offset + read) % tail.Length;
            stored = Math.Min(tail.Length, stored + read);
        }
        var result = new char[stored];
        var oldest = stored == tail.Length ? offset : 0;
        var count = Math.Min(stored, tail.Length - oldest);
        tail.AsSpan(oldest, count).CopyTo(result);
        tail.AsSpan(0, stored - count).CopyTo(result.AsSpan(count));
        // Do not expose half a UTF-16 pair if eviction cuts through an emoji.
        var skip = stored > 0 && char.IsLowSurrogate(result[0]) ? 1 : 0;
        return new string(result, skip, stored - skip);
    }
}

public sealed class YtDlpRadioResolver(RadioTools tools) : IYouTubeResolver
{
    public async Task<RadioAudioInfo> ResolveAsync(YouTubeAddress address, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        using var process = tools.CreateYtDlp("--skip-download", "-f", "bestaudio/best", "--print",
            "{\"id\":%(id)j,\"title\":%(title)j,\"uploader\":%(uploader)j,\"duration\":%(duration)j,\"is_live\":%(is_live)j,\"format_id\":%(format_id)j}", address.Url);
        process.Start();
        var diagnostic = RadioTools.ReadLimitedAsync(process.StandardError, deadline.Token);
        try
        {
            var json = await RadioTools.ReadLimitedAsync(process.StandardOutput, deadline.Token);
            await process.WaitForExitAsync(deadline.Token); await diagnostic;
            if (process.ExitCode != 0) throw new IOException("YouTube could not open this video. Check the address and try again.");
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement;
            if (data.GetProperty("id").GetString() != address.VideoId) throw new IOException("Unexpected YouTube video response.");
            var duration = data.GetProperty("duration");
            var seconds = duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var value) && double.IsFinite(value) ? Math.Max(0, value) : 0;
            var title = data.GetProperty("title").GetString() ?? address.VideoId;
            var artist = data.GetProperty("uploader").GetString() ?? "YouTube";
            return new(address, title, artist, seconds, data.GetProperty("is_live").ValueKind == JsonValueKind.True,
                data.GetProperty("format_id").GetString() ?? "bestaudio/best");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("YouTube took too long to respond. Try again."); }
        finally
        {
            deadline.Cancel(); await RadioTools.StopOwnedAsync(process);
            try { await diagnostic; } catch (OperationCanceledException) { }
        }
    }
}
