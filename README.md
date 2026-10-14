# Musync

A Windows music player that groups speakers into saved links, with independent volume and delay controls.

![Musync player](docs/images/player.png)

## Download and run

Download **Musync-1.0.1-win-x64.zip** from [GitHub Releases](https://github.com/huangxuewu/musync/releases/tag/v1.0.1), extract it, and open **Musync.exe** inside the extracted folder. Keep the accompanying files together. This portable Windows x64 package includes the .NET runtime, the AirPlay helper's Node runtime and the YouTube radio helpers. No SDK installation is required to run it.

The executable is currently unsigned. Settings, playlists and cached artwork are stored in `%LOCALAPPDATA%/Musync`; imported music stays in its original folders. The application does not start playback automatically.

YouTube radio uses an unmodified [FFmpeg](https://ffmpeg.org/) LGPL v3 shared build. Its license texts are included in `RadioTools`; matching source and build recipes are available in **FFmpeg-Sources-1.0.1.zip** on the same release page. FFmpeg's shared libraries remain replaceable.

## Features

- Local folder imports create playlists using folder names; nested folders become separate playlists. WAV, MP3, FLAC, AAC, M4A, WMA and AIFF decoding uses Windows codecs.
- Saved speaker links combine Windows/Bluetooth outputs, supported AirPlay receivers and Chromecast devices. Bluetooth speakers must first be paired in Windows.
- Each linked speaker has its own volume and delay adjustment. Playback preparation waits for receivers before releasing the music; remaining offsets can be calibrated manually.
- Spotify search, playback control, artwork and volume use the official Web API and Spotify Connect.
- Saved YouTube video/live radio stations play through the active speaker link using a shared buffered audio timeline.
- Music recognition and artwork caching has a configurable 500 MB default capacity and cleans unused cache files when songs or playlists are removed.
- Matching local `.lrc` files provide three-line synchronized lyrics.
- Pinned favorites, editable playlists, a mini player, smooth progress feedback and occasional layered portrait transitions.
- English, Simplified Chinese and Japanese; choose the language in Settings. Simplified Chinese uses embedded Noto Sans SC.

In the unreleased UI, adjacent local lyric lines smoothly move into the center highlight and then into the previous-line role. Large jumps and paused seeks show the latest text immediately. The movement follows the Windows animation preference and settles when the poster is hidden or the mini player is open.

The unreleased link panel offers 1, 10 and 100 ms adjustment steps for the delay buttons and keyboard arrows. Directly entered millisecond values are preserved, and the active link applies changes during playback. The step is shared by the open editing session; saved speaker delays persist independently. **Auto calibrate** opens a microphone and playback-source selector; opening the panel does not begin recording. The listening test remains available for manual tuning.

To calibrate, select a saved link containing at least two online, unmuted speakers, place the microphone at the listening position and choose **Start test**. Musync pauses music, prepares the real output paths, plays distinct paired reference/target signals and analyzes their relative arrival times. Cancellation closes the test outputs and microphone; music resumes only when you press Play. Captured audio stays in memory and is never saved or uploaded; temporary generated probe files are removed after the test. Unclear signals, comparable echoes, unstable timing, capture gaps, clipping or out-of-range delays prevent applying the result and offer a retry.

Applying a reliable result saves total speaker delays separately for local files, network radio and Windows audio capture. The active source uses its matching profile, and the delay controls still allow fine adjustment. Changing the linked device set or protocol invalidates the profile; Reset delays clears the current source's profile. Microphone capture starts only after an explicit test request, never at application startup. Physical Onkyo/HomePod calibration and long-term drift remain unverified. Codec tests allow a 2 ms measurement tolerance; they do not establish real speaker accuracy or sample-accurate synchronization.

During preparation, calibration names the receiver currently being prepared and shows how many are ready. A receiver failure identifies the speaker and distinguishes preparation from test-playback failure, with a localized retry instruction. Raw protocol errors stay out of the dialog; failed tests do not apply delay recommendations.

## Spotify setup

1. Use your own Spotify Developer app and a Spotify Premium account.
2. Configure the redirect URI **http://127.0.0.1:43821/callback** in the developer dashboard.
3. Open **Settings → Spotify**, enter the public Client ID and select Connect. No client secret is needed.
4. Authorize in your browser, open Spotify on a playback device, and select that Spotify Connect device in Musync.

Spotify plays through its selected Connect device. Spotify audio is not passed into Musync's local multi-speaker engine. Developer access and account eligibility are controlled by Spotify.

## Library browsing

The search scope appears beside the search field: the library, a selected playlist, favorites, or Spotify. Local searches ignore surrounding spaces. Searching from the playlist overview opens library results; clearing a playlist search retains that playlist. Empty playlists and favorites include a Browse library action, and searches with no songs include Clear search. These browsing actions leave playback and its position unchanged.

The current song has a coral title and an activity or paused indicator in the list. Its cover shows the play/pause action on hover or keyboard focus. Clicking the current song pauses or resumes without restarting it; choosing another song starts that song. The activity graphic represents playback/preparation status, rather than measured audio levels, and becomes static when system animations are reduced. It stops when the song list or window is hidden.

## Music recognition cache

**Settings → General** provides a **Music recognition & artwork cache** limit, initially **500 MB**, with current disk usage and file count. The limit is adjustable from 50 to 10,000 MB. A background task maintains `artwork-cache/cache-index.json`, tracks file ownership and last use, and cleans expired, missing and least recently used files within the configured capacity. The index also counts toward disk usage. Local tag results remain reusable until the source file's size or modification time changes; Spotify matching results and covers remain temporary (up to one hour), with shorter retry periods for misses and failures. Disconnecting Spotify removes Spotify data while retaining independent local tag results.

Cache hits immediately persist their last-use timestamps, including a cover reused from memory, so a restart before the next background cleanup does not lose the eviction order.

The minus button on a local song removes it from the selected playlist, or from the library when viewing the library. Removing a library song also removes its playlist/favorite references. Playlist deletion and song removal clean the corresponding unused recognition/cover caches; a cover shared by other tracks is retained until its last reference is removed. Original music files remain in their folders. Returning explicitly to a retained library song may identify and cache it again.

## YouTube radio

Open **Radio** in the sidebar, paste a YouTube video or live URL, optionally name it, and select **Add**. Select a saved station to play through the active speaker link. Saved addresses survive restart and can be removed. English, Simplified Chinese and Japanese are supported. Browsing Radio alone leaves existing music playing.

The 1.0.1 portable package includes the radio helpers. For a source build, run `./tools/Prepare-RadioTools.ps1` once; it prepares checksum-pinned yt-dlp and an LGPL shared FFmpeg build in `.tools/radio`. Node.js 22+ is also required; the project build already prepares Node.js 24.21.0. Keep FFmpeg's shared libraries beside its executable.

yt-dlp supplies the encoded stream to FFmpeg through a pipe. FFmpeg produces one 44.1 kHz stereo PCM timeline for Windows/Bluetooth, AirPlay and streaming Chromecast. A bounded queue prebuffers about three seconds before preparing the receivers. Faster outputs receive an initial delay based on each transport's estimated latency; the link's manual delay controls remain available for speaker calibration. Hardware synchronization still requires listening tests and is not sample accurate.

Playback supports pause/resume, volume, and next/previous saved stations. Seeking is currently disabled for radio recordings and live streams. Videos requiring sign-in, private access or geographic access may fail; Musync does not import browser cookies. A YouTube playlist page cannot be added, but a single long music video containing a playlist can. No audio file is added to the local library; station URLs are saved and the stream queue stays in memory. This uses [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [FFmpeg](https://ffmpeg.org/), rather than the official YouTube IFrame API. Only play content you are permitted to access and use.

The current source and Debug preview additionally let the current station row pause/resume its existing stream without another metadata lookup. Startup distinguishes loading the station, preparing audio and preparing speakers; receiver readiness shows the actual ready/total count after buffering, including in the mini player. Cancellation remains available throughout startup. Rows show preparation, playing and paused states with contextual controls; their activity stops for reduced motion or hidden views. Failed stations retain a Retry action after the five-second toast expires; selecting Retry starts a fresh stream. A volume-control error on a receiver that is still playing does not offer a stream restart. These refinements are listed under Unreleased and are not included in the 1.0.1 download.

The unreleased decoder bounds a complete PCM block read to 35 seconds, so a connected helper that stops producing audio cannot leave that read waiting indefinitely. Partial bytes do not restart the deadline. Paused-consumer queue waits occur outside it, while user cancellation remains distinct from a timeout. Helper completion after audio EOF has the same waiting limit. A stream that fails offers Retry on its saved station.

## Speaker setup

The unreleased source coalesces volume, mute and delay edits independently for each receiver. While a control response is pending, only the latest edit is kept for the next update; stopping or replacing playback discards that session's queued edits. This reduces repeated requests to slow or rejecting receivers without changing the audio timeline or the link's saved values.

In the current source and Debug preview, open link setup using the top-right link icon beside Settings, create a link, select its devices, then save it. The bottom-right link icon opens each speaker's volume, mute and delay controls. Select Local music or Windows audio using the bottom-right source icon. Press Play to route to the selected link.

Each speaker shows whether it is available, connecting, waiting for synchronized start, playing or paused. Receiver errors remain visible in that speaker's row. While a playback session still has outputs, the link overview's Rescan icon searches again and retries failed routes at the current track position using the existing preparation barrier; its waiting wave respects reduced motion. Volume and delay preferences remain saved.

If every receiver fails during a local song, the current preview preserves the position from before connection cleanup, including when pausing or resuming. Rescan to rediscover the speakers, then press Play to prepare them and continue the same song. Scanning alone does not restart the ended session. Another failed retry keeps the saved position; explicitly restarting the song, choosing another song, changing source or cancelling preparation clears it. Radio retries continue to open a fresh stream, and Spotify keeps its existing playback path.

Stopped local playback also retains a small warning in the current song row after the error toast expires. Hover its state icon for recovery guidance and the original diagnostic; activate the cover with the mouse or keyboard to retry. A retry of the same interrupted song resumes from the saved position, matching the footer's Play action. Language changes preserve this state without replaying the expired toast. The warning clears when starting a new attempt or changing source, and is not applied when other linked speakers are still playing successfully.

The mini player's existing status line also retains a localized failure/retry message for the stopped local song. Hover it for the same guidance and original diagnostic shown in the song row. Changing language or receiving another operation notice does not make the song appear idle or remove its failure reason. Successful retry and source changes restore the ordinary status line.

Recognized Chromecast errors in the unreleased source include localized reasons and recovery guidance in all three interface languages. The upper configuration panel keeps the original diagnostic in its error tooltip; the compact lower controls use the localized guidance. Changing language preserves a notification's original five-second deadline, and an expired notice stays hidden. The upper link overview fits its actual rows and scrolls longer lists within a bounded viewport, including while scanning; draft settings keep their existing fixed editing area.

The unreleased source holds local decoding, PCM publication and the capture clock until receiver activation finishes, including on initial playback. Source duration and its starting position can initialize during the wait. The waiting indicator distinguishes preparing speakers from starting playback in the full and mini players; cancellation and per-speaker delay settings retain their existing behavior. Deferred speaker starts still use those delay settings. This gate is not an absolute-time playback command to each receiver, and actual speaker alignment still needs calibration.

Startup also waits for the current backend's synchronous reader/device initialization before activating receivers or declaring a paused session ready. This work runs in the background, so file opening cannot briefly reuse the previous source's clock in a completed session. A source that cannot open, returns a failed task, or fails while receivers are activating ends startup with an error rather than a playing state. The existing localized waiting wave remains visible and cancellable during initialization. Future backend implementations must establish their source timeline before the playback method returns its task, or complete asynchronous source preparation before this step.

In the unreleased source, the bottom-right link icon also retains an amber attention indicator while a device in the active routed link has a problem. Its tooltip and accessible name report the affected-device count. Opening the overview reveals the first affected speaker and its controls; the indicator clears when the device recovers. Unrelated devices and Spotify playback do not contribute to this indicator.

The unreleased source keeps Chromecast status errors attached to the loaded music. When a stream reloads, delayed status errors from its previous playback are ignored, while failures of the new playback remain visible even if they arrive before the load reply.

Chromecast request rejections in the unreleased source also belong to their requesting operation. Late replies cannot fail a newer song, and a rejected playback control can be retried without marking a healthy stream as failed. Media controls retain the accepted playback ID and receiver transport instead of following the last broadcast; position reads select that playback from multi-session replies. Loading completion uses fresh cancelable status requests. These paths are verified with a local TLS receiver, without sending audio to physical speakers; current Onkyo startup and synchronization timing have not been remeasured.

The unreleased source also recognizes when another sender stops or replaces Musync's accepted Chromecast playback. That speaker retains an error indication while unaffected outputs continue; Musync revokes its own published file or stream and avoids sending further media or volume commands to the replaced playback. It does not reclaim the receiver automatically. Use Rescan or start playback explicitly to retry. Normal track completion, Musync's own Stop, and old or unrelated media-session messages do not report this loss of control.

Notifications containing multiple playback states are processed in full. Multi-session LOAD replies identify the requested content before Musync accepts its playback ID; ambiguous replies fail explicitly. The maintained [SharpCaster source dependency](third-party/SharpCaster/README.md) pins upstream 3.0.0, preserves its MIT license and documents the single notification-delivery patch. Loopback tests verify mixed-session broadcasts and load replies; physical receiver behavior remains subject to device testing.

For finite local files sent to Chromecast, validated MP3 files use their original bytes without re-encoding. Multiple receivers share a read lease that keeps the source intact until playback stops. Other file formats retain the compatible MP3 preparation path. The local file server supports GET, HEAD and byte-range probes. The waiting indicator includes the selected receivers while the source is being prepared; actual receiver startup time and remaining latency still require hardware testing.

Network receivers must be reachable from the PC on the same network. If Windows requests firewall access, allow it for the private network used by your speakers. Musync does not change firewall rules automatically.

A positive per-speaker delay holds that speaker back; add delay to outputs that sound earlier. Chromecast and AirPlay buffering varies by receiver and network. Synchronization is manually adjustable and is not guaranteed to be sample accurate. AirPlay devices that require PIN/password pairing are not currently supported. HomePod and Onkyo compatibility depends on model, firmware and network configuration.

For Windows audio capture, select the playing Windows endpoint in Settings, then choose Windows audio as the source. That endpoint is excluded from routed outputs to avoid feedback.

macOS support is planned after the Windows version is stable; 1.0.1 contains the Windows implementation only.

## Build from source

Run `./build.ps1` in PowerShell to build Release and run the core, UI and AirPlay helper tests. Run `./build.ps1 -Publish` to also produce the self-contained Windows x64 ZIP and its SHA-256 file in `artifacts`. Run `./run.ps1` to open the current package or build the app from source.

The build script downloads checksum-pinned .NET 10.0.401 and Node.js 24.21.0 runtimes to the local `.tools` directory and restores the locked AirPlay helper dependencies. Build outputs, local profiles and development diagnostics are excluded from Git.

## Project structure

- `src/Musync.App`: Avalonia desktop UI and presentation.
- `src/Musync.Core`: audio timeline, routing, library, lyrics and Spotify integration.
- `src/Musync.Platform.Windows`: WASAPI, Media Foundation decoding and encoding, protected token storage.
- `src/Musync.Network`: discovery, AirPlay and Chromecast outputs.
- `src/Musync.AirPlayBridge`: Node helper for supported AirPlay 2 devices.
- `tests`: audio, transport and headless UI tests.

See [CHANGELOG.md](CHANGELOG.md), [release-validation.json](release-validation.json) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). The portable package includes dependency license texts. No proprietary MultiRoom Audio code or assets are included.
