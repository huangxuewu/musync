using Musync.Core;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

namespace Musync.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsTokenVault(string path) : ITokenVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Musync.Spotify.v1");
    public string? Load()
    {
        if (!File.Exists(path)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }
    public void Save(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path + ".writing", bytes);
        File.Move(path + ".writing", path, true);
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}
