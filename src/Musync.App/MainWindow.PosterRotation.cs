using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Musync.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _posterRotationTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _posterInViewport;
    internal bool IsPosterRotationRunning => _posterRotationTimer.IsEnabled;

    private bool CanRotatePoster => !_closing && IsVisible && IsActive && !IsMiniPlayer
        && WindowState != WindowState.Minimized && _posterInViewport && HeroScene.IsEffectivelyVisible
        && Classes.Contains("motionEnabled") && !MusicSourceSelector.IsDropDownOpen
        && _linkModel is { ShowHero: true, ShowArtist: true, SettingsOpen: false, PlaylistOpen: false,
            LinkPanelOpen: false, SpeakerControlsOpen: false, Busy: false, IsPreparingPlayback: false, CalibrationOpen: false };

    private void InitializePosterRotation()
    {
        _posterRotationTimer.Tick += (_, _) => AdvancePosterRotation();
        HeroScene.EffectiveViewportChanged += (_, e) =>
        {
            _posterInViewport = e.EffectiveViewport.Intersects(new Rect(HeroScene.Bounds.Size));
            UpdatePosterRotation();
        };
        HeroScene.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdatePosterRotation(); };
        MusicSourceSelector.PropertyChanged += (_, e) =>
        { if (e.Property.Name == nameof(MusicSourceSelector.IsDropDownOpen)) UpdatePosterRotation(); };
        Activated += (_, _) => UpdatePosterRotation();
        Deactivated += (_, _) => _posterRotationTimer.Stop();
    }

    private void UpdatePosterRotation(bool restart = false)
    {
        if (!CanRotatePoster) { _posterRotationTimer.Stop(); return; }
        if (restart) _posterRotationTimer.Stop();
        if (!_posterRotationTimer.IsEnabled) _posterRotationTimer.Start();
    }

    internal void AdvancePosterRotation()
    {
        if (!CanRotatePoster) { UpdatePosterRotation(); return; }
        // A seek gesture skips this tick without stopping future rotations.
        if (_linkModel!.IsSeeking) return;
        _linkModel.RotatePosterArtwork();
    }
}
