# Development conventions

## Stack and structure

Use C# on .NET 10 with top-level host statements. Windows is the first platform; Linux follows. Use CupriFace for the UI and prefer pinned versioned packages. Keep workflow policy in Kino and reusable mechanisms in independent libraries. Validate extracted libraries using a package-only consumer.

Provide repository-local VS Code launch/tasks when executable code exists. Use Windows PowerShell 5.1-compatible scripts. Do not require Node.js or Python without an explicit project decision. Keep UI assets local and preserve dependency notices.

## Publishing and trimming

Prefer a self-contained single executable with no installed .NET runtime requirement. Evaluate NativeAOT early and record concrete compatibility blockers. Do not sacrifice working capture, global shortcuts, tray behavior, audio or GPU rendering solely to reduce executable size.

Before trimming, publish and run an untrimmed baseline. Define checks covering actual functionality, backend choice and fallback behavior. Gate trimming within application projects rather than passing global publish flags into generators. Investigate trim warnings and compare trimmed and untrimmed binaries on the same machine. Test compression separately and together with trimming. Record size, cold/warm startup time and functional results before adopting release changes.

## Verification

Test geometry, state transitions, track selection/order, cleanup and failure handling. Exercise mixed DPI, multiple monitors, recording interruption and disk pressure with real platform integration checks. Inspect rendered CupriFace screens with synthetic content. Verify the published binary's tray, shortcuts, capture, audio and output playback.

Container and codec capabilities must be validated separately. Probe system FFmpeg, and report unsupported managed-backend combinations before rendering. Never silently drop requested audio tracks or change the selected format.

## CI and releases

Use GitHub Actions `GITHUB_TOKEN` with `packages: read` and consumer-package access for Wixely package restores. Keep credentials out of committed NuGet configuration.

Every artifact upload needs explicit retention: one day for job handoffs, three days for development builds, seven days for test evidence. A longer period needs a documented reason and must not exceed fourteen days. Durable downloads belong in GitHub Releases.

Publish minimal executable/archive assets with SHA-256 checksums and meaningful release notes. Omit PDBs and unused IIS assets. Preserve applicable third-party notices and review each outgoing commit before pushing.

## Repository and data handling

Use the Wixely noreply Git identity. Store settings and credentials outside version control. Exclude captures, temporary recordings, local logs and build outputs. Use synthetic test material that carries no private desktop content.

Kino owns temporary audio and deletes it after a successful validated render, as required by the recording decision. Other large build caches and disposable development artifacts should be identified for user-approved cleanup at milestones.
