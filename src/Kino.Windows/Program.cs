using Kino;
using Kino.App;
using Kino.Platform.Windows;
using Kino.Windows;
using CupriFace.Shell;
using SkiaSharp;

if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
{
    throw new PlatformNotSupportedException("This Kino build supports Windows 10 1809 and later.");
}

// Before any window exists, so pointer positions and capture coordinates are physical pixels on
// every monitor. The manifest asks for the same; this covers hosts that ignore it.
ProcessDpi.EnablePerMonitorV2();

var dataRoot = ArgumentValue(args, "--data-root");
if (ArgumentValue(args, "--selftest-capture") is { Length: > 0 } captureReport)
{
    return await SelfTests.CaptureAsync(Path.GetFullPath(captureReport), dataRoot);
}

if (ArgumentValue(args, "--selftest-record") is { Length: > 0 } recordReport)
{
    var seconds = double.TryParse(ArgumentValue(args, "--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 3;
    var size = (ArgumentValue(args, "--size") ?? "640x480").Split('x');
    // --mode JpegDelta|Jpeg|Lossless limits the run to one recording mode; all are tested by default.
    var modes = ArgumentValue(args, "--mode") is { } mode ? new[] { Enum.Parse<RecordingMode>(mode, ignoreCase: true) } : null;
    return await SelfTests.RecordAsync(Path.GetFullPath(recordReport), seconds, dataRoot, args.Contains("--keep-outputs"),
        int.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture), modes);
}

var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
var storage = new AppStorage(executableDirectory, dataRoot);
var settingsStore = new SettingsStore(() => storage.SettingsPath);
var settings = settingsStore.Load();
var sessions = new RecordingSessionStore(() => storage.RecordingsRoot);
var snapshotPath = ArgumentValue(args, "--snapshot");
if (snapshotPath is not null)
{
    settingsStore.Suspend();
    return RenderSnapshot(snapshotPath);
}

KinoHost? host = null;
using var instance = args.Contains("--allow-multiple-instances")
    ? null
    : SingleInstance.TryAcquire(() => host?.ShowWindow());
if (instance is null && !args.Contains("--allow-multiple-instances"))
{
    // Another Kino owns the shortcuts; it has been asked to open its window.
    return 0;
}

using var shell = new ShellThread();
using var feedback = new WindowsCaptureFeedback(shell);
var ffmpeg = new FfmpegState(() => settings);
var platform = new WindowsCapturePlatform(shell);
var recorder = new WindowsScreenRecorder(() => ffmpeg.Tools, () => ffmpeg.Unavailable, () => settings);
using var workflow = new CaptureWorkflow(platform, feedback, recorder, sessions, () => settings);
var model = new KinoModel(settings) { StoragePath = storage.Root + (storage.IsPortable ? " (portable)" : "") };
host = new KinoHost(shell, settings, ffmpeg, workflow, platform, ReadIcon());
using var app = new KinoApp(model, workflow, sessions, settingsStore, host);
feedback.Notified += host.Notify;
app.RefreshUnfinished();
host.Attach(app);
_ = host.RefreshFfmpegAsync();

// Kino opens the way it was left: in the tray, minimised to the taskbar, or open. --show opens it.
var placement = args.Contains("--show") ? WindowPlacement.Open : settings.WindowPlacement;
// The host shows its window after the first frames, so the placement is enforced for a few seconds
// rather than applied once. Anything that asks for the window (tray, second launch) ends that early.
var placementTimer = (nuint)0;
var startupEnds = Environment.TickCount64 + 4000;
placementTimer = shell.Invoke(() => shell.StartTimer(20, () =>
{
    if (!AppWindow.TryAttach(app.Title))
    {
        return;
    }

    if (placement != WindowPlacement.Open && !AppWindow.ShowRequested && Environment.TickCount64 <= startupEnds)
    {
        if (placement == WindowPlacement.Tray && AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
        else if (placement == WindowPlacement.Taskbar && !AppWindow.IsMinimized)
        {
            AppWindow.ShowMinimized();
        }

        return;
    }

    // From here on, a slower timer notes where the user leaves the window.
    shell.StopTimer(placementTimer);
    placementTimer = shell.StartTimer(1000, RememberPlacement);
}));
if (placement == WindowPlacement.Open)
{
    host.ShowWindow();
}

DesktopHost.Run(app);
host.Dispose();
return 0;

// Saves the window's placement when it changes. Quitting hides the window on its way out, which is
// not a placement to remember.
void RememberPlacement()
{
    if (AppWindow.Quitting || !AppWindow.IsAttached)
    {
        return;
    }

    var current = !AppWindow.IsVisible ? WindowPlacement.Tray : AppWindow.IsMinimized ? WindowPlacement.Taskbar : WindowPlacement.Open;
    if (current == settings.WindowPlacement)
    {
        return;
    }

    settings.WindowPlacement = current;
    try
    {
        settingsStore.Save(settings);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        // Tried again on the next change; the previous placement is used meanwhile.
    }
}

byte[] ReadIcon()
{
    using var stream = typeof(KinoApp).Assembly.GetManifestResourceStream("Assets/KinoIcon.png")
        ?? throw new InvalidOperationException("The Kino icon is missing from the build.");
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
}

// Renders a page without a window, for checking the UI with synthetic content.
int RenderSnapshot(string path)
{
    var model = new KinoModel(settings) { StoragePath = storage.Root };
    using var snapshotShellless = new CaptureWorkflow(new NullPlatform(), new NullFeedback(), new NullRecorder(), sessions, () => settings);
    using var app = new KinoApp(model, snapshotShellless, sessions, settingsStore, new SnapshotHost());
    model.FfmpegStatus = "Rendering with ffmpeg N-91454 (synthetic)";
    model.FfmpegClass = "ok";
    model.RecentRows = [new("Kino 2026-11-24 10-15-02.png", @"C:\Pictures\Kino\Kino 2026-11-24 10-15-02.png", "Image"), new("Kino 2026-11-24 10-12-40.mp4", @"C:\Videos\Kino\Kino 2026-11-24 10-12-40.mp4", "Video")];
    model.Status = "Copied a 640 × 360 screenshot.";
    switch (ArgumentValue(args, "--page"))
    {
        case "finish":
            var manifest = new RecordingManifest
            {
                Id = "synthetic",
                StartedAt = new DateTimeOffset(2026, 11, 24, 10, 12, 40, TimeSpan.Zero),
                Region = new PixelRect(0, 0, 1280, 720),
                FrameRate = 30,
                DurationSeconds = 42.4,
                FramesWritten = 1272,
                FramesRepeated = args.Contains("--with-warning") ? 83 : 0,
                AudioTracks =
                {
                    new RecordedAudioTrack { Id = "system", Kind = AudioSourceKind.System, Label = "System audio", FileName = "system.wav", DurationSeconds = 42.4 },
                    new RecordedAudioTrack { Id = "microphone", Kind = AudioSourceKind.Microphone, Label = "Microphone", FileName = "microphone.wav", DurationSeconds = 42.3 },
                },
            };
            settings.DefaultAudio = args.Contains("--with-audio") ? AudioInclusion.Both : AudioInclusion.None;
            app.OpenFinish(Path.Combine(sessions.Root, "synthetic"), manifest);
            break;
        case "settings":
            model.Page = "settings";
            break;
        case "unfinished":
            model.UnfinishedRows = [new("20261124-101240-000", "24 Nov 2026, 10:12 · 42.4 s", "Rendering failed: ffmpeg exited with code 1.")];
            break;
        case "recording":
            model.StateLabel = "Recording · 00:42";
            model.StateClass = "recording";
            model.Status = "Recording 1280 × 720. Press Ctrl+Num6 to stop.";
            break;
    }

    var width = 680;
    var height = 780;
    var document = app.CreateDocument();
    var renderer = new HeadlessRenderer(width, height);
    using var image = renderer.RenderFrames(2, context =>
    {
        context.Canvas.Clear(app.Background);
        var presentation = app.Present(context.Width, context.Height);
        context.Canvas.Save();
        context.Canvas.Scale(presentation.Scale);
        document.Render(context.Canvas, presentation.LogicalWidth, presentation.LogicalHeight);
        context.Canvas.Restore();
    });
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    using var output = File.Create(fullPath);
    encoded.SaveTo(output);
    if (ArgumentValue(args, "--doctor") is { Length: > 0 } doctorPath)
    {
        var report = CupriFace.Diagnostics.CupriDoctor.Check(app.Html, app.Css, width: 620, height: 720, model: model);
        File.WriteAllText(doctorPath, report.ToString());
    }

    return 0;
}

static string? ArgumentValue(string[] values, string name) => values
    .SkipWhile(value => !string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .FirstOrDefault();

internal sealed class SnapshotHost : IKinoHost
{
    public void ApplySettings() { }
    public WindowState WindowState => WindowState.Shown;
    public void SetWindowState(WindowState state) { }
    public void ShowWindow() { }
    public void HideWindow() { }
    public void RevealInFolder(string path) { }
    public void OpenFolder(string path) { }
    public IReadOnlyList<(string Id, string Name)> Microphones() => [("synthetic-1", "Microphone (USB headset)")];
    public (IRecordingRenderer? Renderer, string Status) Renderer() => (null, "Snapshots do not render.");
    public void CopyText(string text) { }
    public void Notify(string title, string message) { }
}

internal sealed class NullPlatform : ICapturePlatform
{
    public PixelRect VirtualScreen => new(0, 0, 1920, 1080);
    public PixelPoint PointerPosition() => default;
    public CapturedImage Capture(PixelRect region, bool includeCursor) => throw new NotSupportedException();
    public byte[] EncodePng(CapturedImage image) => throw new NotSupportedException();
    public void CopyImage(CapturedImage image, byte[] png) => throw new NotSupportedException();
    public void CopyText(string text) => throw new NotSupportedException();
}

internal sealed class NullFeedback : ICaptureFeedback
{
    public void ShowSelection(SelectionKind kind, PixelPoint firstCorner, string nextStep) { }
    public void HideSelection() { }
    public void ShowRecordingFrame(PixelRect region) { }
    public void HideRecordingFrame() { }
    public void Notify(string title, string message) { }
}

internal sealed class NullRecorder : IScreenRecorder
{
    public string? Unavailable => "Snapshots do not record.";
    public Task<IActiveRecording> StartAsync(PixelRect region, string sessionDirectory, RecordingManifest manifest, KinoSettings settings) =>
        throw new NotSupportedException();
}
