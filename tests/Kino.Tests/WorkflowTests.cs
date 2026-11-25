namespace Kino.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kino-workflow-").FullName;
    private readonly FakePlatform _platform = new();
    private readonly FakeFeedback _feedback = new();
    private readonly FakeRecorder _recorder = new();
    private readonly KinoSettings _settings;
    private readonly CaptureWorkflow _workflow;

    public WorkflowTests()
    {
        _platform.Feedback = _feedback;
        _settings = new KinoSettings { ImageFolder = Path.Combine(_root, "images") };
        _workflow = new CaptureWorkflow(_platform, _feedback, _recorder,
            new RecordingSessionStore(() => Path.Combine(_root, "recordings")), () => _settings);
    }

    public void Dispose()
    {
        _workflow.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Key1_then_key2_copies_the_region_between_the_two_pointer_positions()
    {
        _platform.Pointer = new(300, 200);
        await _workflow.HandleAsync(ShortcutSlot.ImageCorner);
        Assert.Equal(WorkflowPhase.SelectingImage, _workflow.Phase);
        Assert.Equal(SelectionKind.Image, _feedback.SelectionShown);

        _platform.Pointer = new(100, 50);
        await _workflow.HandleAsync(ShortcutSlot.ImageCapture);

        Assert.Equal(WorkflowPhase.Idle, _workflow.Phase);
        Assert.Equal(new PixelRect(100, 50, 201, 151), _platform.CapturedRegion);
        Assert.NotNull(_platform.CopiedImage);
        Assert.Null(_feedback.SelectionShown);
        Assert.False(_platform.CaptureSawSelection, "The preview must be hidden before pixels are captured.");
    }

    [Fact]
    public async Task Key3_after_a_screenshot_saves_a_png_and_copies_its_path()
    {
        await TakeScreenshotAsync();

        await _workflow.HandleAsync(ShortcutSlot.SaveImage);

        var path = Assert.IsType<string>(_platform.CopiedText);
        Assert.True(File.Exists(path));
        Assert.Equal(".png", Path.GetExtension(path));
        Assert.Equal(FakePlatform.PngBytes, File.ReadAllBytes(path));

        // Saving again copies the same file rather than writing a duplicate.
        await _workflow.HandleAsync(ShortcutSlot.SaveImage);
        Assert.Single(Directory.GetFiles(_settings.ImageFolder!));
    }

    [Fact]
    public async Task Key3_without_a_screenshot_changes_nothing()
    {
        await _workflow.HandleAsync(ShortcutSlot.SaveImage);

        Assert.Null(_platform.CopiedText);
        Assert.Equal(WorkflowPhase.Idle, _workflow.Phase);
    }

    [Fact]
    public async Task Cancel_leaves_the_clipboard_untouched()
    {
        await _workflow.HandleAsync(ShortcutSlot.ImageCorner);
        _workflow.Cancel();

        Assert.Equal(WorkflowPhase.Idle, _workflow.Phase);
        Assert.Null(_platform.CopiedImage);
        Assert.Null(_feedback.SelectionShown);
    }

    [Fact]
    public async Task Key4_starts_a_video_selection_and_key5_starts_recording()
    {
        _platform.Pointer = new(10, 10);
        await _workflow.HandleAsync(ShortcutSlot.VideoCorner);
        Assert.Equal(WorkflowPhase.SelectingVideo, _workflow.Phase);
        Assert.Equal(SelectionKind.Video, _feedback.SelectionShown);

        _platform.Pointer = new(330, 250);
        await _workflow.HandleAsync(ShortcutSlot.VideoStart);

        Assert.Equal(WorkflowPhase.Recording, _workflow.Phase);
        // 321 × 241 inclusive becomes 320 × 240 for the encoder.
        Assert.Equal(new PixelRect(10, 10, 320, 240), _recorder.Region);
        Assert.Equal(_recorder.Region, _feedback.Frame);
        Assert.True(File.Exists(Path.Combine(_recorder.SessionDirectory!, RecordingManifest.FileName)));
    }

    [Fact]
    public async Task Other_keys_are_ignored_while_recording_and_key6_stops()
    {
        await StartRecordingAsync();
        string? finishedDirectory = null;
        _workflow.RecordingFinished += (directory, _) => finishedDirectory = directory;

        await _workflow.HandleAsync(ShortcutSlot.ImageCorner);
        await _workflow.HandleAsync(ShortcutSlot.ImageCapture);
        await _workflow.HandleAsync(ShortcutSlot.SaveImage);
        await _workflow.HandleAsync(ShortcutSlot.VideoCorner);
        await _workflow.HandleAsync(ShortcutSlot.VideoStart);
        Assert.Equal(WorkflowPhase.Recording, _workflow.Phase);

        await _workflow.HandleAsync(ShortcutSlot.StopRecording);

        Assert.Equal(WorkflowPhase.Idle, _workflow.Phase);
        Assert.Null(_feedback.Frame);
        Assert.NotNull(finishedDirectory);
        var manifest = RecordingManifest.Read(Path.Combine(finishedDirectory!, RecordingManifest.FileName))!;
        Assert.Equal(RecordingState.AwaitingFinish, manifest.State);
        Assert.Equal(1.5, manifest.DurationSeconds);
    }

    [Fact]
    public async Task Recording_frame_is_not_shown_when_disabled()
    {
        _settings.ShowRecordingFrame = false;

        await StartRecordingAsync();

        Assert.Equal(WorkflowPhase.Recording, _workflow.Phase);
        Assert.Null(_feedback.Frame);
    }

    [Fact]
    public async Task Video_selection_is_refused_with_a_reason_when_recording_is_unavailable()
    {
        _recorder.Unavailable = "ffmpeg was not found on PATH.";

        await _workflow.HandleAsync(ShortcutSlot.VideoCorner);

        Assert.Equal(WorkflowPhase.Idle, _workflow.Phase);
        Assert.Contains("ffmpeg", _feedback.LastNotification, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_start_removes_the_empty_session_and_returns_to_idle()
    {
        _recorder.FailStart = true;

        await StartRecordingAsync();

        Assert.Equal(WorkflowPhase.Idle, _workflow.Phase);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "recordings")));
        Assert.Null(_feedback.Frame);
    }

    [Fact]
    public async Task Pressing_the_first_corner_key_again_moves_the_corner()
    {
        _platform.Pointer = new(0, 0);
        await _workflow.HandleAsync(ShortcutSlot.ImageCorner);
        _platform.Pointer = new(50, 50);
        await _workflow.HandleAsync(ShortcutSlot.ImageCorner);
        _platform.Pointer = new(59, 59);
        await _workflow.HandleAsync(ShortcutSlot.ImageCapture);

        Assert.Equal(new PixelRect(50, 50, 10, 10), _platform.CapturedRegion);
    }

    [Fact]
    public async Task Each_key_has_one_meaning_and_is_ignored_where_it_does_not_apply()
    {
        // Key 2 and key 5 only complete their own kind of selection.
        await _workflow.HandleAsync(ShortcutSlot.ImageCapture);
        await _workflow.HandleAsync(ShortcutSlot.VideoStart);
        Assert.Equal(WorkflowPhase.Idle, _workflow.Phase);

        await _workflow.HandleAsync(ShortcutSlot.VideoCorner);
        await _workflow.HandleAsync(ShortcutSlot.ImageCapture);
        await _workflow.HandleAsync(ShortcutSlot.SaveImage);
        Assert.Equal(WorkflowPhase.SelectingVideo, _workflow.Phase);
        Assert.Null(_platform.CapturedRegion);

        // A first-corner key switches the selection to its own kind.
        await _workflow.HandleAsync(ShortcutSlot.ImageCorner);
        Assert.Equal(WorkflowPhase.SelectingImage, _workflow.Phase);
        await _workflow.HandleAsync(ShortcutSlot.VideoStart);
        Assert.Equal(WorkflowPhase.SelectingImage, _workflow.Phase);
        await _workflow.HandleAsync(ShortcutSlot.VideoCorner);
        Assert.Equal(WorkflowPhase.SelectingVideo, _workflow.Phase);
        Assert.Null(_recorder.Region);
    }

    [Fact]
    public async Task Screenshots_include_the_pointer_only_when_the_setting_is_on()
    {
        await TakeScreenshotAsync();
        Assert.False(_platform.CapturedCursor);

        _settings.IncludeCursorInScreenshots = true;
        await TakeScreenshotAsync();
        Assert.True(_platform.CapturedCursor);
    }

    private async Task TakeScreenshotAsync()
    {
        _platform.Pointer = new(0, 0);
        await _workflow.HandleAsync(ShortcutSlot.ImageCorner);
        _platform.Pointer = new(9, 9);
        await _workflow.HandleAsync(ShortcutSlot.ImageCapture);
    }

    private async Task StartRecordingAsync()
    {
        _platform.Pointer = new(0, 0);
        await _workflow.HandleAsync(ShortcutSlot.VideoCorner);
        _platform.Pointer = new(99, 99);
        await _workflow.HandleAsync(ShortcutSlot.VideoStart);
    }

    private sealed class FakePlatform : ICapturePlatform
    {
        public static readonly byte[] PngBytes = [0x89, 0x50, 0x4e, 0x47];
        public PixelPoint Pointer { get; set; }
        public PixelRect? CapturedRegion { get; private set; }
        public CapturedImage? CopiedImage { get; private set; }
        public string? CopiedText { get; private set; }
        public bool CaptureSawSelection { get; private set; }
        public bool? CapturedCursor { get; private set; }
        public FakeFeedback? Feedback { get; set; }
        public PixelRect VirtualScreen => new(-1920, 0, 3840, 1080);
        public PixelPoint PointerPosition() => Pointer;

        public CapturedImage Capture(PixelRect region, bool includeCursor)
        {
            CapturedRegion = region;
            CapturedCursor = includeCursor;
            CaptureSawSelection = Feedback?.SelectionShown is not null;
            return new CapturedImage(region.Width, region.Height, new byte[region.Width * region.Height * 4]);
        }

        public byte[] EncodePng(CapturedImage image) => PngBytes;
        public void CopyImage(CapturedImage image, byte[] png) => CopiedImage = image;
        public void CopyText(string text) => CopiedText = text;
    }

    private sealed class FakeFeedback : ICaptureFeedback
    {
        public SelectionKind? SelectionShown { get; private set; }
        public PixelRect? Frame { get; private set; }
        public string LastNotification { get; private set; } = "";
        public void ShowSelection(SelectionKind kind, PixelPoint firstCorner, string nextStep) => SelectionShown = kind;
        public void HideSelection() => SelectionShown = null;
        public void ShowRecordingFrame(PixelRect region) => Frame = region;
        public void HideRecordingFrame() => Frame = null;
        public void Notify(string title, string message) => LastNotification = $"{title}: {message}";
    }

    private sealed class FakeRecorder : IScreenRecorder
    {
        public string? Unavailable { get; set; }
        public bool FailStart { get; set; }
        public PixelRect? Region { get; private set; }
        public string? SessionDirectory { get; private set; }

        public Task<IActiveRecording> StartAsync(PixelRect region, string sessionDirectory, RecordingManifest manifest, KinoSettings settings)
        {
            if (FailStart)
            {
                throw new InvalidOperationException("No capture device.");
            }

            Region = region;
            SessionDirectory = sessionDirectory;
            return Task.FromResult<IActiveRecording>(new FakeActive(manifest));
        }

        private sealed class FakeActive(RecordingManifest manifest) : IActiveRecording
        {
            public TimeSpan Elapsed => TimeSpan.FromSeconds(1.5);

            public Task StopAsync()
            {
                manifest.DurationSeconds = 1.5;
                return Task.CompletedTask;
            }
        }
    }
}
