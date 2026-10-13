using Musync.Core;
using NAudio.Wave;
using System.Runtime.Versioning;

namespace Musync.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsCastMediaPreparer : IAudioFilePreparer, IAsyncDisposable
{
    private sealed record Media(string Path, double Duration, FileStream? SourceLease = null);
    private sealed class Entry(string key, CancellationTokenSource lifetime)
    {
        public string Key { get; } = key;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public Task<Media> Work { get; set; } = null!;
        public int Users;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _directory;
    private bool _disposed;
    public WindowsCastMediaPreparer(string directory)
    { _directory = Path.GetFullPath(directory); Directory.CreateDirectory(_directory); }

    public async Task<PreparedAudioFile> PrepareAsync(string source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(Path.GetFullPath(source));
        if (!file.Exists) throw new FileNotFoundException("The music file is unavailable.", file.FullName);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new(key, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                entry.Work = Task.Run(() => PrepareFile(file.FullName, entry.Lifetime.Token));
                _entries.Add(key, entry);
            }
            entry.Users++;
        }
        try
        {
            var media = await entry.Work.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); entry.Lifetime.Token.ThrowIfCancellationRequested();
            return new(media.Path, media.Duration, () => ReleaseAsync(entry));
        }
        catch { await ReleaseAsync(entry); throw; }
    }
    private Media PrepareFile(string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            && TryDirectMp3(source, token) is { } original) return original;
        return Encode(source, token);
    }
    private static Media? TryDirectMp3(string source, CancellationToken token)
    {
        FileStream? lease = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // Validate adjacent Layer III frames, including files with ID3 artwork.
            // A renamed WAV must still take the compatible encoding path.
            Id3v2Tag.ReadTag(lease);
            var first = Mp3Frame.LoadFromStream(lease, false);
            token.ThrowIfCancellationRequested();
            var second = Mp3Frame.LoadFromStream(lease, false);
            if (first is null || second is null || first.MpegLayer != MpegLayer.Layer3
                || second.MpegLayer != MpegLayer.Layer3 || first.MpegVersion != second.MpegVersion
                || first.SampleRate != second.SampleRate || second.FileOffset != first.FileOffset + first.FrameLength) return null;
            using var reader = WindowsAudioBackend.OpenFile(source);
            var duration = reader.TotalTime.TotalSeconds;
            if (!double.IsFinite(duration) || duration <= 0) return null;
            token.ThrowIfCancellationRequested();
            // Hold the source read lease until the final receiver releases it.
            // HTTP range requests can open their own handles, but the original
            // cannot be replaced underneath an active prepared receiver.
            var media = new Media(source, duration, lease); lease = null; return media;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or FormatException)
        { return null; }
        finally { lease?.Dispose(); }
    }
    private Media Encode(string source, CancellationToken token)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            using var reader = WindowsAudioBackend.OpenFile(source);
            var duration = reader.TotalTime.TotalSeconds;
            var provider = WindowsAudioBackend.Normalize(reader.ToSampleProvider());
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                new Mp3StreamEncoder().EncodeMp3(samples =>
                {
                    token.ThrowIfCancellationRequested();
                    var count = provider.Read(samples); return count - count % AudioFormat.Channels;
                }, bytes => { token.ThrowIfCancellationRequested(); stream.Write(bytes.Span); }, token);
                token.ThrowIfCancellationRequested();
            }
            return new(path, duration);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    private async ValueTask ReleaseAsync(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Users > 0) return;
            _entries.Remove(entry.Key); entry.Lifetime.Cancel();
        }
        try
        {
            Media media;
            try { media = await entry.Work; } catch { return; }
            if (media.SourceLease is { } original) { original.Dispose(); return; }
            // Only this lease's randomly named artifact is removed. A receiver
            // may briefly retain an HTTP range handle after STOP.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Delete(media.Path); break; }
                catch (IOException) when (attempt < 2) { await Task.Delay(100); }
            }
        }
        finally { entry.Lifetime.Dispose(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; _lifetime.Cancel(); }
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
