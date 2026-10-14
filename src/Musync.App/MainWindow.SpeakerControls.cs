using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Musync.App;

public partial class MainWindow
{
    private void DismissUtilityPopovers(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual target) return;
        if (InsideUtilityPopup(SpeakerPopover, target) || InsideUtilityPopup(SpeakerControlsPopover, target)) return;
        // Keep real pointer routing for the two menu triggers. Other player
        // interactions dismiss the popover before carrying out their own action.
        if (ReferenceEquals(target, WorkspaceUtilities) || WorkspaceUtilities.IsVisualAncestorOf(target)
            || ReferenceEquals(target, SpeakerToggle) || SpeakerToggle.IsVisualAncestorOf(target)) return;
        LinkConfigurationToggle.IsChecked = false;
        SpeakerToggle.IsChecked = false;
    }
    private static bool InsideUtilityPopup(Avalonia.Controls.Primitives.Popup popup, Avalonia.Visual target) =>
        popup.IsInsidePopup(target) || popup.OpenedPopups.Any(child => InsideUtilityPopup(child, target));
    private void SpeakerControlsOpened(object? sender, EventArgs e)
    {
        UpdateSpeakerPanelSize();
        Dispatcher.UIThread.Post(() =>
        {
            if (!SpeakerControlsPopover.IsOpen) return;
            RevealPanel(SpeakerControlsPanel);
            if (_linkModel?.HasLinkIssue == true)
                SpeakerControlsList.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(row => row.Classes.Contains("speakerControlRow") && row.DataContext is SpeakerViewModel { HasIssue: true })?.BringIntoView();
            CloseSpeakerControlsButton.Focus();
        }, DispatcherPriority.Loaded);
    }
    private void SpeakerControlsClosed(object? sender, EventArgs e)
    {
        SettleReveal(SpeakerControlsPanel);
        SpeakerToggle.IsChecked = false;
        if (_linkModel is not { LinkPanelOpen: true } and not { SettingsOpen: true } and not { CalibrationOpen: true }) SpeakerToggle.Focus();
    }
    private void CloseSpeakerControls(object? sender, RoutedEventArgs e) => SpeakerToggle.IsChecked = false;
    private void SpeakerControlsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SpeakerToggle.IsChecked = false; e.Handled = true;
    }
}
