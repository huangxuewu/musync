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
    private bool _active, _motionEnabled = true, _attached;
    private static readonly IBrush Coral = new SolidColorBrush(Color.Parse("#F87C90"));
    public PreparationIndicator()
    { _timer = new(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => InvalidateVisual()); }
    public bool Active { get => _active; set { _active = value; UpdateClock(); } }
    public bool MotionEnabled { get => _motionEnabled; set { _motionEnabled = value; UpdateClock(); } }
    internal bool AnimationRunning => _timer.IsEnabled;
    private void UpdateClock()
    {
        if (_active && _motionEnabled && _attached) { _clock.Start(); _timer.Start(); }
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
            context.DrawRectangle(Coral, null, new Rect(2 + i * 7, (Bounds.Height - height) / 2, 3, height), 1.5, 1.5);
        }
    }
}
