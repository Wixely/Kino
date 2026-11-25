# Decision: MVP implementation choices

- Status: Proposed, implemented in the first MVP build; awaiting user confirmation
- Date: 2026-11-25
- Owner: Wixely
- Source: Agent implementation of the first vertical slice
- Follows: [Recording controls and final rendering](2026-11-24-recording-and-rendering.md)

These choices answer open questions in the [implementation plan](../plan.md) so that a working MVP could be built. Each can be changed; a follow-up record should replace this one when the user confirms or revises them.

## One key per action (confirmed by the user, 2026-11-25)

The user corrected the earlier four-key reading of the shortcuts. Each action has its own key, so no mode or state-dependent meaning is needed:

| Key (default) | Action | Applies when |
| --- | --- | --- |
| Ctrl+Numpad1 | Mark the first screenshot corner | Idle or selecting (switches a video selection to a screenshot) |
| Ctrl+Numpad2 | Mark the second corner, capture and copy | Selecting a screenshot |
| Ctrl+Numpad3 | Save the last screenshot, copy its path | Idle |
| Ctrl+Numpad4 | Mark the first video corner | Idle or selecting (switches a screenshot selection to a video) |
| Ctrl+Numpad5 | Mark the second corner, start recording | Selecting a video |
| Ctrl+Numpad6 | Stop recording, ask whether to include audio | Recording |
| Esc | Cancel a selection | Selecting |

Keys pressed outside the phase they apply to are ignored. Escape is registered only during a selection so it keeps working in other applications. Settings files from the four-key scheme have their shortcut bindings replaced by these defaults when loaded (`ShortcutsVersion` 2); other settings are kept.

## Finishing: an audio prompt

Stopping opens the finishing screen with two primary buttons, **Save with audio** and **Save without audio**. Either one renders straight away and copies the saved file's path to the clipboard. "With audio" uses the tracks selected on that screen, which start as the tracks chosen in Settings (default: system audio and microphone); it is hidden when no usable track was captured. Format, track order and file name remain adjustable on the same screen before clicking.

The "When recording stops" setting (`AfterRecording`: Ask, SaveWithAudio, SaveWithoutAudio; default Ask) can skip the question. An automatic save keeps the window hidden, uses the default format and track selection, and confirms with a tray notification; "with audio" falls back to a silent video when no usable track was captured. If saving cannot start or fails, the window opens on the finishing screen with the reason, and the recording is kept.

When the question opens the window, Kino remembers whether it was in the tray, minimised or open, and returns it to that state after a successful save. A failed save leaves the window open. CupriFace 0.46 does not run posted document work while its window is minimised, so showing the window for the question and starting an automatic save happen outside the document thread. Minimising is queued through the window's message queue, because minimising synchronously during a CupriFace update crashes its next frame.

## Corner preview

The selection panel is pinned to the bottom-right of the primary monitor's work area and shows the whole region selected so far, scaled to fit (GDI halftone scaling, so the cost depends on the panel size, not the region size). It fades while the pointer is over it, so the screen beneath stays visible.

## Compact-keyboard layout

Settings offers a second layout, Ctrl+Alt with the top-row digits 1 to 6, with the same meanings. Bindings are stored in `settings.json`; conflicting or already-registered combinations are reported on the home screen. A per-key binding editor is not part of the MVP.

## Recording modes (chosen by the user, 2026-11-25)

Three modes, selectable in Settings, in this order:

1. **JPEG delta** (default). PORTÅL's approach, ported by permission of its author: Desktop Duplication capture with the changed-rectangle reports Windows provides, then PORTÅL's delta planner (64 px tiles, merged into rectangles, with scroll detection by row and column hashes). The temporary video is a Kino frame file (`video.kframes`): a full JPEG at the start, when more than half the frame changed and every 10 seconds; otherwise a patch of scroll copies and JPEG rectangles; nothing for unchanged frames.
2. **JPEG**. The same capture and file, storing every frame as a full JPEG.
3. **Lossless**. The original GDI capture into ffmpeg's lossless H.264, described below.

The JPEG modes need no ffmpeg while recording. Comparison runs in frame order; JPEG encoding runs in parallel on up to six cores and is appended in order, so the JPEG mode keeps 30 fps at 1080p. Capture stays on the frame clock: when storage falls behind, captured frames are dropped and play as repeats, and the next change report is treated as unknown. Desktop Duplication does not include the pointer, so it is drawn on a copy of each frame with GDI and its old and new boxes are added to the change report. Rotated displays and regions spanning two adapters use GDI throughout; lost duplication (UAC, display changes) uses GDI and retries after two seconds. Rendering rebuilds every frame from the file and pipes it to ffmpeg as raw video. A file cut short by a crash reads up to its last complete record.

JPEG quality defaults to 90. JPEG adds a lossy step before the final encode; at quality 90 rebuilt frames differ from the screen by about 3 to 6 levels on average in the self-tests. Patches carry whole 64 px tile bands, so constant scrolling of full-width text costs about as much as the lossless mode; smaller tiles for recording are a possible follow-up.

## Temporary recording storage (lossless mode)

Frames are captured with GDI at a fixed frame clock and piped as raw BGRA to system ffmpeg, which writes lossless RGB H.264 (`libx264rgb`, `-qp 0`, ultrafast) into Matroska. Matroska stays readable when the process is interrupted, and lossless staging means the final render is the only lossy step. When capture falls behind, the previous image is repeated so the video keeps wall-clock duration; the manifest records how many frames were repeated.

System audio (WASAPI loopback) and the microphone (WASAPI shared mode) are written to separate WAV files on the same clock. Gaps in delivery are filled with silence, because loopback capture delivers nothing while no application plays sound. WAV headers are rewritten every two seconds so a crash leaves readable audio. A JSON manifest beside the files records the region, frame counts, track offsets and state.

Measured on the development machine (static desktop content, 1920 × 1080 at 30 fps, 20 seconds): 602 frames written, none repeated. Busy content and longer recordings have not been measured yet.

## Rendering, validation and cleanup

The final render uses system ffmpeg: H.264 (CRF 20) with AAC for MP4 and MKV, VP9 (CRF 32) with Opus for WebM. Selected tracks are mapped as separate streams in the chosen order, titled with their source name, downmixed to stereo. Output is written to a hidden partial file beside the destination, checked with ffprobe (one video stream, exactly the selected audio streams, in order, with the expected codecs), then renamed into place.

After a validated render, the whole temporary session is deleted: the staging video as well as every audio source, selected or not. This settles the open temporary-video cleanup question in favour of deletion, since the staging video has no use once the delivery file exists. A failed or cancelled render keeps everything and marks the session for retry or discard.

## Recovery

Sessions left from a crash or a failed render are listed on the home screen as unfinished recordings, with Finish and Discard actions. An interrupted recording can be finished from whatever the staging file holds.

## FFmpeg absent

Screenshots work without ffmpeg, and so does recording in the JPEG modes; the recording waits as unfinished until ffmpeg is available to save it. In the lossless mode, video selection is refused with an explanation when ffmpeg or ffprobe is missing, or when the build lacks `libx264rgb`. Formats that the installed build cannot write are reported before rendering. The managed H.264 backend (milestone M3) is not implemented; no reduced mode is offered.

## Start in the tray

CupriFace 0.46 has no start-hidden option and only a minimal tray menu, so Kino runs its own notification-area icon and menu on a dedicated shell thread and hides the CupriFace window at startup. Closing the window hides it; Quit from the tray stops any active recording (keeping it as unfinished) and exits.
