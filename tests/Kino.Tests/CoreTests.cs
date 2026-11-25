namespace Kino.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void Corners_in_any_order_produce_the_same_inclusive_rectangle()
    {
        var forward = PixelRect.FromCorners(new(10, 20), new(29, 59));
        var reverse = PixelRect.FromCorners(new(29, 59), new(10, 20));
        var mixed = PixelRect.FromCorners(new(10, 59), new(29, 20));

        Assert.Equal(new PixelRect(10, 20, 20, 40), forward);
        Assert.Equal(forward, reverse);
        Assert.Equal(forward, mixed);
    }

    [Fact]
    public void Negative_monitor_coordinates_are_preserved()
    {
        var region = PixelRect.FromCorners(new(-1920, -100), new(-1, 99));

        Assert.Equal(new PixelRect(-1920, -100, 1920, 200), region);
    }

    [Fact]
    public void Same_corner_twice_is_a_single_pixel()
    {
        Assert.Equal(new PixelRect(5, 5, 1, 1), PixelRect.FromCorners(new(5, 5), new(5, 5)));
    }

    [Fact]
    public void Intersect_clips_to_the_virtual_screen_and_reports_empty_when_outside()
    {
        var screen = new PixelRect(-1920, 0, 3840, 1080);

        Assert.Equal(new PixelRect(1800, 1000, 120, 80), new PixelRect(1800, 1000, 500, 500).Intersect(screen));
        Assert.True(new PixelRect(5000, 0, 10, 10).Intersect(screen).IsEmpty);
    }

    [Fact]
    public void Even_size_drops_one_pixel_from_odd_dimensions()
    {
        Assert.Equal(new PixelRect(0, 0, 100, 50), new PixelRect(0, 0, 101, 51).ToEvenSize());
        Assert.Equal(new PixelRect(0, 0, 100, 50), new PixelRect(0, 0, 100, 50).ToEvenSize());
    }
}

public sealed class ShortcutTests
{
    [Fact]
    public void Default_shortcuts_follow_the_ctrl_numpad_layout()
    {
        var bindings = DefaultShortcuts.Create();

        Assert.Equal("Ctrl+Num1", bindings[ShortcutSlot.ImageCorner].ToString());
        Assert.Equal("Ctrl+Num2", bindings[ShortcutSlot.ImageCapture].ToString());
        Assert.Equal("Ctrl+Num3", bindings[ShortcutSlot.SaveImage].ToString());
        Assert.Equal("Ctrl+Num4", bindings[ShortcutSlot.VideoCorner].ToString());
        Assert.Equal("Ctrl+Num5", bindings[ShortcutSlot.VideoStart].ToString());
        Assert.Equal("Ctrl+Num6", bindings[ShortcutSlot.StopRecording].ToString());
        Assert.Equal(Enum.GetValues<ShortcutSlot>().Length, bindings.Count);
        Assert.Empty(DefaultShortcuts.FindConflicts(bindings));
        var compact = DefaultShortcuts.CreateCompact();
        Assert.Equal("Ctrl+Alt+6", compact[ShortcutSlot.StopRecording].ToString());
        Assert.Equal(bindings.Count, compact.Count);
        Assert.Empty(DefaultShortcuts.FindConflicts(compact));
    }

    [Fact]
    public void A_shared_binding_is_reported_as_a_conflict()
    {
        var bindings = new Dictionary<ShortcutSlot, ShortcutBinding>(DefaultShortcuts.Create())
        {
            [ShortcutSlot.StopRecording] = new(ShortcutModifiers.Control, ShortcutKey.Numpad1),
        };

        var conflict = Assert.Single(DefaultShortcuts.FindConflicts(bindings));
        Assert.Equal((ShortcutSlot.ImageCorner, ShortcutSlot.StopRecording), conflict);
    }
}

public sealed class FinishPlanningTests
{
    private static RecordingManifest Manifest() => new()
    {
        Id = "test",
        StartedAt = DateTimeOffset.UnixEpoch,
        Region = new PixelRect(0, 0, 320, 240),
        FrameRate = 30,
        DurationSeconds = 2,
        AudioTracks =
        {
            new RecordedAudioTrack { Id = "system", Kind = AudioSourceKind.System, Label = "System audio", FileName = "system.wav", DurationSeconds = 2 },
            new RecordedAudioTrack { Id = "mic", Kind = AudioSourceKind.Microphone, Label = "Microphone", FileName = "mic.wav", DurationSeconds = 2 },
            new RecordedAudioTrack { Id = "broken", Kind = AudioSourceKind.Microphone, Label = "Broken", FileName = "broken.wav", Problem = "Device missing" },
        },
    };

    [Fact]
    public void Silent_output_is_the_default_selection()
    {
        Assert.Empty(FinishPlanning.DefaultTrackSelection(Manifest(), AudioInclusion.None));
    }

    [Fact]
    public void Inclusion_setting_selects_only_usable_tracks()
    {
        Assert.Equal(["system", "mic"], FinishPlanning.DefaultTrackSelection(Manifest(), AudioInclusion.Both));
        Assert.Equal(["mic"], FinishPlanning.DefaultTrackSelection(Manifest(), AudioInclusion.Microphone));
    }

    [Fact]
    public void Validation_rejects_duplicates_unknown_unusable_and_wrong_extension()
    {
        var manifest = Manifest();
        var path = Path.Combine(Path.GetTempPath(), $"kino-{Guid.NewGuid():N}.mp4");

        Assert.Null(FinishPlanning.Validate(manifest, new FinishChoices(OutputFormat.Mp4, ["mic", "system"], path)));
        Assert.NotNull(FinishPlanning.Validate(manifest, new FinishChoices(OutputFormat.Mp4, ["mic", "mic"], path)));
        Assert.NotNull(FinishPlanning.Validate(manifest, new FinishChoices(OutputFormat.Mp4, ["nope"], path)));
        Assert.NotNull(FinishPlanning.Validate(manifest, new FinishChoices(OutputFormat.Mp4, ["broken"], path)));
        Assert.NotNull(FinishPlanning.Validate(manifest, new FinishChoices(OutputFormat.WebM, [], path)));
    }

    [Fact]
    public void Move_reorders_within_bounds()
    {
        IReadOnlyList<string> order = ["a", "b", "c"];

        Assert.Equal(["b", "a", "c"], FinishPlanning.Move(order, "b", -1));
        Assert.Equal(["a", "c", "b"], FinishPlanning.Move(order, "b", 1));
        Assert.Equal(order, FinishPlanning.Move(order, "a", -1));
        Assert.Equal(order, FinishPlanning.Move(order, "c", 1));
    }

    [Fact]
    public void Unique_output_path_adds_a_counter_when_the_name_exists()
    {
        var folder = Directory.CreateTempSubdirectory("kino-unique-").FullName;
        try
        {
            var first = FinishPlanning.UniqueOutputPath(folder, "Kino", ".png");
            File.WriteAllText(first, "");
            var second = FinishPlanning.UniqueOutputPath(folder, "Kino", ".png");

            Assert.Equal(Path.Combine(folder, "Kino.png"), first);
            Assert.Equal(Path.Combine(folder, "Kino (2).png"), second);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Manifest_round_trips_through_its_file()
    {
        var folder = Directory.CreateTempSubdirectory("kino-manifest-").FullName;
        try
        {
            var path = Path.Combine(folder, RecordingManifest.FileName);
            var manifest = Manifest();
            manifest.State = RecordingState.RenderFailed;
            manifest.AudioTracks[0].OffsetSeconds = 0.125;
            manifest.Write(path);

            var read = RecordingManifest.Read(path)!;

            Assert.Equal(RecordingState.RenderFailed, read.State);
            Assert.Equal(manifest.Region, read.Region);
            Assert.Equal(0.125, read.AudioTracks[0].OffsetSeconds);
            Assert.Equal("Device missing", read.AudioTracks[2].Problem);
            Assert.Contains("\"RenderFailed\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class SessionStoreTests
{
    [Fact]
    public void Unfinished_sessions_are_listed_newest_first_and_can_be_deleted()
    {
        var root = Directory.CreateTempSubdirectory("kino-sessions-").FullName;
        try
        {
            var store = new RecordingSessionStore(() => root);
            var older = store.Create(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
            var newer = store.Create(new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero));
            new RecordingManifest { Id = "x", StartedAt = DateTimeOffset.UnixEpoch, Region = new(0, 0, 2, 2), FrameRate = 30 }
                .Write(RecordingSessionStore.ManifestPath(newer));

            var sessions = store.FindUnfinished();

            Assert.Equal([newer, older], sessions.Select(session => session.Directory));
            Assert.NotNull(sessions[0].Manifest);
            Assert.Null(sessions[1].Manifest);

            store.Delete(newer);
            Assert.False(Directory.Exists(newer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Delete_refuses_paths_outside_the_store()
    {
        var root = Directory.CreateTempSubdirectory("kino-sessions-").FullName;
        var outside = Directory.CreateTempSubdirectory("kino-outside-").FullName;
        try
        {
            var store = new RecordingSessionStore(() => root);

            Assert.Throws<InvalidOperationException>(() => store.Delete(outside));
            Assert.Throws<InvalidOperationException>(() => store.Delete(root));
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}

public sealed class SettingsTests
{
    [Fact]
    public void Settings_round_trip_and_missing_shortcuts_are_restored()
    {
        var folder = Directory.CreateTempSubdirectory("kino-settings-").FullName;
        try
        {
            var store = new SettingsStore(() => Path.Combine(folder, "settings.json"));
            var settings = new KinoSettings
            {
                DefaultAudio = AudioInclusion.Both,
                DefaultFormat = OutputFormat.WebM,
                ShowRecordingFrame = false,
                IncludeCursor = false,
                IncludeCursorInScreenshots = true,
                ShowCornerPreview = false,
                AfterRecording = AfterRecording.SaveWithoutAudio,
                WindowPlacement = WindowPlacement.Taskbar,
            };
            settings.Shortcuts.Remove(ShortcutSlot.StopRecording);
            settings.Shortcuts[ShortcutSlot.ImageCorner] = new(ShortcutModifiers.Control | ShortcutModifiers.Shift, ShortcutKey.F9);
            store.Save(settings);

            var loaded = store.Load();

            Assert.Equal(AudioInclusion.Both, loaded.DefaultAudio);
            Assert.Equal(OutputFormat.WebM, loaded.DefaultFormat);
            Assert.False(loaded.ShowRecordingFrame);
            Assert.False(loaded.IncludeCursor);
            Assert.True(loaded.IncludeCursorInScreenshots);
            Assert.False(loaded.ShowCornerPreview);
            Assert.Equal(AfterRecording.SaveWithoutAudio, loaded.AfterRecording);
            Assert.Equal(WindowPlacement.Taskbar, loaded.WindowPlacement);
            Assert.Equal("Ctrl+Shift+F9", loaded.Shortcuts[ShortcutSlot.ImageCorner].ToString());
            Assert.Equal("Ctrl+Num6", loaded.Shortcuts[ShortcutSlot.StopRecording].ToString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Shortcuts_from_the_four_key_scheme_are_replaced_and_other_settings_kept()
    {
        var folder = Directory.CreateTempSubdirectory("kino-settings-").FullName;
        try
        {
            var path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, """
                {"DefaultFormat":"WebM","Shortcuts":{
                  "ImageCorner":{"Modifiers":"Control","Key":"Numpad1"},
                  "ImageCompleteOrVideoCorner":{"Modifiers":"Control","Key":"Numpad2"},
                  "VideoStartOrSaveImage":{"Modifiers":"Control","Key":"Numpad3"},
                  "StopRecording":{"Modifiers":"Control","Key":"Numpad4"}}}
                """);

            var loaded = new SettingsStore(() => path).Load();

            Assert.Equal(OutputFormat.WebM, loaded.DefaultFormat);
            Assert.Equal(KinoSettings.CurrentShortcutsVersion, loaded.ShortcutsVersion);
            Assert.Equal("Ctrl+Num4", loaded.Shortcuts[ShortcutSlot.VideoCorner].ToString());
            Assert.Equal("Ctrl+Num6", loaded.Shortcuts[ShortcutSlot.StopRecording].ToString());
            Assert.Empty(DefaultShortcuts.FindConflicts(loaded.Shortcuts));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults()
    {
        var folder = Directory.CreateTempSubdirectory("kino-settings-").FullName;
        try
        {
            var path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, "{ not json");

            var loaded = new SettingsStore(() => path).Load();

            Assert.Equal(AudioInclusion.Both, loaded.DefaultAudio);
            Assert.True(loaded.ShowRecordingFrame);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
