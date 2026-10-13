namespace Musync.App;

public sealed partial class MainViewModel
{
    private int _delayStepMs = 10;
    public IReadOnlyList<Choice> DelaySteps { get; private set; } = [];
    private void RefreshDelaySteps()
    {
        DelaySteps = new[] { 1, 10, 100 }.Select(step => new Choice(step.ToString(), string.Format(Text["DelayStepValue"], step))).ToArray();
        Raise(nameof(DelaySteps)); Raise(nameof(DelayStep));
    }
    public Choice DelayStep
    {
        get => DelaySteps.First(c => c.Id == _delayStepMs.ToString());
        set
        {
            if (value is null || !DelaySteps.Any(c => c.Id == value.Id)
                || !int.TryParse(value.Id, out var step) || step == _delayStepMs) return;
            _delayStepMs = step; Raise();
            foreach (var speaker in Speakers.Concat(LinkedSpeakers).Concat(DraftSpeakers).Distinct())
                speaker.DelayStepMs = step;
        }
    }
    public bool HasDelayControls => VisibleLinkSpeakers.Any(s => s.ControlsVisible);
}
