# Kino

<p align="center">
  <img src="docs/artwork/kino-amber-crystal.png" alt="Kino amber crystal snowflake" width="280">
</p>

Kino is a screenshot and screen-recording application for Windows, with Linux support to follow. It uses CupriFace, stays in the system tray and captures with keyboard shortcuts, without clicking or dragging.

## Status

Early MVP for Windows 10 1809 and later (x64). Linux is planned. [Implementation choices](docs/decisions/2026-11-25-mvp-implementation-choices.md) record how the MVP was built and what the owner has confirmed.

## Download

Get the latest build from [Releases](https://github.com/Wixely/Kino/releases/latest):

- `kino-vX.Y.Z-win-x64.exe` is the whole application in one self-contained file. Put it anywhere and run it; there is no installer, and .NET does not need to be installed.
- `kino-vX.Y.Z-win-x64.zip` holds the same executable with the licence and third-party notices.
- Each has a `.sha256` file. In PowerShell, `Get-FileHash .\kino-vX.Y.Z-win-x64.exe` should print the same hash.

The executable is not code-signed yet, so Windows SmartScreen may warn on first run; choose **More info → Run anyway**. To save videos, install ffmpeg (for example `winget install Gyan.FFmpeg`) so `ffmpeg` and `ffprobe` are on PATH, or set the ffmpeg path in Settings. Screenshots work without it.

To keep settings and temporary recordings beside the executable (portable mode), create a folder named `KinoData` next to `Kino.exe`.

## Using Kino

Kino starts the way it was left: in the notification area (the first time), minimised to the taskbar, or open. Left-click the tray icon to open the window; right-click for the menu, which includes Stop recording, a shortcut on/off toggle and Quit. Closing the window hides it again.

| Shortcut | What it does |
| --- | --- |
| Ctrl+Numpad1 | Mark the first corner of a screenshot at the pointer |
| Ctrl+Numpad2 | Mark the second corner, capture the screenshot and copy it |
| Ctrl+Numpad3 | Save the last screenshot as PNG and copy its path |
| Ctrl+Numpad4 | Mark the first corner of a video |
| Ctrl+Numpad5 | Mark the second corner and start recording |
| Ctrl+Numpad6 | Stop recording and choose whether to include audio |
| Esc | Cancel a selection, leaving the clipboard unchanged |

Keep Control held and move the pointer between key presses. While selecting, a panel at the bottom-right of the main monitor shows the whole region selected so far, scaled to fit, with its size; it fades while the pointer is over it. Settings has a Ctrl+Alt+1…6 layout for keyboards without a numeric keypad.

Settings → Capture → Recording mode chooses how video is captured and kept until it is saved:

| Mode | Capture | Temporary video | Needs ffmpeg to record |
| --- | --- | --- | --- |
| JPEG delta (default) | Desktop Duplication | Only what changed: JPEG patches and scroll copies, with a full frame when most of the screen changes and every 10 seconds | No |
| JPEG | Desktop Duplication | Every frame as a full JPEG | No |
| Lossless | GDI | Lossless H.264 through ffmpeg | Yes |

The JPEG modes fall back to GDI when Desktop Duplication cannot capture the region (rotated displays, a region spanning two graphics adapters) and while it is unavailable (for example during a UAC prompt). JPEG quality defaults to 90. Every mode renders the final file with ffmpeg.

Screenshots go to the clipboard as a bitmap and as PNG. Settings has separate switches for including the mouse pointer in screenshots (off by default) and in videos (on by default); both are saved. A red frame outlines the region while recording; it sits outside the recorded pixels and can be turned off. When recording stops, Kino asks whether to include audio: click **Save with audio** or **Save without audio**, and the video is rendered and its path copied to the clipboard. Settings → Output → "When recording stops" can skip the question and always save with or without audio; the window then stays hidden unless saving fails. The preview window can be turned off in Settings → Capture. The same screen can change the format (MP4, MKV or WebM), which audio tracks are included and in what order, and the file name. After a successful save, all temporary video and audio is deleted. A failed or interrupted render keeps the recording for another attempt.

### Requirements

- Windows 10 version 1809 or later, x64.
- `ffmpeg` and `ffprobe` on PATH, or an ffmpeg path set in Settings, to save videos. MP4 and MKV need `libx264` and `aac`, WebM needs `libvpx-vp9` and `libopus`; the lossless recording mode also needs `libx264rgb`. The JPEG recording modes record without ffmpeg, so a recording can be finished later once ffmpeg is available. Screenshots do not need ffmpeg.

Settings and temporary recordings are kept in `%LOCALAPPDATA%\Kino`, or in a `KinoData` folder beside `Kino.exe` when that folder exists (portable mode). Screenshots default to `Pictures\Kino` and videos to `Videos\Kino`.

## Building

Requires the .NET 10 SDK and access to the Wixely GitHub Packages feed for CupriFace.

```powershell
dotnet build Kino.slnx
dotnet test Kino.slnx
./eng/Verify.ps1     # audit, format check, Release build and tests
./eng/Package.ps1    # single-file publish, executable self-tests, release archive and checksums
```

The executable has diagnostic modes that write reports instead of starting the tray application:

- `Kino.exe --selftest-capture report.txt` captures, copies and saves a screenshot through the real workflow.
- `Kino.exe --selftest-record report.txt --seconds 5 --size 1920x1080` records the top-left of the primary monitor in each recording mode (or one, with `--mode JpegDelta|Jpeg|Lossless`), renders every format with and without audio, verifies the streams with ffprobe and removes the temporary files. For the JPEG modes it also rebuilds the last frame from the temporary video and compares it with the screen.
- `Kino.exe --snapshot page.png --page home|finish|settings|unfinished|recording` renders a page with synthetic content.

`--data-root <folder>` keeps settings and recordings out of the per-user folder.

### Continuous integration and releases

Every push to `main` and every pull request runs [CI](.github/workflows/ci.yml) on a Windows runner: format check, NuGet audit, build, tests with ffmpeg, then the published executable's self-tests. Pushing a version tag runs the [release workflow](.github/workflows/release.yml), which does the same and publishes a GitHub release with the executable, zip and checksums:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The tag sets the version (`v0.2.0-beta.1` becomes a pre-release). `./eng/Package.ps1 -Version 0.1.0` builds the same release files locally.

## What has been verified

On a Windows 10 machine with six monitors at mixed positions and ffmpeg N-91454 (2018):

- 69 unit and integration tests, including the ported delta planner, the JPEG frame file and an ffmpeg render from it, including real MP4, MKV and WebM renders of synthetic media with two audio tracks in a chosen order.
- The published single-file executable's screenshot and recording self-tests, including a region that reaches into the gap between monitors.
- The interactive build driven by synthesized hotkeys: start in the tray, single-instance activation, close-to-tray, screenshot to clipboard, save and copy path, the corner preview's position on the main monitor, record, the audio prompt, and saving without audio with the video path copied.
- 1920 × 1080 at 30 fps for 20 seconds with no repeated frames, on mostly static content.
- Each recording mode at 1920 × 1080 and 30 fps for 10 seconds over constantly scrolling text with a moving shape, on Desktop Duplication: no dropped frames in any mode. Temporary video was about 24 MB for JPEG delta, 270 MB for JPEG and 18 MB for lossless; on a still screen, JPEG delta keeps a single frame.

Not yet verified: audio capture from real devices (the test machine has no audio endpoints; the no-device path and gap filling are tested), busy-content and 30-minute recordings, display hot-plug during recording, trimmed or NativeAOT builds, and CI on GitHub-hosted runners.

## Not in the MVP

The managed H.264 backend, a per-key shortcut editor, file browse dialogs, start with Windows, Linux, crop and trim, format conversion and uploads. See the [implementation plan](docs/plan.md).

## Documentation

- [Implementation plan](docs/plan.md)
- [Development conventions](docs/development.md)
- [Initial workflow decision](docs/decisions/2026-11-24-capture-workflow-and-audio.md)
- [Recording and rendering decision](docs/decisions/2026-11-24-recording-and-rendering.md)
- [MVP implementation choices](docs/decisions/2026-11-25-mvp-implementation-choices.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## License

[MIT](LICENSE)
