using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Musync.Core;
using Musync.Network;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;

namespace Musync.App;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAudioBackend _backend;
    private readonly PlaybackCoordinator _player;
    private readonly SpotifyClient _spotify;
    private readonly SpotifyArtworkResolver _listArtwork;
    private readonly LosslessSourceProbe _losslessSources = new();
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly string _dataDirectory;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SpeakerDevice>>> _discoverNetwork;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _playGate = new(1);
    private CancellationTokenSource? _saveDelay;
    private CancellationTokenSource? _routeDelay;
    private readonly List<MusicTrack> _library = [];
    private List<MusicTrack> _spotifyResults = [];
    private MusicTrack? _current;
    private MusicTrack? _playlistTrack;
    private string _search = "";
    private string _searchedQuery = "", _searchError = "";
    private bool _searching;
    private long _searchVersion;
    private CancellationTokenSource? _searchRequest, _artworkRequest;
    private string _status = "";
    private string _view = "home";
    private string _listTitle = "";
    private string? _playlistFilter;
    private Choice? _source;
    private Choice? _language;
    private bool _settingsOpen, _playlistOpen, _busy, _scanning, _isPlaying, _session, _shuffle, _repeat, _ending;
    private bool _disposed;
    private double _position, _duration;
    private Bitmap? _spotifyArtwork;
    private string _playlistName = "";
    private string _playlistError = "";
    private string? _activeSource;
    private DateTime _spotifyNextPoll;
    private bool _spotifyPolling;
    private bool _tickRunning;
    private bool _changingSelection;
    private bool _startingPlayback;
    private bool _preparingPlayback, _preparationCancelled;
    private int _preparationTotal;
    private SpeakerLink? _linkDraft;
    private bool _linkEditing, _linkPanelOpen, _applyingLink;
    private string _linkName = "", _linkEditError = "";

    internal Task<string?> ReadLosslessSourceAsync(MusicTrack track, CancellationToken token) =>
        Task.Run(() => _losslessSources.Read(track.FilePath), token);

    public MainViewModel(IAudioBackend backend, PlaybackCoordinator player, SpotifyClient spotify,
        SettingsStore store, AppSettings settings, string dataDirectory,
        Func<CancellationToken, Task<IReadOnlyList<SpeakerDevice>>>? discoverNetwork = null, IAcousticCalibrationService? calibration = null)
    {
        _backend = backend; _player = player; _spotify = spotify; _store = store; _settings = settings; _dataDirectory = dataDirectory;
        _calibrationService = calibration;
        _discoverNetwork = discoverNetwork ?? new NetworkDiscovery().DiscoverAsync;
        settings.ArtworkCacheLimitMb = settings.ArtworkCacheLimitMb is < 50 or > 10000 ? 500 : settings.ArtworkCacheLimitMb;
        _listArtwork = new(spotify, Path.Combine(dataDirectory, "artwork-cache"), settings.ArtworkCacheLimitMb * 1024L * 1024);
        if (settings.MasterVolume > 0) _localVolumeBeforeMute = settings.MasterVolume;
        Text = new(settings.Language);
        Languages = [new("en", "English"), new("zh-Hans", "简体中文"), new("ja", "日本語")];
        _language = Languages.First(l => l.Id == Text.Language);
        _settings.Language = Text.Language;
        SpeakerLinks.MigrateLegacy(settings, Text["MainLink"]);
        foreach (var link in settings.Links) link.Calibrations ??= new();
        Text.Changed += RefreshText;
        _library.AddRange(settings.Library.Where(t => t.FilePath is not null && File.Exists(t.FilePath)));
        _library.AddRange(DemoMusic.EnsureLibrary(Path.Combine(dataDirectory, "Demos")).Where(t => _library.All(l => l.Id != t.Id)));
        if (!settings.PlaylistsInitialized && settings.Speakers.Count == 0 && settings.Playlists.Count == 0)
        {
            settings.Playlists["Sunday slow"] = ["demo:0", "demo:1"];
            settings.Playlists["After hours"] = ["demo:1", "demo:2"];
        }
        settings.PlaylistsInitialized = true;
        _current = _library.FirstOrDefault();
        _duration = _current?.DurationSeconds ?? 0;
        _source = new("local", Text["Local"]);
        _status = Text["Idle"];
        _spotify.ClientId = _settings.SpotifyClientId;
        RefreshText(); RefreshTracks(); RefreshPlaylists();
        PlayCommand = new AsyncCommand(_ => TogglePlayback(), ShowError);
        CancelPreparation = new AsyncCommand(_ => CancelPreparationAsync(), ShowError);
        PlayEverywhere = new AsyncCommand(_ => PlayTrackAsync(_current), ShowError);
        NextCommand = new AsyncCommand(_ => NextAsync(1), ShowError);
        PreviousCommand = new AsyncCommand(_ => NextAsync(-1), ShowError);
        ScanCommand = new AsyncCommand(_ => ScanAsync(), ShowError);
        SearchCommand = new AsyncCommand(_ => SearchAsync(), ShowError);
        ClearSearch = new AsyncCommand(_ => { Search = ""; return Task.CompletedTask; }, ShowError);
        BrowseLibrary = new AsyncCommand(_ => { Search = ""; Navigate("library"); return Task.CompletedTask; }, ShowError);
        ImportCommand = new AsyncCommand(async _ => { if (ImportFolders is not null) await ImportFolders(); }, ShowError);
        Navigation = new AsyncCommand(p => { Navigate(p?.ToString() ?? "home"); return Task.CompletedTask; }, ShowError);
        SettingsCommand = new AsyncCommand(_ => { SettingsOpen = true; return Task.CompletedTask; }, ShowError);
        SettingsNavigation = new AsyncCommand(p => { SelectSettingsSection(p?.ToString() ?? "general"); return Task.CompletedTask; }, ShowError);
        CloseSettings = new AsyncCommand(async _ => { await SaveSettings(); SettingsOpen = false; }, ShowError);
        ConnectSpotify = new AsyncCommand(_ => ConnectSpotifyAsync(), ShowError);
        DisconnectSpotify = new AsyncCommand(async _ => { _spotify.Disconnect(); _listArtwork.Clear(includeLocalMetadata: false); RefreshTracks(); _spotifyStateVersion++; Raise(nameof(SpotifyStatus)); if (SpotifyMode) { await StopAsync(); ClearSpotifyPlayback(); } Status = Text["NotConnected"]; }, ShowError);
        RefreshSpotifyDevices = new AsyncCommand(_ => RefreshSpotifyDevicesAsync(), ShowError);
        DashboardCommand = new AsyncCommand(_ => { OpenBrowser(new("https://developer.spotify.com/dashboard")); return Task.CompletedTask; }, ShowError);
        ShuffleCommand = new AsyncCommand(_ => ToggleSpotifyOptionAsync(shuffle: true), ShowError);
        RepeatCommand = new AsyncCommand(_ => ToggleSpotifyOptionAsync(shuffle: false), ShowError);
        MuteCommand = new AsyncCommand(_ => { TogglePlayerMute(); return WaitForVolumeAsync(); }, ShowError);
        ResetDelays = new AsyncCommand(_ => { ResetCalibrationDelays(); return Task.CompletedTask; }, ShowError);
        CalibrateCommand = new AsyncCommand(_ => CalibrateAsync(), ShowError);
        LikeCurrent = new AsyncCommand(_ => { if (CanLikeCurrent) ToggleLike(_current!.Id); return Task.CompletedTask; }, ShowError);
        TogglePlaylists = new AsyncCommand(_ => { PlaylistsExpanded = !PlaylistsExpanded; return Task.CompletedTask; }, ShowError);
        NewPlaylist = new AsyncCommand(_ => { OpenPlaylist(null); return Task.CompletedTask; }, ShowError, () => CanManagePlaylists);
        SavePlaylist = new AsyncCommand(_ => { CreatePlaylist(); return Task.CompletedTask; }, ShowError, () => CanManagePlaylists);
        ClosePlaylist = new AsyncCommand(_ => { PlaylistOpen = false; return Task.CompletedTask; }, ShowError);
        CreateLink = new AsyncCommand(_ => { BeginLinkEdit(null); return Task.CompletedTask; }, ShowError);
        EditLink = new AsyncCommand(_ => { if (ActiveLinkProfile is not null) BeginLinkEdit(ActiveLinkProfile); return Task.CompletedTask; }, ShowError);
        SaveLink = new AsyncCommand(_ => SaveLinkAsync(), ShowError);
        CancelLink = new AsyncCommand(_ => { DiscardLinkDraft(); return Task.CompletedTask; }, ShowError);
        LinkedSpeakers.CollectionChanged += LinkedSpeakersChanged;
        RefreshLinks();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, async (_, _) => await TickAsync());
        _timer.Start();
        InitializeRadio();
        RefreshPresentation();
        _cacheMaintenance = MaintainArtworkCacheAsync();
    }
    public Localizer Text { get; }
    public ObservableCollection<SpeakerViewModel> Speakers { get; } = [];
    public ObservableCollection<Choice> Links { get; } = [];
    public ObservableCollection<SpeakerViewModel> LinkedSpeakers { get; } = [];
    public ObservableCollection<SpeakerViewModel> DraftSpeakers { get; } = [];
    private SpeakerLink? ActiveLinkProfile => _settings.Links.FirstOrDefault(l => l.Id == _settings.ActiveLinkId);
    public Choice? ActiveLink
    {
        get => Links.FirstOrDefault(l => l.Id == _settings.ActiveLinkId);
        set
        {
            if (value is null || value.Id == _settings.ActiveLinkId || Busy || IsLinkEditing) return;
            _ = RunSafely(async () =>
            {
                await _playGate.WaitAsync();
                try
                {
                    Busy = true; if (!SpotifyMode) await StopAsync();
                    _settings.ActiveLinkId = value.Id; ApplyActiveLink(); SaveSoon();
                    Status = Text["LinkReady"];
                }
                finally { Busy = false; _playGate.Release(); }
            });
        }
    }
    public bool HasActiveLink => ActiveLinkProfile is not null;
    public bool NoLinks => Links.Count == 0;
    public bool LinkPanelOpen { get => _linkPanelOpen; set => Set(ref _linkPanelOpen, value); }
    public bool IsLinkEditing
    {
        get => _linkEditing;
        private set { if (Set(ref _linkEditing, value)) { Raise(nameof(LinkOverview)); Raise(nameof(VisibleLinkSpeakers)); Raise(nameof(LinkPopoverTitle)); Raise(nameof(CanPlayLink)); Raise(nameof(CanSaveLink)); Raise(nameof(NoDraftSpeakers)); Raise(nameof(HasDelayControls)); } }
    }
    public bool LinkOverview => !IsLinkEditing;
    public string LinkName { get => _linkName; set { if (Set(ref _linkName, value)) LinkEditError = ""; } }
    public string LinkEditError { get => _linkEditError; private set { if (Set(ref _linkEditError, value)) Raise(nameof(HasLinkEditError)); } }
    public bool HasLinkEditError => !string.IsNullOrEmpty(LinkEditError);
    public bool NoDraftSpeakers => IsLinkEditing && DraftSpeakers.Count == 0;
    public string LinkSelectionSummary => string.Format(Text["LinkSelectionSummary"], DraftSpeakers.Count(s => s.Selected), DraftSpeakers.Count);
    public string LinkPopoverTitle => Text[IsLinkEditing ? "LinkConfiguration" : "Links"];
    public IEnumerable<SpeakerViewModel> VisibleLinkSpeakers => IsLinkEditing ? DraftSpeakers : LinkedSpeakers;
    public string PlaybackTargetLabel => SpotifyMode ? "Spotify" : ActiveLinkProfile?.Name ?? Text["NoActiveLink"];
    public string LinkSummary => ActiveLinkProfile is { } link
        ? string.Format(Text["LinkSummary"], link.Speakers.Count, SpeakerLinks.Resolve(link, Speakers.Select(s => s.Device)).Count)
        : Text["LinkEmpty"];
    public bool CanPlayLink => HasActiveLink && RoutingAvailable && !Busy && !IsLinkEditing
        && SpeakerLinks.Resolve(ActiveLinkProfile!, Speakers.Select(s => s.Device)).Count > 0;
    public bool CanConfigureLink => !Busy;
    public bool CanEditLink => HasActiveLink && !Busy;
    public bool CanRescan => !Busy && !IsScanning;
    public bool CanSaveLink => !Busy && IsLinkEditing;
    public ICommand CreateLink { get; }
    public ICommand EditLink { get; }
    public ICommand SaveLink { get; }
    public ICommand CancelLink { get; }
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];
    public ObservableCollection<PlaylistViewModel> Playlists { get; } = [];
    public ObservableCollection<Choice> Sources { get; } = [];
    public List<Choice> Languages { get; }
    public ObservableCollection<Choice> CaptureEndpoints { get; } = [];
    public ObservableCollection<Choice> SpotifyDevices { get; } = [];
    public Func<Task>? ImportFolders { get; set; }
    private Choice? _spotifyDevice;
    public Choice? SpotifyDevice { get => _spotifyDevice; set => Set(ref _spotifyDevice, value); }
    public Choice? CaptureEndpoint
    {
        get => CaptureEndpoints.FirstOrDefault(c => c.Id == _settings.CaptureDeviceId);
        set { if (value is null) return; _settings.CaptureDeviceId = value.Id; Raise(); SaveSoon(); }
    }
    public Choice? Language
    {
        get => _language;
        set { if (value is null || !Set(ref _language, value)) return; _settings.Language = value.Id; Text.Switch(value.Id); SaveSoon(); }
    }
    public Choice? Source
    {
        get => _source;
        set
        {
            if (_disposed || value is null || value.Id == _source?.Id) return;
            _source = value;
            InvalidateCalibration();
            var version = ++_sourceVersion;
            _sourceLookup?.Cancel();
            _radioRequest?.Cancel();
            IsSwitchingSource = true;
            _player.CancelPreparation();
            _session = false; IsPlaying = false; Position = 0;
            Status = Text["SwitchingSource"];
            ResetSpotifyVolume();
            _spotifyStateVersion++; IsSeeking = false; ResetProgressTransition();
            InvalidateSearch(); _artworkRequest?.Cancel(); SpotifyArtwork = null;
            if (value.Id == "local") { _current = _library.FirstOrDefault(); Duration = _current?.DurationSeconds ?? 0; }
            if (value.Id == "spotify") { _current = null; Duration = 0; }
            if (value.Id == "youtube") { _current = null; Duration = 0; }
            if (value.Id == "youtube") { _view = "radio"; _playlistFilter = null; Raise(nameof(IsRadioPage)); Raise(nameof(IsMusicView)); }
            if (value.Id == "capture") Duration = 0;
            Raise(); RefreshTracks(); Raise(nameof(SpotifyMode)); Raise(nameof(YouTubeMode)); Raise(nameof(IsLocal)); Raise(nameof(RoutingAvailable)); RefreshCurrent();
            RefreshActiveCalibration();
            Raise(nameof(PlaybackTargetLabel)); Raise(nameof(CanPlayLink));
            _sourceTransition = SwitchSourceAsync(version);
        }
    }
    public bool SpotifyMode => Source?.Id == "spotify";
    public bool IsLocal => Source?.Id == "local";
    public bool RoutingAvailable => !SpotifyMode;
    public bool IsMusicView => _view is not ("playlists" or "radio");
    public bool IsHomePage => !SettingsOpen && _playlistFilter is null && _view == "home";
    public bool IsLibraryPage => !SettingsOpen && _playlistFilter is null && _view == "library";
    public bool IsLikedPage => !SettingsOpen && _playlistFilter is null && _view == "liked";
    public bool IsPlaylistsPage => !SettingsOpen && _playlistFilter is null && _view == "playlists";
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            // The collection overview has no song rows. A library search must
            // reveal its results while named playlists retain their own scope.
            if (IsLocal && HasSearchQuery && _view == "playlists") _view = "library";
            InvalidateSearch(); RefreshTracks(); Raise(nameof(IsMusicView));
        }
    }
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(Search);
    public bool IsSearching { get => _searching; private set { if (Set(ref _searching, value)) RefreshSearchState(); } }
    public bool HasSearchError => _searchError.Length > 0;
    public bool HasSearchFeedback => HasSearchError || IsSearching
        || HasSearchQuery && (!SpotifyMode || _searchedQuery == Search.Trim());
    public string SearchPlaceholder => Text[SpotifyMode ? "SearchSpotify" : "Search"];
    public string SearchScope => SpotifyMode ? "Spotify" : _playlistFilter ?? Text[_view == "liked" ? "Liked" : "Library"];
    public string SearchNote => HasSearchError ? _searchError : IsSearching ? Text["Searching"]
        : HasSearchQuery && (!SpotifyMode || _searchedQuery == Search.Trim())
            ? string.Format(Text["SearchResultCount"], Tracks.Count) : Text[SpotifyMode ? "SearchSpotifyHint" : "SearchLibraryHint"];
    public bool NoSearchResults => IsMusicView && HasSearchQuery && !IsSearching && !HasSearchError && Tracks.Count == 0
        && (!SpotifyMode || _searchedQuery == Search.Trim());
    public bool IsEmptyCollection => IsLocal && IsMusicView && !HasSearchQuery && Tracks.Count == 0 && (_playlistFilter is not null || _view == "liked");
    public string EmptyCollectionTitle => Text[_view == "liked" ? "EmptyLikedTitle" : "EmptyPlaylistTitle"];
    public string EmptyCollectionHint => Text[_view == "liked" ? "EmptyLikedHint" : "EmptyPlaylistHint"];
    public bool ShowHero => IsMusicView && !HasSearchQuery;
    public bool ShowPlaylists => !HasSearchQuery;
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value) && value != Text["Idle"] && value != Text["Playing"] && value != Text["Paused"]
                && value != Text["Starting"] && value != Text["Scanning"] && value != Text["SwitchingSource"])
                ShowToast(value);
        }
    }
    public bool Busy
    {
        get => _busy || IsSwitchingSource;
        private set
        {
            var previous = Busy;
            if (_busy == value) return;
            _busy = value;
            if (previous != Busy) Raise();
            RefreshBusyProperties();
        }
    }
    public bool IsScanning { get => _scanning; private set { if (Set(ref _scanning, value)) Raise(nameof(CanRescan)); } }
    public bool SettingsOpen
    {
        get => _settingsOpen;
        set { if (Set(ref _settingsOpen, value)) { RefreshNavigationState(); UpdatePlaylistSelection(); if (value) _ = RunSafely(RefreshArtworkCacheAsync); } }
    }
    public bool PlaylistOpen { get => _playlistOpen; set => Set(ref _playlistOpen, value); }
    public string PlaylistName { get => _playlistName; set { if (Set(ref _playlistName, value)) PlaylistError = ""; } }
    public string PlaylistError { get => _playlistError; private set { if (Set(ref _playlistError, value)) Raise(nameof(HasPlaylistError)); } }
    public bool HasPlaylistError => PlaylistError.Length > 0;
    public bool CanManagePlaylists => !Busy;
    public string PlaylistDialogTitle => Text[_playlistTrack is null ? "CreatePlaylist" : "AddPlaylist"];
    public string PlaylistSectionLabel => Text[PlaylistsExpanded ? "CollapsePlaylists" : "ExpandPlaylists"];
    public bool PlaylistsExpanded
    {
        get => _settings.PlaylistsExpanded;
        set { if (_settings.PlaylistsExpanded == value) return; _settings.PlaylistsExpanded = value; Raise(); Raise(nameof(PlaylistSectionLabel)); SaveSoon(); }
    }
    public string ListTitle { get => _listTitle; private set => Set(ref _listTitle, value); }
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) { Raise(nameof(PlayIcon)); Raise(nameof(PlayLabel)); RefreshTrackPlayback(); RefreshRadioPlayback(); } } }
    public string PlayIcon => IsPlaying ? "pause" : "play";
    public string PlayLabel => IsSwitchingSource ? Text["SwitchingSource"] : IsPreparingPlayback ? PreparationHeadline : Text[IsPlaying ? "Pause" : "Play"];
    public bool IsPreparingPlayback
    {
        get => _preparingPlayback;
        private set
        {
            if (!Set(ref _preparingPlayback, value)) return;
            Raise(nameof(PlayEnabled)); Raise(nameof(Seekable)); RefreshPreparationFeedback();
            RefreshSpeakerPlaybackStatus();
            RefreshTrackPlayback();
            RefreshRadioPlayback();
        }
    }
    public bool PlayEnabled => !Busy && !IsPreparingPlayback && (!YouTubeMode || _current is not null || RadioStations.Count > 0);
    private bool PreparingRadioAudio => YouTubeMode && _startingPlayback && !RadioLoading
        && (!_player.IsPreparing || _player.PreparingOutputs == 0);
    public string PreparationHeadline => Text[_player.IsActivating ? "StartingPlayback" : YouTubeMode && RadioLoading ? "RadioLoadingTitle" : PreparingRadioAudio ? "RadioBuffering" : "PreparingSpeakers"];
    public string PreparationSummary => _player.IsActivating || YouTubeMode && (RadioLoading || PreparingRadioAudio) ? PreparationHeadline : PreparationDetail;
    public string PreparationDetail
    {
        get
        {
            var total = _player.IsPreparing ? _player.PreparingOutputs : 0;
            if (_player.IsActivating) return Text["StartingPlaybackDetail"];
            if (YouTubeMode && RadioLoading) return Text["RadioLoadingDetail"];
            if (PreparingRadioAudio) return Text["RadioBufferingDetail"];
            return string.Format(Text["SpeakerPreparationProgress"], _player.IsPreparing ? _player.ReadyOutputs : 0,
                total > 0 ? total : _preparationTotal);
        }
    }
    private void RefreshPreparationFeedback()
    {
        Raise(nameof(PlayLabel)); Raise(nameof(PreparationHeadline)); Raise(nameof(PreparationDetail)); Raise(nameof(PreparationSummary));
        RefreshRadioPlayback();
    }
    public ICommand CancelPreparation { get; }
    private async Task CancelPreparationAsync()
    {
        if (!IsPreparingPlayback) return;
        _preparationCancelled = true;
        _radioRequest?.Cancel();
        _player.CancelPreparation();
        await _player.StopAsync();
        _session = false; _activeSource = null; IsPlaying = false; Status = Text["Idle"]; RefreshCurrent();
    }
    public string Title => Source?.Id == "capture" ? Text["CaptureTitle"] : _current?.Title ?? (SpotifyMode ? "Spotify" : "Musync");
    public string Artist => Source?.Id == "capture" ? CaptureEndpoint?.Name ?? Text["CaptureInfo"]
        : _current is null ? Text[SpotifyMode ? "SpotifySetup" : "LocalHint"]
        : string.IsNullOrWhiteSpace(_current.Artist) && IsLocal ? Text["Local"] : _current.Artist;
    public string HeroNote => _current?.IsDemo == true ? Text["Demo"] : Artist;
    public bool CurrentLiked => _current is not null && IsLiked(_current.Id);
    public bool CanLikeCurrent => _current is not null && !YouTubeMode;
    public string LikeCurrentLabel => Text[CurrentLiked ? "Unlike" : "Like"];
    public Bitmap? SpotifyArtwork
    {
        get => _spotifyArtwork;
        private set
        {
            var old = _spotifyArtwork;
            if (!Set(ref _spotifyArtwork, value)) return;
            Raise(nameof(HasSpotifyArtwork)); RetireArtwork(old);
        }
    }
    public bool HasSpotifyArtwork => SpotifyArtwork is not null && SpotifyMode;
    public bool ShowArtist => !SpotifyMode;
    public double Position { get => _position; private set { if (Set(ref _position, value)) { Raise(nameof(PositionText)); RefreshLyricWindow(); } } }
    public double Duration { get => _duration; private set { if (Set(ref _duration, value)) { Raise(nameof(DurationText)); Raise(nameof(Seekable)); } } }
    public string PositionText => TimeSpan.FromSeconds(Math.Max(0, Position)).ToString(@"m\:ss");
    public string DurationText => Duration > 0 ? TimeSpan.FromSeconds(Duration).ToString(@"m\:ss") : Source?.Id == "capture" ? "LIVE" : "0:00";
    public bool Seekable => _session && !Busy && !IsPreparingPlayback && !YouTubeMode && Source?.Id != "capture" && Duration > 0;
    public bool IsSeeking { get; set; }
    public string SelectedCount => $"{Speakers.Count(s => s.Selected && s.Available)} {Text["Selected"]}";
    public string AvailableCount => $"{Speakers.Count} {Text["OutputCount"]}";
    public double MasterVolume
    {
        get => _settings.MasterVolume;
        set
        {
            _settings.MasterVolume = Math.Clamp(value, 0, 100); Raise();
            if (_settings.MasterVolume > 0) _localVolumeBeforeMute = _settings.MasterVolume;
            if (!_applyingLink && ActiveLinkProfile is { } link) link.MasterVolume = _settings.MasterVolume;
            foreach (var speaker in Speakers) _player.Update(speaker.Id, PlaybackPreferences(speaker), _settings.MasterVolume);
            if (!SpotifyMode) RefreshVolumeProperties();
            SaveSoon();
        }
    }
    public bool Shuffle { get => _shuffle; set { if (Set(ref _shuffle, value)) Raise(nameof(ShuffleColor)); } }
    public bool Repeat { get => _repeat; set { if (Set(ref _repeat, value)) Raise(nameof(RepeatColor)); } }
    public string ShuffleColor => Shuffle ? "#FF5974" : "#D5CADF";
    public string RepeatColor => Repeat ? "#FF5974" : "#D5CADF";
    public string ClientId { get => _settings.SpotifyClientId; set { _settings.SpotifyClientId = value.Trim(); Raise(); } }
    public string SpotifyStatus => Text[_spotify.Connected ? "Connected" : "NotConnected"];
    public string RedirectUri => SpotifyClient.RedirectUri;
    public ICommand PlayCommand { get; }
    public ICommand PlayEverywhere { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearch { get; }
    public ICommand BrowseLibrary { get; }
    public ICommand ImportCommand { get; }
    public ICommand Navigation { get; }
    public ICommand SettingsCommand { get; }
    public ICommand CloseSettings { get; }
    public ICommand ConnectSpotify { get; }
    public ICommand DisconnectSpotify { get; }
    public ICommand RefreshSpotifyDevices { get; }
    public ICommand DashboardCommand { get; }
    public ICommand ShuffleCommand { get; }
    public ICommand RepeatCommand { get; }
    public ICommand ResetDelays { get; }
    public ICommand CalibrateCommand { get; }
    public ICommand LikeCurrent { get; }
    public ICommand NewPlaylist { get; }
    public ICommand TogglePlaylists { get; }
    public ICommand SavePlaylist { get; }
    public ICommand ClosePlaylist { get; }

    private void RefreshLinks()
    {
        Links.Clear(); foreach (var link in _settings.Links) Links.Add(new(link.Id, link.Name));
        foreach (var property in new[] { nameof(ActiveLink), nameof(HasActiveLink), nameof(NoLinks), nameof(LinkSummary), nameof(PlaybackTargetLabel), nameof(CanPlayLink), nameof(CanEditLink) }) Raise(property);
        RefreshLinkFeedback();
    }
    private void ApplyActiveLink()
    {
        foreach (var old in LinkedSpeakers.Where(s => !Speakers.Contains(s))) old.Detach();
        LinkedSpeakers.Clear();
        _applyingLink = true;
        try
        {
            var link = ActiveLinkProfile;
            foreach (var speaker in Speakers)
            {
                speaker.SetCalibratedDelay(null);
                speaker.DelayStepMs = _delayStepMs;
                if (link is not null && link.Speakers.TryGetValue(speaker.Id, out var member))
                {
                    member.Name = speaker.Name; member.Transport = speaker.Device.Transport;
                    speaker.Selected = true; speaker.Volume = member.Preferences.Volume;
                    speaker.DelayMs = member.Preferences.DelayMs; speaker.Muted = member.Preferences.Muted;
                    LinkedSpeakers.Add(speaker);
                }
                else speaker.Selected = false;
            }
            if (link is not null)
            {
                foreach (var (id, member) in link.Speakers.Where(m => Speakers.All(s => s.Id != m.Key)))
                {
                    var preferences = SpeakerLinks.Copy(member.Preferences);
                    preferences.Selected = true;
                    _settings.Speakers[id] = preferences;
                    LinkedSpeakers.Add(new(new(id, member.Name, member.Transport, UnsupportedReason: "DeviceOffline"), preferences, Text, SpeakerChanged) { DelayStepMs = _delayStepMs });
                }
                MasterVolume = link.MasterVolume;
            }
        }
        finally { _applyingLink = false; }
        RefreshActiveCalibration();
        RefreshLinks(); Raise(nameof(SelectedCount)); Raise(nameof(VisibleLinkSpeakers)); Raise(nameof(HasDelayControls));
    }
    private void BeginLinkEdit(SpeakerLink? link)
    {
        if (Busy) return;
        DiscardLinkDraft();
        _linkDraft = SpeakerLinks.Draft(link); LinkName = _linkDraft.Name; LinkEditError = "";
        IsLinkEditing = true; RefreshDraftDevices(); LinkPanelOpen = true;
    }
    private void RefreshDraftDevices()
    {
        if (_linkDraft is null) return;
        var devices = Speakers.Select(s => s.Device).ToDictionary(d => d.Id);
        foreach (var (id, member) in _linkDraft.Speakers)
            devices.TryAdd(id, new(id, member.Name, member.Transport, UnsupportedReason: "DeviceOffline"));
        foreach (var draft in DraftSpeakers)
            draft.UpdateDevice(devices.GetValueOrDefault(draft.Id) ?? draft.Device with { UnsupportedReason = "DeviceOffline" });
        foreach (var device in devices.Values.Where(d => DraftSpeakers.All(s => s.Id != d.Id)))
        {
            var preferences = _linkDraft.Speakers.TryGetValue(device.Id, out var member)
                ? SpeakerLinks.Copy(member.Preferences) : new SpeakerPreferences();
            preferences.Selected = member is not null;
            DraftSpeakers.Add(new(device, preferences, Text, DraftSpeakerChanged, selectionVisible: true) { DelayStepMs = _delayStepMs });
        }
        Raise(nameof(LinkSelectionSummary)); Raise(nameof(NoDraftSpeakers)); Raise(nameof(HasDelayControls));
    }
    private void DraftSpeakerChanged(SpeakerViewModel speaker, bool selectionChanged)
    {
        LinkEditError = "";
        if (selectionChanged) { Raise(nameof(LinkSelectionSummary)); Raise(nameof(HasDelayControls)); }
        if (!selectionChanged || !speaker.Selected) return;
        foreach (var alternate in DraftSpeakers.Where(s => s.Id != speaker.Id && s.Selected && ReceiverRoutes.SameReceiver(s.Device, speaker.Device)))
            alternate.Selected = false;
    }
    public void DiscardLinkDraft()
    {
        foreach (var speaker in DraftSpeakers) speaker.Detach();
        DraftSpeakers.Clear(); _linkDraft = null; IsLinkEditing = false; LinkEditError = "";
        Raise(nameof(LinkSelectionSummary));
        Raise(nameof(CanPlayLink));
        Raise(nameof(HasDelayControls));
    }
    private async Task SaveLinkAsync()
    {
        if (_linkDraft is null || Busy) return;
        var saved = _linkDraft;
        saved.Name = LinkName.Trim();
        saved.Speakers = DraftSpeakers.Where(s => s.Selected).ToDictionary(s => s.Id, s => new LinkedSpeaker
        {
            Name = s.Name, Transport = s.Device.Transport, Preferences = SpeakerLinks.Copy(s.Preferences)
        });
        if (SpeakerLinks.ValidationError(saved, _settings.Links) is { } error) { LinkEditError = Text[error]; return; }
        await _playGate.WaitAsync();
        try
        {
            Busy = true; if (!SpotifyMode) await StopAsync();
            var index = _settings.Links.FindIndex(l => l.Id == saved.Id);
            if (index < 0) _settings.Links.Add(saved); else _settings.Links[index] = saved;
            _settings.ActiveLinkId = saved.Id;
            ApplyActiveLink(); await _store.SaveAsync(_settings);
            DiscardLinkDraft(); Status = Text["LinkReady"];
        }
        finally { Busy = false; _playGate.Release(); }
    }
    public async Task InitializeAsync() { await ScanAsync(); SaveSoon(); }
    private void RefreshText()
    {
        var id = _source?.Id ?? "local";
        Raise(nameof(Text));
        RefreshDelaySteps();
        RefreshCalibrationText();
        Raise(nameof(InterfaceFont)); Raise(nameof(InterfaceFontSize));
        Sources.Clear();
        Sources.Add(new("local", Text["Local"])); Sources.Add(new("capture", Text["Capture"])); Sources.Add(new("spotify", "Spotify"));
        Sources.Add(new("youtube", Text["Radio"]));
        _source = Sources.First(c => c.Id == id); Raise(nameof(Source));
        foreach (var name in new[] { nameof(Title), nameof(Artist), nameof(HeroNote), nameof(PlayLabel), nameof(MusicSourceHint), nameof(PreparationHeadline), nameof(PreparationDetail), nameof(PreparationSummary), nameof(LikeCurrentLabel), nameof(SelectedCount), nameof(AvailableCount), nameof(SpotifyStatus), nameof(LinkSelectionSummary) }) Raise(name);
        RefreshSearchState();
        RefreshTracks();
        Raise(nameof(RoutingAvailable));
        Raise(nameof(LinkPopoverTitle)); RefreshLinks();
        RefreshVolumeProperties();
        RefreshLyricWindow(force: true);
        Raise(nameof(PlaylistSectionLabel)); Raise(nameof(PlaylistDialogTitle)); PlaylistError = "";
        foreach (var playlist in Playlists) playlist.RefreshText();
        RefreshRadioText();
        Raise(nameof(ArtworkCacheUsage));
        if (!_session) Status = Text[IsSwitchingSource ? "SwitchingSource" : "Idle"];
    }
    private void RefreshTracks()
    {
        var available = SpotifyMode ? _spotifyResults.AsEnumerable() : _library.AsEnumerable();
        if (_view == "liked") available = available.Where(t => IsLiked(t.Id));
        if (_playlistFilter is not null && _settings.Playlists.TryGetValue(_playlistFilter, out var ids)) available = available.Where(t => ids.Contains(t.Id));
        if (!string.IsNullOrWhiteSpace(Search) && !SpotifyMode) available = available.Where(t =>
            (t.Title + " " + t.Artist).Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase));
        foreach (var row in Tracks) row.Dispose();
        Tracks.Clear(); foreach (var track in available) Tracks.Add(new(track, this));
        ListTitle = HasSearchQuery ? Text["SearchResults"] : _playlistFilter ?? Text[_view == "liked" ? "Liked" : _view == "playlists" ? "Playlists" : "Recent"];
        UpdatePlaylistSelection();
        RefreshNavigationState(); RefreshPoster();
        RefreshSearchState();
    }
    private void Navigate(string view) { _cacheOmittedTracks.Clear(); _view = view; _playlistFilter = null; RefreshTracks(); Raise(nameof(IsMusicView)); Raise(nameof(ShowHero)); Raise(nameof(IsRadioPage)); }
    private void RefreshPlaylists()
    {
        Playlists.Clear();
        // Favorites remain backed by the existing liked-ID set, independently of user playlist names.
        Playlists.Add(new("", _settings.LikedTracks.Count,
            new AsyncCommand(_ => { Search = ""; Navigate("liked"); return Task.CompletedTask; }, ShowError),
            new AsyncCommand(_ => Task.CompletedTask, ShowError, () => false), this, isFavorites: true));
        foreach (var (name, ids) in _settings.Playlists)
            Playlists.Add(new(name, ids.Count,
                new AsyncCommand(_ => { SelectPlaylist(name); return Task.CompletedTask; }, ShowError),
                new AsyncCommand(_ => RemovePlaylistAsync(name), ShowError, () => CanManagePlaylists), this));
        UpdatePlaylistSelection();
    }
    private void SelectPlaylist(string name)
    {
        _cacheOmittedTracks.Clear();
        Source = Sources.First(s => s.Id == "local");
        _playlistFilter = name; _view = "home"; Search = "";
        RefreshTracks(); Raise(nameof(IsMusicView)); Raise(nameof(ShowHero)); Raise(nameof(IsRadioPage));
    }
    private void UpdatePlaylistSelection()
    {
        foreach (var playlist in Playlists) playlist.IsActive = playlist.IsFavorites ? IsLikedPage
            : !SettingsOpen && IsLocal && playlist.Name == _playlistFilter;
    }
    private void RefreshNavigationState()
    {
        foreach (var property in new[] { nameof(IsHomePage), nameof(IsLibraryPage), nameof(IsLikedPage), nameof(IsPlaylistsPage), nameof(IsRadioNav) }) Raise(property);
    }
    public bool IsLiked(string id) => _settings.LikedTracks.Contains(id);
    public void ToggleLike(string id)
    {
        if (!_settings.LikedTracks.Add(id)) _settings.LikedTracks.Remove(id);
        Playlists.FirstOrDefault(p => p.IsFavorites)?.RefreshCount(_settings.LikedTracks.Count);
        Raise(nameof(CurrentLiked)); Raise(nameof(LikeCurrentLabel));
        if (_view == "liked") RefreshTracks();
        else foreach (var track in Tracks.Where(t => t.Track.Id == id)) track.RefreshLike();
        SaveSoon();
    }
    public void OpenPlaylist(MusicTrack? track)
    {
        if (!CanManagePlaylists) return;
        _playlistTrack = track; PlaylistName = track is null ? "" : _settings.Playlists.Keys.FirstOrDefault() ?? "";
        PlaylistError = ""; Raise(nameof(PlaylistDialogTitle)); PlaylistOpen = true;
    }
    private void CreatePlaylist()
    {
        if (!CanManagePlaylists) return;
        var name = PlaylistName.Trim();
        if (name.Length is < 1 or > 80) { PlaylistError = Text["PlaylistNameRequired"]; return; }
        if (_playlistTrack is null && _settings.Playlists.Keys.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { PlaylistError = Text["PlaylistNameExists"]; return; }
        if (!_settings.Playlists.TryGetValue(name, out var ids)) _settings.Playlists[name] = ids = [];
        if (_playlistTrack is not null && !ids.Contains(_playlistTrack.Id)) ids.Add(_playlistTrack.Id);
        if (_playlistTrack is not null) _cacheOmittedTracks.Remove(_playlistTrack.Id);
        var creating = _playlistTrack is null;
        RefreshPlaylists(); PlaylistsExpanded = true; PlaylistOpen = false;
        if (creating && IsLocal) SelectPlaylist(name);
        ShowToast(creating ? string.Format(Text["PlaylistCreated"], name) : Text["PlaylistSaved"]); SaveSoon();
    }
    private async Task RemovePlaylistAsync(string name)
    {
        if (!CanManagePlaylists || !_settings.Playlists.Remove(name, out var ids)) return;
        // A playlist is an index into the library. Never remove its audio files or library entries.
        foreach (var folder in _settings.ImportedPlaylistFolders.Where(p => p.Value == name).Select(p => p.Key).ToArray())
            _settings.ImportedPlaylistFolders.Remove(folder);
        var item = Playlists.FirstOrDefault(p => !p.IsFavorites && p.Name == name);
        if (item is not null) Playlists.Remove(item);
        if (_playlistFilter == name) Navigate("library");
        ShowToast(string.Format(Text["PlaylistRemoved"], name)); SaveSoon();
        await ForgetUnusedTrackCachesAsync(_library.Where(t => ids.Contains(t.Id)).ToArray());
    }
    public async Task ImportFoldersAsync(IEnumerable<string> paths)
    {
        Busy = true;
        try
        {
            var selected = paths.ToArray();
            if (selected.Length == 0) return;
            var library = _library.ToArray();
            var playlists = _settings.Playlists.ToDictionary(p => p.Key, p => p.Value.ToList());
            var folders = _settings.ImportedPlaylistFolders.ToDictionary(p => p.Key, p => p.Value);
            var result = await Task.Run(() => FolderMusicImporter.Import(selected, library, playlists,
                folders, _backend.ProbeFile, _lifetime.Token), _lifetime.Token);
            if (_disposed) return;
            _library.AddRange(result.Tracks);
            _cacheOmittedTracks.Clear();
            _settings.Playlists = result.Playlists; _settings.ImportedPlaylistFolders = result.Folders;
            _settings.Library = _library.Where(t => !t.IsDemo).ToList();
            Source = Sources.First(s => s.Id == "local");
            _view = "home"; _playlistFilter = result.ImportedPlaylists.FirstOrDefault(); Search = "";
            RefreshTracks(); RefreshPlaylists(); SaveSoon();
            Raise(nameof(IsMusicView)); Raise(nameof(ShowHero)); Raise(nameof(IsRadioPage));
            var message = string.Format(Text["FoldersImported"], result.Tracks.Count, result.ImportedPlaylists.Count)
                + (result.Skipped > 0 ? " · " + string.Format(Text["ImportSkipped"], result.Skipped) : "");
            Set(ref _status, message, nameof(Status));
            ShowToast(message, importSummary: true);
        }
        finally { Busy = false; }
    }
    private async Task ScanAsync()
    {
        if (_disposed || IsScanning) return;
        var scanToken = _lifetime.Token;
        var retryRouting = false;
        Exception? networkFailure = null;
        ClearScanToast();
        IsScanning = true;
        try
        {
            var localDevices = _backend.GetDevices().ToArray();
            var devices = localDevices.ToList();
            try { devices.AddRange(await _discoverNetwork(scanToken).WaitAsync(scanToken)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                networkFailure = ex;
                // A failed scan cannot establish that a receiver disappeared.
                // Retain known network devices without disrupting healthy audio.
                devices.AddRange(Speakers.Where(s => s.Device.Transport is SpeakerTransport.AirPlay or SpeakerTransport.Chromecast)
                    .Select(s => s.Device));
            }
            // Discovery can wait independently of playback. Commit its result
            // after any start/pause operation, without owning that operation's Busy flag.
            await _playGate.WaitAsync(scanToken);
            try
            {
                scanToken.ThrowIfCancellationRequested();
                CaptureEndpoints.Clear();
                foreach (var device in localDevices) CaptureEndpoints.Add(new(device.Id, device.Name));
                _settings.CaptureDeviceId ??= _backend.DefaultDeviceId; Raise(nameof(CaptureEndpoint));
                foreach (var old in Speakers.Where(s => devices.All(d => d.Id != s.Id)).ToArray()) { old.Detach(); Speakers.Remove(old); }
                foreach (var device in devices)
                {
                    var existing = Speakers.FirstOrDefault(s => s.Id == device.Id);
                    if (existing is not null) { existing.UpdateDevice(device); continue; }
                    if (!_settings.Speakers.TryGetValue(device.Id, out var preferences))
                        _settings.Speakers[device.Id] = preferences = new();
                    if (preferences.Room == "All rooms") preferences.Room = "all";
                    Speakers.Add(new(device, preferences, Text, SpeakerChanged));
                }
                ApplyActiveLink(); RefreshDraftDevices();
                var selected = ReceiverRoutes.Choose(Speakers.Where(s => s.Selected && s.Available).Select(s => (s.Device, s.Preferences)))
                    .Select(r => r.Device.Id).ToHashSet();
                SetSelections(selected);
                retryRouting = _session && !SpotifyMode && (!selected.SetEquals(_player.Outputs.Keys)
                    || Speakers.Any(s => s.Selected && _player.Outputs.TryGetValue(s.Id, out var output)
                        && (output.Error is not null || output.Device.Host != s.Device.Host || output.Device.Port != s.Device.Port
                            || output.Device.Properties?.GetValueOrDefault("ap2") != s.Device.Properties?.GetValueOrDefault("ap2"))));
                Raise(nameof(AvailableCount));
                SaveSoon();
            }
            finally { _playGate.Release(); }
            // Scan notices belong in the toast; the mini player keeps its actual
            // playback status, including an unrelated output/decoder error.
            if (networkFailure is not null) LogError(networkFailure);
            ShowScanToast(networkFailure is not null);
        }
        finally { IsScanning = false; }
        if (retryRouting) QueueRoutingChange();
    }
    private void SpeakerChanged(SpeakerViewModel speaker, bool routeChanged)
    {
        if (_applyingLink || _changingSelection) return;
        if (ActiveLinkProfile is { } link && link.Speakers.TryGetValue(speaker.Id, out var member))
        {
            member.Preferences = SpeakerLinks.Copy(speaker.Preferences);
            member.Name = speaker.Name; member.Transport = speaker.Device.Transport;
        }
        if (speaker.HasCalibratedDelay && CurrentCalibration is { } calibration && calibration.Speakers.TryGetValue(speaker.Id, out var calibrated))
            calibrated.DelayMs = (int)speaker.DelayMs;
        _player.Update(speaker.Id, PlaybackPreferences(speaker), MasterVolume); Raise(nameof(SelectedCount)); SaveSoon();
        if (!routeChanged || _changingSelection) return;
        if (speaker.Selected)
        {
            var alternate = Speakers.Where(s => s.Id != speaker.Id && s.Selected && ReceiverRoutes.SameReceiver(s.Device, speaker.Device)).ToArray();
            if (alternate.Length > 0)
            {
                _changingSelection = true;
                try { foreach (var other in alternate) other.Selected = false; }
                finally { _changingSelection = false; }
                Status = string.Format(Text["RouteSwitch"], speaker.Transport, speaker.Name);
            }
        }
        QueueRoutingChange();
    }
    private void SetSelections(IReadOnlySet<string> selected)
    {
        _changingSelection = true;
        try { foreach (var speaker in Speakers) speaker.Selected = speaker.Available && selected.Contains(speaker.Id); }
        finally { _changingSelection = false; }
        Raise(nameof(SelectedCount)); SaveSoon();
    }
    private void QueueRoutingChange()
    {
        if ((!_session && !_startingPlayback) || SpotifyMode) return;
        _routeDelay?.Cancel(); _routeDelay?.Dispose(); _routeDelay = new();
        var token = _routeDelay.Token;
        _ = RunSafely(async () =>
        {
            await Task.Delay(500, token);
            while (Busy) await Task.Delay(100, token);
            if (_disposed || !_session || SpotifyMode) return;
            await PlayTrackAsync(_current, Position, startPaused: !IsPlaying);
        });
    }
    public async Task PlayTrackAsync(MusicTrack? track, double resumePosition = 0, bool startPaused = false)
    {
        if (_disposed || Busy) return;
        _calibrationResumeTrack = null;
        if (YouTubeMode && track?.FilePath is { } localPath && !YouTubeAddress.TryParse(localPath, out _))
        { Source = Sources.First(s => s.Id == "local"); await _sourceTransition; }
        var sourceVersion = _sourceVersion;
        if (!SpotifyMode && !HasActiveLink)
        {
            if (!IsLinkEditing) BeginLinkEdit(null); else LinkPanelOpen = true;
            Status = Text["CreateLinkFirst"]; return;
        }
        await _playGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!OwnsSource(sourceVersion) || IsSwitchingSource) return;
            Busy = true; _startingPlayback = true; _preparationCancelled = false; Status = Text["Starting"];
            if (YouTubeMode) ClearRadioPlaybackError(track);
            if (SpotifyMode)
            {
                _spotifyStateVersion++;
                if (!_spotify.Connected) { ShowSpotifySettings(); Status = Text["NotConnected"]; return; }
                await _player.StopAsync();
                if (!OwnsSource(sourceVersion)) return;
                if (SpotifyDevice is null) await RefreshSpotifyDevicesAsync();
                if (!OwnsSource(sourceVersion)) return;
                if (SpotifyDevice is null) throw new InvalidOperationException(Text["NoSpotifyDevices"]);
                // An empty URI resumes Spotify's own queue. A current-track response or cover is not a prerequisite.
                _activeSource = "spotify";
                await _spotify.PlayOnDeviceAsync(track?.SpotifyUri, SpotifyDevice!.Id, _lifetime.Token);
                if (!OwnsSource(sourceVersion)) return;
                _current = track?.SpotifyUri is not null ? track : null;
                Duration = _current?.DurationSeconds ?? 0;
            }
            else
            {
                var capture = Source?.Id == "capture";
                if (!capture && track?.FilePath is null) throw new InvalidOperationException(Text["NoTracks"]);
                var selected = SpeakerLinks.Resolve(ActiveLinkProfile!, Speakers.Select(s => s.Device), CurrentCalibrationPath);
                if (selected.Count == 0) { await _player.StopAsync(); throw new InvalidOperationException(Text["LinkOffline"]); }
                if (YouTubeMode)
                {
                    resumePosition = 0;
                    if (_backend is not IYouTubeRadioBackend radio || !YouTubeAddress.TryParse(track!.FilePath, out var address))
                        throw new InvalidOperationException(Text["RadioToolsMissing"]);
                    _current = track; Duration = 0; RefreshCurrent();
                    IsPreparingPlayback = true; RadioLoading = true;
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _radioRequest = request;
                    try { track = await radio.ResolveRadioAsync(address!, request.Token); }
                    finally { if (ReferenceEquals(_radioRequest, request)) _radioRequest = null; RadioLoading = false; }
                    if (!OwnsSource(sourceVersion)) return;
                    RefreshRadioName(track);
                }
                _current = track;
                IsPlaying = false; Position = resumePosition; Duration = capture ? 0 : track!.DurationSeconds;
                _preparationTotal = selected.Count; IsPreparingPlayback = true; RefreshCurrent();
                var source = capture ? _settings.CaptureDeviceId ?? _backend.DefaultDeviceId ?? throw new InvalidOperationException(Text["SourceEndpoint"]) : track!.FilePath!;
                await _player.StartAsync(source, capture, selected, MasterVolume, resumePosition, startPaused);
                if (!OwnsSource(sourceVersion)) return;
                Duration = _player.Duration;
            }
            ResetProgressTransition();
            _session = true; _activeSource = Source?.Id; IsPlaying = SpotifyMode || _player.IsPlaying; Position = resumePosition;
            if (SpotifyMode) AnchorSpotifyProgress(Position);
            _spotifyNextPoll = DateTime.UtcNow.AddSeconds(2);
            if (!SpotifyMode) RefreshSpeakerPlaybackStatus();
            ShowPlaybackStatus();
            RefreshCurrent();
            if (SpotifyMode && _current is { } spotifyTrack) _ = RunSafely(() => LoadArtwork(spotifyTrack));
        }
        catch (Exception) when (!OwnsSource(sourceVersion)) { /* The new source owns the visible state. */ }
        catch (OperationCanceledException) when (_preparationCancelled)
        {
            _session = false; IsPlaying = false; Status = Text["Idle"]; RefreshCurrent();
        }
        catch (Exception ex)
        {
            _session = false; IsPlaying = false; RefreshCurrent();
            foreach (var speaker in Speakers) speaker.UpdateStatus(false, _player.OutputErrors.GetValueOrDefault(speaker.Id));
            if (YouTubeMode)
            {
                var errorKey = ex is FileNotFoundException ? "RadioToolsMissing" : "RadioPlaybackFailed";
                RememberRadioPlaybackError(track, errorKey);
                LogError(ex); ReportPlayerError(Text[errorKey]); return;
            }
            throw;
        }
        finally { RadioLoading = false; IsPreparingPlayback = false; _startingPlayback = false; Busy = false; _playGate.Release(); }
    }
    private async Task TogglePlayback()
    {
        if (_disposed || Busy) return;
        var sourceVersion = _sourceVersion;
        if (!_session)
        {
            if (YouTubeMode && _current is null && _settings.RadioStations.FirstOrDefault() is { } station)
                await PlayRadioAsync(station);
            else await PlayTrackAsync(_current, _calibrationResumeTrack == _current?.Id ? _calibrationResumePosition : 0);
            return;
        }
        await _playGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!OwnsSource(sourceVersion) || IsSwitchingSource) return;
            Busy = true;
            var wasPlaying = IsPlaying;
            if (SpotifyMode)
            {
                _spotifyStateVersion++;
                if (wasPlaying) await _spotify.PauseAsync(_lifetime.Token);
                else
                {
                    if (SpotifyDevice is null) await RefreshSpotifyDevicesAsync();
                    if (!OwnsSource(sourceVersion)) return;
                    if (SpotifyDevice is null) throw new InvalidOperationException(Text["NoSpotifyDevices"]);
                    _activeSource = "spotify";
                    await _spotify.PlayOnDeviceAsync(null, SpotifyDevice.Id, _lifetime.Token);
                }
                if (!OwnsSource(sourceVersion)) return;
                AdvanceSpotifyProgress();
                IsPlaying = !wasPlaying; AnchorSpotifyProgress(Position);
                _spotifyNextPoll = DateTime.UtcNow.AddSeconds(1);
            }
            else
            {
                if (!wasPlaying) { _preparationCancelled = false; _preparationTotal = _player.Outputs.Count; Status = Text["Starting"]; IsPreparingPlayback = true; }
                await _player.PauseAsync(wasPlaying);
                if (!OwnsSource(sourceVersion)) return;
                IsPlaying = _player.IsPlaying;
                RefreshSpeakerPlaybackStatus();
            }
            ShowPlaybackStatus();
        }
        catch (Exception) when (!OwnsSource(sourceVersion)) { }
        catch (OperationCanceledException) when (_preparationCancelled)
        { _session = false; IsPlaying = false; Status = Text["Idle"]; RefreshCurrent(); }
        catch
        {
            if (!SpotifyMode)
            {
                IsPlaying = _player.IsPlaying;
                _session = _player.Outputs.Count > 0;
                RefreshCurrent();
            }
            throw;
        }
        finally { IsPreparingPlayback = false; Busy = false; _playGate.Release(); }
    }
    private async Task StopAsync(bool resetStatus = true)
    {
        _spotifyStateVersion++;
        if (_activeSource == "spotify" && _spotify.Connected) await _spotify.PauseAsync(_lifetime.Token);
        await _player.StopAsync(); _session = false; IsPlaying = false; Position = 0; Raise(nameof(Seekable));
        ClearReportedPlayerError();
        if (resetStatus) Status = Text["Idle"];
        _activeSource = null;
        foreach (var speaker in Speakers.Concat(LinkedSpeakers.Where(s => !Speakers.Contains(s)))) speaker.UpdateStatus(false, null);
    }
    private async Task NextAsync(int direction)
    {
        if (_disposed || Busy) return;
        if (YouTubeMode)
        {
            var stations = _settings.RadioStations;
            if (stations.Count == 0) return;
            var radioIndex = stations.FindIndex(s => s.Url == _current?.FilePath);
            var radioNext = Shuffle ? Random.Shared.Next(stations.Count) : (Math.Max(0, radioIndex) + direction + stations.Count) % stations.Count;
            await PlayRadioAsync(stations[radioNext]); return;
        }
        if (SpotifyMode)
        {
            if (!_spotify.Connected) { ShowSpotifySettings(); return; }
            _spotifyStateVersion++;
            if (direction > 0) await _spotify.NextAsync(_lifetime.Token); else await _spotify.PreviousAsync(_lifetime.Token);
            _spotifyNextPoll = DateTime.UtcNow.AddSeconds(1);
            await RefreshSpotifyPlaybackAsync(); return;
        }
        var queue = Tracks.Select(t => t.Track).ToList();
        if (queue.Count == 0) return;
        var index = queue.FindIndex(t => t.Id == _current?.Id);
        var next = Shuffle ? Random.Shared.Next(queue.Count) : (Math.Max(0, index) + direction + queue.Count) % queue.Count;
        await PlayTrackAsync(queue[next]);
    }
    public async Task SeekAsync(double seconds)
    {
        if (!Seekable || Busy) return;
        seconds = Math.Clamp(seconds, 0, Duration);
        if (SpotifyMode)
        {
            var version = ++_spotifyStateVersion;
            var previousPosition = _spotifyProgress.Position;
            _spotifySeeksPending++;
            var acquired = false;
            ResetProgressTransition(); AnchorSpotifyProgress(seconds); Position = seconds;
            try
            {
                await _spotifySeekGate.WaitAsync(_lifetime.Token); acquired = true;
                if (_disposed || !SpotifyMode || version != _spotifyStateVersion) return;
                await _spotify.SeekAsync(seconds, _lifetime.Token);
                if (_disposed || !SpotifyMode || version != _spotifyStateVersion) return;
                _spotifySeekGuardUntil = DateTime.UtcNow.AddSeconds(2);
                _spotifyNextPoll = DateTime.UtcNow.AddSeconds(1);
            }
            catch
            {
                if (!_disposed && SpotifyMode && version == _spotifyStateVersion)
                { ResetProgressTransition(); AnchorSpotifyProgress(previousPosition); Position = previousPosition; }
                throw;
            }
            finally { if (acquired) _spotifySeekGate.Release(); _spotifySeeksPending--; }
            return;
        }
        else await PlayTrackAsync(_current, seconds, startPaused: !IsPlaying);
        Position = seconds;
    }
    private void RefreshCurrent()
    {
        RefreshLinkFeedback();
        RefreshTrackPlayback();
        RefreshRadioSelection();
        RefreshPresentation();
        foreach (var property in new[] { nameof(Title), nameof(Artist), nameof(HeroNote), nameof(CurrentLiked), nameof(CanLikeCurrent), nameof(LikeCurrentLabel), nameof(PlayEnabled), nameof(Seekable), nameof(HasSpotifyArtwork), nameof(ShowArtist) }) Raise(property);
    }
    private async Task SearchAsync()
    {
        if (!SpotifyMode) { RefreshTracks(); return; }
        if (!_spotify.Connected) { ShowSpotifySettings(); return; }
        if (string.IsNullOrWhiteSpace(Search)) return;
        var query = Search.Trim(); var version = _searchVersion;
        _searchRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchRequest = request; _searchError = ""; IsSearching = true;
        try
        {
            var results = await _spotify.SearchAsync(query, request.Token);
            if (request.IsCancellationRequested || version != _searchVersion || !SpotifyMode) return;
            _spotifyResults = results.ToList(); _searchedQuery = query;
            _view = "home"; _playlistFilter = null;
            RefreshTracks(); Raise(nameof(IsMusicView)); Raise(nameof(IsRadioPage));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _searchVersion || !SpotifyMode) return;
            _searchError = ErrorMessage(ex); RefreshSearchState(); ShowError(ex);
        }
        finally
        {
            if (ReferenceEquals(_searchRequest, request)) { _searchRequest = null; IsSearching = false; }
        }
    }
    private void InvalidateSearch()
    {
        _searchRequest?.Cancel(); _searchVersion++; _searchedQuery = ""; _searchError = "";
        _spotifyResults.Clear(); IsSearching = false; RefreshSearchState();
    }
    private void RefreshSearchState()
    {
        foreach (var name in new[] { nameof(HasSearchQuery), nameof(HasSearchError), nameof(HasSearchFeedback), nameof(SearchNote), nameof(SearchPlaceholder),
            nameof(SearchScope), nameof(NoSearchResults), nameof(IsEmptyCollection), nameof(EmptyCollectionTitle), nameof(EmptyCollectionHint), nameof(ShowHero), nameof(ShowPlaylists) }) Raise(name);
    }
    private async Task SaveSettings()
    {
        if (_spotify.ClientId != ClientId && _spotify.Connected) _spotify.Disconnect();
        _spotify.ClientId = ClientId; await _store.SaveAsync(_settings); Raise(nameof(SpotifyStatus)); Status = Text["Copied"];
    }
    private async Task ConnectSpotifyAsync()
    {
        await SaveSettings(); Status = Text["SpotifySetup"];
        await _spotify.ConnectAsync(OpenBrowser, _lifetime.Token); Raise(nameof(SpotifyStatus));
        _listArtwork.Clear(includeLocalMetadata: false); RefreshTracks();
        await RefreshSpotifyDevicesAsync(); Status = Text["Connected"];
        if (SpotifyMode) await RefreshSpotifyPlaybackAsync();
    }
    private async Task RefreshSpotifyDevicesAsync(bool reportStatus = true, CancellationToken? token = null)
    {
        if (!_spotify.Connected) { Status = Text["NotConnected"]; return; }
        var previous = SpotifyDevice?.Id;
        var sourceVersion = _sourceVersion;
        var devices = (await _spotify.DevicesAsync(token ?? _lifetime.Token)).Where(d => !d.Restricted).ToArray();
        if (!OwnsSource(sourceVersion)) return;
        SpotifyDevices.Clear(); foreach (var device in devices) SpotifyDevices.Add(new(device.Id, device.Name));
        SpotifyDevice = SpotifyDevices.FirstOrDefault(d => d.Id == previous)
            ?? SpotifyDevices.FirstOrDefault(d => devices.Any(s => s.Id == d.Id && s.Active)) ?? SpotifyDevices.FirstOrDefault();
        Raise(nameof(SpotifyDevice));
        if (reportStatus) Status = devices.Length == 0 ? Text["NoSpotifyDevices"] : Text["Connected"];
    }
    private async Task LoadArtwork(MusicTrack track)
    {
        _artworkRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _artworkRequest = request;
        SpotifyArtwork = null;
        Raise(nameof(HasSpotifyArtwork)); Raise(nameof(ShowArtist));
        try
        {
            var data = await _spotify.ArtworkAsync(track.ImageUrl, request.Token);
            if (data is null || request.IsCancellationRequested || !SpotifyMode || _current?.Id != track.Id) return;
            // The decoder receives a seekable stream rather than the CDN's forward-only network stream.
            using var stream = new MemoryStream(data, writable: false);
            var bitmap = new Bitmap(stream);
            if (request.IsCancellationRequested || !SpotifyMode || _current?.Id != track.Id) { bitmap.Dispose(); return; }
            SpotifyArtwork = bitmap; Raise(nameof(HasSpotifyArtwork)); Raise(nameof(ShowArtist));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or NotSupportedException or InvalidOperationException)
        { /* Optional cover failures must never interrupt music or replace the playback status. */ }
        finally { if (ReferenceEquals(_artworkRequest, request)) _artworkRequest = null; }
    }
    private async Task CalibrateAsync()
    {
        Source = Sources.First(s => s.Id == "local");
        await _sourceTransition;
        if (_disposed || !IsLocal) return;
        var path = DemoMusic.Calibration(_dataDirectory);
        await PlayTrackAsync(new("calibration", Text["Calibration"], "Musync", path, DurationSeconds: 16));
        Status = Text["CalibrationInfo"];
    }
    private async Task TickAsync()
    {
        if (_disposed || _ending || CalibrationRunning) return;
        if (IsPreparingPlayback) { RefreshPreparationFeedback(); RefreshSpeakerPlaybackStatus(); return; }
        if (_tickRunning)
        {
            // A failed receiver may take seconds to close. Healthy outputs
            // continue playing, so keep their clock and lyrics current without
            // starting a second overlapping receiver check.
            if (!Busy && _session && !SpotifyMode && !IsSeeking && _player.Outputs.Count > 0)
                RefreshLocalProgress();
            return;
        }
        if (SpotifyMode)
        {
            AdvanceSpotifyProgress();
            if (!Busy && _spotify.Connected && DateTime.UtcNow >= _spotifyNextPoll)
                _ = RefreshSpotifyPlaybackAsync();
            return;
        }
        if (Busy || !_session || IsSeeking) return;
        var sourceVersion = _sourceVersion;
        _tickRunning = true;
        try { await UpdatePlaybackAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (OwnsSource(sourceVersion) && !Busy) ShowError(ex); }
        finally { _tickRunning = false; }
    }
    private async Task UpdatePlaybackAsync()
    {
        if (!SpotifyMode)
        {
            var sourceVersion = _sourceVersion;
            await _player.CheckOutputsAsync();
            // Receiver cleanup may await network timeouts. Its old poll must
            // not restore progress or errors after a source/link transition,
            // or publish a toast after the view model has been disposed.
            if (!OwnsSource(sourceVersion) || SpotifyMode || Busy || !_session) return;
            RefreshLocalProgress();
            RefreshSpeakerPlaybackStatus();
            if (!_player.IsPlaying && _player.Error is not null) IsPlaying = false;
            if (_player.Outputs.Count == 0)
            {
                // A volume/control error on a surviving receiver is recoverable
                // in place. Only a terminated session offers a new radio stream.
                if (YouTubeMode && _player.Error is not null)
                {
                    RememberRadioPlaybackError(_current, "RadioPlaybackInterrupted");
                    ReportPlayerError(Text["RadioPlaybackInterrupted"]);
                }
                else RefreshPlayerError();
                _session = false; IsPlaying = false; RefreshCurrent(); return;
            }
            RefreshPlayerError();
            if (_player.Finished && IsPlaying)
            {
                if (!_player.TailComplete) return;
                _ending = true;
                try { if (_current?.Id == "calibration") await StopAsync(); else if (Repeat) await PlayTrackAsync(_current); else await NextAsync(1); }
                catch (Exception ex) { ShowError(ex); }
                finally { _ending = false; }
            }
        }
    }
    private void RefreshLocalProgress()
    {
        Position = _player.Position; Duration = _player.Duration;
    }
    private void RefreshSpeakerPlaybackStatus()
    {
        foreach (var speaker in Speakers.Concat(LinkedSpeakers.Where(s => !Speakers.Contains(s))))
        {
            _player.Outputs.TryGetValue(speaker.Id, out var output);
            var phase = SpotifyMode || output is null ? SpeakerPlaybackPhase.Available
                : IsPreparingPlayback ? _player.IsOutputPrepared(speaker.Id) ? SpeakerPlaybackPhase.WaitingForSync : SpeakerPlaybackPhase.Connecting
                : !_session ? SpeakerPlaybackPhase.Available : IsPlaying ? SpeakerPlaybackPhase.Playing : SpeakerPlaybackPhase.Paused;
            speaker.UpdateStatus(phase, SpotifyMode ? null : output?.Error ?? _player.OutputErrors.GetValueOrDefault(speaker.Id));
        }
    }
    private void SaveSoon()
    {
        if (_disposed) return;
        _saveDelay?.Cancel(); _saveDelay = new();
        var token = _saveDelay.Token;
        _ = RunSafely(async () => { await Task.Delay(300, token); await _store.SaveAsync(_settings); });
    }
    private async Task RunSafely(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { ShowError(ex); }
    }
    public void ShowError(Exception exception)
        => ShowError(exception, Text["Error"] + " " + ErrorMessage(exception));
    private void ShowError(Exception exception, string message)
    {
        Status = message;
        LogError(exception);
    }
    private void LogError(Exception exception) => File.AppendAllText(Path.Combine(_dataDirectory, "musync.log"),
        DateTime.UtcNow.ToString("O") + " " + exception.GetType().Name + ": " + exception.Message + Environment.NewLine);
    private string ErrorMessage(Exception exception) => exception is SpotifyApiException spotifyError
        ? Text[spotifyError.ErrorKey] : exception.Message == "NoOutputs" ? Text["NoOutputs"] : exception.Message;
    private static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    internal CancellationToken ArtworkLifetime => _lifetime.Token;
    internal Task<TrackArtwork> ResolveListArtworkAsync(MusicTrack track, CancellationToken token) => _cacheOmittedTracks.Contains(track.Id)
        ? Task.FromResult(new TrackArtwork(track, null, null)) : _listArtwork.ResolveAsync(track, token);
    internal void OpenArtworkSource(Uri link) => OpenBrowser(link);
    internal void ApplyLocalMetadata(MusicTrack track)
    {
        if (_disposed || track.FilePath is null || track.IsDemo) return;
        var index = _library.FindIndex(t => t.Id == track.Id);
        if (index < 0 || _library[index] == track) return;
        _library[index] = track;
        foreach (var row in Tracks.Where(r => r.Track.Id == track.Id)) row.UpdateMetadata(track);
        if (_current?.Id == track.Id) { _current = track; RefreshCurrent(); }
        _settings.Library = _library.Where(t => !t.IsDemo).ToList(); SaveSoon();
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _timer.Stop(); _toastTimer.Stop(); ToastMessage = "";
        DetachLinkFeedback();
        _lifetime.Cancel(); _saveDelay?.Cancel(); _routeDelay?.Cancel();
        _calibrationCancellation?.Cancel();
        _searchRequest?.Cancel(); _artworkRequest?.Cancel(); _lyricsRequest?.Cancel();
        _radioRequest?.Cancel();
        _player.CancelPreparation();
        foreach (var row in Tracks) row.Dispose();
        await _cacheMaintenance;
        _listArtwork.ReleaseMemory();
        ResetSpotifyVolume();
        await _sourceTransition;
        await _playGate.WaitAsync(); _playGate.Release();
        await _store.SaveAsync(_settings);
        foreach (var speaker in Speakers) speaker.Detach();
        foreach (var speaker in LinkedSpeakers.Where(s => !Speakers.Contains(s))) speaker.Detach();
        foreach (var speaker in DraftSpeakers) speaker.Detach();
        await _player.DisposeAsync(); _spotify.Dispose(); _spotifyArtwork?.Dispose();
        foreach (var bitmap in _retiringArtwork.ToArray()) bitmap.Dispose();
        _retiringArtwork.Clear();
        _lifetime.Dispose(); _playGate.Dispose(); Text.Changed -= RefreshText;
    }
}
