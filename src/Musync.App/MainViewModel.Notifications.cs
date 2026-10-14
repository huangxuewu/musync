using Avalonia.Threading;
using System.Diagnostics;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string _toastMessage = "";
    private long _toastEpoch, _toastStarted;
    private bool _toastTimerWired;
    private bool _toastIsImportSummary;
    private string? _reportedPlayerError;
    private string? _reportedPlayerMessage;
    private long _playerErrorToastEpoch;
    private long _scanToastEpoch = -1;

    public string ToastMessage
    {
        get => _toastMessage;
        private set { if (Set(ref _toastMessage, value)) Raise(nameof(HasToast)); }
    }
    public bool HasToast => ToastMessage.Length > 0;
    public long ToastEpoch => _toastEpoch;

    private void ShowToast(string message, bool importSummary = false)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        if (!_toastTimerWired)
        {
            _toastTimer.Tick += (_, _) =>
            {
                if (!HasToast) { _toastTimer.Stop(); return; }
                // A queued tick from an earlier notification must not hide a new one.
                var remaining = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(_toastStarted);
                _toastTimer.Stop();
                if (remaining > TimeSpan.Zero)
                {
                    _toastTimer.Interval = remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining;
                    _toastTimer.Start();
                    return;
                }
                // Do not leave an expired import summary in the mini player or Settings.
                if (_toastIsImportSummary && Status == ToastMessage)
                    Status = Text[_session ? IsPlaying ? "Playing" : "Paused" : "Idle"];
                ToastMessage = "";
            };
            _toastTimerWired = true;
        }
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(5);
        _toastIsImportSummary = importSummary;
        ToastMessage = message;
        _toastStarted = Stopwatch.GetTimestamp();
        _toastEpoch++; Raise(nameof(ToastEpoch));
        _toastTimer.Start();
    }
    private void ReportPlayerError(string error)
    {
        _reportedPlayerError = error;
        _reportedPlayerMessage = PlaybackErrorPresentation.Format(error, Text);
        Status = _reportedPlayerMessage;
        _playerErrorToastEpoch = ToastMessage == _reportedPlayerMessage ? ToastEpoch : -1;
    }
    private void ShowScanToast(bool failed)
    {
        ShowToast(Text[failed ? "ScanNetworkFailed" : "ScanDone"]);
        _scanToastEpoch = ToastEpoch;
    }
    private void ClearScanToast()
    {
        var owned = _scanToastEpoch == ToastEpoch;
        _scanToastEpoch = -1;
        if (!owned) return;
        _toastTimer.Stop(); _toastIsImportSummary = false; ToastMessage = "";
    }
    private void ClearReportedPlayerError()
    {
        if (_reportedPlayerMessage is { } message && ToastMessage == message && ToastEpoch == _playerErrorToastEpoch)
        {
            _toastTimer.Stop(); _toastIsImportSummary = false; ToastMessage = "";
        }
        _reportedPlayerError = null;
        _reportedPlayerMessage = null;
    }
    private void RefreshReportedPlayerErrorText()
    {
        if (_reportedPlayerError is not { } error || _reportedPlayerMessage is not { } previous) return;
        var message = PlaybackErrorPresentation.Format(error, Text);
        var ownsStatus = Status == previous;
        var ownsToast = HasToast && ToastMessage == previous && ToastEpoch == _playerErrorToastEpoch;
        _reportedPlayerMessage = message;
        // Language changes preserve the existing notification's timer and
        // ownership. Expired or newer notices must not be replayed or replaced.
        if (ownsStatus) Set(ref _status, message, nameof(Status));
        if (ownsToast) ToastMessage = message;
    }
    private void ShowPlaybackStatus()
    {
        if (!SpotifyMode && _player.Error is { } error) ReportPlayerError(error);
        else
        {
            ClearReportedPlayerError();
            Status = Text[IsPlaying ? "Playing" : "Paused"];
        }
    }
    private void RefreshPlayerError()
    {
        if (_player.Error is { } error)
        {
            if (_reportedPlayerError != error) ReportPlayerError(error);
        }
        else if (_reportedPlayerMessage is { } previous)
        {
            var restoreStatus = Status == previous;
            ClearReportedPlayerError();
            if (restoreStatus) Status = Text[IsPlaying ? "Playing" : "Paused"];
        }
    }
}
