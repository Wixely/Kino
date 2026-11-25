using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kino;

public enum RenderBackend
{
    Auto,
    FFmpeg,
}

/// <summary>How frames are captured and stored while recording. Every mode renders the delivery file
/// with ffmpeg afterwards.</summary>
public enum RecordingMode
{
    /// <summary>Desktop Duplication capture, storing only what changed as JPEG patches. No ffmpeg is
    /// needed while recording.</summary>
    JpegDelta,

    /// <summary>Desktop Duplication capture, storing every frame as a full JPEG. No ffmpeg is needed
    /// while recording.</summary>
    Jpeg,

    /// <summary>GDI capture piped to ffmpeg as lossless H.264. Exact pixels, needs ffmpeg to record.</summary>
    Lossless,
}

/// <summary>Where the window was when Kino last ran, which is how it starts next time.</summary>
public enum WindowPlacement
{
    /// <summary>Hidden, with only the notification-area icon.</summary>
    Tray,

    /// <summary>Minimised to the taskbar.</summary>
    Taskbar,

    /// <summary>Open on screen.</summary>
    Open,
}

/// <summary>What happens when a recording stops.</summary>
public enum AfterRecording
{
    /// <summary>Open the window and ask whether to include audio.</summary>
    Ask,

    /// <summary>Save with the selected audio tracks without asking.</summary>
    SaveWithAudio,

    /// <summary>Save a silent video without asking.</summary>
    SaveWithoutAudio,
}

public sealed class KinoSettings
{
    /// <summary>Where saved screenshots go. Null uses the default Pictures\Kino folder.</summary>
    public string? ImageFolder { get; set; }

    /// <summary>Where rendered videos go. Null uses the default Videos\Kino folder.</summary>
    public string? VideoFolder { get; set; }

    public bool ShowRecordingFrame { get; set; } = true;
    public bool ShowCornerPreview { get; set; } = true;
    /// <summary>Whether recordings show the mouse pointer.</summary>
    public bool IncludeCursor { get; set; } = true;

    /// <summary>Whether the recording region moves to follow the mouse pointer, keeping its selected
    /// size. See <see cref="CursorFollower"/>.</summary>
    public bool FollowCursor { get; set; }

    /// <summary>Whether screenshots show the mouse pointer. Off by default because the pointer is
    /// always at the far corner when the screenshot is taken.</summary>
    public bool IncludeCursorInScreenshots { get; set; }
    public int FrameRate { get; set; } = 30;
    public RecordingMode RecordingMode { get; set; } = RecordingMode.JpegDelta;

    /// <summary>JPEG quality for the JPEG recording modes.</summary>
    public int JpegQuality { get; set; } = 90;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; } = true;

    /// <summary>The microphone to record. Null uses the system default.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>The tracks "Save with audio" includes. None falls back to every usable track.</summary>
    public AudioInclusion DefaultAudio { get; set; } = AudioInclusion.Both;
    public OutputFormat DefaultFormat { get; set; } = OutputFormat.Mp4;
    public AfterRecording AfterRecording { get; set; } = AfterRecording.Ask;
    public RenderBackend Backend { get; set; } = RenderBackend.Auto;

    /// <summary>An explicit ffmpeg executable. Null searches PATH.</summary>
    public string? FfmpegPath { get; set; }

    public bool ShortcutsEnabled { get; set; } = true;

    /// <summary>Kept up to date while Kino runs, so the next start opens the window the same way.</summary>
    public WindowPlacement WindowPlacement { get; set; } = WindowPlacement.Tray;

    /// <summary>The shortcut slot scheme <see cref="Shortcuts"/> was saved with. Bindings saved with an
    /// older scheme are replaced by the defaults when loading.</summary>
    public int ShortcutsVersion { get; set; } = CurrentShortcutsVersion;

    public Dictionary<ShortcutSlot, ShortcutBinding> Shortcuts { get; set; } = new(DefaultShortcuts.Create());

    /// <summary>Version 2 gave every action its own key (1 to 6).</summary>
    public const int CurrentShortcutsVersion = 2;

    public KinoSettings Normalized()
    {
        FrameRate = FrameRate is 15 or 24 or 30 or 60 ? FrameRate : 30;
        JpegQuality = JpegQuality is >= 50 and <= 100 ? JpegQuality : 90;
        foreach (var (slot, binding) in DefaultShortcuts.Create())
        {
            Shortcuts.TryAdd(slot, binding);
        }

        return this;
    }
}

public sealed class SettingsStore
{
    private readonly Func<string> _pathProvider;
    private bool _suspended;

    public SettingsStore(Func<string> pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public string Path => _pathProvider();

    public KinoSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                using var stream = File.OpenRead(Path);
                if (JsonNode.Parse(stream) is not JsonObject document)
                {
                    return new KinoSettings().Normalized();
                }

                // Older slot names no longer exist and their keys mean different actions now, so the
                // stored bindings are dropped rather than failing the whole file.
                if (document["ShortcutsVersion"]?.GetValue<int>() is not KinoSettings.CurrentShortcutsVersion)
                {
                    document.Remove("Shortcuts");
                    document["ShortcutsVersion"] = KinoSettings.CurrentShortcutsVersion;
                }

                return (document.Deserialize(KinoJsonContext.Default.KinoSettings) ?? new KinoSettings()).Normalized();
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            // An unreadable file falls back to defaults; the next save replaces it.
        }

        return new KinoSettings().Normalized();
    }

    public void Save(KinoSettings settings)
    {
        if (_suspended)
        {
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, settings, KinoJsonContext.Default.KinoSettings);
        }

        File.Move(temporary, Path, overwrite: true);
    }

    /// <summary>Stops writes for the rest of the process, for diagnostic runs that must not touch the
    /// user's settings.</summary>
    public void Suspend() => _suspended = true;
}

/// <summary>Where Kino keeps settings and temporary recordings. A <c>KinoData</c> folder beside the
/// executable selects portable mode; otherwise the per-user application-data folder is used.</summary>
public sealed class AppStorage
{
    public AppStorage(string executableDirectory, string? dataRootOverride = null)
    {
        var portable = Path.Combine(executableDirectory, "KinoData");
        if (dataRootOverride is not null)
        {
            Root = Path.GetFullPath(dataRootOverride);
            IsPortable = false;
        }
        else if (Directory.Exists(portable))
        {
            Root = portable;
            IsPortable = true;
        }
        else
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kino");
            IsPortable = false;
        }
    }

    public string Root { get; }
    public bool IsPortable { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string RecordingsRoot => Path.Combine(Root, "recordings");

    public static string DefaultImageFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Kino");

    public static string DefaultVideoFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kino");
}
