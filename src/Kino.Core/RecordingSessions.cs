namespace Kino;

/// <summary>Temporary recording sources, one directory per recording. Kino owns these files: it
/// deletes a session only after a validated render or an explicit discard.</summary>
public sealed class RecordingSessionStore
{
    private readonly Func<string> _rootProvider;

    public RecordingSessionStore(Func<string> rootProvider)
    {
        _rootProvider = rootProvider;
    }

    public string Root => _rootProvider();

    public string Create(DateTimeOffset startedAt)
    {
        var id = startedAt.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var directory = Path.Combine(Root, id);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string ManifestPath(string sessionDirectory) => Path.Combine(sessionDirectory, RecordingManifest.FileName);

    /// <summary>Recordings that have not been rendered or discarded, newest first. A session with an
    /// unreadable manifest is reported with a null manifest so it can still be discarded.</summary>
    public IReadOnlyList<(string Directory, RecordingManifest? Manifest)> FindUnfinished()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var sessions = new List<(string, RecordingManifest?)>();
        foreach (var directory in Directory.EnumerateDirectories(Root).OrderDescending(StringComparer.Ordinal))
        {
            var manifestPath = ManifestPath(directory);
            if (!File.Exists(manifestPath))
            {
                sessions.Add((directory, null));
                continue;
            }

            try
            {
                sessions.Add((directory, RecordingManifest.Read(manifestPath)));
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                sessions.Add((directory, null));
            }
        }

        return sessions;
    }

    /// <summary>Deletes a session's temporary video, audio and manifest.</summary>
    public void Delete(string sessionDirectory)
    {
        var full = Path.GetFullPath(sessionDirectory);
        var root = Path.GetFullPath(Root);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only recording sessions inside Kino's temporary folder can be deleted.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    public static long SizeOnDisk(string sessionDirectory) => Directory.Exists(sessionDirectory)
        ? new DirectoryInfo(sessionDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
        : 0;
}

/// <summary>Finishing-screen defaults and validation that do not depend on a renderer.</summary>
public static class FinishPlanning
{
    /// <summary>Audio tracks selected before the user changes anything, in the order they will be
    /// written. Silent output is the default.</summary>
    public static IReadOnlyList<string> DefaultTrackSelection(RecordingManifest manifest, AudioInclusion inclusion)
    {
        bool Wanted(RecordedAudioTrack track) => inclusion switch
        {
            AudioInclusion.Both => true,
            AudioInclusion.System => track.Kind == AudioSourceKind.System,
            AudioInclusion.Microphone => track.Kind == AudioSourceKind.Microphone,
            _ => false,
        };

        return manifest.AudioTracks.Where(track => track.IsUsable && Wanted(track)).Select(track => track.Id).ToArray();
    }

    public static string? Validate(RecordingManifest manifest, FinishChoices choices)
    {
        if (choices.AudioTrackIds.Distinct(StringComparer.Ordinal).Count() != choices.AudioTrackIds.Count)
        {
            return "An audio track is selected twice.";
        }

        foreach (var id in choices.AudioTrackIds)
        {
            var track = manifest.AudioTracks.FirstOrDefault(candidate => candidate.Id == id);
            if (track is null)
            {
                return $"The audio track '{id}' is not part of this recording.";
            }

            if (!track.IsUsable)
            {
                return $"{track.Label} has no usable audio{(track.Problem is { } problem ? $": {problem}" : ".")}";
            }
        }

        if (string.IsNullOrWhiteSpace(choices.OutputPath))
        {
            return "Choose where to save the video.";
        }

        if (!string.Equals(Path.GetExtension(choices.OutputPath), OutputFormats.Extension(choices.Format), StringComparison.OrdinalIgnoreCase))
        {
            return $"The file name must end in {OutputFormats.Extension(choices.Format)}.";
        }

        return File.Exists(choices.OutputPath) ? "A file with that name already exists." : null;
    }

    /// <summary>A file name in <paramref name="folder"/> that does not exist yet.</summary>
    public static string UniqueOutputPath(string folder, string stem, string extension)
    {
        var candidate = Path.Combine(folder, stem + extension);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");
        }

        return candidate;
    }

    /// <summary>Moves one entry of an ordered selection up (-1) or down (+1).</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> order, string id, int direction)
    {
        var list = order.ToList();
        var index = list.IndexOf(id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return list;
        }

        (list[index], list[target]) = (list[target], list[index]);
        return list;
    }
}
