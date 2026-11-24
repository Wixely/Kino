# Decision: Recording controls and final rendering

- Status: Accepted
- Date: 2026-11-24
- Owner: Wixely
- Source: User follow-up answers to Kino planning questions
- Supersedes: Open recording, retention and upload choices in [the initial workflow decision](2026-11-24-capture-workflow-and-audio.md)

## Decision

Video capture uses keys 2, 3 and 4, with key 3 starting recording. Show a red frame around the recording region by default and allow it to be disabled. The working shortcut interpretation is Ctrl+Numpad2 for the first corner, Ctrl+Numpad3 for the second corner and start, and Ctrl+Numpad4 for stop. Mode switching remains to be specified.

Immediately after capture, let the user choose audio inclusion, individual tracks and their order before rendering the final video. Support multiple separate audio tracks. The earlier silent-output default remains in force and is configurable in Settings.

Support MKV, MP4 and WebM output. Final H.264 or other delivery encoding follows the user's finishing choices. Temporary capture storage is a separate implementation decision.

Delete temporary audio after the final render succeeds, including unselected tracks. Preserve temporary sources if rendering fails or is cancelled so the user can retry or discard them. Validate and commit the final file before cleanup. Temporary-video cleanup policy remains open.

Uploads are outside the MVP. Later, support configurable destinations using a bounded set of protocol adapters, including HTTP POST and response parsing. Specific image-host integrations and protocol configuration details can follow during that work.

## Consequences

The recording pipeline needs temporary storage, synchronized source tracks and a finishing screen before delivery rendering. Selected sources remain separate tracks in user-selected order. A successful silent export deliberately discards the temporary audio, so it cannot be restored later from Kino's source files.

The optional red frame must be excluded from captured pixels. Disabling it leaves tray recording status available.

Qualify codec/container combinations and track ordering for all three output formats. Managed H.264 alone does not cover the full required output matrix; keep fallback capability limits visible and do not silently remove requested tracks or change formats.
