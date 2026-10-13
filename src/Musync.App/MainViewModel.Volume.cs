using Musync.Core;
using System.Windows.Input;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private double _localVolumeBeforeMute = 80;
    private int? _spotifyVolume, _spotifyConfirmedVolume;
    private int _spotifyVolumeBeforeMute = 50;
    private string? _volumeDeviceId;
    private bool _spotifySupportsVolume, _volumePending;
    private long _volumeTargetVersion, _volumeEditVersion;
    private CancellationTokenSource? _volumeDelay;
    private readonly SemaphoreSlim _volumeSendGate = new(1);
    private Task _volumeTask = Task.CompletedTask;

    public ICommand MuteCommand { get; }
    public bool CanAdjustVolume => !SpotifyMode || (_spotify.Connected && _spotifySupportsVolume
        && _spotifyVolume is not null && !string.IsNullOrWhiteSpace(_volumeDeviceId));
    public double PlayerVolume
    {
        get => SpotifyMode ? _spotifyVolume ?? 0 : MasterVolume;
        set
        {
            if (!CanAdjustVolume || !double.IsFinite(value)) return;
            var percent = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (percent == PlayerVolume) return;
            if (!SpotifyMode) { MasterVolume = percent; return; }
            _spotifyVolume = percent;
            if (percent > 0) _spotifyVolumeBeforeMute = percent;
            _volumePending = true; var edit = ++_volumeEditVersion;
            _volumeDelay?.Cancel();
            var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _volumeDelay = delay;
            RefreshVolumeProperties();
            _volumeTask = SendSpotifyVolumeAsync(percent, _volumeDeviceId!, _volumeTargetVersion, edit, delay);
        }
    }
    public string PlayerVolumeText => SpotifyMode && _spotifyVolume is null ? "—" : $"{PlayerVolume:0}%";
    public bool PlayerMuted => PlayerVolume == 0 && (!SpotifyMode || _spotifyVolume is not null);
    public string VolumeIcon => PlayerMuted ? "volume-x" : "volume-2";
    public string MuteLabel => Text[PlayerMuted ? "PlayerUnmute" : "PlayerMute"];
    public string VolumeHint => CanAdjustVolume ? Text["Volume"] + " · " + PlayerVolumeText : Text["SpotifyVolumeUnavailable"];

    private void TogglePlayerMute()
    {
        if (!CanAdjustVolume) return;
        PlayerVolume = PlayerMuted ? SpotifyMode ? _spotifyVolumeBeforeMute : _localVolumeBeforeMute : 0;
    }

    private void RefreshVolumeProperties()
    {
        foreach (var name in new[] { nameof(PlayerVolume), nameof(PlayerVolumeText), nameof(PlayerMuted), nameof(VolumeIcon),
            nameof(MuteLabel), nameof(VolumeHint), nameof(CanAdjustVolume) }) Raise(name);
    }

    private void ResetSpotifyVolume()
    {
        _volumeDelay?.Cancel(); _volumeTargetVersion++; _volumeEditVersion++;
        _volumePending = false; _volumeDeviceId = null; _spotifyVolume = _spotifyConfirmedVolume = null;
        _spotifySupportsVolume = false; _spotifyVolumeBeforeMute = 50;
        RefreshVolumeProperties();
    }

    private void ApplySpotifyVolume(SpotifyPlayback state, long snapshotEdit, bool snapshotWasPending)
    {
        var accepted = false;
        if (state.DeviceId != _volumeDeviceId || state.SupportsVolume != _spotifySupportsVolume)
        {
            ResetSpotifyVolume();
            _volumeDeviceId = state.DeviceId; _spotifySupportsVolume = state.SupportsVolume;
            _spotifyVolume = _spotifyConfirmedVolume = state.VolumePercent;
            accepted = true;
        }
        else if (!_volumePending && !snapshotWasPending && snapshotEdit == _volumeEditVersion)
        {
            _spotifyVolume = _spotifyConfirmedVolume = state.VolumePercent;
            accepted = true;
        }
        if (accepted && _spotifyConfirmedVolume is > 0) _spotifyVolumeBeforeMute = _spotifyConfirmedVolume.Value;
        RefreshVolumeProperties();
    }

    private async Task SendSpotifyVolumeAsync(int percent, string deviceId, long target, long edit, CancellationTokenSource delay)
    {
        var acquired = false;
        try
        {
            await Task.Delay(150, delay.Token);
            await _volumeSendGate.WaitAsync(delay.Token); acquired = true;
            if (!VolumeTargetCurrent(deviceId, target) || edit != _volumeEditVersion) return;
            // New slider edits cancel queued work, not a request already sent.
            // Awaiting each response serializes writes to the explicitly observed device.
            await _spotify.VolumeAsync(percent, deviceId, _lifetime.Token);
            if (!VolumeTargetCurrent(deviceId, target)) return;
            _spotifyConfirmedVolume = percent;
            if (edit == _volumeEditVersion) _spotifyVolume = percent;
            _spotifyNextPoll = DateTime.UtcNow.AddSeconds(1);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (VolumeTargetCurrent(deviceId, target) && edit == _volumeEditVersion)
            {
                _spotifyVolume = _spotifyConfirmedVolume;
                ShowError(ex);
            }
        }
        finally
        {
            if (acquired) _volumeSendGate.Release();
            if (ReferenceEquals(_volumeDelay, delay)) _volumeDelay = null;
            delay.Dispose();
            if (VolumeTargetCurrent(deviceId, target) && edit == _volumeEditVersion)
            {
                _volumePending = false; RefreshVolumeProperties();
            }
        }
    }

    private bool VolumeTargetCurrent(string deviceId, long target) => !_disposed && SpotifyMode && _spotify.Connected
        && target == _volumeTargetVersion && deviceId == _volumeDeviceId;
    internal Task WaitForVolumeAsync() => _volumeTask;
}
