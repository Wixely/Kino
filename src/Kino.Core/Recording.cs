using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kino;

public enum OutputFormat
{
    Mp4,
    Mkv,
    WebM,
}

public static class OutputFormats
{
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.Mp4 => ".mp4",
        OutputFormat.Mkv => ".mkv",
        OutputFormat.WebM => ".webm",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string DisplayName(OutputFormat format) => format switch
    {
        OutputFormat.Mp4 => "MP4",
        OutputFormat.Mkv => "MKV",
        OutputFormat.WebM => "WebM",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

public enum AudioSourceKind
{
    System,
    Microphone,
}

/// <summary>Which audio tracks the finishing screen selects before the user changes anything.</summary>
public enum AudioInclusion
{
    None,
    System,
    Microphone,
    Both,
}

public enum StagingFormat
{
    /// <summary>Lossless H.264 in Matroska, written by ffmpeg during capture.</summary>
    LosslessMatroska,

    /// <summary>A Kino frame stream of JPEG frames and patches, rebuilt into frames when rendering.</summary>
    JpegFrames,
}

public enum RecordingState
{
    /// <summary>Capture is in progress, or the application stopped before capture finished.</summary>
    Recording,

    /// <summary>Capture finished; the sources wait for the user's finishing choices.</summary>
    AwaitingFinish,

    /// <summary>A render was attempted and failed or was cancelled. The sources are kept.</summary>
    RenderFailed,
}

/// <summary>One captured audio source, stored as its own temporary WAV file.</summary>
public sealed class RecordedAudioTrack
{
    public required string Id { get; init; }
    public required AudioSourceKind Kind { get; init; }
    public required string Label { get; init; }

    /// <summary>File name relative to the session directory.</summary>
    public required string FileName { get; init; }

    /// <summary>When the first sample was captured, relative to the first video frame.</summary>
    public double OffsetSeconds { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>Something the user should know about usable audio, such as a device lost part-way.</summary>
    public string? Warning { get; set; }

    /// <summary>Why the source has no usable audio, for example a denied or missing device.</summary>
    public string? Problem { get; set; }

    [JsonIgnore]
    public bool IsUsable => Problem is null && DurationSeconds > 0;
}

/// <summary>The small file kept beside temporary recording sources. It is enough to finish or discard
/// a recording after Kino restarts.</summary>
public sealed class RecordingManifest
{
    public const string FileName = "recording.json";

    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required PixelRect Region { get; init; }
    public required int FrameRate { get; init; }

    /// <summary>The staging video file name relative to the session directory.</summary>
    public string VideoFileName { get; set; } = "video.mkv";

    /// <summary>How the staging video is stored. Manifests from before the JPEG modes have no value and
    /// mean lossless Matroska.</summary>
    public StagingFormat VideoFormat { get; set; } = StagingFormat.LosslessMatroska;

    /// <summary>The recording mode used, for display.</summary>
    public RecordingMode? Mode { get; set; }

    /// <summary>How frames were captured and stored, for diagnostics.</summary>
    public string? CaptureDetails { get; set; }

    public RecordingState State { get; set; } = RecordingState.Recording;
    public double DurationSeconds { get; set; }
    public long FramesWritten { get; set; }

    /// <summary>Frames repeated because capture fell behind the frame clock.</summary>
    public long FramesRepeated { get; set; }

    public List<RecordedAudioTrack> AudioTracks { get; init; } = [];
    public string? LastError { get; set; }

    public static RecordingManifest? Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, KinoJsonContext.Default.RecordingManifest);
    }

    /// <summary>Writes through a temporary file, so a crash mid-write leaves the previous manifest.</summary>
    public void Write(string path)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, this, KinoJsonContext.Default.RecordingManifest);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>The user's choices on the finishing screen.</summary>
public sealed record FinishChoices(OutputFormat Format, IReadOnlyList<string> AudioTrackIds, string OutputPath);

public sealed record RenderProgress(double Fraction, string Stage);

/// <summary>Renders a finished recording into its delivery format.</summary>
public interface IRecordingRenderer
{
    string Name { get; }

    /// <summary>Explains why a choice cannot be rendered, or returns null when it can.</summary>
    string? Explain(RecordingManifest manifest, FinishChoices choices);

    /// <summary>Renders, validates the result, then moves it to <see cref="FinishChoices.OutputPath"/>.
    /// Throws when any step fails; the sources are left untouched either way.</summary>
    Task RenderAsync(string sessionDirectory, RecordingManifest manifest, FinishChoices choices,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RecordingManifest))]
[JsonSerializable(typeof(KinoSettings))]
internal sealed partial class KinoJsonContext : JsonSerializerContext;
