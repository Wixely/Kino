using System.Diagnostics;
using System.Text.Json;
using Kino.Media;

namespace Kino.Media.FFmpeg;

/// <summary>An ffmpeg process that receives raw BGRA frames on stdin and writes the temporary
/// recording.</summary>
public sealed class StagingEncoder : IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Task<string> _errors;
    private bool _completed;

    public StagingEncoder(FfmpegTools tools, int width, int height, int frameRate, string outputPath)
    {
        FrameBytes = width * height * 4;
        _process = FfmpegProcess.Start(tools.FfmpegPath, RenderPlan.Staging(width, height, frameRate, outputPath), redirectInput: true);
        _input = _process.StandardInput.BaseStream;
        _errors = _process.StandardError.ReadToEndAsync();
        _ = _process.StandardOutput.ReadToEndAsync();
    }

    public int FrameBytes { get; }

    /// <summary>Writes one frame. Blocks when the encoder is behind, which the caller measures as
    /// lateness against its frame clock.</summary>
    public void WriteFrame(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length != FrameBytes)
        {
            throw new ArgumentException("The frame does not match the staging size.", nameof(bgra));
        }

        try
        {
            _input.Write(bgra);
        }
        catch (IOException exception)
        {
            throw new FfmpegException("The staging encoder stopped accepting frames.", exception);
        }
    }

    /// <summary>Closes stdin so ffmpeg flushes and finalises the file.</summary>
    public async Task CompleteAsync(TimeSpan timeout)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        try
        {
            _input.Flush();
            _input.Dispose();
        }
        catch (IOException)
        {
            // The exit code below reports what happened.
        }

        using var bounded = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
            throw new FfmpegException("The staging encoder did not finish in time.", "");
        }

        if (_process.ExitCode != 0)
        {
            throw new FfmpegException($"The staging encoder exited with code {_process.ExitCode}.", await _errors.ConfigureAwait(false));
        }
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }
        }

        _process.Dispose();
    }
}

public sealed record MediaStreamInfo(int Index, string CodecType, string CodecName, string? Title, double? DurationSeconds);

public static class Ffprobe
{
    public static async Task<IReadOnlyList<MediaStreamInfo>> StreamsAsync(FfmpegTools tools, string path, CancellationToken cancellationToken = default)
    {
        var json = await FfmpegProcess.CaptureOutputAsync(tools.FfprobePath,
            ["-v", "error", "-show_entries", "stream=index,codec_type,codec_name,duration:stream_tags=title,handler_name,DURATION", "-of", "json", path],
            cancellationToken).ConfigureAwait(false);
        return ParseStreams(json);
    }

    public static IReadOnlyList<MediaStreamInfo> ParseStreams(string json)
    {
        using var document = JsonDocument.Parse(json);
        var streams = new List<MediaStreamInfo>();
        if (!document.RootElement.TryGetProperty("streams", out var array))
        {
            return streams;
        }

        foreach (var stream in array.EnumerateArray())
        {
            string? title = null;
            double? duration = null;
            if (stream.TryGetProperty("duration", out var durationValue) &&
                double.TryParse(durationValue.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }

            if (stream.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                        (title is null && tag.Name.Equals("handler_name", StringComparison.OrdinalIgnoreCase)))
                    {
                        title = tag.Value.GetString();
                    }

                    // Matroska stores per-stream duration as a tag in hh:mm:ss.fffffffff form.
                    if (duration is null && tag.Name.Equals("DURATION", StringComparison.OrdinalIgnoreCase) &&
                        TimeSpan.TryParse(TrimFraction(tag.Value.GetString()), System.Globalization.CultureInfo.InvariantCulture, out var span))
                    {
                        duration = span.TotalSeconds;
                    }
                }
            }

            streams.Add(new MediaStreamInfo(
                stream.GetProperty("index").GetInt32(),
                stream.TryGetProperty("codec_type", out var type) ? type.GetString() ?? "" : "",
                stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "",
                title,
                duration));
        }

        return streams;
    }

    /// <summary>Checks that a rendered file holds one video stream followed by exactly the selected
    /// audio tracks, in order, with the expected codecs. Returns the problem, or null.</summary>
    public static string? Verify(IReadOnlyList<MediaStreamInfo> streams, OutputFormat format, IReadOnlyList<string> expectedAudioLabels)
    {
        var preset = RenderPreset.For(format);
        var video = streams.Where(stream => stream.CodecType == "video").ToArray();
        var audio = streams.Where(stream => stream.CodecType == "audio").ToArray();
        if (video.Length != 1 || video[0].CodecName != preset.VideoCodecName)
        {
            return $"Expected one {preset.VideoCodecName} video stream, found {string.Join(", ", video.Select(stream => stream.CodecName)).DefaultIfEmpty("none")}.";
        }

        if (audio.Length != expectedAudioLabels.Count)
        {
            return $"Expected {expectedAudioLabels.Count} audio streams, found {audio.Length}.";
        }

        for (var index = 0; index < audio.Length; index++)
        {
            if (audio[index].CodecName != preset.AudioCodecName)
            {
                return $"Audio stream {index + 1} is {audio[index].CodecName}, expected {preset.AudioCodecName}.";
            }

            if (audio[index].Title is { } title && !string.Equals(title, expectedAudioLabels[index], StringComparison.Ordinal))
            {
                return $"Audio stream {index + 1} is '{title}', expected '{expectedAudioLabels[index]}'.";
            }
        }

        return null;
    }

    // Matroska writes nine fractional digits; TimeSpan parses at most seven.
    private static string? TrimFraction(string? value) =>
        value is null ? null : value.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0 && value.Length > dot + 8 ? value[..(dot + 8)] : value;

    private static string DefaultIfEmpty(this string value, string fallback) => value.Length == 0 ? fallback : value;
}

/// <summary>Renders with system ffmpeg, validates the result with ffprobe, then commits it.</summary>
public sealed class FfmpegRenderer : IRecordingRenderer
{
    private readonly FfmpegTools _tools;
    private readonly FfmpegCapabilities _capabilities;

    public FfmpegRenderer(FfmpegTools tools, FfmpegCapabilities capabilities)
    {
        _tools = tools;
        _capabilities = capabilities;
    }

    public string Name => "FFmpeg";

    public string? Explain(RecordingManifest manifest, FinishChoices choices) =>
        _capabilities.Explain(choices.Format) ?? FinishPlanning.Validate(manifest, choices);

    public async Task RenderAsync(string sessionDirectory, RecordingManifest manifest, FinishChoices choices,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        if (Explain(manifest, choices) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(choices.OutputPath))!;
        Directory.CreateDirectory(folder);
        // Rendered beside the destination so the final move is a rename on the same volume.
        var temporary = Path.Combine(folder, $".{Path.GetFileName(choices.OutputPath)}.kino-partial");
        try
        {
            progress?.Report(new RenderProgress(0, "Rendering"));
            Func<Stream, CancellationToken, Task>? input = null;
            var expectedSeconds = manifest.DurationSeconds;
            if (manifest.VideoFormat == StagingFormat.JpegFrames)
            {
                var stream = Path.Combine(sessionDirectory, manifest.VideoFileName);
                // An interrupted recording never wrote its frame count; the stream itself knows.
                var frames = manifest.FramesWritten > 0 ? manifest.FramesWritten : FrameFile.CountSlots(stream);
                if (frames == 0)
                {
                    throw new InvalidDataException("The recording holds no frames.");
                }

                expectedSeconds = frames / (double)manifest.FrameRate;
                input = (stdin, token) => FrameFile.WriteFramesAsync(stream, frames, stdin, null, token);
            }

            await FfmpegProcess.RunJobAsync(
                _tools.FfmpegPath,
                RenderPlan.Final(sessionDirectory, manifest, choices, temporary),
                expectedSeconds,
                new Progress<double>(fraction => progress?.Report(new RenderProgress(fraction * 0.95, "Rendering"))),
                cancellationToken,
                input).ConfigureAwait(false);

            progress?.Report(new RenderProgress(0.96, "Checking the rendered file"));
            var streams = await Ffprobe.StreamsAsync(_tools, temporary, cancellationToken).ConfigureAwait(false);
            var labels = choices.AudioTrackIds.Select(id => manifest.AudioTracks.First(track => track.Id == id).Label).ToArray();
            if (Ffprobe.Verify(streams, choices.Format, labels) is { } mismatch)
            {
                throw new FfmpegException($"The rendered file failed validation: {mismatch}", "");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, choices.OutputPath, overwrite: false);
            progress?.Report(new RenderProgress(1, "Done"));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
