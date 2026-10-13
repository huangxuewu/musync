using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace Musync.App;

public partial class MainWindow
{
    private readonly Dictionary<Control, CancellationTokenSource> _reveals = [];
    private void AnimatePosterLayers()
    {
        if (_closing || !IsVisible || IsMiniPlayer || !HeroScene.IsVisible || !Classes.Contains("motionEnabled"))
        { SettlePosterLayers(); return; }
        // The poster changes as one composition: its ground changes color while
        // the independent subject arrives slightly later. Lyric rows keep their layout.
        foreach (var image in new Control[] { HeroArtwork, HeroSpotifyArtwork })
        {
            image.Transitions = null;
            image.RenderTransform = TransformOperations.Parse("translate(12px, 0px)");
            image.Opacity = .82;
            image.Transitions =
            [
                new TransformOperationsTransition { Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(420), Delay = TimeSpan.FromMilliseconds(60), Easing = new ExponentialEaseOut() },
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(280), Easing = new ExponentialEaseOut() }
            ];
            image.RenderTransform = TransformOperations.Parse("translate(0px, 0px)");
            image.Opacity = 1;
        }
    }
    private void SettlePosterLayers()
    {
        foreach (var image in new Control[] { HeroArtwork, HeroSpotifyArtwork })
        { image.Transitions = null; image.RenderTransform = TransformOperations.Parse("translate(0px, 0px)"); image.Opacity = 1; }
    }
    private void RevealPanel(Control control, int distance = 8, int milliseconds = 220)
    {
        SettleReveal(control);
        if (_closing || !IsVisible || !control.IsVisible || !Classes.Contains("motionEnabled"))
            return;
        var cancellation = new CancellationTokenSource(); _reveals[control] = cancellation;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = new ExponentialEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters =
                { new Setter(OpacityProperty, .82), new Setter(TranslateTransform.YProperty, (double)distance) } },
                new KeyFrame { Cue = new Cue(1), Setters =
                { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } }
            }
        };
        _ = RunRevealAsync(control, animation, cancellation);
    }
    private async Task RunRevealAsync(Control control, Animation animation, CancellationTokenSource cancellation)
    {
        try { await animation.RunAsync(control, cancellation.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (_reveals.TryGetValue(control, out var current) && current == cancellation) _reveals.Remove(control);
            cancellation.Dispose();
        }
    }
    private void SettleReveal(Control control)
    {
        if (_reveals.Remove(control, out var cancellation)) cancellation.Cancel();
        control.Transitions = null; control.RenderTransform = new TranslateTransform(); control.Opacity = 1;
    }
}
