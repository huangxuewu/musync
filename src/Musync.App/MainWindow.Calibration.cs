using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Musync.App;

public partial class MainWindow
{
    private void UpdateCalibrationSurface(string? property)
    {
        if (property == nameof(MainViewModel.CalibrationOpen) && _linkModel?.CalibrationOpen == true)
        {
            if (IsMiniPlayer) RestoreFullPlayer(returnFocus: false);
            MusicSourceSelector.IsDropDownOpen = false;
            MiniVolumePopover.IsOpen = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (_linkModel?.CalibrationOpen != true) return;
                RevealPanel(CalibrationPanel);
                (CalibrationInputSelector.ItemsSource is not null ? CalibrationInputSelector : CalibrationPathSelector).Focus();
            }, DispatcherPriority.Loaded);
        }
        if (property == nameof(MainViewModel.CalibrationOpen) && _linkModel?.CalibrationOpen == false)
        {
            SettleReveal(CalibrationPanel);
            Dispatcher.UIThread.Post(() => { if (_linkModel?.CalibrationOpen != true) LinkConfigurationToggle.Focus(); });
        }
        CalibrationPanel.MaxHeight = Math.Max(320, Bounds.Height - 100);
        CalibrationWave.Active = IsVisible && IsActive && WindowState != WindowState.Minimized && _linkModel is { CalibrationOpen: true, CalibrationRunning: true };
    }
    private void CalibrationDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _linkModel?.CalibrationOpen == true)
        { _linkModel.CloseCalibration.Execute(null); e.Handled = true; }
    }
}
