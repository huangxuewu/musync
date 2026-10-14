using Musync.Core;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Musync.App;

public enum SpeakerPlaybackPhase { Available, Connecting, WaitingForSync, Playing, Paused }

public sealed class SpeakerViewModel : ObservableObject
{
    private readonly Localizer _text;
    private readonly Action<SpeakerViewModel, bool> _changed;
    private string? _error;
    private SpeakerPlaybackPhase _phase;
    private int _delayStepMs = 10;
    private int? _calibratedDelay;
    public SpeakerViewModel(SpeakerDevice device, SpeakerPreferences preferences, Localizer text, Action<SpeakerViewModel, bool> changed, bool selectionVisible = false)
    {
        Device = device; Preferences = preferences; _text = text; _changed = changed;
        SelectionVisible = selectionVisible;
        Minus = new AsyncCommand(_ => { DelayMs -= DelayStepMs; return Task.CompletedTask; }, _ => { }, () => DelayMs > 0);
        Plus = new AsyncCommand(_ => { DelayMs += DelayStepMs; return Task.CompletedTask; }, _ => { }, () => DelayMs < 10000);
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
    public string DelayLabel => _text[_calibratedDelay.HasValue ? "CalibratedDelay" : "SyncDelay"];
    public string RoomLabel => _text["Room"];
    public bool Available => Device.UnsupportedReason is null;
    public bool SelectionVisible { get; }
    public bool Selected { get => Preferences.Selected; set { if (value == Preferences.Selected) return; Preferences.Selected = value; Raise(); Raise(nameof(ControlsVisible)); _changed(this, true); } }
    public bool ControlsVisible => !SelectionVisible || Selected;
    public double Volume { get => Preferences.Volume; set { Preferences.Volume = Math.Clamp(value, 0, 100); Raise(); _changed(this, false); } }
    public decimal DelayMs
    {
        get => _calibratedDelay ?? Preferences.DelayMs;
        set
        {
            var delay = (int)Math.Clamp(value, 0, 10000);
            if (delay == DelayMs) return;
            if (_calibratedDelay.HasValue) _calibratedDelay = delay; else Preferences.DelayMs = delay;
            Raise();
            ((AsyncCommand)Minus).RefreshAvailability(); ((AsyncCommand)Plus).RefreshAvailability();
            _changed(this, false);
        }
    }
    public bool HasCalibratedDelay => _calibratedDelay.HasValue;
    public void SetCalibratedDelay(int? value)
    {
        if (_calibratedDelay == value) return;
        _calibratedDelay = value; Raise(nameof(DelayMs)); Raise(nameof(DelayLabel)); Raise(nameof(HasCalibratedDelay));
        ((AsyncCommand)Minus).RefreshAvailability(); ((AsyncCommand)Plus).RefreshAvailability();
    }
    public int DelayStepMs
    {
        get => _delayStepMs;
        set
        {
            if (value is not (1 or 10 or 100) || !Set(ref _delayStepMs, value)) return;
            Raise(nameof(DecreaseDelayLabel)); Raise(nameof(IncreaseDelayLabel));
        }
    }
    public bool Muted { get => Preferences.Muted; set { Preferences.Muted = value; Raise(); Raise(nameof(MuteIcon)); Raise(nameof(MuteLabel)); _changed(this, false); } }
    public string MuteIcon => Muted ? "volume-x" : "volume-2";
    public string MuteLabel => _text[Muted ? "Unmute" : "Mute"];
    public string DecreaseDelayLabel => string.Format(_text["DecreaseDelay"], DelayStepMs);
    public string IncreaseDelayLabel => string.Format(_text["IncreaseDelay"], DelayStepMs);
    public string Status => _error is not null ? _text["DeviceError"] : Device.UnsupportedReason is not null ? _text[Device.UnsupportedReason] : _text[_phase switch
    {
        SpeakerPlaybackPhase.Connecting => "SpeakerConnecting",
        SpeakerPlaybackPhase.WaitingForSync => "SpeakerWaitingForSync",
        SpeakerPlaybackPhase.Playing => "Playing",
        SpeakerPlaybackPhase.Paused => "Paused",
        _ => "SpeakerAvailable"
    }];
    public bool HasIssue => _error is not null || Device.UnsupportedReason is not null;
    public string StatusColor => HasIssue ? "#FFC970" : _phase == SpeakerPlaybackPhase.Playing ? "#64E8AD"
        : _phase is SpeakerPlaybackPhase.Connecting or SpeakerPlaybackPhase.WaitingForSync ? "#FF91A5" : "#CABFD4";
    public string Details => _error ?? Status;
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
        => UpdateStatus(playing ? SpeakerPlaybackPhase.Playing : SpeakerPlaybackPhase.Available, error);
    public void UpdateStatus(SpeakerPlaybackPhase phase, string? error)
    {
        if (_phase == phase && _error == error) return;
        _phase = phase; _error = error;
        Raise(nameof(Status)); Raise(nameof(StatusColor)); Raise(nameof(Details)); Raise(nameof(HasIssue));
    }
    public void UpdateDevice(SpeakerDevice device)
    {
        Device = device;
        foreach (var name in new[] { nameof(Name), nameof(Transport), nameof(Icon), nameof(Available), nameof(Status), nameof(StatusColor), nameof(Details), nameof(HasIssue) }) Raise(name);
    }
    public void Detach() => _text.Changed -= RefreshText;
}
