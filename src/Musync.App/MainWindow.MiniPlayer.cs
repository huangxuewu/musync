using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Musync.App;

public partial class MainWindow
{
    public static readonly StyledProperty<bool> IsMiniPlayerProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsMiniPlayer));

    private bool _changingPlayerMode;
    private Size _fullPlayerSize = new(1280, 940);
    private PixelPoint _fullPlayerPosition;
    private WindowState _fullPlayerState;
    private double _fullMinWidth, _fullMinHeight;
    private bool _fullCanResize, _fullTopmost;

    public bool IsMiniPlayer { get => GetValue(IsMiniPlayerProperty); private set => SetValue(IsMiniPlayerProperty, value); }

    private void TrackFullPlayerBounds()
    {
        if (IsMiniPlayer || _changingPlayerMode || WindowState != WindowState.Normal
            || Bounds.Width < MinWidth || Bounds.Height < MinHeight) return;
        _fullPlayerSize = Bounds.Size;
        _fullPlayerPosition = Position;
    }

    private void ToggleMiniPlayer(object? sender, RoutedEventArgs e)
    {
        if (IsMiniPlayer) { RestoreFullPlayer(); return; }
        TrackFullPlayerBounds();
        _fullPlayerState = WindowState;
        _fullMinWidth = MinWidth; _fullMinHeight = MinHeight;
        _fullCanResize = CanResize; _fullTopmost = Topmost;
        var screen = Screens.ScreenFromWindow(this);
        var scaling = RenderScaling;
        _changingPlayerMode = true;
        try
        {
            _seekingSlider = null;
            if (_linkModel is { } model) model.IsSeeking = false;
            SpeakerToggle.IsChecked = false;
            LinkConfigurationToggle.IsChecked = false;
            MiniVolumeToggle.IsChecked = false;
            MusicSourceSelector.IsDropDownOpen = false;
            WindowState = WindowState.Normal;
            IsMiniPlayer = true;
            MinWidth = 520; MinHeight = 196;
            CanResize = false;
            Width = 520; Height = 196;
            Topmost = true;
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                Position = new PixelPoint(
                    Math.Max(area.X, area.Right - (int)Math.Ceiling((Width + 24) * scaling)),
                    Math.Max(area.Y, area.Bottom - (int)Math.Ceiling((Height + 24) * scaling)));
            }
            UpdateWindowFrame();
        }
        finally { _changingPlayerMode = false; }
        Dispatcher.UIThread.Post(() => { if (IsMiniPlayer) RestoreFullPlayerButton.Focus(); });
    }

    private void RestoreFullPlayer(bool returnFocus = true)
    {
        if (!IsMiniPlayer) return;
        _changingPlayerMode = true;
        try
        {
            _seekingSlider = null;
            if (_linkModel is { } model) model.IsSeeking = false;
            IsMiniPlayer = false;
            MiniVolumeToggle.IsChecked = false;
            Topmost = _fullTopmost;
            CanResize = _fullCanResize;
            MinWidth = _fullMinWidth; MinHeight = _fullMinHeight;
            Width = _fullPlayerSize.Width; Height = _fullPlayerSize.Height;
            Position = _fullPlayerPosition;
            WindowState = _fullPlayerState;
            UpdateWindowFrame();
        }
        finally { _changingPlayerMode = false; }
        if (returnFocus) Dispatcher.UIThread.Post(() => { if (!IsMiniPlayer) MiniPlayerButton.Focus(); });
    }
}
