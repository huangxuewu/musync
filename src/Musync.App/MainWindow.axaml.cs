using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Musync.App.Controls;

namespace Musync.App;

public partial class MainWindow : Window
{
    private MainViewModel? _linkModel;
    private bool _closing;
    private Slider? _seekingSlider;
    private double _pendingSeek;
    private Control? _playlistReturnFocus;
    private Control? _settingsReturnFocus;
    public Func<Task>? Cleanup { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        // Disabled volume controls cannot take focus; Escape must also work
        // while focus remains on the mini player's disclosure button.
        AddHandler(KeyDownEvent, MiniVolumeKeyDown, RoutingStrategies.Tunnel);
        UpdateWindowFrame();
        InitializePosterRotation();
        SizeChanged += (_, _) => { UpdateSpeakerPanelSize(); TrackFullPlayerBounds(); };
        PositionChanged += (_, _) => TrackFullPlayerBounds();
        Opened += (_, _) => { UpdateSpeakerPanelSize(); TrackFullPlayerBounds(); AnimatePosterLayers(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == IsMiniPlayerProperty)
            { UpdatePreparationMotion(); UpdatePosterRotation(); }
            if (e.Property != WindowStateProperty) return;
            UpdatePreparationMotion();
            UpdatePosterRotation();
            if (IsMiniPlayer && WindowState == WindowState.Maximized && !_changingPlayerMode) RestoreFullPlayer(returnFocus: false);
            UpdateWindowFrame();
            SpeakerToggle.IsChecked = false;
            MiniVolumeToggle.IsChecked = false;
        };
        DataContextChanged += (_, _) =>
        {
            if (_linkModel is not null) _linkModel.PropertyChanged -= LinkLayoutChanged;
            _linkModel = DataContext as MainViewModel;
            if (_linkModel is not null)
            {
                _linkModel.ImportFolders = ImportFoldersAsync;
                _linkModel.PropertyChanged += LinkLayoutChanged;
            }
            UpdatePlaylistDisclosure(animate: false);
            UpdatePreparationMotion();
            UpdatePosterRotation();
        };
        WireSeekSlider(SeekSlider);
        WireSeekSlider(MiniSeekSlider);
        MusicSourceSelector.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Popup>("PART_Popup") is { } menu)
            {
                menu.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => { if (menu.IsOpen && menu.Child is { } panel) RevealPanel(panel); }, Avalonia.Threading.DispatcherPriority.Loaded);
                menu.Closed += (_, _) => { if (menu.Child is { } panel) SettleReveal(panel); };
            }
        };
        UpdateProgressMotionPreference();
        Activated += (_, _) => UpdateProgressMotionPreference();
        Deactivated += (_, _) => CancelSeeking();
        Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true; _closing = true;
            ApplyMotionPreference(false);
            try { if (Cleanup is not null) await Cleanup(); }
            catch (Exception ex) { if (DataContext is MainViewModel model) model.ShowError(ex); }
            finally { Close(); }
        };
    }
    private void UpdateProgressMotionPreference()
    {
        ApplyMotionPreference(DesktopMotionPreferences.AnimationsEnabled);
    }
    internal void ApplyMotionPreference(bool enabled)
    {
        Classes.Set("motionEnabled", enabled);
        SeekSlider.MotionEnabled = MiniSeekSlider.MotionEnabled = enabled;
        HeroSurface.MotionEnabled = HeroArtwork.MotionEnabled = HeroSpotifyArtwork.MotionEnabled = enabled;
        RadioPage.ApplyMotionPreference(enabled);
        PlayerArtwork.MotionEnabled = MiniPlayerArtwork.MotionEnabled = enabled;
        FullPreparationWave.MotionEnabled = MiniPreparationWave.MotionEnabled = NetworkScanWave.MotionEnabled = MusicSourceWave.MotionEnabled = enabled;
        if (!enabled) UpdatePlaylistDisclosure(animate: false);
        UpdatePreparationMotion();
        UpdatePosterRotation();
        if (!enabled)
        {
            SettlePosterLayers();
            foreach (var control in _reveals.Keys.ToArray()) SettleReveal(control);
            foreach (var control in new Control[] { CurrentLyric, SpeakerPanel, MiniVolumePanel, SettingsPanel, PlaylistPanel }) SettleReveal(control);
        }
    }
    private void WireSeekSlider(PlaybackSlider slider)
    {
        slider.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!slider.IsEnabled) return;
            slider.SuspendProgressMotion();
            _seekingSlider = slider; _pendingSeek = slider.Value;
            if (DataContext is MainViewModel model) model.IsSeeking = true;
        }, RoutingStrategies.Tunnel, true);
        slider.AddHandler(PointerReleasedEvent, async (_, _) =>
        {
            if (_seekingSlider != slider) return;
            _seekingSlider = null;
            slider.ResumeProgressMotion();
            if (DataContext is MainViewModel model)
            {
                model.IsSeeking = false;
                try { await model.SeekAsync(_pendingSeek); } catch (Exception ex) { model.ShowError(ex); }
            }
        }, RoutingStrategies.Tunnel, true);
        // Capture-loss events are direct: a captured Thumb does not bubble to its Slider.
        slider.AddHandler(PointerCaptureLostEvent, (_, _) =>
        { if (_seekingSlider == slider) CancelSeeking(); }, RoutingStrategies.Direct, true);
        slider.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Track>("PART_Track")?.Thumb is { } thumb)
                thumb.AddHandler(PointerCaptureLostEvent, (_, _) =>
                { if (_seekingSlider == slider) CancelSeeking(); }, RoutingStrategies.Direct, true);
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (_seekingSlider == slider && e.Property == Slider.ValueProperty) _pendingSeek = slider.Value;
        };
        slider.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
                slider.SuspendProgressMotion();
        }, RoutingStrategies.Tunnel, true);
        slider.KeyUp += async (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)) return;
            slider.ResumeProgressMotion();
            if (DataContext is MainViewModel model)
                try { await model.SeekAsync(slider.Value); } catch (Exception ex) { model.ShowError(ex); }
        };
    }
    private void CancelSeeking()
    {
        _seekingSlider = null;
        SeekSlider.CancelProgressInteraction(); MiniSeekSlider.CancelProgressInteraction();
        if (DataContext is MainViewModel model) model.IsSeeking = false;
    }
    private void MiniVolumeOpened(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (MiniVolumePopover.IsOpen) RevealPanel(MiniVolumePanel); }, Avalonia.Threading.DispatcherPriority.Loaded);
        if (DataContext is MainViewModel { CanAdjustVolume: true }) MiniVolumeSlider.Focus();
    }
    private void MiniVolumeClosed(object? sender, EventArgs e)
    {
        SettleReveal(MiniVolumePanel);
        MiniVolumeToggle.IsChecked = false;
        if (IsMiniPlayer) MiniVolumeToggle.Focus();
    }
    private void MiniVolumeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !MiniVolumePopover.IsOpen) return;
        MiniVolumeToggle.IsChecked = false; e.Handled = true;
    }
    private async Task ImportFoldersAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = (DataContext as MainViewModel)?.Text["Import"] ?? "Import music folders", AllowMultiple = true
        });
        if (DataContext is MainViewModel model) await model.ImportFoldersAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    }
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (e.Key == Key.Enter) { model.SearchCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.Escape && model.HasSearchQuery) { model.ClearSearch.Execute(null); e.Handled = true; }
    }
    private void ClearSearchClicked(object? sender, RoutedEventArgs e) => MusicSearchBox.Focus();
    private void UpdateWindowFrame()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeWindowButton.IsVisible = !maximized;
        RestoreWindowButton.IsVisible = maximized;
        ResizeHandles.IsVisible = WindowState == WindowState.Normal && !IsMiniPlayer;
        ShellFrame.Margin = new Thickness(maximized ? 0 : 8);
        ShellFrame.CornerRadius = new CornerRadius(maximized ? 0 : 16);
    }
    private void LinkLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsScanning) or nameof(MainViewModel.IsLinkEditing) or nameof(MainViewModel.LinkPanelOpen) or nameof(MainViewModel.IsSwitchingSource))
            UpdatePreparationMotion();
        if (e.PropertyName == nameof(MainViewModel.IsPreparingPlayback))
        {
            UpdatePreparationMotion();
            if (_linkModel?.IsPreparingPlayback == true)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_linkModel?.IsPreparingPlayback == true && !IsMiniPlayer) RevealPanel(PreparationNotice, 6, 180); });
            else SettleReveal(PreparationNotice);
        }
        if (e.PropertyName == nameof(MainViewModel.ToastEpoch))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_linkModel?.HasToast == true) RevealPanel(StatusToast, 6, 180); });
        if (e.PropertyName == nameof(MainViewModel.HasToast) && _linkModel?.HasToast == false)
            SettleReveal(StatusToast);
        if (e.PropertyName == nameof(MainViewModel.HeroEpoch))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(AnimatePosterLayers);
            UpdatePosterRotation(restart: true);
        }
        if (e.PropertyName is nameof(MainViewModel.ShowHero) or nameof(MainViewModel.ShowArtist)
            or nameof(MainViewModel.SettingsOpen) or nameof(MainViewModel.PlaylistOpen)
            or nameof(MainViewModel.LinkPanelOpen) or nameof(MainViewModel.Busy)
            or nameof(MainViewModel.IsPreparingPlayback)) UpdatePosterRotation();
        if (e.PropertyName == nameof(MainViewModel.LyricCurrent))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RevealPanel(CurrentLyric, 4, 180));
        if (e.PropertyName == nameof(MainViewModel.SettingsOpen) && _linkModel?.SettingsOpen == true)
        {
            _settingsReturnFocus = FocusManager?.GetFocusedElement() as Control;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_linkModel is not { SettingsOpen: true } model) return;
                RevealPanel(SettingsPanel, 8, 320);
                (model.SettingsSpotify ? SettingsSpotifyTab : model.SettingsAudio ? SettingsAudioTab : SettingsGeneralTab).Focus();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        if (e.PropertyName == nameof(MainViewModel.SettingsOpen) && _linkModel?.SettingsOpen == false)
        {
            SettleReveal(SettingsPanel);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_linkModel?.SettingsOpen == true) return;
                var target = _settingsReturnFocus is { IsEffectivelyVisible: true, IsEnabled: true } control && TopLevel.GetTopLevel(control) is not null
                    ? control : SettingsNav;
                target.Focus(); _settingsReturnFocus = null;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        if (e.PropertyName == nameof(MainViewModel.PlaylistsExpanded)) UpdatePlaylistDisclosure(animate: true);
        if (e.PropertyName == nameof(MainViewModel.PlaylistOpen) && _linkModel?.PlaylistOpen == true)
        {
            _playlistReturnFocus = FocusManager?.GetFocusedElement() as Control;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { if (_linkModel?.PlaylistOpen == true) { RevealPanel(PlaylistPanel); PlaylistNameInput.Focus(); } }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        if (e.PropertyName == nameof(MainViewModel.PlaylistOpen) && _linkModel?.PlaylistOpen == false)
        {
            SettleReveal(PlaylistPanel);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_linkModel?.PlaylistOpen == true) return;
                var target = _playlistReturnFocus is { IsEffectivelyVisible: true, IsEnabled: true } control && TopLevel.GetTopLevel(control) is not null
                    ? control : SidebarAddPlaylistButton;
                target.Focus(); _playlistReturnFocus = null;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        if (IsMiniPlayer && e.PropertyName is (nameof(MainViewModel.LinkPanelOpen) or nameof(MainViewModel.SettingsOpen) or nameof(MainViewModel.PlaylistOpen))
            && _linkModel is { } model && (model.LinkPanelOpen || model.SettingsOpen || model.PlaylistOpen))
            RestoreFullPlayer(returnFocus: false);
        if (e.PropertyName is nameof(MainViewModel.IsLinkEditing) or nameof(MainViewModel.LinkSummary)) UpdateSpeakerPanelSize();
        if (e.PropertyName == nameof(MainViewModel.IsLinkEditing)
            || (e.PropertyName == nameof(MainViewModel.HasLinkEditError) && _linkModel?.HasLinkEditError == true))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (SpeakerPopover.IsOpen) FocusLinkPanel(); });
    }
    private void PlaylistDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (_linkModel is not { PlaylistOpen: true } model) return;
        if (e.Key == Key.Escape) { model.ClosePlaylist.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() == PlaylistNameInput)
        { model.SavePlaylist.Execute(null); e.Handled = true; }
    }
    private void PlaylistRemovedClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        { if (TopLevel.GetTopLevel(button) is null) PlaylistsNav.Focus(); }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
    private void UpdatePlaylistDisclosure(bool animate)
    {
        var expanded = _linkModel?.PlaylistsExpanded != false;
        PlaylistChevron.Transitions = animate && Classes.Contains("motionEnabled")
            ? [new Avalonia.Animation.TransformOperationsTransition { Property = RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(160), Easing = new Avalonia.Animation.Easings.ExponentialEaseOut() }] : null;
        PlaylistChevron.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(expanded ? "rotate(0deg)" : "rotate(-90deg)");
        if (expanded && animate)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_linkModel?.PlaylistsExpanded == true) RevealPanel(SidebarPlaylistList, 4, 180); });
        else
        {
            SettleReveal(SidebarPlaylistList);
            if (!expanded && FocusManager?.GetFocusedElement() is Control focused && focused.GetVisualAncestors().Contains(SidebarPlaylistList))
                PlaylistsNav.Focus();
        }
    }
    private void UpdatePreparationMotion()
    {
        var visible = !_closing && IsVisible && WindowState != WindowState.Minimized;
        var active = visible && _linkModel?.IsPreparingPlayback == true;
        FullPreparationWave.Active = active && !IsMiniPlayer;
        MiniPreparationWave.Active = active && IsMiniPlayer;
        NetworkScanWave.Active = visible && !IsMiniPlayer && SpeakerPopover.IsOpen
            && _linkModel is { IsScanning: true };
        MusicSourceWave.Active = visible && !IsMiniPlayer && _linkModel?.IsSwitchingSource == true;
    }
    private void UpdateSpeakerPanelSize()
    {
        var maximum = Math.Clamp(Bounds.Height - 152, 360, 640);
        SpeakerPanel.Width = _linkModel?.IsLinkEditing == true ? 440 : 336;
        SpeakerPanel.MaxHeight = maximum;
        var content = _linkModel is null || _linkModel.IsLinkEditing ? 640
            : 360 + _linkModel.LinkedSpeakers.Count * 150;
        SpeakerPanel.Height = _linkModel is { NoLinks: true, IsLinkEditing: false } ? double.NaN : Math.Min(maximum, content);
    }
    private void SpeakerPopoverOpened(object? sender, EventArgs e)
    {
        UpdateSpeakerPanelSize();
        UpdatePreparationMotion();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (SpeakerPopover.IsOpen) RevealPanel(SpeakerPanel); }, Avalonia.Threading.DispatcherPriority.Loaded);
        FocusLinkPanel();
    }
    private void FocusLinkPanel()
    {
        if (_linkModel?.IsLinkEditing == true) LinkNameEditor.Focus(); else CloseSpeakersButton.Focus();
    }
    private void SpeakerPopoverClosed(object? sender, EventArgs e)
    {
        SettleReveal(SpeakerPanel);
        if (DataContext is MainViewModel model) model.DiscardLinkDraft();
        SpeakerToggle.IsChecked = false;
        UpdatePreparationMotion();
        SpeakerToggle.Focus();
    }
    private void CloseSpeakers(object? sender, RoutedEventArgs e) => SpeakerToggle.IsChecked = false;
    private void SpeakerPopoverKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SpeakerToggle.IsChecked = false;
        e.Handled = true;
    }
    private void SettingsPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel { SettingsOpen: true } model) return;
        model.CloseSettings.Execute(null); e.Handled = true;
    }
    private void ResizeWindow(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { Tag: string edgeName } && Enum.TryParse<WindowEdge>(edgeName, out var edge))
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
        }
    }
    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleMaximizeWindow(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
