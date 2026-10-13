using Avalonia.Platform;
using System.Globalization;
using System.Text.Json;

namespace Musync.App;

public sealed class Localizer : ObservableObject
{
    private readonly Dictionary<string, string> _english;
    private Dictionary<string, string> _current;
    public string Language { get; private set; } = "en";
    public event Action? Changed;
    public Localizer(string language)
    {
        _english = Load("en");
        _current = _english;
        if (string.IsNullOrEmpty(language)) language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Switch(language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : language.StartsWith("ja") ? "ja" : "en");
    }
    public string this[string key] => _current.GetValueOrDefault(key) ?? _english.GetValueOrDefault(key) ?? key;
    private static Dictionary<string, string> Load(string language)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Musync/Assets/Languages/{language}.json"));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    public void Switch(string language)
    {
        Language = language is "zh-Hans" or "ja" ? language : "en";
        _current = Load(Language);
        Raise("Item"); Raise("Item[]"); Raise(null); Raise(nameof(Language)); Changed?.Invoke();
    }
}
