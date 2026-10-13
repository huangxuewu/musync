# Changelog

## Unreleased

- Chromecast media-status errors follow the playback session returned by the active LOAD. Delayed errors and replies from earlier playback no longer fail the new music; matching errors received before the LOAD response are retained until that response identifies the session. Stopping prevents late replies from rearming feedback.
- The bottom-right link icon retains a localized device-attention indicator after the five-second toast disappears. Opening the overview brings the first affected speaker into view, including its volume and delay controls; reconnecting clears the indicator. Only devices in the active routed link contribute, and Spotify mode keeps its existing playback controls.
- Speaker volume, mute and delay edits share one update loop per receiver and playback session. A slow response retains only the latest pending edit instead of queuing every slider event; a stopped or replaced session drops its pending edits. Failed updates retain the existing error/retry behavior while other receivers remain independently controllable.
- Radio startup reports loading the station, preparing audio and preparing speakers separately. Receiver preparation shows the actual ready/total count after metadata and buffering finish; the station row and mini player follow the same phase. Cancellation remains available throughout startup.
- Radio PCM reads have a 35-second deadline for each complete block, including partial reads. Normal consumer backpressure and time between completed blocks do not use that deadline; cancellation stays cancellation. Waiting for helper completion after audio EOF is also bounded, and disposal observes the retained closing tasks. A terminated radio stream uses the station's existing Retry action.
- Radio playback failures remain on the affected station with a localized Retry action after the five-second toast expires. Activating Retry starts a fresh stream; playback errors stay separate from Add-station validation. Volume-control errors on a surviving receiver do not offer a restart, and source changes reject late failures from the old session.
- Long-running yt-dlp and FFmpeg stderr readers keep draining after 65,536 characters, retaining a bounded Unicode-safe tail instead of failing at the capture limit and leaving a helper's pipe blocked. The separate startup metadata size limit remains in place. Windows pipe regressions cover a one-megabyte log flood and cancellation while no data is arriving.
- Clicking the current YouTube radio row pauses/resumes its existing stream instead of repeating metadata lookup and restarting the decoder. Station rows show preparation/playback activity, localized status and contextual play/pause actions. The active preparing row stays readable while duplicate requests are disabled; activity stops for reduced motion, hidden/minimized windows, the mini player and other pages. Explicit station selection for next/previous continues to start the chosen station.

## 1.0.1 — 2026-10-13

- YouTube radio accepts saved video/live addresses and routes one buffered audio timeline through the active Windows/Bluetooth, AirPlay and Chromecast speaker link. It includes pause/resume, volume and station navigation, with bundled checksum-pinned yt-dlp and LGPL shared FFmpeg helpers. Seeking is disabled; physical multi-speaker radio synchronization has not been retested for this release.

- Playback visual tests verify contrasting foreground and background pixels for navigation and transport icons in English, Simplified Chinese and Japanese. Animated headless captures advance pending render frames before inspection, avoiding incomplete snapshots being mistaken for a language or palette defect.
- Song rows highlight the current track, show preparing/playing activity or a paused icon, and expose a play/pause overlay on hover or keyboard focus. Activating the current row pauses/resumes at its existing position; Spotify resumes its queue without resending the track URI. Row activity stops for reduced motion, minimized/hidden windows, the mini player and hidden song lists. Busy rows do not queue duplicate playback requests.
- Search identifies the active playlist or favorites collection, preserves its scope when cleared, and tolerates surrounding spaces. Searching from the playlist overview reveals library song results instead of leaving them hidden. Empty playlists and favorites offer a localized library action; empty searches offer Clear search with focus restored to the search field, without interrupting playback.
- Chromecast local-file preparation validates MP3 frames and serves the original MP3 bytes through a shared read lease, avoiding another lossy encoding pass. Other formats retain the existing compatible MP3 encoding path. File routes now support HEAD and ranged probes; source preparation displays the selected receiver count before handshakes start.
- Cache hits persist last-use timestamps immediately, including covers reused from memory, so restarting before background maintenance preserves the correct eviction order when capacity is reduced.
- Linked speakers report Available, Connecting, Waiting for sync, Playing and Paused from the playback barrier's per-device readiness. Connection failures appear inline while other receivers prepare, and small windows expose more of the device list. A direct Rescan action and waiting wave let the normal link overview retry failed routes without opening configuration. Rediscovery updates status color and explanations; cancellation is no longer recorded as a failed receiver handshake.
- Settings groups General, Audio and Spotify controls in a compact frosted panel with a fixed Save action. Unconnected Spotify actions open the setup section directly; closing settings restores keyboard focus without interrupting playback.
- Music recognition and artwork caches have a configurable 500 MB default capacity, an atomic ownership index, background expiry/unused-file maintenance and least-recently-used eviction. Local tags survive restart and Spotify disconnection; source file size/timestamp changes invalidate their cached identification.
- Local songs can be removed from playlists or the library using a Lucide minus action. Song/playlist removal cleans unreferenced cache files and retains shared covers; original audio files stay in place. Settings shows cache usage and the capacity control in all three languages.
- Spotify attribution icons use high-quality bitmap scaling so the official mark remains clear at 24 px.

- The frosted lyric sheet reveals more of the poster's cropped color disc and widened diagonal ribbon, with sharp text above the blur and a single glass edge. Navigation, music lists and playback controls retain their existing layout.
- AirPlay helper control queues are independent for each receiver, so a slow handshake or volume reply cannot delay another speaker's setup or close. Concurrent handshakes wait for one shared timing clock; pending sessions retain that clock and peer until their own cleanup completes.
- AirPlay helper instances own their requests, diagnostics and receiver sessions. Replacing a stopped helper retains old receivers' failures and prevents their PCM, volume or close commands from being sent to the replacement; both output readers are cancelled and awaited during cleanup.
- AirPlay preparation, PCM pipe writes and volume requests honor the playback session's cancellation. Stop or Pause can cancel pending sends and control replies, and helper session closure has a five-second wait limit. Late replies to cancelled requests cannot become errors in a newer session.
- Discovery queries a receiver's missing IPv4 address when service details arrive separately, retries within the existing three scan rounds, and avoids repeated queries for shared or already resolved hosts.
- The sidebar uses a larger wave wordmark, plum selection for primary navigation, compact playlist icons and localized track counts. Playlist creation and settings stay reachable while the song lists scroll, with restrained hover and disclosure motion.
- Invalid Unicode character escapes in a receiver's friendly name remain visible instead of interrupting discovery for all speakers; valid Unicode names and supplementary characters still decode.
- A translucent frosted panel separates the poster title and synchronized lyrics from the colorful geometry behind them; background colors remain visible through a bounded blur while text stays sharp.
- Music-source changes wait for the previous Play/Pause and Stop to finish before allowing new playback. Late responses cannot restore an old source's track, progress or error state.
- Switching away from Spotify cancels an obsolete device lookup; changing sources during receiver preparation cancels that preparation without starting audio.
- A waiting wave replaces the music-source icon during a transition, with localized status in the mini player. Motion stops when hidden, minimized or reduced motion is enabled.
- Play and seek controls reflect the pending transition, and shutdown cancels and settles it.
- A playback poll finishing slow disconnected-receiver cleanup cannot restore an old source's progress or error toast after a source change or shutdown.
- Healthy outputs retain progress and lyric updates while a disconnected receiver is being cleaned up, without overlapping receiver checks. Successful local playback retries immediately clear obsolete speaker status.
- Confirmed Chromecast transport loss skips unreachable Stop and volume-restore requests while revoking local media. Media errors on a live connection retain normal cleanup, and file playback bounds session disposal; disposal also cancels pending Cast requests and the heartbeat after an incomplete connection.

## 1.0.0 — 2026-10-13

First Windows x64 release.

- Saved speaker links with per-device volume and sync delay controls for Windows/Bluetooth, supported AirPlay receivers and Chromecast.
- Chromecast synchronization adjustments briefly retry missing or invalid receiver positions, apply the latest requested delay, and cancel pending adjustments when playback stops.
- Late speaker-control failures from stopped or replaced playback sessions no longer overwrite the current player's status.
- Successful speaker-control retries clear their own error and restore playback status without hiding another receiver's failure, a decoder error or a newer notification; Spotify playback no longer inherits an earlier local-control error.
- Failed network rescans retain the known speaker list and healthy playback, with a localized retry message that stays visible beside the link popup.
- Speaker discovery keeps progress, lyrics, transport controls and Spotify state updates responsive. The link editor shows a localized waiting wave, stops its motion when hidden or reduced motion is enabled, and cancels outstanding discovery waits on shutdown.
- Link selection is disabled while speakers are being prepared, keeping the displayed link consistent with the current routing; cancellation restores selection.
- Local folder playlists, nested-folder imports, FLAC decoding and local synchronized LRC lyrics.
- Spotify Connect playback controls, search, now-playing artwork, progress and volume, plus cached artwork matching for library tracks.
- Refined layered poster UI, compact search, pinned favorites, playlist add/remove/collapse controls and a mini player.
- Virtualized song rows keep large libraries from creating every row at once, retaining viewport-based artwork loading and the existing scrollbar-free layout.
- Occasional photographic portrait transitions and embedded Noto Sans SC for Simplified Chinese.
- English, Simplified Chinese and Japanese interface languages, smooth playback progress and five-second toast notifications.
- Self-contained portable package with bundled .NET, the AirPlay helper's Node runtime and third-party notices.

Spotify uses its selected Connect device; local and captured Windows audio use Musync's multi-speaker routing. Network buffering still requires per-device adjustment. PIN/password AirPlay pairing and macOS are deferred.
