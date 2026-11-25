using System.Diagnostics;
using System.Threading.Channels;
using Kino.App;
using Kino.Media.FFmpeg;
using Kino.Platform.Windows;
using SkiaSharp;

namespace Kino.Windows;

/// <summary>The ffmpeg found for the current settings, and what it can do.</summary>
internal sealed class FfmpegState
{
    private readonly Func<KinoSettings> _settings;
    private volatile Snapshot _current = new(null, null, "Checking for ffmpeg…");

    public FfmpegState(Func<KinoSettings> settings)
    {
        _settings = settings;
    }

    public FfmpegTools? Tools => _current.Capabilities is { CanStage: true } ? _current.Tools : null;
    public string Status => _current.Status;
    public bool IsAvailable => Tools is not null;

    /// <summary>Why recording cannot start, or null.</summary>
    public string? Unavailable => IsAvailable ? null : _current.Status;

    public (IRecordingRenderer? Renderer, string Status) Renderer() =>
        _current is { Tools: { } tools, Capabilities: { } capabilities }
            ? (new FfmpegRenderer(tools, capabilities), _current.Status)
            : (null, _current.Status);

    public async Task RefreshAsync()
    {
        var explicitPath = _settings().FfmpegPath;
        var tools = FfmpegTools.Locate(explicitPath);
        if (tools is null)
        {
            _current = new(null, null, explicitPath is null
                ? "ffmpeg was not found on PATH. Screenshots work; install ffmpeg or set its path in Settings to record video."
                : $"No ffmpeg (with ffprobe beside it) at {explicitPath}.");
            return;
        }

        try
        {
            var capabilities = await FfmpegCapabilities.ProbeAsync(tools).ConfigureAwait(false);
            var missing = Enum.GetValues<OutputFormat>().Where(format => capabilities.Explain(format) is not null).Select(OutputFormats.DisplayName).ToArray();
            var status = !capabilities.CanStage
                ? $"{tools.FfmpegPath} lacks {FfmpegCapabilities.StagingEncoder}, which recording needs."
                : missing.Length == 0
                    ? $"Rendering with {capabilities.Version.Replace("ffmpeg version ", "ffmpeg ", StringComparison.Ordinal).Split(" Copyright", 2)[0]}"
                    : $"Rendering with {tools.FfmpegPath}; it cannot write {string.Join(", ", missing)}.";
            _current = new(tools, capabilities, status);
        }
        catch (Exception exception) when (exception is FfmpegException or System.ComponentModel.Win32Exception or IOException)
        {
            _current = new(null, null, $"{tools.FfmpegPath} could not be run: {exception.Message}");
        }
    }

    private sealed record Snapshot(FfmpegTools? Tools, FfmpegCapabilities? Capabilities, string Status);
}

/// <summary>Connects the shell thread, the capture workflow and the CupriFace app.</summary>
internal sealed class KinoHost : IKinoHost, IDisposable
{
    private const int EscapeHotkey = 99;
    private static readonly ShortcutSlot[] Slots = Enum.GetValues<ShortcutSlot>();
    private readonly ShellThread _shell;
    private readonly KinoSettings _settings;
    private readonly FfmpegState _ffmpeg;
    private readonly CaptureWorkflow _workflow;
    private readonly ICapturePlatform _platform;
    private readonly Channel<Func<Task>> _actions = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly byte[] _iconPng;
    private readonly byte[] _recordingIconPng;
    private KinoApp? _app;
    private TrayIcon? _tray;
    private bool _escapeRegistered;
    private bool _quitting;

    public KinoHost(ShellThread shell, KinoSettings settings, FfmpegState ffmpeg, CaptureWorkflow workflow, ICapturePlatform platform, byte[] iconPng)
    {
        _shell = shell;
        _settings = settings;
        _ffmpeg = ffmpeg;
        _workflow = workflow;
        _platform = platform;
        _iconPng = iconPng;
        _recordingIconPng = WithRecordingDot(iconPng);
        _ = Task.Run(ProcessActionsAsync);
    }

    public void Attach(KinoApp app)
    {
        _app = app;
        _tray = new TrayIcon(_shell, _iconPng, BuildMenu);
        _tray.Activated += ShowWindow;
        _shell.HotkeyPressed += OnHotkey;
        _workflow.Changed += OnWorkflowChanged;
        app.VideoSaved += path => _tray.ShowBalloon("Video saved, path copied", Path.GetFileName(path), error: false);
        _shell.Post(RegisterShortcuts);
    }

    public void ShowWindow()
    {
        AppWindow.AllowForeground();
        AppWindow.Show();
    }

    public void HideWindow() => AppWindow.Hide();

    public WindowState WindowState =>
        !AppWindow.IsVisible ? WindowState.Hidden : AppWindow.IsMinimized ? WindowState.Minimized : WindowState.Shown;

    public void SetWindowState(WindowState state)
    {
        switch (state)
        {
            case WindowState.Hidden:
                AppWindow.Hide();
                break;
            case WindowState.Minimized:
                AppWindow.Minimize();
                break;
            default:
                ShowWindow();
                break;
        }
    }

    public void ApplySettings()
    {
        _shell.Post(RegisterShortcuts);
        _ = RefreshFfmpegAsync();
    }

    public async Task RefreshFfmpegAsync()
    {
        await _ffmpeg.RefreshAsync().ConfigureAwait(false);
        _app?.SetFfmpegStatus(_ffmpeg.Status, _ffmpeg.IsAvailable);
    }

    public void RevealInFolder(string path) => Start("explorer.exe", $"/select,\"{path}\"");

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Notify("Folder unavailable", exception.Message);
        }
    }

    public IReadOnlyList<(string Id, string Name)> Microphones() =>
        AudioDevices.Microphones().Select(device => (device.Id, device.Name)).ToArray();

    public (IRecordingRenderer? Renderer, string Status) Renderer() => _ffmpeg.Renderer();

    public void CopyText(string text) => _platform.CopyText(text);

    public void Notify(string title, string message) => _tray?.ShowBalloon(title, message, error: true);

    /// <summary>Stops an active recording so its sources are kept, then closes the window, which ends
    /// the CupriFace host loop.</summary>
    public async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        if (_workflow.Phase == WorkflowPhase.Recording)
        {
            _tray?.ShowBalloon("Stopping the recording", "Kino keeps it as an unfinished recording.", error: false);
            await Enqueue(_workflow.StopAsync).ConfigureAwait(false);
        }

        AppWindow.Quit();
    }

    public void Dispose()
    {
        _actions.Writer.TryComplete();
        _tray?.Dispose();
        _shell.Invoke(() =>
        {
            foreach (var slot in Slots)
            {
                _shell.UnregisterHotkey(HotkeyId(slot));
            }

            _shell.UnregisterHotkey(EscapeHotkey);
        });
    }

    private static int HotkeyId(ShortcutSlot slot) => (int)slot + 1;

    private void RegisterShortcuts()
    {
        foreach (var slot in Slots)
        {
            _shell.UnregisterHotkey(HotkeyId(slot));
        }

        var problems = new List<string>();
        if (_settings.ShortcutsEnabled)
        {
            var conflicts = DefaultShortcuts.FindConflicts(_settings.Shortcuts);
            if (conflicts.Count > 0)
            {
                problems.AddRange(conflicts.Select(pair => $"{_settings.Shortcuts[pair.First]} is assigned twice."));
            }
            else
            {
                foreach (var slot in Slots)
                {
                    if (!_shell.RegisterHotkey(HotkeyId(slot), _settings.Shortcuts[slot]))
                    {
                        problems.Add($"{_settings.Shortcuts[slot]} is already used by another application.");
                    }
                }
            }
        }
        else
        {
            problems.Add("Shortcuts are turned off.");
        }

        _app?.SetShortcutWarning(string.Join(" ", problems));
        _app?.ViewModel.RefreshShortcutRows();
        UpdateTray();
    }

    private void OnHotkey(int id)
    {
        // A hotkey grants this process the right to bring a window forward, which the finishing
        // screen relies on after a recording stops.
        AppWindow.AllowForeground();
        if (id == EscapeHotkey)
        {
            _ = Enqueue(() =>
            {
                _workflow.Cancel();
                return Task.CompletedTask;
            });
            return;
        }

        if (id >= 1 && id <= Slots.Length)
        {
            var slot = Slots[id - 1];
            _ = Enqueue(() => _workflow.HandleAsync(slot));
        }
    }

    // Hotkey actions run one at a time, in order, away from the shell thread. The workflow calls back
    // into the shell thread to show and hide overlays and to write the clipboard, so running it on that
    // thread could deadlock.
    private Task Enqueue(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _actions.Writer.TryWrite(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    private async Task ProcessActionsAsync()
    {
        await foreach (var action in _actions.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Kino action failed: {exception}");
            }
        }
    }

    private void OnWorkflowChanged()
    {
        var selecting = _workflow.IsSelecting;
        _shell.Post(() =>
        {
            // Escape is claimed only during a selection, so it keeps working everywhere else.
            if (selecting && !_escapeRegistered)
            {
                _escapeRegistered = _shell.RegisterHotkey(EscapeHotkey, new ShortcutBinding(ShortcutModifiers.None, ShortcutKey.Escape));
            }
            else if (!selecting && _escapeRegistered)
            {
                _shell.UnregisterHotkey(EscapeHotkey);
                _escapeRegistered = false;
            }
        });
        UpdateTray();
    }

    private void UpdateTray()
    {
        var recording = _workflow.Phase is WorkflowPhase.Recording or WorkflowPhase.StartingRecording or WorkflowPhase.Stopping;
        var tip = recording ? "Kino · recording" : _settings.ShortcutsEnabled ? "Kino" : "Kino · shortcuts off";
        _tray?.SetState(recording ? _recordingIconPng : _iconPng, tip);
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var recording = _workflow.Phase == WorkflowPhase.Recording;
        var stop = _settings.Shortcuts[ShortcutSlot.StopRecording];
        return
        [
            new(recording ? $"Recording · {_workflow.RecordingElapsed:mm\\:ss}" : "Kino is ready", null, Enabled: false),
            new("Open Kino", ShowWindow),
            new($"Stop recording\t{stop}", recording ? () => _ = Enqueue(_workflow.StopAsync) : null, Enabled: recording),
            new("Shortcuts enabled", () => { if (_app is { } app) app.ViewModel.ShortcutsEnabled = !_settings.ShortcutsEnabled; }, Checked: _settings.ShortcutsEnabled),
            TrayMenuItem.Separator,
            new("Open screenshot folder", () => OpenFolder(_settings.ImageFolder ?? AppStorage.DefaultImageFolder)),
            new("Open video folder", () => OpenFolder(_settings.VideoFolder ?? AppStorage.DefaultVideoFolder)),
            TrayMenuItem.Separator,
            new("Quit Kino", () => _ = QuitAsync()),
        ];
    }

    private void Start(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Notify("Could not open Explorer", exception.Message);
        }
    }

    /// <summary>The tray icon with a red dot, shown while recording.</summary>
    private static byte[] WithRecordingDot(byte[] png)
    {
        using var source = SKBitmap.Decode(png);
        using var surface = SKSurface.Create(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, 0, 0);
        var radius = source.Width * 0.22f;
        var centre = new SKPoint(source.Width - radius - 2, source.Height - radius - 2);
        using var ring = new SKPaint { Color = new SKColor(0x12, 0x12, 0x12), IsAntialias = true };
        using var dot = new SKPaint { Color = new SKColor(0xe8, 0x2a, 0x1e), IsAntialias = true };
        canvas.DrawCircle(centre, radius + (source.Width * 0.04f), ring);
        canvas.DrawCircle(centre, radius, dot);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
