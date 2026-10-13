using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;

namespace Musync.App.Controls;

// Per-control state keeps interrupted pointer/keyboard feedback independent.
internal sealed class InteractionMotion : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<InteractionMotion, Control, bool>("Enabled");
    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<InteractionMotion, Control, State?>("State");
    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);
    public static void SetEnabled(Control control, bool enabled) => control.SetValue(EnabledProperty, enabled);

    static InteractionMotion() => EnabledProperty.Changed.AddClassHandler<Control>((control, change) =>
    {
        control.GetValue(StateProperty)?.Dispose();
        control.SetValue(StateProperty, change.GetNewValue<bool>() ? new State(control) : null);
    });

    private sealed class State : IDisposable
    {
        private readonly Control _control;
        private bool _pressed;
        public State(Control control)
        {
            _control = control;
            control.RenderTransformOrigin = RelativePoint.Center;
            control.RenderTransform = TransformOperations.Parse("scale(1)");
            control.Transitions =
            [
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(160), Easing = new ExponentialEaseOut() },
                new DoubleTransition { Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(120), Easing = new ExponentialEaseOut() }
            ];
            control.PointerEntered += Entered; control.PointerExited += Exited;
            control.PointerPressed += Pressed; control.PointerReleased += Released;
            control.PointerCaptureLost += CaptureLost; control.PropertyChanged += Changed;
            control.DetachedFromVisualTree += Detached;
        }
        private void Entered(object? sender, PointerEventArgs e) => Update();
        private void Exited(object? sender, PointerEventArgs e) => Update();
        private void Pressed(object? sender, PointerPressedEventArgs e)
        { _pressed = e.GetCurrentPoint(_control).Properties.IsLeftButtonPressed; Update(); }
        private void Released(object? sender, PointerReleasedEventArgs e) { _pressed = false; Update(); }
        private void CaptureLost(object? sender, PointerCaptureLostEventArgs e) { _pressed = false; Update(); }
        private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        { if (e.Property == Button.IsPressedProperty || e.Property == InputElement.IsEnabledProperty) Update(); }
        private void Update()
        {
            var pressed = _pressed || _control is Button { IsPressed: true };
            var scale = !_control.IsEnabled ? 1 : pressed ? .94 : _control.IsPointerOver ? 1.055 : 1;
            _control.RenderTransform = TransformOperations.Parse($"scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        }
        private void Detached(object? sender, VisualTreeAttachmentEventArgs e)
        { _pressed = false; _control.Transitions = null; _control.RenderTransform = TransformOperations.Parse("scale(1)"); }
        public void Dispose()
        {
            _control.PointerEntered -= Entered; _control.PointerExited -= Exited;
            _control.PointerPressed -= Pressed; _control.PointerReleased -= Released;
            _control.PointerCaptureLost -= CaptureLost; _control.PropertyChanged -= Changed;
            _control.DetachedFromVisualTree -= Detached;
            _control.Transitions = null; _control.RenderTransform = TransformOperations.Parse("scale(1)");
        }
    }
}
