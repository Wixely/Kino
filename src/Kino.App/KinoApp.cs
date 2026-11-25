using System.Globalization;
using CupriFace;
using CupriFace.Resources;
using SkiaSharp;

namespace Kino.App;

public enum WindowState
{
    Hidden,
    Minimized,
    Shown,
}

/// <summary>Operations the desktop host provides to the UI.</summary>
public interface IKinoHost
{
    WindowState WindowState { get; }

    /// <summary>Returns the window to <paramref name="state"/>.</summary>
    void SetWindowState(WindowState state);

    /// <summary>Re-registers shortcuts and re-detects ffmpeg after settings change.</summary>
    void ApplySettings();

    void ShowWindow();
    void HideWindow();
    void RevealInFolder(string path);
    void OpenFolder(string path);
    IReadOnlyList<(string Id, string Name)> Microphones();

    /// <summary>The renderer for the current backend setting, or null with a reason.</summary>
    (IRecordingRenderer? Renderer, string Status) Renderer();

    /// <summary>Puts text on the clipboard. Throws when the clipboard cannot be written.</summary>
    void CopyText(string text);

    void Notify(string title, string message);
}

public sealed class KinoApp : CupriApp, IDisposable
{
    private const float DesignWidth = 620;
    private const float DesignHeight = 720;
    private readonly KinoModel _model;
    private readonly CaptureWorkflow _workflow;
    private readonly RecordingSessionStore _sessions;
    private readonly SettingsStore _settingsStore;
    private readonly IKinoHost _host;
    private readonly byte[] _icon;
    private readonly List<RecentRow> _recent = [];
    private CupriDocument? _document;
    private string? _finishDirectory;
    private RecordingManifest? _finishManifest;
    private List<string> _trackOrder = [];
    private CancellationTokenSource? _renderCancellation;
    private bool _confirmDiscard;

    // How the window was before a stopped recording brought it up to ask about audio, so a save can
    // put it back.
    private WindowState _windowBeforeFinish = WindowState.Shown;

    public KinoApp(KinoModel model, CaptureWorkflow workflow, RecordingSessionStore sessions, SettingsStore settingsStore, IKinoHost host)
    {
        _model = model;
        _workflow = workflow;
        _sessions = sessions;
        _settingsStore = settingsStore;
        _host = host;
        _icon = EmbeddedAsset("Assets/KinoIcon.png").ReadBytes();
        _model.LiveStateLabel = () => _workflow.Phase == WorkflowPhase.Recording ? $"Recording · {_workflow.RecordingElapsed:mm\\:ss}" : null;
        _model.SettingsChanged += OnSettingsChanged;
        _workflow.Changed += () => Ui(UpdateFromWorkflow);
        _workflow.RecordingFinished += FinishRecording;
        UpdateFromWorkflow();
    }

    /// <summary>Raised after a render is committed, with the output path.</summary>
    public event Action<string>? VideoSaved;

    public void Dispose() => _renderCancellation?.Dispose();

    public KinoModel ViewModel => _model;
    public override string Title => "Kino";
    public override int Width => 680;
    public override int Height => 780;
    public override SKColor Background => new(0x12, 0x12, 0x12);
    public override bool DarkWindowChrome => true;
    public override byte[] Icon => _icon;
    public override object Model => _model;

    // A rebuild cancels in-progress pointer drags, so the tick runs only while something changes on
    // its own: a recording clock or a render.
    public override double RefreshIntervalSeconds =>
        _workflow.Phase == WorkflowPhase.Recording || _model.Rendering ? 0.25 : 0;

    protected override CupriSource MarkupSource => EmbeddedAsset("Assets/Kino.html");
    protected override CupriSource StyleSource => EmbeddedAsset("Assets/Kino.css");

    public override PresentInfo Present(float windowWidth, float windowHeight)
    {
        var zoom = Math.Clamp(MathF.Min(windowWidth / DesignWidth, windowHeight / DesignHeight), 0.65f, 2f);
        return new PresentInfo(windowWidth / zoom, windowHeight / zoom, zoom);
    }

    public override void Configure(CupriDocument document)
    {
        _document = document;
        document.OnClick(".nav-settings", _ => OpenSettings());
        document.OnClick(".nav-home", _ => _model.Page = "home");
        document.OnClick(".hide-window", _ => _host.HideWindow());
        document.OnClick(".render-with-audio", _ => { StartRender(withAudio: true); });
        document.OnClick(".render-silent", _ => { StartRender(withAudio: false); });
        document.OnAction("data-layout", action =>
        {
            _model.ApplyShortcutLayout(action.Value == "compact" ? DefaultShortcuts.CreateCompact() : DefaultShortcuts.Create());
            return true;
        });
        document.OnClick(".render-cancel", _ => _renderCancellation?.Cancel());
        document.OnClick(".finish-later", _ => LeaveFinish("The recording is kept. Finish it later from the unfinished list."));
        document.OnClick(".discard", _ => Discard());
        document.OnClick(".open-image-folder", _ => _host.OpenFolder(_model.ImageFolder));
        document.OnClick(".open-video-folder", _ => _host.OpenFolder(_model.VideoFolder));
        document.OnClick(".detect-ffmpeg", _ => _host.ApplySettings());
        document.OnAction("data-finish-session", action =>
        {
            OpenUnfinished(action.Value);
            return true;
        });
        document.OnAction("data-discard-session", action =>
        {
            DiscardUnfinished(action.Value);
            return true;
        });
        document.OnAction("data-reveal", action =>
        {
            _host.RevealInFolder(action.Value);
            return true;
        });
        document.OnAction("data-toggle-track", action =>
        {
            ToggleTrack(action.Value);
            return true;
        });
        document.OnAction("data-track-up", action =>
        {
            _trackOrder = [.. FinishPlanning.Move(_trackOrder, action.Value, -1)];
            RefreshTracks();
            return true;
        });
        document.OnAction("data-track-down", action =>
        {
            _trackOrder = [.. FinishPlanning.Move(_trackOrder, action.Value, 1)];
            RefreshTracks();
            return true;
        });
        document.OnAction("data-format", action =>
        {
            SelectFormat(action.Value);
            return true;
        });
    }

    /// <summary>Lists recordings left from earlier sessions or failed renders.</summary>
    public void RefreshUnfinished()
    {
        _model.UnfinishedRows = _sessions.FindUnfinished()
            .Where(session => session.Directory != _finishDirectory || _model.Page != "finish")
            .Select(session => new UnfinishedRow(
                Path.GetFileName(session.Directory),
                session.Manifest is { } manifest
                    ? $"{manifest.StartedAt.ToLocalTime():d MMM yyyy, HH:mm} · {Duration(manifest.DurationSeconds)}"
                    : $"{Path.GetFileName(session.Directory)} · unreadable",
                session.Manifest switch
                {
                    null => "The recording details are missing. It can only be discarded.",
                    { State: RecordingState.Recording } => "Kino stopped before this recording finished.",
                    { State: RecordingState.RenderFailed, LastError: { } error } => $"Rendering failed: {error}",
                    _ => $"{Megabytes(RecordingSessionStore.SizeOnDisk(session.Directory))} of temporary files",
                }))
            .ToList();
    }

    public void SetFfmpegStatus(string status, bool available)
    {
        Ui(() =>
        {
            _model.FfmpegStatus = status;
            _model.FfmpegClass = available ? "ok" : "warning";
        });
    }

    public void SetShortcutWarning(string warning) => Ui(() => _model.ShortcutWarning = warning);

    public void SetMicrophones(IReadOnlyList<(string Id, string Name)> microphones) => Ui(() =>
        _model.MicrophoneOptions = [new("", "System default"), .. microphones.Select(device => new OptionRow(device.Id, device.Name))]);

    /// <summary>Runs <paramref name="change"/> on the document thread, or immediately before the
    /// document exists.</summary>
    public void Ui(Action change)
    {
        if (_document is { } document)
        {
            document.Post(() =>
            {
                change();
                document.Refresh();
            });
        }
        else
        {
            change();
        }
    }

    public void OpenSettings()
    {
        _model.MicrophoneOptions = [new("", "System default"), .. _host.Microphones().Select(device => new OptionRow(device.Id, device.Name))];
        _model.Page = "settings";
    }

    public void OpenFinish(string directory, RecordingManifest manifest)
    {
        _finishDirectory = directory;
        _finishManifest = manifest;
        _confirmDiscard = false;
        _windowBeforeFinish = WindowState.Shown;
        _model.DiscardLabel = "Discard recording";
        _model.FinishError = "";
        _model.Rendering = false;
        _trackOrder = [.. FinishPlanning.DefaultTrackSelection(manifest, _model.Settings.DefaultAudio)];
        if (_trackOrder.Count == 0)
        {
            _trackOrder = [.. FinishPlanning.DefaultTrackSelection(manifest, AudioInclusion.Both)];
        }

        _model.FinishTitle = $"Include audio in the {Duration(manifest.DurationSeconds)} recording?";
        _model.FinishDetail = $"{manifest.Region.Width} × {manifest.Region.Height} at {manifest.FrameRate} fps · " +
            $"{Megabytes(RecordingSessionStore.SizeOnDisk(directory))} of temporary files";
        var warnings = new List<string>();
        if (manifest.LastError is { } error) warnings.Add(error);
        if (manifest.FramesRepeated > 0 && manifest.FramesWritten > 0)
        {
            warnings.Add($"{manifest.FramesRepeated} of {manifest.FramesWritten} frames repeat the previous image because capture fell behind.");
        }

        _model.FinishWarning = string.Join(" ", warnings);
        var format = _model.Settings.DefaultFormat;
        _model.FinishFormat = format.ToString();
        var stem = "Kino " + manifest.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        _model.OutputPath = FinishPlanning.UniqueOutputPath(_model.VideoFolder, stem, OutputFormats.Extension(format));
        RefreshTracks();
        _model.Page = "finish";
    }

    /// <summary>Asks about audio, or saves straight away when Settings chose an answer. Anything that
    /// stops an automatic save opens the window so the recording is not left unnoticed.</summary>
    /// <remarks>Runs on the thread that stopped the recording. The document thread does not run posted
    /// work while the window is minimised, so the window is shown, and an automatic save is started,
    /// from here rather than from a posted action.</remarks>
    public void FinishRecording(string directory, RecordingManifest manifest)
    {
        var settings = _model.Settings;
        if (settings.AfterRecording == AfterRecording.Ask)
        {
            var before = _host.WindowState;
            _host.ShowWindow();
            Ui(() =>
            {
                OpenFinish(directory, manifest);
                _windowBeforeFinish = before;
            });
            return;
        }

        IReadOnlyList<string> tracks = [];
        if (settings.AfterRecording == AfterRecording.SaveWithAudio)
        {
            tracks = FinishPlanning.DefaultTrackSelection(manifest, settings.DefaultAudio);
            if (tracks.Count == 0)
            {
                // With no usable audio captured this falls back to a silent video.
                tracks = FinishPlanning.DefaultTrackSelection(manifest, AudioInclusion.Both);
            }
        }

        var stem = "Kino " + manifest.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var choices = new FinishChoices(settings.DefaultFormat, tracks,
            FinishPlanning.UniqueOutputPath(_model.VideoFolder, stem, OutputFormats.Extension(settings.DefaultFormat)));
        var (renderer, status) = _host.Renderer();
        var problem = renderer is null ? status : renderer.Explain(manifest, choices);
        if (problem is not null)
        {
            _host.ShowWindow();
            Ui(() =>
            {
                OpenFinish(directory, manifest);
                _model.FinishError = problem;
            });
            return;
        }

        _ = Task.Run(() => RenderAsync(renderer!, directory, manifest, choices, progress: null, automatic: true, CancellationToken.None));
    }

    private void OpenUnfinished(string id)
    {
        var directory = Path.Combine(_sessions.Root, id);
        var manifestPath = RecordingSessionStore.ManifestPath(directory);
        RecordingManifest? manifest = null;
        try
        {
            manifest = File.Exists(manifestPath) ? RecordingManifest.Read(manifestPath) : null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            manifest = null;
        }

        if (manifest is null)
        {
            _model.Status = "That recording cannot be finished because its details are missing. Discard it instead.";
            return;
        }

        if (manifest.State == RecordingState.Recording)
        {
            // Kino stopped mid-recording, so the duration was never written. Matroska staging files
            // stay readable when interrupted; the render reads as much as is there.
            manifest.DurationSeconds = 0;
            manifest.LastError ??= "Kino stopped before this recording finished. The end of the recording may be missing.";
        }

        OpenFinish(directory, manifest);
    }

    private void DiscardUnfinished(string id)
    {
        try
        {
            _sessions.Delete(Path.Combine(_sessions.Root, id));
            _model.Status = "Discarded the unfinished recording.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _model.Status = $"The recording could not be discarded: {exception.Message}";
        }

        RefreshUnfinished();
    }

    private void SelectFormat(string value)
    {
        if (!Enum.TryParse<OutputFormat>(value, out var format))
        {
            return;
        }

        var previous = Enum.Parse<OutputFormat>(_model.FinishFormat);
        _model.FinishFormat = format.ToString();
        if (_model.OutputPath.EndsWith(OutputFormats.Extension(previous), StringComparison.OrdinalIgnoreCase))
        {
            _model.OutputPath = _model.OutputPath[..^OutputFormats.Extension(previous).Length] + OutputFormats.Extension(format);
        }

        RefreshTracks();
    }

    private void ToggleTrack(string id)
    {
        var track = _finishManifest?.AudioTracks.FirstOrDefault(candidate => candidate.Id == id);
        if (track is null || !track.IsUsable)
        {
            return;
        }

        if (!_trackOrder.Remove(id))
        {
            _trackOrder.Add(id);
        }

        RefreshTracks();
    }

    private void RefreshTracks()
    {
        if (_finishManifest is not { } manifest)
        {
            return;
        }

        var rows = new List<TrackRow>();
        foreach (var (id, index) in _trackOrder.Select((id, index) => (id, index)))
        {
            var track = manifest.AudioTracks.First(candidate => candidate.Id == id);
            rows.Add(new TrackRow(id, track.Label, Describe(track), included: true, $"Track {index + 1}", usable: true));
        }

        foreach (var track in manifest.AudioTracks.Where(track => !_trackOrder.Contains(track.Id)))
        {
            rows.Add(new TrackRow(track.Id, track.Label, Describe(track), included: false, "", track.IsUsable));
        }

        _model.TrackRows = rows;
        _model.WithAudioAvailable = _trackOrder.Count > 0;
        _model.TrackSummary = _trackOrder.Count switch
        {
            0 => "No track is included, so the video can only be saved without audio.",
            1 => "Saving with audio includes one track.",
            var count => $"Saving with audio includes {count} tracks as separate streams, in this order.",
        };
    }

    /// <summary>Starts rendering in the background. Returns false, with the reason shown on the
    /// finishing screen, when it cannot start.</summary>
    private bool StartRender(bool withAudio)
    {
        if (_finishManifest is not { } manifest || _finishDirectory is not { } directory || _model.Rendering)
        {
            return false;
        }

        var (renderer, status) = _host.Renderer();
        if (renderer is null)
        {
            _model.FinishError = status;
            return false;
        }

        if (withAudio && _trackOrder.Count == 0)
        {
            return false;
        }

        var choices = new FinishChoices(Enum.Parse<OutputFormat>(_model.FinishFormat), withAudio ? [.. _trackOrder] : [], _model.OutputPath.Trim());
        if (renderer.Explain(manifest, choices) is { } problem)
        {
            _model.FinishError = problem;
            return false;
        }

        _model.FinishError = "";
        _model.Rendering = true;
        _model.RenderPercent = 0;
        _model.RenderStage = "Starting ffmpeg";
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        var token = _renderCancellation.Token;
        var progress = new Progress<RenderProgress>(value => Ui(() =>
        {
            _model.RenderPercent = (int)Math.Round(value.Fraction * 100);
            _model.RenderStage = value.Stage;
        }));
        _ = Task.Run(() => RenderAsync(renderer, directory, manifest, choices, progress, automatic: false, token), CancellationToken.None);
        return true;
    }

    /// <summary>Renders, then cleans up and copies the path, all off the document thread. Only the
    /// on-screen result is posted to it. An automatic save never opened the finishing screen, so a
    /// failure opens it with the reason.</summary>
    private async Task RenderAsync(IRecordingRenderer renderer, string directory, RecordingManifest manifest, FinishChoices choices,
        IProgress<RenderProgress>? progress, bool automatic, CancellationToken token)
    {
        try
        {
            await renderer.RenderAsync(directory, manifest, choices, progress, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var message = exception is OperationCanceledException ? "Rendering was cancelled." : exception.Message;
            manifest.State = RecordingState.RenderFailed;
            manifest.LastError = message;
            TryWriteManifest(directory, manifest);
            _host.ShowWindow();
            Ui(() =>
            {
                if (automatic)
                {
                    OpenFinish(directory, manifest);
                }

                _model.Rendering = false;
                _model.FinishError = $"{message} The recording is kept, so you can try again or choose other options.";
            });
            return;
        }

        // The output is validated and in place, so the temporary video and every audio source,
        // selected or not, can go.
        string? cleanupProblem = null;
        try
        {
            _sessions.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupProblem = exception.Message;
        }

        string? copyProblem = null;
        try
        {
            _host.CopyText(choices.OutputPath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            copyProblem = exception.Message;
        }

        VideoSaved?.Invoke(choices.OutputPath);
        var name = Path.GetFileName(choices.OutputPath);
        var status = copyProblem is null ? $"Saved {name} and copied its path." : $"Saved {name}, but its path could not be copied: {copyProblem}";
        if (cleanupProblem is not null)
        {
            status += $" The temporary files could not be removed: {cleanupProblem}";
        }

        Ui(() =>
        {
            AddRecent(choices.OutputPath, "Video");
            if (automatic)
            {
                _model.Status = status;
                RefreshUnfinished();
                return;
            }

            _model.Rendering = false;
            LeaveFinish(status);
            if (_windowBeforeFinish != WindowState.Shown)
            {
                _host.SetWindowState(_windowBeforeFinish);
                _windowBeforeFinish = WindowState.Shown;
            }
        });
    }

    private void Discard()
    {
        if (_finishDirectory is not { } directory)
        {
            return;
        }

        if (!_confirmDiscard)
        {
            _confirmDiscard = true;
            _model.DiscardLabel = "Click again to delete it";
            return;
        }

        try
        {
            _sessions.Delete(directory);
            LeaveFinish("Discarded the recording.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _model.FinishError = $"The recording could not be discarded: {exception.Message}";
        }
    }

    private void LeaveFinish(string status)
    {
        _finishDirectory = null;
        _finishManifest = null;
        _model.Status = status;
        _model.Page = "home";
        RefreshUnfinished();
    }

    public void AddRecent(string path, string kind)
    {
        _recent.RemoveAll(row => row.Path == path);
        _recent.Insert(0, new RecentRow(Path.GetFileName(path), path, kind));
        if (_recent.Count > 5) _recent.RemoveAt(_recent.Count - 1);
        _model.RecentRows = [.. _recent];
    }

    private void UpdateFromWorkflow()
    {
        var (label, cssClass) = _workflow.Phase switch
        {
            WorkflowPhase.SelectingImage => ("Selecting a screenshot", "selecting"),
            WorkflowPhase.SelectingVideo => ("Selecting a video region", "selecting"),
            WorkflowPhase.StartingRecording => ("Starting to record", "recording"),
            WorkflowPhase.Recording => ($"Recording · {_workflow.RecordingElapsed:mm\\:ss}", "recording"),
            WorkflowPhase.Stopping => ("Finishing the recording", "recording"),
            _ => ("Ready", "ready"),
        };
        _model.StateLabel = label;
        _model.StateClass = cssClass;
        if (_workflow.LastMessage.Length > 0)
        {
            _model.Status = _workflow.LastMessage;
        }

        if (_workflow.LastImagePath is { } saved && _recent.All(row => row.Path != saved))
        {
            AddRecent(saved, "Image");
        }
    }

    private void OnSettingsChanged()
    {
        try
        {
            _settingsStore.Save(_model.Settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _model.Status = $"Settings could not be saved: {exception.Message}";
        }

        _host.ApplySettings();
    }

    private static void TryWriteManifest(string directory, RecordingManifest manifest)
    {
        try
        {
            manifest.Write(RecordingSessionStore.ManifestPath(directory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The earlier manifest still describes the sources.
        }
    }

    private static string Describe(RecordedAudioTrack track) =>
        track.Problem ?? track.Warning ?? $"{Duration(track.DurationSeconds)} captured";

    private static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:0.0} s" : TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) => $"{bytes / (1024d * 1024d):0.0} MB";
}
