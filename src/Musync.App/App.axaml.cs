using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Musync.Core;
using Musync.Network;
using Musync.Platform.Windows;

namespace Musync.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var data = Environment.GetEnvironmentVariable("MUSYNC_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musync");
            Directory.CreateDirectory(data);
            var store = new SettingsStore(Path.Combine(data, "settings.json"));
            var radioTools = RadioTools.Locate();
            var backend = new YouTubeAudioBackend(new WindowsAudioBackend(), new YtDlpRadioResolver(radioTools), new FfmpegRadioDecoder(radioTools));
            var server = new LocalStreamServer();
            var encoder = new Mp3StreamEncoder();
            var preparedMusic = new WindowsCastMediaPreparer(Path.Combine(data, "cast-cache"));
            var bridgeDirectory = Path.Combine(AppContext.BaseDirectory, "AirPlayBridge");
            if (!File.Exists(Path.Combine(bridgeDirectory, "bridge.mjs")))
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
                bridgeDirectory = root is null ? bridgeDirectory : Path.Combine(root.FullName, "src", "Musync.AirPlayBridge");
            }
            var bundledNode = Path.Combine(bridgeDirectory, "node.exe");
            var bridge = new AirPlayBridge(bridgeDirectory, File.Exists(bundledNode) ? bundledNode : "node");
            var player = new PlaybackCoordinator(backend, (device, source) => device.Transport switch
            {
                SpeakerTransport.Chromecast when !source.IsCapture && !source.IsStreaming => new FileCastOutput(device, source, server, preparedMusic),
                SpeakerTransport.Chromecast => new CastOutput(device, server, encoder),
                SpeakerTransport.AirPlay when device.Properties?.GetValueOrDefault("ap2") == "true" => new AirPlay2Output(device, bridge),
                SpeakerTransport.AirPlay => new RaopOutput(device),
                _ => backend.CreateOutput(device)
            });
            var viewModel = new MainViewModel(backend, player, new SpotifyClient(new WindowsTokenVault(Path.Combine(data, "spotify.tokens"))), store, store.Load(), data);
            var window = new MainWindow { DataContext = viewModel };
            window.Cleanup = async () => { await viewModel.DisposeAsync(); await preparedMusic.DisposeAsync(); await bridge.DisposeAsync(); await server.DisposeAsync(); };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try { await viewModel.InitializeAsync(); } catch (Exception ex) { viewModel.ShowError(ex); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
