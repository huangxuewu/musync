using System.Collections.Specialized;
using System.ComponentModel;

namespace Musync.App;

public sealed partial class MainViewModel
{
    private readonly HashSet<SpeakerViewModel> _linkedFeedbackSpeakers = [];
    public bool HasLinkIssue => RoutingAvailable && HasActiveLink && LinkedSpeakers.Any(s => s.HasIssue);
    public string LinkControlName
    {
        get
        {
            if (!HasLinkIssue) return Text["Links"];
            var count = LinkedSpeakers.Count(s => s.HasIssue);
            return string.Format(Text[count == 1 ? "LinkAttentionOne" : "LinkAttentionMany"], count);
        }
    }
    public string LinkControlHint => HasLinkIssue ? LinkControlName : LinkSummary;

    private void LinkedSpeakersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var old in _linkedFeedbackSpeakers.Where(s => !LinkedSpeakers.Contains(s)).ToArray())
        {
            old.PropertyChanged -= LinkedSpeakerFeedbackChanged;
            _linkedFeedbackSpeakers.Remove(old);
        }
        foreach (var speaker in LinkedSpeakers)
            if (_linkedFeedbackSpeakers.Add(speaker)) speaker.PropertyChanged += LinkedSpeakerFeedbackChanged;
        RefreshLinkFeedback();
    }
    private void LinkedSpeakerFeedbackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpeakerViewModel.HasIssue)) RefreshLinkFeedback();
    }
    private void RefreshLinkFeedback()
    {
        if (_disposed) return;
        Raise(nameof(HasLinkIssue)); Raise(nameof(LinkControlName)); Raise(nameof(LinkControlHint));
    }
    private void DetachLinkFeedback()
    {
        LinkedSpeakers.CollectionChanged -= LinkedSpeakersChanged;
        foreach (var speaker in _linkedFeedbackSpeakers) speaker.PropertyChanged -= LinkedSpeakerFeedbackChanged;
        _linkedFeedbackSpeakers.Clear();
    }
}
