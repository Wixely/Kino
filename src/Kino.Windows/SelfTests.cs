using System.Diagnostics;
using System.Globalization;
using Kino.Media;
using Kino.Media.FFmpeg;
using Kino.Platform.Windows;

namespace Kino.Windows;

/// <summary>
/// End-to-end checks run by the executable itself, so a published build can be verified on a real
/// desktop. They drive the actual workflow, platform capture, overlays, audio devices and ffmpeg, with
/// scripted pointer positions in place of the user's mouse. Results are written to a report file
/// because a windowed executable has no console.
/// </summary>
internal static partial class SelfTests
{
    public static async Task<int> CaptureAsync(string reportPath, string? dataRoot)
    {
        var report = new List<string>();
        var root = Path.GetFullPath(dataRoot ?? Directory.CreateTempSubdirectory("kino-selftest-").FullName);
        using var shell = new ShellThread();
        using var feedback = new WindowsCaptureFeedback(shell);
        var platform = new ScriptedPlatform(new WindowsCapturePlatform(shell));
        var settings = new KinoSettings { ImageFolder = Path.Combine(root, "images") };
        using var workflow = new CaptureWorkflow(platform, feedback, new UnavailableRecorder(),
            new RecordingSessionStore(() => Path.Combine(root, "recordings")), () => settings);
        var screen = platform.VirtualScreen;
        report.Add($"virtual screen: {screen}");

        // Reverse-direction corners: the second corner is above and left of the first.
        // The primary monitor always has its top-left corner at 0, 0.
        platform.Next = new PixelPoint(319, 239);
        await workflow.HandleAsync(ShortcutSlot.ImageCorner);
        report.Add($"after key 1: {workflow.Phase}");
        await Task.Delay(300);
        platform.Next = new PixelPoint(0, 0);
        await workflow.HandleAsync(ShortcutSlot.ImageCapture);
        report.Add($"after key 2: {workflow.Phase}; {workflow.LastMessage}");
        var formats = shell.Invoke(WindowsClipboard.AvailableFormats);
        report.Add($"clipboard after copy: DIB={formats.Dib} PNG={formats.Png}");
        var image = workflow.LastImage;
        var copied = image is { Width: 320, Height: 240 } && formats.Dib && formats.Png;

        await workflow.HandleAsync(ShortcutSlot.SaveImage);
        formats = shell.Invoke(WindowsClipboard.AvailableFormats);
        var saved = workflow.LastImagePath is { } path && File.Exists(path) && formats.Text;
        report.Add($"saved: {workflow.LastImagePath} (text on clipboard: {formats.Text})");
        if (saved)
        {
            report.Add($"png bytes: {new FileInfo(workflow.LastImagePath!).Length}");
        }

        // A cancelled selection must leave the clipboard alone.
        await workflow.HandleAsync(ShortcutSlot.ImageCorner);
        workflow.Cancel();
        var stillText = shell.Invoke(WindowsClipboard.AvailableFormats).Text;
        report.Add($"after cancel: {workflow.Phase}; clipboard still holds the path: {stillText}");

        // A region spanning the whole virtual screen reaches into any gaps between monitors.
        var whole = new WindowsCapturePlatform(shell).Capture(new PixelRect(screen.X, screen.Y, Math.Min(screen.Width, 4096), Math.Min(screen.Height, 4096)), includeCursor: false);
        report.Add($"virtual-screen corner capture: {whole.Width} Ãƒâ€” {whole.Height}");

        var passed = copied && saved && stillText && workflow.Phase == WorkflowPhase.Idle;
        report.Add(passed ? "PASS" : "FAIL");
        await File.WriteAllLinesAsync(reportPath, report);
        if (dataRoot is null)
        {
            Directory.Delete(root, recursive: true);
        }

        return passed ? 0 : 1;
    }

    /// <summary>Records with each mode in <paramref name="modes"/> (all when null), renders every format
    /// with and without audio, and verifies the results.</summary>
    public static async Task<int> RecordAsync(string reportPath, double seconds, string? dataRoot, bool keepOutputs, int width = 640, int height = 480,
        IReadOnlyList<RecordingMode>? modes = null, bool follow = false)
    {
        var report = new List<string>();
        var root = Path.GetFullPath(dataRoot ?? Directory.CreateTempSubdirectory("kino-selftest-").FullName);
        var settings = new KinoSettings { VideoFolder = Path.Combine(root, "videos"), FollowCursor = follow };
        var ffmpeg = new FfmpegState(() => settings);
        await ffmpeg.RefreshAsync();
        report.Add($"ffmpeg: {ffmpeg.Status}");
        if (ffmpeg.Tools is not { } tools)
        {
            report.Add("FAIL: rendering needs ffmpeg");
            await File.WriteAllLinesAsync(reportPath, report);
            return 1;
        }

        using var shell = new ShellThread();
        using var windowsFeedback = new WindowsCaptureFeedback(shell);
        var feedback = new ObservedFeedback(windowsFeedback);
        var platform = new ScriptedPlatform(new WindowsCapturePlatform(shell));
        var sessions = new RecordingSessionStore(() => Path.Combine(root, "recordings"));
        using var workflow = new CaptureWorkflow(platform, feedback, new WindowsScreenRecorder(() => ffmpeg.Tools, () => ffmpeg.Unavailable, () => settings),
            sessions, () => settings);
        var passed = true;
        foreach (var mode in modes ?? Enum.GetValues<RecordingMode>())
        {
            settings.RecordingMode = mode;
            report.Add($"== {mode}{(follow ? ", following the pointer" : "")}");
            passed &= await RecordModeAsync(report, workflow, platform, feedback, sessions, tools, ffmpeg, settings, seconds, width, height);
        }

        report.Add(passed ? "PASS" : "FAIL");
        await File.WriteAllLinesAsync(reportPath, report);
        if (dataRoot is null && !keepOutputs)
        {
            Directory.Delete(root, recursive: true);
        }

        return passed ? 0 : 1;
    }

    private static async Task<bool> RecordModeAsync(List<string> report, CaptureWorkflow workflow, ScriptedPlatform platform, ObservedFeedback feedback,
        RecordingSessionStore sessions, FfmpegTools tools, FfmpegState ffmpeg, KinoSettings settings, double seconds, int width, int height)
    {
        string? sessionDirectory = null;
        RecordingManifest? manifest = null;
        void OnFinished(string directory, RecordingManifest finished) => (sessionDirectory, manifest) = (directory, finished);
        workflow.RecordingFinished += OnFinished;
        _ = Pointer.GetCursorPos(out var userPointer);
        feedback.Reset();
        try
        {
            platform.Next = new PixelPoint(0, 0);
            await workflow.HandleAsync(ShortcutSlot.VideoCorner);
            await Task.Delay(300);
            // Inclusive corners one pixel larger than requested, so the even-size rounding is exercised too.
            platform.Next = new PixelPoint(width, height);
            if (settings.FollowCursor)
            {
                // Following reads the real pointer, so it starts where the scripted second corner is.
                _ = Pointer.SetCursorPos(width, height);
            }

            await workflow.HandleAsync(ShortcutSlot.VideoStart);
            report.Add($"after key 5: {workflow.Phase}; {workflow.LastMessage}");
            if (workflow.Phase != WorkflowPhase.Recording)
            {
                report.Add("FAIL: recording did not start");
                return false;
            }

            if (settings.FollowCursor)
            {
                // Sweeps the pointer down and right over half the recording, then holds it so the region
                // settles before the end.
                var sweep = TimeSpan.FromSeconds(seconds / 2);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < sweep)
                {
                    var progress = clock.Elapsed / sweep;
                    _ = Pointer.SetCursorPos(width + (int)(600 * progress), height + (int)(300 * progress));
                    await Task.Delay(15);
                }

                _ = Pointer.SetCursorPos(width + 600, height + 300);
                await Task.Delay(TimeSpan.FromSeconds(seconds) - sweep);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }

            await workflow.HandleAsync(ShortcutSlot.StopRecording);
            report.Add($"after key 6: {workflow.Phase}; {workflow.LastMessage}");
        }
        finally
        {
            workflow.RecordingFinished -= OnFinished;
            if (settings.FollowCursor)
            {
                _ = Pointer.SetCursorPos(userPointer.X, userPointer.Y);
            }
        }

        if (manifest is null || sessionDirectory is null)
        {
            report.Add("FAIL: no finished recording");
            return false;
        }

        report.Add(string.Create(CultureInfo.InvariantCulture,
            $"video: {manifest.Region} {manifest.DurationSeconds:0.000}s {manifest.FramesWritten} frames, {manifest.FramesRepeated} repeated; error: {manifest.LastError ?? "none"}"));
        report.Add($"capture: {manifest.CaptureDetails}");
        foreach (var track in manifest.AudioTracks)
        {
            report.Add(string.Create(CultureInfo.InvariantCulture,
                $"track {track.Id}: {track.DurationSeconds:0.000}s offset {track.OffsetSeconds:+0.000;-0.000}s usable={track.IsUsable} {track.Problem ?? track.Warning}"));
        }

        var passed = manifest.LastError is null && Math.Abs(manifest.DurationSeconds - seconds) < 1.0 &&
            manifest.Region.Width == width && manifest.Region.Height == height;
        var finalRegion = manifest.Region;
        if (settings.FollowCursor)
        {
            // The frame moves with the region, so its moves show where the region went and how smoothly.
            finalRegion = feedback.LastRegion ?? manifest.Region;
            report.Add($"follow: {feedback.Moves} moves, largest step {feedback.LargestStep} px, from {manifest.Region} to {finalRegion}");
            passed &= feedback.Moves > 10 && feedback.LargestStep <= 120 && finalRegion != manifest.Region &&
                finalRegion.Width == manifest.Region.Width && finalRegion.Height == manifest.Region.Height;
        }

        var stagingPath = Path.Combine(sessionDirectory, manifest.VideoFileName);
        if (manifest.VideoFormat == StagingFormat.JpegFrames)
        {
            passed &= CheckFrameFile(report, stagingPath, manifest, finalRegion);
        }
        else
        {
            var staged = await Ffprobe.StreamsAsync(tools, stagingPath);
            var stagedDuration = staged.Count > 0 ? staged[0].DurationSeconds : null;
            report.Add(string.Create(CultureInfo.InvariantCulture,
                $"staging file: {string.Join(", ", staged.Select(stream => stream.CodecName))} {stagedDuration:0.000}s, {new FileInfo(stagingPath).Length / 1024} KB"));
        }

        var usable = manifest.AudioTracks.Where(track => track.IsUsable).Select(track => track.Id).Reverse().ToArray();
        var renderer = ffmpeg.Renderer().Renderer!;
        foreach (var format in Enum.GetValues<OutputFormat>())
        {
            foreach (var selection in usable.Length > 0 ? new[] { [], usable } : new[] { Array.Empty<string>() })
            {
                var output = FinishPlanning.UniqueOutputPath(settings.VideoFolder!, $"selftest-{manifest.Mode}-{format}-{selection.Length}", OutputFormats.Extension(format));
                var clock = Stopwatch.StartNew();
                try
                {
                    await renderer.RenderAsync(sessionDirectory, manifest, new FinishChoices(format, selection, output), null, CancellationToken.None);
                    var streams = await Ffprobe.StreamsAsync(tools, output);
                    var labels = selection.Select(id => manifest.AudioTracks.First(track => track.Id == id).Label).ToArray();
                    var verify = Ffprobe.Verify(streams, format, labels);
                    var duration = streams.FirstOrDefault(stream => stream.CodecType == "video")?.DurationSeconds;
                    report.Add(string.Create(CultureInfo.InvariantCulture,
                        $"render {format} [{string.Join(", ", labels)}]: {clock.Elapsed.TotalSeconds:0.0}s, {new FileInfo(output).Length / 1024} KB, {duration:0.000}s, streams {string.Join(" ", streams.Select(stream => $"{stream.CodecType}:{stream.CodecName}:{stream.Title}"))} {verify ?? "verified"}"));
                    passed &= verify is null;
                }
                catch (Exception exception) when (exception is FfmpegException or InvalidOperationException or IOException)
                {
                    report.Add($"render {format} [{string.Join(", ", selection)}]: FAILED {exception.Message} {(exception as FfmpegException)?.Diagnostics}");
                    passed = false;
                }
            }
        }

        sessions.Delete(sessionDirectory);
        report.Add($"temporary files removed: {!Directory.Exists(sessionDirectory)}");
        return passed;
    }

    /// <summary>Reports what a JPEG frame file holds, and compares its rebuilt last frame with
    /// <paramref name="finalRegion"/> of the screen as it is now. The region is near the top-left of the
    /// primary monitor, which rarely changes during the test; JPEG at the default quality differs from
    /// the source by a few levels on average.</summary>
    private static bool CheckFrameFile(List<string> report, string path, RecordingManifest manifest, PixelRect finalRegion)
    {
        long full = 0;
        long patches = 0;
        long scrolls = 0;
        long patchBytes = 0;
        long lastSlot = -1;
        var rebuilder = default(FrameRebuilder);
        using (var reader = FrameStreamReader.Open(path))
        {
            rebuilder = new FrameRebuilder(reader.Width, reader.Height);
            while (reader.Next() is { } record)
            {
                rebuilder.Apply(record);
                lastSlot = record.Slot;
                if (record.Kind == FrameRecordKind.Full)
                {
                    full++;
                    continue;
                }

                patches++;
                patchBytes += record.Payload.Length;
                if (FramePatches.Decode(record.Payload, reader.Width, reader.Height).Copies.Count > 0)
                {
                    scrolls++;
                }
            }
        }

        using var grabber = new GdiFrameGrabber(manifest.Region.Width, manifest.Region.Height);
        var screen = grabber.Grab(finalRegion.X, finalRegion.Y, includeCursor: false);
        long difference = 0;
        for (var index = 0; index < screen.Length; index++)
        {
            if ((index & 3) != 3)
            {
                difference += Math.Abs(screen[index] - rebuilder.Pixels[index]);
            }
        }

        var mean = difference / (screen.Length * 0.75);
        report.Add(string.Create(CultureInfo.InvariantCulture,
            $"frame file: {new FileInfo(path).Length / 1024} KB, {full} full, {patches} patches ({scrolls} with a scroll copy, {(patches == 0 ? 0 : patchBytes / patches / 1024)} KB on average), {manifest.FramesWritten - full - patches} slots without a record, last slot {lastSlot}; rebuilt last frame vs screen: mean difference {mean:0.00} levels"));
        return full >= 1 && lastSlot < manifest.FramesWritten && mean < 12;
    }

    /// <summary>Passes feedback through, remembering where the recording frame was moved.</summary>
    private sealed class ObservedFeedback(ICaptureFeedback inner) : ICaptureFeedback
    {
        private readonly Lock _lock = new();
        private PixelRect? _last;

        public int Moves { get; private set; }
        public int LargestStep { get; private set; }
        public PixelRect? LastRegion { get { lock (_lock) return _last; } }

        public void Reset()
        {
            lock (_lock)
            {
                (_last, Moves, LargestStep) = (null, 0, 0);
            }
        }

        public void ShowSelection(SelectionKind kind, PixelPoint firstCorner, string nextStep) => inner.ShowSelection(kind, firstCorner, nextStep);
        public void HideSelection() => inner.HideSelection();

        public void ShowRecordingFrame(PixelRect region)
        {
            lock (_lock) _last = region;
            inner.ShowRecordingFrame(region);
        }

        public void MoveRecordingFrame(PixelRect region)
        {
            lock (_lock)
            {
                if (_last is { } previous)
                {
                    LargestStep = Math.Max(LargestStep, Math.Max(Math.Abs(region.X - previous.X), Math.Abs(region.Y - previous.Y)));
                }

                _last = region;
                Moves++;
            }

            inner.MoveRecordingFrame(region);
        }

        public void HideRecordingFrame() => inner.HideRecordingFrame();
        public void Notify(string title, string message) => inner.Notify(title, message);
    }

    /// <summary>Moves the real pointer for the follow test.</summary>
    private static partial class Pointer
    {
        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out PointerPosition point);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointerPosition
    {
        public int X;
        public int Y;
    }

    /// <summary>The real platform, except that pointer positions come from the test script.</summary>
    private sealed class ScriptedPlatform(ICapturePlatform inner) : ICapturePlatform
    {
        public PixelPoint Next { get; set; }
        public PixelRect VirtualScreen => inner.VirtualScreen;
        public PixelPoint PointerPosition() => Next;
        public CapturedImage Capture(PixelRect region, bool includeCursor) => inner.Capture(region, includeCursor);
        public byte[] EncodePng(CapturedImage image) => inner.EncodePng(image);
        public void CopyImage(CapturedImage image, byte[] png) => inner.CopyImage(image, png);
        public void CopyText(string text) => inner.CopyText(text);
    }

    private sealed class UnavailableRecorder : IScreenRecorder
    {
        public string? Unavailable => "Not used by the screenshot self-test.";

        public Task<IActiveRecording> StartAsync(PixelRect region, string sessionDirectory, RecordingManifest manifest, KinoSettings settings) =>
            throw new NotSupportedException();
    }
}
