using System.Globalization;

namespace Kino.Media.FFmpeg;

/// <summary>The codecs and muxer used for one output format.</summary>
public sealed record RenderPreset(string Muxer, string VideoEncoder, string AudioEncoder, string VideoCodecName, string AudioCodecName)
{
    public static RenderPreset For(OutputFormat format) => format switch
    {
        OutputFormat.Mp4 => new("mp4", "libx264", "aac", "h264", "aac"),
        OutputFormat.Mkv => new("matroska", "libx264", "aac", "h264", "aac"),
        OutputFormat.WebM => new("webm", "libvpx-vp9", "libopus", "vp9", "opus"),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

/// <summary>Builds ffmpeg argument lists. Kept free of process handling so the exact commands can be
/// tested.</summary>
public static class RenderPlan
{
    /// <summary>Arguments for the capture-time encoder: raw BGRA frames on stdin, written losslessly to
    /// a Matroska file that stays readable if the process is interrupted.</summary>
    public static IReadOnlyList<string> Staging(int width, int height, int frameRate, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "rawvideo", "-pix_fmt", "bgra",
        "-s", $"{width}x{height}",
        "-framerate", frameRate.ToString(CultureInfo.InvariantCulture),
        "-i", "pipe:0",
        "-c:v", FfmpegCapabilities.StagingEncoder, "-preset", "ultrafast", "-qp", "0", "-pix_fmt", "bgr0",
        "-g", (frameRate * 2).ToString(CultureInfo.InvariantCulture),
        "-f", "matroska", outputPath,
    ];

    /// <summary>Arguments for the final render. Selected audio tracks become separate output streams in
    /// the given order; none are mixed.</summary>
    public static IReadOnlyList<string> Final(string sessionDirectory, RecordingManifest manifest, FinishChoices choices, string temporaryOutputPath)
    {
        var preset = RenderPreset.For(choices.Format);
        var tracks = choices.AudioTrackIds
            .Select(id => manifest.AudioTracks.First(track => track.Id == id))
            .ToArray();
        var arguments = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
        if (manifest.VideoFormat == StagingFormat.JpegFrames)
        {
            // JPEG recordings are rebuilt into frames by Kino and arrive on stdin.
            arguments.AddRange([
                "-f", "rawvideo", "-pix_fmt", "bgra",
                "-s", $"{manifest.Region.Width}x{manifest.Region.Height}",
                "-framerate", manifest.FrameRate.ToString(CultureInfo.InvariantCulture),
                "-i", "pipe:0",
            ]);
        }
        else
        {
            arguments.Insert(1, "-nostdin");
            arguments.AddRange(["-i", Path.Combine(sessionDirectory, manifest.VideoFileName)]);
        }
        foreach (var track in tracks)
        {
            // A positive offset means the audio started after the first frame and is delayed to match;
            // a negative one means it started earlier and its head is skipped.
            if (track.OffsetSeconds > 0)
            {
                arguments.AddRange(["-itsoffset", Seconds(track.OffsetSeconds)]);
            }
            else if (track.OffsetSeconds < 0)
            {
                arguments.AddRange(["-ss", Seconds(-track.OffsetSeconds)]);
            }

            arguments.AddRange(["-i", Path.Combine(sessionDirectory, track.FileName)]);
        }

        arguments.AddRange(["-map", "0:v:0"]);
        for (var index = 0; index < tracks.Length; index++)
        {
            arguments.AddRange(["-map", $"{index + 1}:a:0"]);
        }

        arguments.AddRange([
            "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-c:v", preset.VideoEncoder,
        ]);
        arguments.AddRange(choices.Format == OutputFormat.WebM
            ? ["-b:v", "0", "-crf", "32", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1"]
            : ["-preset", "medium", "-crf", "20"]);

        if (tracks.Length > 0)
        {
            arguments.AddRange(["-c:a", preset.AudioEncoder, "-b:a", choices.Format == OutputFormat.WebM ? "160k" : "192k", "-ac", "2"]);
            for (var index = 0; index < tracks.Length; index++)
            {
                var label = tracks[index].Label;
                arguments.AddRange([$"-metadata:s:a:{index}", $"title={label}"]);
                arguments.AddRange([$"-metadata:s:a:{index}", $"handler_name={label}"]);
                arguments.AddRange([$"-disposition:a:{index}", index == 0 ? "default" : "0"]);
            }
        }
        else
        {
            arguments.Add("-an");
        }

        if (manifest.DurationSeconds > 0)
        {
            arguments.AddRange(["-t", Seconds(manifest.DurationSeconds)]);
        }

        if (choices.Format == OutputFormat.Mp4)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.AddRange(["-f", preset.Muxer, temporaryOutputPath]);
        return arguments;
    }

    private static string Seconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
}
