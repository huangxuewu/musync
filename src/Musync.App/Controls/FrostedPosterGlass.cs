using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Musync.App.Controls;

// Samples the poster's actual geometry at the panel's coordinates. Only the
// backdrop is blurred: title, lyrics and their tooltips stay on a sharp layer.
public sealed class FrostedPosterGlass : Control
{
    public static readonly StyledProperty<PosterBackdrop?> BackdropProperty =
        AvaloniaProperty.Register<FrostedPosterGlass, PosterBackdrop?>(nameof(Backdrop));
    private readonly BlurEffect _blur = new() { Radius = 18 };
    private readonly IBrush _tint = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = [new(Color.Parse("#A6FFFFFF"), 0), new(Color.Parse("#8FFFFFFF"), 1)]
    };
    private readonly Pen _edge = new(new SolidColorBrush(Color.Parse("#D9FFFFFF")), 1);
    private PosterBackdrop? _observed;
    private Point? _origin;
    private bool _attached;

    public PosterBackdrop? Backdrop { get => GetValue(BackdropProperty); set => SetValue(BackdropProperty, value); }
    static FrostedPosterGlass() => AffectsRender<FrostedPosterGlass>(BackdropProperty, BoundsProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BackdropProperty) ObserveBackdrop();
    }

    private void ObserveBackdrop()
    {
        if (_observed is not null) _observed.PropertyChanged -= BackdropChanged;
        _observed = _attached ? Backdrop : null;
        if (_observed is not null) _observed.PropertyChanged += BackdropChanged;
        _origin = null;
        InvalidateVisual();
    }

    private void BackdropChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == PosterBackdrop.PaletteProperty || change.Property == PosterBackdrop.BlendProperty ||
            change.Property == BoundsProperty) InvalidateVisual();
    }

    private void UpdateSamplingOrigin(object? sender, EventArgs args)
    {
        var origin = Backdrop?.TranslatePoint(default, this);
        if (_origin == origin) return;
        _origin = origin;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ObserveBackdrop();
        LayoutUpdated += UpdateSamplingOrigin;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= UpdateSamplingOrigin;
        _attached = false;
        ObserveBackdrop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using (context.PushClip(new RoundedRect(bounds, 16)))
        {
            if (Backdrop is { } source && source.TranslatePoint(default, this) is { } origin)
            {
                // A bounded sample with extra pixels avoids seams at the glass
                // edge. There is no full-window capture or continuously ticking timer.
                var sample = bounds.Inflate(36);
                using (context.PushEffect(_blur, sample))
                using (context.PushClip(sample))
                using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
                    source.Render(context);
            }
            context.DrawRectangle(_tint, null, bounds);
        }
        context.DrawRectangle(null, _edge, bounds.Deflate(.5), 16, 16);
    }
}
