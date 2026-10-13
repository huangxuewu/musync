using Avalonia;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]
namespace Musync.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex)
        {
            var directory = Environment.GetEnvironmentVariable("MUSYNC_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musync");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"), DateTime.UtcNow.ToString("O") + " " + ex + Environment.NewLine);
            throw;
        }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
