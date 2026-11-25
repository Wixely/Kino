namespace Kino;

/// <summary>Captured pixels in top-down BGRA order with a stride of <c>Width * 4</c>.</summary>
public sealed class CapturedImage
{
    public CapturedImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException("The pixel buffer does not match the image size.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

/// <summary>Screen, pointer and clipboard access for the capture workflow.</summary>
public interface ICapturePlatform
{
    PixelPoint PointerPosition();
    PixelRect VirtualScreen { get; }
    CapturedImage Capture(PixelRect region, bool includeCursor);
    byte[] EncodePng(CapturedImage image);
    void CopyImage(CapturedImage image, byte[] png);
    void CopyText(string text);
}

public enum SelectionKind
{
    Image,
    Video,
}

/// <summary>What the user sees while selecting and recording. None of it may appear in captured
/// pixels.</summary>
public interface ICaptureFeedback
{
    void ShowSelection(SelectionKind kind, PixelPoint firstCorner, string nextStep);
    void HideSelection();
    void ShowRecordingFrame(PixelRect region);
    void HideRecordingFrame();
    void Notify(string title, string message);
}

public interface IScreenRecorder
{
    /// <summary>Why recording cannot start, or null when it can.</summary>
    string? Unavailable { get; }

    /// <summary>Starts capturing video and audio into <paramref name="sessionDirectory"/>. The recorder
    /// adds its audio tracks to <paramref name="manifest"/>.</summary>
    Task<IActiveRecording> StartAsync(PixelRect region, string sessionDirectory, RecordingManifest manifest, KinoSettings settings);
}

public interface IActiveRecording
{
    TimeSpan Elapsed { get; }

    /// <summary>Stops capture and finalises the temporary files, updating the manifest's duration,
    /// frame counts and audio offsets.</summary>
    Task StopAsync();
}

public enum WorkflowPhase
{
    Idle,
    SelectingImage,
    SelectingVideo,
    StartingRecording,
    Recording,
    Stopping,
}

/// <summary>
/// The click-free capture workflow. Each shortcut has one action:
/// <list type="bullet">
/// <item>Key 1 marks the first corner of a screenshot.</item>
/// <item>Key 2 marks the second corner, captures the screenshot and copies it.</item>
/// <item>Key 3 saves the last screenshot and copies its path.</item>
/// <item>Key 4 marks the first corner of a video.</item>
/// <item>Key 5 marks the second corner and starts recording.</item>
/// <item>Key 6 stops recording and raises <see cref="RecordingFinished"/> for the audio choice.</item>
/// </list>
/// Pressing a first-corner key while selecting moves the first corner, switching to that kind of
/// selection. Keys that do not apply to the current phase are ignored. Cancel abandons a selection
/// without touching the clipboard.
/// </summary>
public sealed class CaptureWorkflow : IDisposable
{
    private readonly ICapturePlatform _platform;
    private readonly ICaptureFeedback _feedback;
    private readonly IScreenRecorder _recorder;
    private readonly RecordingSessionStore _sessions;
    private readonly Func<KinoSettings> _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IActiveRecording? _activeRecording;
    private RecordingManifest? _activeManifest;
    private string? _activeSessionDirectory;

    public CaptureWorkflow(
        ICapturePlatform platform,
        ICaptureFeedback feedback,
        IScreenRecorder recorder,
        RecordingSessionStore sessions,
        Func<KinoSettings> settings,
        TimeProvider? time = null)
    {
        _platform = platform;
        _feedback = feedback;
        _recorder = recorder;
        _sessions = sessions;
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after any phase or output change, on the thread that caused it.</summary>
    public event Action? Changed;

    /// <summary>Raised when a recording has stopped and its sources wait for finishing choices.</summary>
    public event Action<string, RecordingManifest>? RecordingFinished;

    public WorkflowPhase Phase { get; private set; }
    public PixelPoint? FirstCorner { get; private set; }
    public PixelRect? RecordingRegion { get; private set; }
    public TimeSpan RecordingElapsed => _activeRecording?.Elapsed ?? TimeSpan.Zero;
    public CapturedImage? LastImage { get; private set; }
    public PixelRect? LastImageRegion { get; private set; }
    public string? LastImagePath { get; private set; }
    public string LastMessage { get; private set; } = "";
    public bool IsSelecting => Phase is WorkflowPhase.SelectingImage or WorkflowPhase.SelectingVideo;

    public async Task HandleAsync(ShortcutSlot slot)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (slot, Phase)
            {
                case (ShortcutSlot.ImageCorner, WorkflowPhase.Idle or WorkflowPhase.SelectingImage or WorkflowPhase.SelectingVideo):
                    BeginSelection(SelectionKind.Image);
                    break;
                case (ShortcutSlot.ImageCapture, WorkflowPhase.SelectingImage):
                    CompleteScreenshot();
                    break;
                case (ShortcutSlot.SaveImage, WorkflowPhase.Idle):
                    SaveLastImage();
                    break;
                case (ShortcutSlot.VideoCorner, WorkflowPhase.Idle or WorkflowPhase.SelectingImage or WorkflowPhase.SelectingVideo):
                    BeginSelection(SelectionKind.Video);
                    break;
                case (ShortcutSlot.VideoStart, WorkflowPhase.SelectingVideo):
                    await StartRecordingAsync().ConfigureAwait(false);
                    break;
                case (ShortcutSlot.StopRecording, WorkflowPhase.Recording):
                    await StopRecordingAsync().ConfigureAwait(false);
                    break;
                default:
                    // Keys that mean nothing in this phase are ignored rather than reinterpreted, so a
                    // stray press during recording cannot start a second capture.
                    return;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Abandons a selection. Has no effect outside selection.</summary>
    public void Cancel()
    {
        _gate.Wait();
        try
        {
            if (!IsSelecting)
            {
                return;
            }

            _feedback.HideSelection();
            FirstCorner = null;
            Phase = WorkflowPhase.Idle;
            Report("Selection cancelled.");
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Stops an active recording, for example from the tray menu or before quitting.</summary>
    public Task StopAsync() => HandleAsync(ShortcutSlot.StopRecording);

    private void BeginSelection(SelectionKind kind)
    {
        if (kind == SelectionKind.Video && _recorder.Unavailable is { } reason)
        {
            _feedback.HideSelection();
            Phase = WorkflowPhase.Idle;
            FirstCorner = null;
            Report($"Recording is unavailable: {reason}");
            _feedback.Notify("Kino cannot record", reason);
            return;
        }

        var corner = _platform.PointerPosition();
        FirstCorner = corner;
        Phase = kind == SelectionKind.Image ? WorkflowPhase.SelectingImage : WorkflowPhase.SelectingVideo;
        var shortcuts = _settings().Shortcuts;
        var next = kind == SelectionKind.Image
            ? $"{shortcuts[ShortcutSlot.ImageCapture]} at the far corner copies"
            : $"{shortcuts[ShortcutSlot.VideoStart]} at the far corner records";
        if (_settings().ShowCornerPreview)
        {
            _feedback.ShowSelection(kind, corner, next);
        }

        Report($"{(kind == SelectionKind.Image ? "Screenshot" : "Video")} corner marked at {corner}.");
    }

    private PixelRect? CompleteSelection(bool evenSize)
    {
        var first = FirstCorner!.Value;
        var second = _platform.PointerPosition();
        _feedback.HideSelection();
        FirstCorner = null;
        Phase = WorkflowPhase.Idle;
        var region = PixelRect.FromCorners(first, second).Intersect(_platform.VirtualScreen);
        if (evenSize)
        {
            region = region.ToEvenSize();
        }

        if (region.IsEmpty)
        {
            Report("The selected region is outside the screen or too small.");
            return null;
        }

        return region;
    }

    private void CompleteScreenshot()
    {
        if (CompleteSelection(evenSize: false) is not { } region)
        {
            return;
        }

        try
        {
            var image = _platform.Capture(region, _settings().IncludeCursorInScreenshots);
            var png = _platform.EncodePng(image);
            _platform.CopyImage(image, png);
            LastImage = image;
            LastImageRegion = region;
            LastImagePath = null;
            Report($"Copied a {region.Width} × {region.Height} screenshot.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report($"The screenshot failed: {exception.Message}");
            _feedback.Notify("Screenshot failed", exception.Message);
        }
    }

    private void SaveLastImage()
    {
        if (LastImage is not { } image)
        {
            Report("There is no screenshot to save yet.");
            return;
        }

        try
        {
            if (LastImagePath is null || !File.Exists(LastImagePath))
            {
                var folder = _settings().ImageFolder ?? AppStorage.DefaultImageFolder;
                Directory.CreateDirectory(folder);
                var stem = "Kino " + _time.GetLocalNow().ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                var path = FinishPlanning.UniqueOutputPath(folder, stem, ".png");
                File.WriteAllBytes(path, _platform.EncodePng(image));
                LastImagePath = path;
            }

            _platform.CopyText(LastImagePath);
            Report($"Saved and copied the path: {LastImagePath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Report($"The screenshot could not be saved: {exception.Message}");
            _feedback.Notify("Save failed", exception.Message);
        }
    }

    private async Task StartRecordingAsync()
    {
        if (CompleteSelection(evenSize: true) is not { } region)
        {
            return;
        }

        if (region.Width < 16 || region.Height < 16)
        {
            Report("Select a region of at least 16 × 16 pixels to record.");
            return;
        }

        Phase = WorkflowPhase.StartingRecording;
        var startedAt = _time.GetLocalNow();
        var sessionDirectory = _sessions.Create(startedAt);
        var settings = _settings();
        var manifest = new RecordingManifest
        {
            Id = Path.GetFileName(sessionDirectory),
            StartedAt = startedAt,
            Region = region,
            FrameRate = settings.FrameRate,
        };
        try
        {
            manifest.Write(RecordingSessionStore.ManifestPath(sessionDirectory));
            _activeRecording = await _recorder.StartAsync(region, sessionDirectory, manifest, settings).ConfigureAwait(false);
            _activeManifest = manifest;
            _activeSessionDirectory = sessionDirectory;
            manifest.Write(RecordingSessionStore.ManifestPath(sessionDirectory));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _activeRecording = null;
            Phase = WorkflowPhase.Idle;
            TryDelete(sessionDirectory);
            Report($"Recording could not start: {exception.Message}");
            _feedback.Notify("Recording failed", exception.Message);
            return;
        }

        RecordingRegion = region;
        Phase = WorkflowPhase.Recording;
        if (settings.ShowRecordingFrame)
        {
            _feedback.ShowRecordingFrame(region);
        }

        var stop = settings.Shortcuts[ShortcutSlot.StopRecording];
        var problems = manifest.AudioTracks.Where(track => track.Problem is not null).Select(track => $"{track.Label} ({track.Problem!.TrimEnd('.')})").ToArray();
        Report(problems.Length == 0
            ? $"Recording {region.Width} × {region.Height}. Press {stop} to stop."
            : $"Recording {region.Width} × {region.Height} without {string.Join("; ", problems)}. Press {stop} to stop.");
    }

    private async Task StopRecordingAsync()
    {
        var recording = _activeRecording!;
        var manifest = _activeManifest!;
        var sessionDirectory = _activeSessionDirectory!;
        Phase = WorkflowPhase.Stopping;
        _feedback.HideRecordingFrame();
        try
        {
            await recording.StopAsync().ConfigureAwait(false);
            manifest.State = RecordingState.AwaitingFinish;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The sources stay on disk; the finishing screen decides whether they are usable.
            manifest.State = RecordingState.AwaitingFinish;
            manifest.LastError = $"Capture did not stop cleanly: {exception.Message}";
        }
        finally
        {
            _activeRecording = null;
            _activeManifest = null;
            _activeSessionDirectory = null;
            RecordingRegion = null;
            Phase = WorkflowPhase.Idle;
        }

        manifest.Write(RecordingSessionStore.ManifestPath(sessionDirectory));
        Report($"Recorded {manifest.DurationSeconds:0.0} seconds. Choose whether to include audio.");
        RecordingFinished?.Invoke(sessionDirectory, manifest);
    }

    private void TryDelete(string sessionDirectory)
    {
        try
        {
            _sessions.Delete(sessionDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the user to discard from the unfinished-recordings list.
        }
    }

    private void Report(string message) => LastMessage = message;
}
