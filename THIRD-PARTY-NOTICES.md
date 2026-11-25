# Third-party notices

Kino depends on the following projects. This summary is informational; the license files distributed with each dependency are authoritative.

| Dependency | Purpose | License | Source |
| --- | --- | --- | --- |
| CupriFace | Native HTML/CSS UI runtime and desktop host | MIT | <https://github.com/Wixely/CupriFace> |
| NAudio | WASAPI system-audio and microphone capture, WAV writing | MIT | <https://github.com/naudio/NAudio> |
| SkiaSharp | PNG and JPEG encoding, tray icon and corner-preview drawing | MIT | <https://github.com/mono/SkiaSharp> |
| Vortice.Direct3D11 (with Vortice.DXGI and Vortice.Mathematics) | Desktop Duplication capture for the JPEG recording modes | MIT | <https://github.com/amerkoleci/Vortice.Windows> |

The delta planner, patch format and Desktop Duplication capture are adapted from PORTÅL, a project by the same author, and are released here under Kino's MIT licence.

CupriFace has additional permissively licensed transitive dependencies, including AngleSharp, SkiaSharp, HarfBuzzSharp, Silk.NET, GLFW and SDL. NuGet restores their license metadata with the packages.

## FFmpeg

Kino does not include FFmpeg. Video recording and rendering run the `ffmpeg` and `ffprobe` executables that the user has installed, found on PATH or at a path chosen in Settings. Those executables are covered by their own license, which depends on how they were built (LGPL or GPL). Screenshots do not use FFmpeg.
