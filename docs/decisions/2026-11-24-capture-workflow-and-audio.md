# Decision: MVP capture workflow and audio handling

- Status: Accepted
- Date: 2026-11-24
- Owner: Wixely
- Source: User answers to Kino planning questions
- Supersedes: Initial provisional editing, audio and platform scope in the project plan
- Follow-up: [Recording controls and final rendering](2026-11-24-recording-and-rendering.md) resolves the open retention, track, format and upload-scope choices below. This record preserves the earlier decision state.

## Decision

Select the capture region before capturing an image or starting a video. The MVP has no post-capture crop or trim tools. Windows ships first; Linux follows.

Preserve the user's Cliptrix interaction: Ctrl+Numpad1 marks the top-left corner under the pointer, then Ctrl+Numpad2 marks the bottom-right corner and immediately copies the resulting screenshot. Control can remain held while the mouse moves. Between corner selection steps, show a small preview at the bottom-right of the screen. This flow requires no mouse-button clicks, dragging or full-screen overlay.

Ctrl+Numpad3 saves the image and copies its file path. Direct-upload shortcuts with a completion notification and URL copying are desired workflow references; their providers and MVP inclusion remain undecided.

Video recording captures audio sources so the user can choose what to include after recording. The finished video contains no audio by default, and Settings can change that inclusion default. The implementation must preserve the source audio independently of the silent export choice. System output plus the selected microphone is the current interpretation of the requested sources.

Add crop and trim later. Also add quick image/video format conversion later, using FFmpeg commands for video conversion.

## Consequences

The MVP needs an audio-selection finishing step despite deferring editing tools. Source tracks need synchronized timestamps and retention until the finishing choice is made. Retention after export still requires a decision. A silent default does not justify omitting source audio from the managed backend.

The two-corner capture path is the primary interaction acceptance test. Additional selection modes must preserve its speed and click-free operation.

## Open decisions

Video start/stop gestures, post-export source retention, upload scope/providers, compact-keyboard bindings and managed-backend capability limits remain open.
