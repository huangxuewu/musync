using Musync.Core;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Musync.App;

public sealed class SpeakerViewModel : ObservableObject
{
    private readonly Localizer _text;
    private readonly Action<SpeakerViewModel, bool> _changed;
    private string? _error;
    private bool _playing;
    public SpeakerViewModel(SpeakerDevice device, SpeakerPreferences preferences, Localizer text, Action<SpeakerViewModel, bool> changed, bool selectionVisible = false)
    {
        Device = device; Preferences = preferences; _text = text; _changed = changed;
        SelectionVisible = selectionVisible;
        Minus = new AsyncCommand(_ => { DelayMs -= 10; return Task.CompletedTask; }, _ => { });
        Plus = new AsyncCommand(_ => { DelayMs += 10; return Task.CompletedTask; }, _ => { });
        MuteCommand = new AsyncCommand(_ => { Muted = !Muted; return Task.CompletedTask; }, _ => { });
        text.Changed += RefreshText;
        RefreshText();
    }
    public SpeakerDevice Device { get; private set; }
    public SpeakerPreferences Preferences { get; }
    public string Id => Device.Id;
    public string Name => Device.Name;
    public string Transport => Device.Transport.ToString();
    public string Icon => Device.Transport switch { SpeakerTransport.Bluetooth => "bluetooth", SpeakerTransport.AirPlay => "airplay", SpeakerTransport.Chromecast => "cast", _ => "monitor" };
    public string VolumeLabel => _text["Volume"];
    public string DelayLabel => _text["SyncDelay"];
    public string RoomLabel => _text["Room"];
    public bool Available => Device.UnsupportedReason is null;
    public bool SelectionVisible { get; }
    public bool Selected { get => Preferences.Selected; set { if (value == Preferences.Selected) return; Preferences.Selected = value; Raise(); Raise(nameof(ControlsVisible)); _changed(this, true); } }
    public bool ControlsVisible => !SelectionVisible || Selected;
    public double Volume { get => Preferences.Volume; set { Preferences.Volume = Math.Clamp(value, 0, 100); Raise(); _changed(this, false); } }
    public decimal DelayMs { get => Preferences.DelayMs; set { Preferences.DelayMs = Math.Clamp((int)value, 0, 10000); Raise(); _changed(this, false); } }
    public bool Muted { get => Preferences.Muted; set { Preferences.Muted = value; Raise(); Raise(nameof(MuteIcon)); Raise(nameof(MuteLabel)); _changed(this, false); } }
    public string MuteIcon => Muted ? "volume-x" : "volume-2";
    public string MuteLabel => _text[Muted ? "Unmute" : "Mute"];
    public string DecreaseDelayLabel => _text["DecreaseDelay"];
    public string IncreaseDelayLabel => _text["IncreaseDelay"];
    public string Status => _error is not null ? _text["DeviceError"] : Device.UnsupportedReason is not null ? _text[Device.UnsupportedReason] : _playing ? _text["Playing"] : _text["Ready"];
    public string StatusColor => _error is not null || Device.UnsupportedReason is not null ? "#C48C33" : _playing ? "#14B875" : "#A4ACB6";
    public string Details => _error ?? (Device.UnsupportedReason is null ? _text["Ready"] : _text[Device.UnsupportedReason]);
    public ObservableCollection<Choice> Rooms { get; } = new();
    public Choice? Room
    {
        get => Rooms.FirstOrDefault(r => r.Id == Preferences.Room) ?? Rooms.FirstOrDefault();
        set { if (value is null || value.Id == Preferences.Room) return; Preferences.Room = value.Id; Raise(); _changed(this, false); }
    }
    public ICommand Minus { get; }
    public ICommand Plus { get; }
    public ICommand MuteCommand { get; }
    private void RefreshText()
    {
        Rooms.Clear();
        foreach (var id in new[] { "all", "living", "bedroom", "kitchen" }) Rooms.Add(new(id, _text[id]));
        foreach (var name in new[] { nameof(VolumeLabel), nameof(DelayLabel), nameof(RoomLabel), nameof(MuteLabel), nameof(DecreaseDelayLabel), nameof(IncreaseDelayLabel), nameof(Status), nameof(Details), nameof(Room) }) Raise(name);
    }
    public void UpdateStatus(bool playing, string? error)
    {
        if (_playing == playing && _error == error) return;
        _playing = playing; _error = error;
        Raise(nameof(Status)); Raise(nameof(StatusColor)); Raise(nameof(Details));
    }
    public void UpdateDevice(SpeakerDevice device) { Device = device; Raise(nameof(Name)); Raise(nameof(Transport)); Raise(nameof(Icon)); Raise(nameof(Available)); Raise(nameof(Status)); }
    public void Detach() => _text.Changed -= RefreshText;
}
