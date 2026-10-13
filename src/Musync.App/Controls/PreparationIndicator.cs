using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;

namespace Musync.App.Controls;

// Drawing the wave never changes layout. The window owns visibility/lifecycle.
public sealed class PreparationIndicator : Control
{
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private bool _attached;
    private static readonly IBrush Coral = new SolidColorBrush(Color.Parse("#F87C90"));
    public static readonly StyledProperty<bool> ActiveProperty = AvaloniaProperty.Register<PreparationIndicator, bool>(nameof(Active));
    public static readonly StyledProperty<bool> MotionEnabledProperty = AvaloniaProperty.Register<PreparationIndicator, bool>(nameof(MotionEnabled), true);
    public static readonly StyledProperty<IBrush> ForegroundProperty = AvaloniaProperty.Register<PreparationIndicator, IBrush>(nameof(Foreground), Coral);
    static PreparationIndicator() { AffectsRender<PreparationIndicator>(ForegroundProperty); }
    public PreparationIndicator()
    { _timer = new(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => InvalidateVisual()); }
    public bool Active { get => GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public bool MotionEnabled { get => GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    public IBrush Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActiveProperty || change.Property == MotionEnabledProperty || change.Property == IsVisibleProperty) UpdateClock();
    }
    internal bool AnimationRunning => _timer.IsEnabled;
    private void UpdateClock()
    {
        if (Active && MotionEnabled && _attached && IsVisible) { _clock.Start(); _timer.Start(); }
        else { _timer.Stop(); _clock.Reset(); }
        InvalidateVisual();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; UpdateClock(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; UpdateClock(); base.OnDetachedFromVisualTree(e); }
    public override void Render(DrawingContext context)
    {
        for (var i = 0; i < 3; i++)
        {
            var scale = AnimationRunning ? .35 + .65 * (.5 + .5 * Math.Sin(_clock.Elapsed.TotalSeconds * Math.Tau / .9 - i * .85)) : .55 + i * .15;
            var height = Math.Max(1, Bounds.Height * scale);
            context.DrawRectangle(Foreground, null, new Rect(2 + i * 7, (Bounds.Height - height) / 2, 3, height), 1.5, 1.5);
        }
    }
}
