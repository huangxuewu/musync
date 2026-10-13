using Avalonia.Controls;
using Avalonia.Input;

namespace Musync.App;

public sealed partial class RadioView : UserControl
{
    public RadioView() => InitializeComponent();
    internal void ApplyMotionPreference(bool enabled)
    { RadioBackdrop.MotionEnabled = RadioPortrait.MotionEnabled = enabled; }
    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel model || model.Busy) return;
        model.SaveRadio.Execute(null);
        if (!model.HasRadioError) RadioUrlInput.Focus();
        e.Handled = true;
    }
}
