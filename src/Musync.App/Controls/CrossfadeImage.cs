using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Musync.App.Controls;

public sealed class CrossfadeImage : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<CrossfadeImage, IImage?>(nameof(Source));
    public static readonly StyledProperty<Stretch> StretchProperty = AvaloniaProperty.Register<CrossfadeImage, Stretch>(nameof(Stretch), Stretch.Uniform);
    public static readonly StyledProperty<double> BlendProperty = AvaloniaProperty.Register<CrossfadeImage, double>(nameof(Blend), 1);
    public static readonly StyledProperty<bool> MotionEnabledProperty = AvaloniaProperty.Register<CrossfadeImage, bool>(nameof(MotionEnabled), true);
    private readonly Transitions _fade = [new DoubleTransition { Property = BlendProperty, Duration = TimeSpan.FromMilliseconds(280), Easing = new ExponentialEaseOut() }];
    private IImage? _previous;
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Stretch Stretch { get => GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public double Blend { get => GetValue(BlendProperty); set => SetValue(BlendProperty, value); }
    public bool MotionEnabled { get => GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    static CrossfadeImage()
    { AffectsRender<CrossfadeImage>(SourceProperty, BlendProperty, StretchProperty); AffectsMeasure<CrossfadeImage>(SourceProperty, StretchProperty); }
    internal bool HasPreviousImage => _previous is not null;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MotionEnabledProperty && !MotionEnabled)
        { Transitions = null; Blend = 1; _previous = null; }
        if (change.Property != SourceProperty) return;
        _previous = MotionEnabled ? change.OldValue as IImage : null;
        Transitions = null; Blend = MotionEnabled ? 0 : 1;
        if (MotionEnabled) { Transitions = _fade; Blend = 1; }
    }
    public override void Render(DrawingContext context)
    {
        var blend = Math.Clamp(Blend, 0, 1);
        if (_previous is { } previous && blend < 1)
            using (context.PushOpacity(1 - blend)) Draw(context, previous);
        if (Source is { } current)
            using (context.PushOpacity(blend)) Draw(context, current);
        if (blend >= 1) _previous = null;
    }
    private void Draw(DrawingContext context, IImage image)
    {
        var size = image.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var sx = Bounds.Width / size.Width; var sy = Bounds.Height / size.Height;
        var scale = Stretch == Stretch.UniformToFill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var width = Stretch == Stretch.Fill ? Bounds.Width : Stretch == Stretch.None ? size.Width : size.Width * scale;
        var height = Stretch == Stretch.Fill ? Bounds.Height : Stretch == Stretch.None ? size.Height : size.Height * scale;
        var target = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        context.DrawImage(image, new Rect(size), target);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is not { } source) return default;
        var size = source.Size;
        if (size.Width <= 0 || size.Height <= 0) return default;
        var scale = Math.Min(availableSize.Width / size.Width, availableSize.Height / size.Height);
        if (double.IsInfinity(scale)) scale = 1;
        return new Size(size.Width * scale, size.Height * scale);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnDetachedFromVisualTree(e); Transitions = null; Blend = 1; _previous = null; }
}
