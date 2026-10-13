using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Musync.App.Controls;

// The ItemsControl can contain an entire library. Only query/download artwork
// whose row intersects the actual ScrollViewer viewport.
public sealed class TrackArtworkImage : Image
{
    private bool _inViewport;
    private TrackViewModel? _row;
    public TrackArtworkImage()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _inViewport = e.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateRequest();
        };
        DataContextChanged += (_, _) =>
        {
            _row?.SetArtworkVisible(false); _row = DataContext as TrackViewModel; UpdateRequest();
        };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateRequest(); };
    }
    private void UpdateRequest() => _row?.SetArtworkVisible(_inViewport && IsEffectivelyVisible && this.IsAttachedToVisualTree());
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _row?.SetArtworkVisible(false); _inViewport = false; base.OnDetachedFromVisualTree(e); }
}
