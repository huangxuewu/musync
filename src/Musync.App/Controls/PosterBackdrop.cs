using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Musync.App.Controls;

// Geometry is intentionally confined to the artwork side. Lyrics keep a quiet,
// high-contrast ground while the background remains a separate poster layer.
public sealed class PosterBackdrop : Control
{
    public static readonly StyledProperty<int> PaletteProperty = AvaloniaProperty.Register<PosterBackdrop, int>(nameof(Palette));
    public static readonly StyledProperty<double> BlendProperty = AvaloniaProperty.Register<PosterBackdrop, double>(nameof(Blend), 1);
    public static readonly StyledProperty<bool> MotionEnabledProperty = AvaloniaProperty.Register<PosterBackdrop, bool>(nameof(MotionEnabled), true);
    private readonly Transitions _transition = [new DoubleTransition { Property = BlendProperty, Duration = TimeSpan.FromMilliseconds(420), Easing = new ExponentialEaseOut() }];
    private int _previousPalette;
    private static readonly (IBrush Ground, IBrush Wedge, IBrush Circle, IBrush Stripe)[] Colors =
    [
        (Brush("#FFF4BA"), Brush("#244DFF"), Brush("#FF6B53"), Brush("#CFFF71")),
        (Brush("#FFF1F3"), Brush("#FF527D"), Brush("#C7F4FF"), Brush("#6734D4")),
        (Brush("#F6F0FF"), Brush("#6539D7"), Brush("#CDFF64"), Brush("#FF6B53"))
    ];
    public int Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public double Blend { get => GetValue(BlendProperty); set => SetValue(BlendProperty, value); }
    public bool MotionEnabled { get => GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    static PosterBackdrop() => AffectsRender<PosterBackdrop>(PaletteProperty, BlendProperty);
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color)).ToImmutable();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MotionEnabledProperty && !MotionEnabled) { Transitions = null; Blend = 1; }
        if (change.Property != PaletteProperty) return;
        _previousPalette = (int)change.OldValue!;
        Transitions = null; Blend = MotionEnabled ? 0 : 1;
        if (MotionEnabled) { Transitions = _transition; Blend = 1; }
    }
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var clip = context.PushClip(new RoundedRect(bounds, 16));
        var blend = Math.Clamp(Blend, 0, 1);
        if (blend < 1) Draw(context, _previousPalette, bounds);
        using (context.PushOpacity(blend)) Draw(context, Palette, bounds);
    }
    private static void Draw(DrawingContext context, int palette, Rect bounds)
    {
        var colors = Colors[((palette % 3) + 3) % 3];
        var w = bounds.Width; var h = bounds.Height;
        context.DrawRectangle(colors.Ground, null, bounds);
        Polygon(context, colors.Wedge, new(.78 * w, 0), new(w, 0), new(w, h), new(.62 * w, h));
        context.DrawEllipse(colors.Circle, null, new Point(.86 * w, .34 * h), .38 * h, .38 * h);
        Polygon(context, colors.Stripe, new(.66 * w, .83 * h), new(w, .25 * h), new(w, .35 * h), new(.69 * w, h));
        Polygon(context, colors.Ground, new(.80 * w, 0), new(.84 * w, 0), new(w, .28 * h), new(w, .35 * h));
    }
    private static void Polygon(DrawingContext context, IBrush brush, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], true);
            foreach (var point in points.Skip(1)) path.LineTo(point);
            path.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }
}
