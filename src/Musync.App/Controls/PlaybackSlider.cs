using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace Musync.App.Controls;

// Keep the real playback position separate from the interpolated thumb value.
// Pointer and keyboard edits always use the ordinary, immediate Slider behavior.
public sealed class PlaybackSlider : Slider
{
    public static readonly StyledProperty<double> PlaybackPositionProperty =
        AvaloniaProperty.Register<PlaybackSlider, double>(nameof(PlaybackPosition));
    public static readonly StyledProperty<bool> IsAdvancingProperty =
        AvaloniaProperty.Register<PlaybackSlider, bool>(nameof(IsAdvancing));
    public static readonly StyledProperty<long> ProgressEpochProperty =
        AvaloniaProperty.Register<PlaybackSlider, long>(nameof(ProgressEpoch));
    public static readonly StyledProperty<bool> MotionEnabledProperty =
        AvaloniaProperty.Register<PlaybackSlider, bool>(nameof(MotionEnabled), true);

    private readonly Transitions _progressTransition =
    [new DoubleTransition { Property = ValueProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = new LinearEasing() }];
    private bool _interacting, _hasPosition, _skipNextTransition;
    private double _lastPosition;
    protected override Type StyleKeyOverride => typeof(Slider);
    public double PlaybackPosition { get => GetValue(PlaybackPositionProperty); set => SetValue(PlaybackPositionProperty, value); }
    public bool IsAdvancing { get => GetValue(IsAdvancingProperty); set => SetValue(IsAdvancingProperty, value); }
    public long ProgressEpoch { get => GetValue(ProgressEpochProperty); set => SetValue(ProgressEpochProperty, value); }
    public bool MotionEnabled { get => GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }

    internal void SuspendProgressMotion()
    {
        _interacting = true; Transitions = null;
    }
    internal void ResumeProgressMotion()
    {
        _interacting = false; _skipNextTransition = true;
    }
    internal void CancelProgressInteraction()
    {
        ResumeProgressMotion(); Transitions = null;
        SetCurrentValue(ValueProperty, PlaybackPosition);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProgressEpochProperty || change.Property == MotionEnabledProperty
            || change.Property == MaximumProperty || change.Property == MinimumProperty
            || change.Property == IsAdvancingProperty && !IsAdvancing)
        {
            Transitions = null; _skipNextTransition = true;
            if (!_interacting) SetCurrentValue(ValueProperty, PlaybackPosition);
        }
        if (change.Property != PlaybackPositionProperty) return;
        var target = Math.Clamp(PlaybackPosition, Minimum, Maximum);
        var step = target - _lastPosition;
        var animate = _hasPosition && !_interacting && !_skipNextTransition && MotionEnabled
            && IsAdvancing && step is > 0 and <= 1.5;
        _lastPosition = target; _hasPosition = true;
        if (_interacting) return;
        Transitions = animate ? _progressTransition : null;
        SetCurrentValue(ValueProperty, target);
        _skipNextTransition = false;
    }
}
