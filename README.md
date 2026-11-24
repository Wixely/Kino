# Kino

Kino is a planned screenshot and screen-recording application for Windows, with Linux support to follow. It uses CupriFace and stays in the system tray, ready for quick keyboard-driven captures.

## Planned workflow

- Select a region with two pointer positions and keyboard shortcuts, without dragging or clicking the mouse.
- Copy screenshots directly to the clipboard, or save a file and copy its path.
- Record a selected region with an optional red frame showing the recording bounds.
- Capture system and microphone audio, then select and order audio tracks before rendering. Finished videos are silent by default.
- Export MKV, MP4 or WebM through system FFmpeg, with a managed H.264 alternative subject to capability validation.

Crop, trim, quick format conversion and configurable uploads follow the MVP.

## Status

This repository contains the implementation plan and decisions. Application code, builds and releases are not available yet.

- [Implementation plan](docs/plan.md)
- [Development conventions](docs/development.md)
- [Initial workflow decision](docs/decisions/2026-11-24-capture-workflow-and-audio.md)
- [Recording and rendering decision](docs/decisions/2026-11-24-recording-and-rendering.md)

The first implementation step is a Windows capture and temporary-recording spike that validates ordered audio tracks, final rendering and cleanup. Use C#/.NET 10 and CupriFace; evaluate reusable capture/input components before adding equivalent code.

## License

[MIT](LICENSE)
