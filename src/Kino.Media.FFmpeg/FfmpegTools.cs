using System.Diagnostics;

namespace Kino.Media.FFmpeg;

/// <summary>The ffmpeg and ffprobe executables Kino runs.</summary>
public sealed record FfmpegTools(string FfmpegPath, string FfprobePath)
{
    /// <summary>Finds ffmpeg at an explicit path or on PATH, with ffprobe beside it or on PATH.
    /// Returns null when either is missing.</summary>
    public static FfmpegTools? Locate(string? explicitFfmpegPath = null)
    {
        var ffmpeg = !string.IsNullOrWhiteSpace(explicitFfmpegPath)
            ? (File.Exists(explicitFfmpegPath) ? Path.GetFullPath(explicitFfmpegPath) : null)
            : FindOnPath("ffmpeg");
        if (ffmpeg is null)
        {
            return null;
        }

        var beside = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var ffprobe = File.Exists(beside) ? beside : FindOnPath("ffprobe");
        return ffprobe is null ? null : new FfmpegTools(ffmpeg, ffprobe);
    }

    public static string? FindOnPath(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return null;
    }
}

/// <summary>The encoders and muxers a particular ffmpeg build offers.</summary>
public sealed class FfmpegCapabilities
{
    public const string StagingEncoder = "libx264rgb";

    private readonly HashSet<string> _encoders;
    private readonly HashSet<string> _muxers;

    public FfmpegCapabilities(string version, IEnumerable<string> encoders, IEnumerable<string> muxers)
    {
        Version = version;
        _encoders = new HashSet<string>(encoders, StringComparer.Ordinal);
        _muxers = new HashSet<string>(muxers, StringComparer.Ordinal);
    }

    public string Version { get; }
    public IReadOnlyCollection<string> Encoders => _encoders;
    public IReadOnlyCollection<string> Muxers => _muxers;

    public bool CanStage => _encoders.Contains(StagingEncoder) && _muxers.Contains("matroska");

    /// <summary>Explains what the build lacks for <paramref name="format"/>, or returns null.</summary>
    public string? Explain(OutputFormat format)
    {
        var preset = RenderPreset.For(format);
        var missing = new[] { preset.VideoEncoder, preset.AudioEncoder }.Where(name => !_encoders.Contains(name))
            .Concat(_muxers.Contains(preset.Muxer) ? [] : [preset.Muxer + " muxer"])
            .ToArray();
        return missing.Length == 0
            ? null
            : $"This ffmpeg build cannot write {OutputFormats.DisplayName(format)}: it lacks {string.Join(", ", missing)}.";
    }

    public static async Task<FfmpegCapabilities> ProbeAsync(FfmpegTools tools, CancellationToken cancellationToken = default)
    {
        var version = await FfmpegProcess.CaptureOutputAsync(tools.FfmpegPath, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false);
        var encoders = await FfmpegProcess.CaptureOutputAsync(tools.FfmpegPath, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
        var muxers = await FfmpegProcess.CaptureOutputAsync(tools.FfmpegPath, ["-hide_banner", "-muxers"], cancellationToken).ConfigureAwait(false);
        return Parse(version, encoders, muxers);
    }

    public static FfmpegCapabilities Parse(string versionOutput, string encodersOutput, string muxersOutput)
    {
        var version = versionOutput.Split('\n', 2)[0].Trim();
        return new FfmpegCapabilities(version, ParseList(encodersOutput, flagsLength: 6), ParseList(muxersOutput, flagsLength: 2));
    }

    // Both listings put a flags column, then a name, after a "--" separator line. Encoder flags are
    // six characters (V....D); muxer flags are two (DE).
    private static IEnumerable<string> ParseList(string output, int flagsLength)
    {
        var started = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!started)
            {
                started = line.TrimStart().StartsWith("--", StringComparison.Ordinal);
                continue;
            }

            // The flags are a fixed-width column after one leading space; a muxer that cannot demux
            // shows a blank flag, so the column cannot be found by splitting on whitespace.
            if (line.Length <= flagsLength + 2 || line[0] != ' ')
            {
                continue;
            }

            var name = line[(flagsLength + 1)..].TrimStart().Split(' ', 2)[0];
            // A muxer line can name several aliases, separated by commas.
            foreach (var alias in name.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return alias;
            }
        }
    }
}

/// <summary>Runs ffmpeg and ffprobe with structured arguments and drained output streams.</summary>
public static class FfmpegProcess
{
    private const int DiagnosticLines = 40;

    public static async Task<string> CaptureOutputAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Start(executable, arguments, redirectInput: false);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await WaitAsync(process, cancellationToken).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false);
        var errorText = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new FfmpegException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}.", Tail(errorText));
        }

        return text;
    }

    /// <summary>Runs an ffmpeg job that reports progress on stdout through <c>-progress pipe:1</c>.</summary>
    /// <param name="executable">The ffmpeg executable.</param>
    /// <param name="arguments">The job's arguments.</param>
    /// <param name="expectedSeconds">The output duration, for progress.</param>
    /// <param name="progress">Receives the completed fraction.</param>
    /// <param name="cancellationToken">Kills the job.</param>
    /// <param name="input">Writes the job's stdin, which is closed when it returns; null for jobs that
    /// read only files.</param>
    public static async Task RunJobAsync(string executable, IReadOnlyList<string> arguments, double expectedSeconds,
        IProgress<double>? progress, CancellationToken cancellationToken, Func<Stream, CancellationToken, Task>? input = null)
    {
        using var process = Start(executable, arguments, redirectInput: input is not null);
        Exception? inputFailure = null;
        var inputWriter = input is null ? Task.CompletedTask : Task.Run(async () =>
        {
            var stdin = process.StandardInput.BaseStream;
            try
            {
                await input(stdin, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // ffmpeg stopped reading; its exit code and diagnostics say why.
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                inputFailure = exception;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
            finally
            {
                try
                {
                    stdin.Dispose();
                }
                catch (IOException)
                {
                    // The pipe is already broken.
                }
            }
        }, CancellationToken.None);
        var diagnostics = new Queue<string>();
        var errorReader = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                lock (diagnostics)
                {
                    diagnostics.Enqueue(line);
                    if (diagnostics.Count > DiagnosticLines) diagnostics.Dequeue();
                }
            }
        }, CancellationToken.None);
        var progressReader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                // out_time_ms is in microseconds despite its name.
                if (expectedSeconds > 0 && line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), out var micros))
                {
                    progress?.Report(Math.Clamp(micros / 1_000_000d / expectedSeconds, 0, 1));
                }
            }
        }, CancellationToken.None);

        await WaitAsync(process, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(errorReader, progressReader, inputWriter).ConfigureAwait(false);
        if (inputFailure is not null)
        {
            throw new FfmpegException($"The recording could not be read: {inputFailure.Message}", inputFailure);
        }

        if (process.ExitCode != 0)
        {
            string tail;
            lock (diagnostics)
            {
                tail = string.Join('\n', diagnostics);
            }

            throw new FfmpegException($"ffmpeg exited with code {process.ExitCode}.", tail);
        }
    }

    public static Process Start(string executable, IReadOnlyList<string> arguments, bool redirectInput)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new FfmpegException($"{Path.GetFileName(executable)} could not be started.", "");
    }

    /// <summary>Waits for exit; on cancellation kills the process and waits a bounded time for it.</summary>
    public static async Task WaitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            throw;
        }
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - DiagnosticLines)));
    }
}

public sealed class FfmpegException : Exception
{
    public FfmpegException(string message, string diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }

    public FfmpegException()
    {
        Diagnostics = "";
    }

    public FfmpegException(string message)
        : base(message)
    {
        Diagnostics = "";
    }

    public FfmpegException(string message, Exception innerException)
        : base(message, innerException)
    {
        Diagnostics = "";
    }

    /// <summary>The last lines ffmpeg wrote to stderr.</summary>
    public string Diagnostics { get; }
}
