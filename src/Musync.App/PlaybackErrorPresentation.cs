namespace Musync.App;

// Known receiver diagnostics stay intact in the audio layer and logs. The
// presentation layer supplies concise recovery text in the chosen language.
internal static class PlaybackErrorPresentation
{
    private static readonly (string Diagnostic, string Key)[] Messages =
    [
        ("Chromecast connection lost.", "SpeakerDisconnected"),
        ("Chromecast stopped playing because the audio stream failed.", "SpeakerStreamInterrupted"),
        ("Chromecast playback was stopped on the receiver.", "SpeakerPlaybackStopped"),
        ("Chromecast playback was replaced by another source.", "SpeakerPlaybackReplaced"),
        ("Chromecast did not accept the audio stream.", "SpeakerLoadFailed"),
        ("Chromecast could not load the audio stream.", "SpeakerLoadFailed"),
        ("Chromecast canceled the audio load.", "SpeakerLoadFailed"),
        ("Chromecast lost the loading music.", "SpeakerLoadFailed"),
        ("Chromecast lost the prepared music.", "SpeakerLoadFailed"),
        ("Chromecast did not identify the requested music in its load response.", "SpeakerLoadUnconfirmed"),
        ("Chromecast cannot perform the audio request in its current state.", "SpeakerRequestRejected"),
        ("Chromecast rejected Play.", "SpeakerRequestRejected"),
        ("Chromecast rejected Pause.", "SpeakerRequestRejected"),
        ("Chromecast rejected the start position.", "SpeakerRequestRejected"),
        ("Chromecast rejected the synchronization adjustment.", "SpeakerRequestRejected"),
        ("Chromecast did not report a valid playback position. Try adjusting the delay again.", "SpeakerDelayUnavailable"),
        ("Chromecast playback position timed out. Try adjusting the delay again.", "SpeakerDelayUnavailable")
    ];

    public static string Format(string diagnostic, Localizer text)
    {
        foreach (var (message, key) in Messages)
        {
            if (diagnostic == message) return text[key];
            // Coordinator errors prefix the unchanged receiver name. Match
            // only the complete diagnostic after its separator, not substrings.
            if (diagnostic.EndsWith(": " + message, StringComparison.Ordinal))
                return diagnostic[..^message.Length] + text[key];
        }
        const string rejected = "Chromecast rejected the audio request: ";
        var index = diagnostic.LastIndexOf(rejected, StringComparison.Ordinal);
        if (index == 0 || index > 1 && diagnostic.AsSpan(index - 2, 2).SequenceEqual(": "))
            return diagnostic[..index] + text["SpeakerRequestRejected"];
        return diagnostic;
    }
}
