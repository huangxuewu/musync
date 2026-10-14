using System.Windows.Input;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private string _settingsSection = "general";
    public bool SettingsGeneral => _settingsSection == "general";
    public bool SettingsAudio => _settingsSection == "audio";
    public bool SettingsSpotify => _settingsSection == "spotify";
    public ICommand SettingsNavigation { get; private set; } = null!;
    private void SelectSettingsSection(string section)
    {
        if (section is not ("general" or "audio" or "spotify")) return;
        _settingsSection = section;
        foreach (var name in new[] { nameof(SettingsGeneral), nameof(SettingsAudio), nameof(SettingsSpotify) }) Raise(name);
    }
    private void ShowSpotifySettings()
    { SelectSettingsSection("spotify"); SettingsOpen = true; }
}
