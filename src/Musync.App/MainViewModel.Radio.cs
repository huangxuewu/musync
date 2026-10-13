using Musync.Core;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private string _radioUrl = "", _radioName = "", _radioError = "";
    private bool _radioLoading;
    private CancellationTokenSource? _radioRequest;
    private readonly Dictionary<string, string> _radioPlaybackErrors = new(StringComparer.Ordinal);
    public ObservableCollection<RadioStationViewModel> RadioStations { get; } = [];
    public bool YouTubeMode => Source?.Id == "youtube";
    public bool IsRadioPage => _view == "radio";
    public bool IsRadioNav => IsRadioPage && !SettingsOpen;
    public bool NoRadioStations => RadioStations.Count == 0;
    public string RadioUrl { get => _radioUrl; set { if (Set(ref _radioUrl, value)) RadioError = ""; } }
    public string RadioName { get => _radioName; set { if (Set(ref _radioName, value)) RadioError = ""; } }
    public string RadioError { get => _radioError; private set { if (Set(ref _radioError, value)) Raise(nameof(HasRadioError)); } }
    public bool HasRadioError => RadioError.Length > 0;
    public bool RadioLoading { get => _radioLoading; private set { if (Set(ref _radioLoading, value)) Raise(nameof(PreparationDetail)); } }
    public string RadioHeadline => YouTubeMode && _current is not null ? Title : Text["RadioHeadline"];
    public ICommand SaveRadio { get; private set; } = null!;

    private void InitializeRadio()
    {
        _settings.RadioStations ??= [];
        _settings.RadioStations = _settings.RadioStations.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Id)
            && !string.IsNullOrWhiteSpace(s.Name) && s.Name.Length <= 80 && YouTubeAddress.TryParse(s.Url, out _))
            .Select(s => s with { Url = ParseRadioAddress(s.Url).Url }).DistinctBy(s => s.Url).Take(200).ToList();
        SaveRadio = new AsyncCommand(_ => { AddRadioStation(); return Task.CompletedTask; }, ShowError, () => !Busy);
        RefreshRadios();
    }
    private static YouTubeAddress ParseRadioAddress(string url)
    {
        if (!YouTubeAddress.TryParse(url, out var address)) throw new ArgumentException("Invalid YouTube address.");
        return address!;
    }
    private void AddRadioStation()
    {
        if (!YouTubeAddress.TryParse(RadioUrl, out var address)) { RadioError = Text["RadioInvalidUrl"]; return; }
        var name = RadioName.Trim();
        if (name.Length > 80) { RadioError = Text["RadioNameTooLong"]; return; }
        if (_settings.RadioStations.Any(s => s.Url == address!.Url)) { RadioError = Text["RadioAlreadySaved"]; return; }
        if (_settings.RadioStations.Count >= 200) { RadioError = Text["RadioLimit"]; return; }
        var station = new RadioStation(Guid.NewGuid().ToString("N"), name.Length == 0 ? address!.VideoId : name, address!.Url, name.Length == 0);
        _settings.RadioStations.Add(station); RefreshRadios(); SaveSoon();
        RadioUrl = ""; RadioName = ""; RadioError = ""; Status = Text["RadioSaved"];
    }
    public async Task PlayRadioAsync(RadioStation station)
    {
        if (_disposed || Busy || !_settings.RadioStations.Any(s => s.Id == station.Id)) return;
        RadioError = "";
        Navigate("radio"); Search = "";
        Source = Sources.First(s => s.Id == "youtube");
        await _sourceTransition;
        if (_disposed || !YouTubeMode) return;
        await PlayTrackAsync(new("youtube:" + ParseRadioAddress(station.Url).VideoId, station.Name, "YouTube", station.Url));
    }
    private async Task ActivateRadioAsync(RadioStation station)
    {
        if (_disposed || Busy || !_settings.RadioStations.Any(s => s.Id == station.Id)) return;
        if (_session && YouTubeMode && _current?.FilePath == station.Url) await TogglePlayback();
        else await PlayRadioAsync(station);
    }
    private TrackPlaybackPhase RadioPhase(RadioStation station)
    {
        if (!YouTubeMode || IsSwitchingSource || _current?.FilePath != station.Url) return TrackPlaybackPhase.None;
        if (IsPreparingPlayback) return TrackPlaybackPhase.Preparing;
        return !_session ? TrackPlaybackPhase.None : IsPlaying ? TrackPlaybackPhase.Playing : TrackPlaybackPhase.Paused;
    }
    private void RefreshRadioPlayback()
    {
        foreach (var station in RadioStations) station.RefreshPlayback();
    }
    private void ClearRadioPlaybackError(MusicTrack? track)
    {
        if (track?.FilePath is { } url && _radioPlaybackErrors.Remove(url)) RefreshRadioPlayback();
    }
    private void RememberRadioPlaybackError(MusicTrack? track, string errorKey)
    {
        if (track?.FilePath is not { } url || !_settings.RadioStations.Any(s => s.Url == url)) return;
        _radioPlaybackErrors[url] = errorKey;
        RefreshRadioPlayback();
    }
    private async Task RemoveRadioAsync(RadioStation station)
    {
        if (Busy) return;
        if (YouTubeMode && _current?.FilePath == station.Url)
        {
            await _playGate.WaitAsync(_lifetime.Token);
            try { await StopAsync(); _current = null; Duration = 0; RefreshCurrent(); }
            finally { _playGate.Release(); }
        }
        _settings.RadioStations.RemoveAll(s => s.Id == station.Id); _radioPlaybackErrors.Remove(station.Url);
        RefreshRadios(); SaveSoon(); Status = Text["RadioRemoved"];
    }
    private void RefreshRadioName(MusicTrack track)
    {
        var index = _settings.RadioStations.FindIndex(s => s.Url == track.FilePath);
        if (index < 0 || !_settings.RadioStations[index].AutoName) return;
        _settings.RadioStations[index] = _settings.RadioStations[index] with { Name = track.Title.Length > 80 ? track.Title[..80] : track.Title };
        RefreshRadios(); SaveSoon();
    }
    private void RefreshRadios()
    {
        RadioStations.Clear();
        foreach (var station in _settings.RadioStations) RadioStations.Add(new(station, this));
        Raise(nameof(NoRadioStations)); Raise(nameof(PlayEnabled)); RefreshRadioSelection();
    }
    private void RefreshRadioSelection()
    {
        foreach (var station in RadioStations)
        {
            station.IsActive = YouTubeMode && station.Station.Url == _current?.FilePath;
            station.RefreshPlayback();
        }
        Raise(nameof(RadioHeadline));
    }
    private void RefreshRadioText()
    {
        Raise(nameof(RadioHeadline));
        foreach (var station in RadioStations) station.RefreshText();
    }
    public sealed class RadioStationViewModel : ObservableObject
    {
        private readonly MainViewModel _owner;
        private bool _active;
        private TrackPlaybackPhase _phase;
        private string? _failureKey;
        public RadioStation Station { get; }
        public string Name => Station.Name;
        public string Url => Station.Url;
        public bool IsActive { get => _active; internal set => Set(ref _active, value); }
        public bool IsPreparing => _phase == TrackPlaybackPhase.Preparing;
        public bool HasPlaybackError => _failureKey is not null;
        public bool HasPlaybackActivity => _phase is TrackPlaybackPhase.Preparing or TrackPlaybackPhase.Playing;
        public string PlayIcon => HasPlaybackError ? "refresh-cw" : _phase == TrackPlaybackPhase.Playing ? "pause" : "play";
        public string StatusCaption => HasPlaybackError ? _owner.Text["RadioRetryHint"] : _phase == TrackPlaybackPhase.None ? Url : _owner.Text[IsPreparing ? "RadioBuffering" : _phase == TrackPlaybackPhase.Playing ? "Playing" : "Paused"];
        public string CaptionTooltip => _failureKey is { } key ? _owner.Text[key] : Url;
        public string PlayLabel => string.Format(_owner.Text[HasPlaybackError ? "RadioRetryNamed" : IsPreparing ? "RadioPreparingNamed" : _phase == TrackPlaybackPhase.Playing ? "RadioPauseNamed" : "RadioPlayNamed"], Name);
        public string RemoveLabel => string.Format(_owner.Text["RadioRemoveNamed"], Name);
        public bool CanEdit => !_owner.Busy;
        public ICommand Play { get; }
        public ICommand Remove { get; }
        public RadioStationViewModel(RadioStation station, MainViewModel owner)
        {
            Station = station; _owner = owner;
            Play = new AsyncCommand(_ => owner.ActivateRadioAsync(station), owner.ShowError, () => CanEdit);
            Remove = new AsyncCommand(_ => owner.RemoveRadioAsync(station), owner.ShowError, () => CanEdit);
            RefreshPlayback();
        }
        internal void RefreshText() { Raise(nameof(PlayLabel)); Raise(nameof(RemoveLabel)); Raise(nameof(StatusCaption)); Raise(nameof(CaptionTooltip)); }
        internal void RefreshAvailability() => Raise(nameof(CanEdit));
        internal void RefreshPlayback()
        {
            var phase = _owner.RadioPhase(Station);
            var failure = _owner._radioPlaybackErrors.GetValueOrDefault(Url);
            if (_phase == phase && _failureKey == failure) return;
            _phase = phase; _failureKey = failure;
            Raise(nameof(HasPlaybackError)); Raise(nameof(CaptionTooltip));
            Raise(nameof(IsPreparing)); Raise(nameof(HasPlaybackActivity)); Raise(nameof(PlayIcon));
            Raise(nameof(StatusCaption)); Raise(nameof(PlayLabel));
        }
    }
}
