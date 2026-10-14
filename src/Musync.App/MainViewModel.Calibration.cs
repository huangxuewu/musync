using System.Collections.ObjectModel;
using System.Windows.Input;
using Musync.Core;

namespace Musync.App;

public sealed record CalibrationResultRow(string Name, string Value, string Detail);
public sealed partial class MainViewModel
{
    private readonly IAcousticCalibrationService? _calibrationService;
    private bool _calibrationOpen, _calibrationRunning;
    private int _calibrationEpoch;
    private string _calibrationError = "", _calibrationPath = "file";
    private Choice? _calibrationInput;
    private CalibrationProgress _calibrationProgress = new(CalibrationPhase.PreparingSpeakers, 0, 0);
    private CalibrationRunResult? _calibrationResult;
    private CancellationTokenSource? _calibrationCancellation;
    private Task _calibrationWork = Task.CompletedTask;
    private string? _calibrationResumeTrack;
    private double _calibrationResumePosition;
    private AsyncCommand? _openCalibrationCommand, _startCalibrationCommand, _closeCalibrationCommand, _applyCalibrationCommand, _refreshCalibrationInputs, _listenCalibrationCommand, _changeCalibrationSettings;
    public bool HasCalibrationService => _calibrationService is not null;
    public bool CalibrationOpen { get => _calibrationOpen; private set => Set(ref _calibrationOpen, value); }
    public bool CalibrationRunning { get => _calibrationRunning; private set { if (Set(ref _calibrationRunning, value)) RefreshCalibrationAvailability(); } }
    public bool CalibrationSetup => !CalibrationRunning && _calibrationResult is null;
    public bool CalibrationHasResults => _calibrationResult is not null;
    public bool CalibrationShowStart => !CalibrationRunning && (_calibrationResult is null || !CanApplyCalibration);
    public bool CalibrationShowApply => CalibrationHasResults && CanApplyCalibration;
    public string CalibrationStartLabel => Text[_calibrationResult is null ? "CalibrationStart" : "CalibrationRetry"];
    public bool CalibrationHasError => !string.IsNullOrEmpty(CalibrationError);
    public string CalibrationError { get => _calibrationError; private set { if (Set(ref _calibrationError, value)) Raise(nameof(CalibrationHasError)); } }
    public ObservableCollection<Choice> CalibrationInputs { get; } = [];
    public ObservableCollection<Choice> CalibrationPaths { get; } = [];
    public ObservableCollection<CalibrationResultRow> CalibrationResults { get; } = [];
    public Choice? CalibrationInput { get => _calibrationInput; set { if (!Set(ref _calibrationInput, value)) return; ClearCalibrationResult(); } }
    public Choice? CalibrationPath
    {
        get => CalibrationPaths.FirstOrDefault(p => p.Id == _calibrationPath);
        set { if (value is null || !CalibrationProfiles.IsPath(value.Id) || _calibrationPath == value.Id) return; _calibrationPath = value.Id; Raise(); Raise(nameof(CalibrationDevices)); ClearCalibrationResult(); }
    }
    private string CurrentCalibrationPath => Source?.Id == "youtube" ? "stream" : Source?.Id == "capture" ? "capture" : "file";
    private CalibrationProfile? CurrentCalibration => ActiveLinkProfile is { } link ? CalibrationProfiles.Active(link, CurrentCalibrationPath, Speakers.Select(s => s.Device)) : null;
    public string CalibrationDevices => ActiveLinkProfile is { } link
        ? string.Format(Text["CalibrationDevices"], link.Speakers.Count, link.Name) + " · " + Text["CalibrationPath_" + _calibrationPath]
        : Text["NoActiveLink"];
    public string CalibrationStatus => CalibrationRunning
        ? _calibrationProgress.Phase == CalibrationPhase.PreparingSpeakers
            ? string.IsNullOrEmpty(_calibrationProgress.SpeakerName)
                ? string.Format(Text["CalibrationPreparing"], _calibrationProgress.Completed, _calibrationProgress.Total)
                : string.Format(Text["CalibrationPreparingSpeaker"], _calibrationProgress.SpeakerName, _calibrationProgress.Completed, _calibrationProgress.Total)
            : Text[_calibrationProgress.Phase switch
            {
                CalibrationPhase.OpeningMicrophone => "CalibrationOpeningMic",
                CalibrationPhase.StartingPlayback => "StartingPlayback",
                CalibrationPhase.Recording => "CalibrationListening",
                CalibrationPhase.Analyzing => "CalibrationAnalyzing",
                _ => "CalibrationClosing"
            }]
        : _calibrationResult is null ? Text["CalibrationIntro"] : Text[_calibrationResult.CanApply ? "CalibrationReady" : "CalibrationUnreliable"];
    public string CalibrationCloseLabel => Text[CalibrationRunning ? "Cancel" : "Close"];
    public bool CanStartCalibration => CalibrationOpen && !Busy && !CalibrationRunning && CalibrationInput is not null
        && CalibrationEligible();
    public bool CanApplyCalibration => !Busy && !CalibrationRunning && _calibrationResult?.CanApply == true && CalibrationResultIsCurrent();
    public ICommand OpenCalibration => _openCalibrationCommand ??= new(_ => { OpenCalibrationDialog(); return Task.CompletedTask; }, ShowError);
    public ICommand StartCalibration => _startCalibrationCommand ??= new(async _ => { if (CanStartCalibration) await (_calibrationWork = RunCalibrationAsync()); }, ShowError);
    public ICommand CloseCalibration => _closeCalibrationCommand ??= new(async _ => { _calibrationCancellation?.Cancel(); await _calibrationWork; CalibrationOpen = false; ClearCalibrationResult(); }, ShowError);
    public ICommand ApplyCalibration => _applyCalibrationCommand ??= new(_ => ApplyCalibrationAsync(), ShowError);
    public ICommand RefreshCalibrationInputs => _refreshCalibrationInputs ??= new(_ => { LoadCalibrationInputs(); return Task.CompletedTask; }, ShowError);
    public ICommand ListenCalibration => _listenCalibrationCommand ??= new(async _ => { CalibrationOpen = false; ClearCalibrationResult(); await CalibrateAsync(); }, ShowError);
    public ICommand ChangeCalibrationSettings => _changeCalibrationSettings ??= new(_ => { ClearCalibrationResult(); return Task.CompletedTask; }, ShowError);
    private bool CalibrationEligible()
    {
        if (ActiveLinkProfile is not { } link || IsScanning || IsLinkEditing || link.Speakers.Count is < 2 or > 32 || MasterVolume <= 0) return false;
        var routes = SpeakerLinks.Resolve(link, Speakers.Select(s => s.Device), _calibrationPath);
        return routes.Count == link.Speakers.Count && routes.All(r => !r.Preferences.Muted && r.Preferences.Volume > 0);
    }
    private void OpenCalibrationDialog()
    {
        if (Busy || !HasCalibrationService || !HasActiveLink) return;
        _calibrationPath = CurrentCalibrationPath; Raise(nameof(CalibrationPath));
        SettingsOpen = false; PlaylistOpen = false; LinkPanelOpen = SpeakerControlsOpen = false;
        ClearCalibrationResult(); LoadCalibrationInputs(); CalibrationOpen = true; Raise(nameof(CalibrationDevices)); RefreshCalibrationAvailability();
    }
    private void LoadCalibrationInputs()
    {
        if (CalibrationRunning) return;
        var selected = _calibrationInput?.Id; CalibrationInputs.Clear();
        try
        {
            foreach (var input in _calibrationService?.GetInputs() ?? []) CalibrationInputs.Add(new(input.Id, input.Name));
            _calibrationInput = CalibrationInputs.FirstOrDefault(i => i.Id == selected) ?? CalibrationInputs.FirstOrDefault();
            CalibrationError = CalibrationInputs.Count == 0 ? Text["CalibrationNoMicrophone"] : !CalibrationEligible() ? Text["CalibrationDevicesRequired"] : "";
        }
        catch (Exception ex) { LogError(ex); CalibrationError = Text["CalibrationNoMicrophone"]; }
        Raise(nameof(CalibrationInput)); RefreshCalibrationAvailability();
    }
    private async Task RunCalibrationAsync()
    {
        var epoch = ++_calibrationEpoch; var sourceVersion = _sourceVersion; var oldPosition = Position; var acquired = false;
        _calibrationCancellation?.Dispose(); _calibrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _calibrationCancellation.Token;
        ClearCalibrationResult(); CalibrationError = ""; CalibrationRunning = true; Busy = true;
        try
        {
            await _playGate.WaitAsync(token); acquired = true; await StopAsync(); token.ThrowIfCancellationRequested();
            var link = ActiveLinkProfile ?? throw new InvalidOperationException("CalibrationDevicesRequired");
            var routes = SpeakerLinks.Resolve(link, Speakers.Select(s => s.Device), _calibrationPath);
            var request = new CalibrationRequest(link.Id, _calibrationPath, CalibrationInput!.Id,
                routes.Select(r => new CalibrationSpeaker(r.Device, r.Preferences)).ToArray(), MasterVolume);
            _calibrationProgress = new(CalibrationPhase.PreparingSpeakers, 0, routes.Count); Raise(nameof(CalibrationStatus));
            var progress = new Progress<CalibrationProgress>(p => { if (_calibrationEpoch == epoch && CalibrationRunning) { _calibrationProgress = p; Raise(nameof(CalibrationStatus)); } });
            var result = await _calibrationService!.RunAsync(request, progress, token);
            token.ThrowIfCancellationRequested();
            if (sourceVersion != _sourceVersion || epoch != _calibrationEpoch) return;
            _calibrationResult = result; RefreshCalibrationResults();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogError(ex);
            CalibrationError = ex is CalibrationSpeakerException receiver
                ? string.Format(Text[receiver.Phase == CalibrationPhase.PreparingSpeakers ? "CalibrationSpeakerPreparingFailed" : "CalibrationSpeakerPlaybackFailed"], receiver.SpeakerName)
                : Text[ex.Message.StartsWith("Calibration", StringComparison.Ordinal) ? ex.Message : "CalibrationFailed"];
        }
        finally
        {
            if (sourceVersion == _sourceVersion) { Position = oldPosition; _calibrationResumeTrack = _current?.Id; _calibrationResumePosition = YouTubeMode ? 0 : oldPosition; }
            CalibrationRunning = false; Busy = false; RefreshCalibrationAvailability(); Raise(nameof(CalibrationStatus));
            if (acquired) _playGate.Release();
        }
    }
    private bool CalibrationResultIsCurrent()
    {
        if (_calibrationResult is not { } result || ActiveLinkProfile is not { } link || link.Id != result.Request.LinkId || _calibrationPath != result.Request.Path) return false;
        var routes = SpeakerLinks.Resolve(link, Speakers.Select(s => s.Device), _calibrationPath);
        return routes.Count == result.Request.Speakers.Count && result.Request.Speakers.All(s => routes.Any(r => r.Device.Id == s.Device.Id
            && CalibrationProfiles.Protocol(r.Device) == CalibrationProfiles.Protocol(s.Device) && r.Preferences.DelayMs == s.Preferences.DelayMs));
    }
    private async Task ApplyCalibrationAsync()
    {
        if (!CanApplyCalibration || _calibrationResult is not { } result || ActiveLinkProfile is not { } link) return;
        var path = result.Request.Path; var previous = link.Calibrations.GetValueOrDefault(path);
        var profile = new CalibrationProfile { Speakers = result.Request.Speakers.ToDictionary(s => s.Device.Id,
            s => new CalibratedSpeaker { Transport = s.Device.Transport, Protocol = CalibrationProfiles.Protocol(s.Device), DelayMs = result.Delays[s.Device.Id] }) };
        link.Calibrations[path] = profile;
        Busy = true;
        try { await _store.SaveAsync(_settings); }
        catch { if (previous is null) link.Calibrations.Remove(path); else link.Calibrations[path] = previous; throw; }
        finally { Busy = false; }
        RefreshActiveCalibration(); CalibrationOpen = false; ClearCalibrationResult(); LinkPanelOpen = true; Status = Text["CalibrationSaved"];
    }
    private void RefreshActiveCalibration()
    {
        var profile = CurrentCalibration;
        foreach (var speaker in LinkedSpeakers) speaker.SetCalibratedDelay(profile?.Speakers.GetValueOrDefault(speaker.Id)?.DelayMs);
    }
    private SpeakerPreferences PlaybackPreferences(SpeakerViewModel speaker)
    { var preferences = SpeakerLinks.Copy(speaker.Preferences); preferences.DelayMs = (int)speaker.DelayMs; return preferences; }
    private void ResetCalibrationDelays()
    {
        ActiveLinkProfile?.Calibrations.Remove(CurrentCalibrationPath); RefreshActiveCalibration();
        foreach (var speaker in LinkedSpeakers) { speaker.DelayMs = 0; _player.Update(speaker.Id, PlaybackPreferences(speaker), MasterVolume); }
        SaveSoon();
    }
    private void RefreshCalibrationText()
    {
        CalibrationPaths.Clear();
        foreach (var path in new[] { "file", "stream", "capture" }) CalibrationPaths.Add(new(path, Text["CalibrationPath_" + path]));
        Raise(nameof(CalibrationPath)); RefreshCalibrationResults();
    }
    private void RefreshCalibrationResults()
    {
        CalibrationResults.Clear();
        if (_calibrationResult is { } result)
            foreach (var row in result.Analysis.Speakers)
                CalibrationResults.Add(new(result.Request.Speakers.First(s => s.Device.Id == row.Id).Device.Name,
                    row.Status == AcousticMeasurementStatus.Reliable && result.Delays.TryGetValue(row.Id, out var delay) ? delay + " ms" : Text["CalibrationNeedsRetry"],
                    row.Status == AcousticMeasurementStatus.Reliable ? string.Format(Text["CalibrationSpread"], row.SpreadMs.ToString("0.0")) : Text["CalibrationIssue_" + row.Status]));
        RefreshCalibrationAvailability(); Raise(nameof(CalibrationStatus));
    }
    private void ClearCalibrationResult() { _calibrationResult = null; CalibrationResults.Clear(); RefreshCalibrationAvailability(); Raise(nameof(CalibrationStatus)); }
    private void RefreshCalibrationAvailability()
    {
        foreach (var name in new[] { nameof(CalibrationSetup), nameof(CalibrationHasResults), nameof(CanStartCalibration), nameof(CanApplyCalibration), nameof(CalibrationCloseLabel), nameof(CalibrationShowStart), nameof(CalibrationShowApply), nameof(CalibrationStartLabel) }) Raise(name);
    }
    private void InvalidateCalibration() { _calibrationEpoch++; _calibrationCancellation?.Cancel(); _calibrationResumeTrack = null; ClearCalibrationResult(); }
}
