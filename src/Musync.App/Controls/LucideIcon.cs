using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Musync.App.Controls;

/// <summary>Lucide geometry rendered on its original 24-unit canvas, including safe margins.</summary>
public sealed class LucideIcon : Control
{
    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<LucideIcon, string>(nameof(Kind), "music-2");
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TemplatedControl.ForegroundProperty.AddOwner<LucideIcon>();
    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<LucideIcon, bool>(nameof(Filled));

    private static readonly Dictionary<string, Geometry[]> GeometryCache = new(StringComparer.Ordinal);

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(KindProperty, ForegroundProperty, FilledProperty);
        WidthProperty.OverrideDefaultValue<LucideIcon>(20);
        HeightProperty.OverrideDefaultValue<LucideIcon>(20);
        HorizontalAlignmentProperty.OverrideDefaultValue<LucideIcon>(HorizontalAlignment.Center);
        VerticalAlignmentProperty.OverrideDefaultValue<LucideIcon>(VerticalAlignment.Center);
        IsHitTestVisibleProperty.OverrideDefaultValue<LucideIcon>(false);
        FocusableProperty.OverrideDefaultValue<LucideIcon>(false);
    }

    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Foreground is null || Bounds.Width <= 0 || Bounds.Height <= 0 ||
            !LucideGeometry.Paths.TryGetValue(Kind, out var paths)) return;

        // Parse each selected icon once on the UI thread; never normalize to tight path bounds.
        if (!GeometryCache.TryGetValue(Kind, out var geometries))
            GeometryCache[Kind] = geometries = paths.Select(Geometry.Parse).ToArray();

        var size = Math.Min(Bounds.Width, Bounds.Height);
        var scale = size / 24;
        var transform = Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var pen = new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(transform))
            foreach (var geometry in geometries)
                context.DrawGeometry(Filled ? Foreground : null, pen, geometry);
    }
}
