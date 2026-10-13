# Changelog

## Unreleased

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
