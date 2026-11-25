using CupriFace.Binding;

namespace Kino.App;

/// <summary>The view model bound by Kino.html. Setters for persisted options update
/// <see cref="Settings"/> and raise <see cref="SettingsChanged"/>.</summary>
[CupriBindable]
public sealed partial class KinoModel
{
    private readonly KinoSettings _settings;

    public KinoModel(KinoSettings settings)
    {
        _settings = settings;
        RefreshShortcutRows();
    }

    public event Action? SettingsChanged;

    public KinoSettings Settings => _settings;

    // Navigation.
    public string Page { get; set; } = "home";
    public string HomeDisplay => Page == "home" ? "flex" : "none";
    public string FinishDisplay => Page == "finish" ? "flex" : "none";
    public string SettingsDisplay => Page == "settings" ? "flex" : "none";

    // Home.
    private string _stateLabel = "Ready";

    /// <summary>Supplies a label that changes on its own, such as the recording clock. Read on every
    /// rebind, so the refresh tick keeps it current.</summary>
    internal Func<string?>? LiveStateLabel { get; set; }

    public string StateLabel
    {
        get => LiveStateLabel?.Invoke() ?? _stateLabel;
        set => _stateLabel = value;
    }

    public string StateClass { get; set; } = "ready";
    public string Status { get; set; } = "Kino is in the tray. Use the shortcuts below from any application.";
    public List<ShortcutRow> ShortcutRows { get; private set; } = [];
    public string ShortcutWarning { get; set; } = "";
    public string ShortcutWarningDisplay => ShortcutWarning.Length > 0 ? "flex" : "none";
    public string FfmpegStatus { get; set; } = "Checking for ffmpeg…";
    public string FfmpegClass { get; set; } = "";
    public List<UnfinishedRow> UnfinishedRows { get; set; } = [];
    public string UnfinishedDisplay => UnfinishedRows.Count > 0 ? "flex" : "none";
    public List<RecentRow> RecentRows { get; set; } = [];
    public string RecentDisplay => RecentRows.Count > 0 ? "flex" : "none";
    public string AppVersion { get; } = typeof(KinoModel).Assembly.GetName().Version?.ToString(3) ?? "development";

    // Finishing screen.
    public string FinishTitle { get; set; } = "";
    public string FinishDetail { get; set; } = "";
    public string FinishWarning { get; set; } = "";
    public string FinishWarningDisplay => FinishWarning.Length > 0 ? "flex" : "none";
    public string FinishFormat { get; set; } = nameof(OutputFormat.Mp4);
    public string Mp4Class => FinishFormat == nameof(OutputFormat.Mp4) ? "selected" : "";
    public string MkvClass => FinishFormat == nameof(OutputFormat.Mkv) ? "selected" : "";
    public string WebMClass => FinishFormat == nameof(OutputFormat.WebM) ? "selected" : "";
    public List<TrackRow> TrackRows { get; set; } = [];
    public string TracksEmptyDisplay => TrackRows.Count == 0 ? "flex" : "none";
    public string TrackSummary { get; set; } = "";
    public bool WithAudioAvailable { get; set; }
    public string WithAudioDisplay => WithAudioAvailable ? "flex" : "none";
    public string OutputPath { get; set; } = "";
    public string FinishError { get; set; } = "";
    public string FinishErrorDisplay => FinishError.Length > 0 ? "flex" : "none";
    public bool Rendering { get; set; }
    public string RenderDisplay => Rendering ? "flex" : "none";
    public string FinishActionsDisplay => Rendering ? "none" : "flex";
    public int RenderPercent { get; set; }
    public string RenderStage { get; set; } = "";
    public string DiscardLabel { get; set; } = "Discard recording";

    // Settings.
    public bool ShowRecordingFrame
    {
        get => _settings.ShowRecordingFrame;
        set => Update(() => _settings.ShowRecordingFrame = value);
    }

    public bool ShowCornerPreview
    {
        get => _settings.ShowCornerPreview;
        set => Update(() => _settings.ShowCornerPreview = value);
    }

    public bool IncludeCursor
    {
        get => _settings.IncludeCursor;
        set => Update(() => _settings.IncludeCursor = value);
    }

    public bool IncludeCursorInScreenshots
    {
        get => _settings.IncludeCursorInScreenshots;
        set => Update(() => _settings.IncludeCursorInScreenshots = value);
    }

    public bool RecordSystemAudio
    {
        get => _settings.RecordSystemAudio;
        set => Update(() => _settings.RecordSystemAudio = value);
    }

    public bool RecordMicrophone
    {
        get => _settings.RecordMicrophone;
        set => Update(() => _settings.RecordMicrophone = value);
    }

    public bool ShortcutsEnabled
    {
        get => _settings.ShortcutsEnabled;
        set => Update(() => _settings.ShortcutsEnabled = value);
    }

    public string DefaultAudio
    {
        get => _settings.DefaultAudio.ToString();
        set
        {
            if (Enum.TryParse<AudioInclusion>(value, out var parsed))
            {
                Update(() => _settings.DefaultAudio = parsed);
            }
        }
    }

    public string DefaultFormat
    {
        get => _settings.DefaultFormat.ToString();
        set
        {
            if (Enum.TryParse<OutputFormat>(value, out var parsed))
            {
                Update(() => _settings.DefaultFormat = parsed);
            }
        }
    }

    public string RecordingMode
    {
        get => _settings.RecordingMode.ToString();
        set
        {
            if (Enum.TryParse<Kino.RecordingMode>(value, out var parsed))
            {
                Update(() => _settings.RecordingMode = parsed);
            }
        }
    }

    public string JpegQuality
    {
        get => _settings.JpegQuality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                Update(() => _settings.JpegQuality = parsed);
            }
        }
    }

    public string JpegQualityDisplay => _settings.RecordingMode == Kino.RecordingMode.Lossless ? "none" : "flex";

    public string AfterRecording
    {
        get => _settings.AfterRecording.ToString();
        set
        {
            if (Enum.TryParse<Kino.AfterRecording>(value, out var parsed))
            {
                Update(() => _settings.AfterRecording = parsed);
            }
        }
    }

    public string FrameRate
    {
        get => _settings.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                Update(() => _settings.FrameRate = parsed);
            }
        }
    }

    public string MicrophoneId
    {
        get => _settings.MicrophoneDeviceId ?? "";
        set => Update(() => _settings.MicrophoneDeviceId = string.IsNullOrEmpty(value) ? null : value);
    }

    public List<OptionRow> MicrophoneOptions { get; set; } = [new("", "System default")];

    public string ImageFolder
    {
        get => _settings.ImageFolder ?? AppStorage.DefaultImageFolder;
        set => Update(() => _settings.ImageFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string VideoFolder
    {
        get => _settings.VideoFolder ?? AppStorage.DefaultVideoFolder;
        set => Update(() => _settings.VideoFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string FfmpegPath
    {
        get => _settings.FfmpegPath ?? "";
        set => Update(() => _settings.FfmpegPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string StoragePath { get; set; } = "";

    // Each picker needs its own open state to toggle.
    public bool FrameRateOpen { get; set; }
    public bool MicrophoneOpen { get; set; }
    public bool DefaultAudioOpen { get; set; }
    public bool DefaultFormatOpen { get; set; }
    public bool AfterRecordingOpen { get; set; }
    public bool RecordingModeOpen { get; set; }
    public bool JpegQualityOpen { get; set; }

    public void RefreshShortcutRows()
    {
        var bindings = _settings.Shortcuts;
        ShortcutRows =
        [
            new($"{bindings[ShortcutSlot.ImageCorner]}", "Mark the first corner of a screenshot"),
            new($"{bindings[ShortcutSlot.ImageCapture]}", "Mark the second corner and copy the screenshot"),
            new($"{bindings[ShortcutSlot.SaveImage]}", "Save the last screenshot and copy its path"),
            new($"{bindings[ShortcutSlot.VideoCorner]}", "Mark the first corner of a video"),
            new($"{bindings[ShortcutSlot.VideoStart]}", "Mark the second corner and start recording"),
            new($"{bindings[ShortcutSlot.StopRecording]}", "Stop recording, choose audio, save and copy the path"),
        ];
    }

    public string NumpadLayoutClass => Matches(DefaultShortcuts.Create()) ? "selected" : "";
    public string CompactLayoutClass => Matches(DefaultShortcuts.CreateCompact()) ? "selected" : "";

    private bool Matches(IReadOnlyDictionary<ShortcutSlot, ShortcutBinding> layout) =>
        layout.All(pair => _settings.Shortcuts.TryGetValue(pair.Key, out var binding) && binding == pair.Value);

    public void ApplyShortcutLayout(IReadOnlyDictionary<ShortcutSlot, ShortcutBinding> layout) => Update(() =>
    {
        _settings.Shortcuts = new Dictionary<ShortcutSlot, ShortcutBinding>(layout);
        RefreshShortcutRows();
    });

    private void Update(Action change)
    {
        change();
        SettingsChanged?.Invoke();
    }
}

[CupriBindable]
public sealed partial class ShortcutRow(string keys, string action)
{
    public string Keys { get; } = keys;
    public string Action { get; } = action;
}

[CupriBindable]
public sealed partial class UnfinishedRow(string id, string title, string detail)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
}

[CupriBindable]
public sealed partial class RecentRow(string name, string path, string kind)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Kind { get; } = kind;
}

[CupriBindable]
public sealed partial class TrackRow(string id, string label, string detail, bool included, string position, bool usable)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Detail { get; } = detail;
    public string IncludedLabel { get; } = !usable ? "Unavailable" : included ? "Included" : "Not included";
    public string RowClass { get; } = !usable ? "unavailable" : included ? "included" : "";
    public string Position { get; } = position;
    public string MoveDisplay { get; } = included ? "flex" : "none";
}

[CupriBindable]
public sealed partial class OptionRow(string value, string label)
{
    public string Value { get; } = value;
    public string Label { get; } = label;
}
