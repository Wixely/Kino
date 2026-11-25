using Kino.Media.FFmpeg;

namespace Kino.Tests;

public sealed class FfmpegParsingTests
{
    [Fact]
    public void Capability_listing_is_parsed_and_missing_encoders_are_explained()
    {
        const string encoders = """
            Encoders:
             V..... = Video
             ------
             V..... libx264              libx264 H.264
             V..... libx264rgb           libx264 H.264 RGB
             A..... aac                  AAC (Advanced Audio Coding)
            """;
        const string muxers = """
            File formats:
             D. = Demuxing supported
             --
              E matroska        Matroska
              E mp4             MP4 (MPEG-4 Part 14)
            """;

        var capabilities = FfmpegCapabilities.Parse("ffmpeg version test\nbuilt with", encoders, muxers);

        Assert.Equal("ffmpeg version test", capabilities.Version);
        Assert.True(capabilities.CanStage);
        Assert.Null(capabilities.Explain(OutputFormat.Mp4));
        Assert.Null(capabilities.Explain(OutputFormat.Mkv));
        var webm = capabilities.Explain(OutputFormat.WebM);
        Assert.NotNull(webm);
        Assert.Contains("libvpx-vp9", webm, StringComparison.Ordinal);
        Assert.Contains("libopus", webm, StringComparison.Ordinal);
        Assert.Contains("webm muxer", webm, StringComparison.Ordinal);
    }

    [Fact]
    public void Final_render_maps_selected_tracks_in_order_with_offsets()
    {
        var manifest = new RecordingManifest
        {
            Id = "x",
            StartedAt = DateTimeOffset.UnixEpoch,
            Region = new(0, 0, 320, 240),
            FrameRate = 30,
            DurationSeconds = 4.5,
            AudioTracks =
            {
                new RecordedAudioTrack { Id = "system", Kind = AudioSourceKind.System, Label = "System audio", FileName = "system.wav", OffsetSeconds = 0.25, DurationSeconds = 4 },
                new RecordedAudioTrack { Id = "mic", Kind = AudioSourceKind.Microphone, Label = "Microphone", FileName = "mic.wav", OffsetSeconds = -0.1, DurationSeconds = 4 },
            },
        };

        var arguments = RenderPlan.Final("session", manifest, new FinishChoices(OutputFormat.Mp4, ["mic", "system"], "out.mp4"), "partial");
        var joined = string.Join(' ', arguments);

        // The microphone is input 1 and the system audio input 2, because that is the chosen order.
        Assert.Contains($"-ss 0.100 -i {Path.Combine("session", "mic.wav")} -itsoffset 0.250 -i {Path.Combine("session", "system.wav")}", joined, StringComparison.Ordinal);
        Assert.Contains("-map 0:v:0 -map 1:a:0 -map 2:a:0", joined, StringComparison.Ordinal);
        Assert.Contains("-metadata:s:a:0 title=Microphone", joined, StringComparison.Ordinal);
        Assert.Contains("-metadata:s:a:1 title=System audio", joined, StringComparison.Ordinal);
        Assert.Contains("-t 4.500", joined, StringComparison.Ordinal);
        Assert.Contains("-movflags +faststart", joined, StringComparison.Ordinal);
        Assert.Equal("partial", arguments[^1]);
        Assert.DoesNotContain("-an", arguments);
    }

    [Fact]
    public void Silent_render_disables_audio()
    {
        var manifest = new RecordingManifest { Id = "x", StartedAt = DateTimeOffset.UnixEpoch, Region = new(0, 0, 2, 2), FrameRate = 30 };

        var arguments = RenderPlan.Final("s", manifest, new FinishChoices(OutputFormat.WebM, [], "out.webm"), "p");

        Assert.Contains("-an", arguments);
        Assert.Contains("libvpx-vp9", arguments);
        Assert.DoesNotContain("-map 1:a:0", string.Join(' ', arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_catches_missing_and_reordered_tracks()
    {
        IReadOnlyList<MediaStreamInfo> streams =
        [
            new(0, "video", "h264", null, 2),
            new(1, "audio", "aac", "Microphone", 2),
            new(2, "audio", "aac", "System audio", 2),
        ];

        Assert.Null(Ffprobe.Verify(streams, OutputFormat.Mp4, ["Microphone", "System audio"]));
        Assert.NotNull(Ffprobe.Verify(streams, OutputFormat.Mp4, ["System audio", "Microphone"]));
        Assert.NotNull(Ffprobe.Verify(streams, OutputFormat.Mp4, ["Microphone"]));
        Assert.NotNull(Ffprobe.Verify(streams, OutputFormat.WebM, ["Microphone", "System audio"]));
    }
}

/// <summary>Renders synthetic media with the ffmpeg on PATH. Skipped when ffmpeg is not installed.</summary>
public sealed class FfmpegRenderTests : IDisposable
{
    private readonly string _session = Directory.CreateTempSubdirectory("kino-render-").FullName;
    private readonly string _output = Directory.CreateTempSubdirectory("kino-output-").FullName;

    public void Dispose()
    {
        Directory.Delete(_session, recursive: true);
        Directory.Delete(_output, recursive: true);
    }

    public static TheoryData<OutputFormat> Formats => new() { OutputFormat.Mp4, OutputFormat.Mkv, OutputFormat.WebM };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Two_tracks_render_in_the_chosen_order(OutputFormat format)
    {
        if (await PrepareAsync() is not { } prepared)
        {
            return;
        }

        var (tools, capabilities, manifest) = prepared;

        var path = Path.Combine(_output, "ordered" + OutputFormats.Extension(format));
        var renderer = new FfmpegRenderer(tools, capabilities);
        await renderer.RenderAsync(_session, manifest, new FinishChoices(format, ["mic", "system"], path), null, CancellationToken.None);

        var streams = await Ffprobe.StreamsAsync(tools, path);
        Assert.Equal(["video", "audio", "audio"], streams.Select(stream => stream.CodecType));
        Assert.Equal(["Microphone", "System audio"], streams.Where(stream => stream.CodecType == "audio").Select(stream => stream.Title));
        Assert.Null(Ffprobe.Verify(streams, format, ["Microphone", "System audio"]));
        Assert.Empty(Directory.GetFiles(_output, "*.kino-partial", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Silent_output_has_no_audio_streams(OutputFormat format)
    {
        if (await PrepareAsync() is not { } prepared)
        {
            return;
        }

        var (tools, capabilities, manifest) = prepared;

        var path = Path.Combine(_output, "silent" + OutputFormats.Extension(format));
        await new FfmpegRenderer(tools, capabilities).RenderAsync(_session, manifest, new FinishChoices(format, [], path), null, CancellationToken.None);

        var streams = await Ffprobe.StreamsAsync(tools, path);
        var video = Assert.Single(streams);
        Assert.Equal("video", video.CodecType);
    }

    [Fact]
    public async Task Staging_encoder_writes_a_readable_lossless_file()
    {
        var tools = FfmpegTools.Locate();
        if (tools is null)
        {
            return;
        }

        var path = Path.Combine(_session, "staged.mkv");
        using (var encoder = new StagingEncoder(tools, 64, 48, 30, path))
        {
            var frame = new byte[encoder.FrameBytes];
            for (var index = 0; index < 30; index++)
            {
                Array.Fill(frame, (byte)(index * 8));
                encoder.WriteFrame(frame);
            }

            await encoder.CompleteAsync(TimeSpan.FromSeconds(30));
        }

        var streams = await Ffprobe.StreamsAsync(tools, path);
        Assert.Equal("h264", Assert.Single(streams).CodecName);
    }

    [Fact]
    public async Task A_failed_render_leaves_no_partial_file_and_keeps_sources()
    {
        if (await PrepareAsync() is not { } prepared)
        {
            return;
        }

        var (tools, capabilities, manifest) = prepared;

        File.WriteAllText(Path.Combine(_session, "mic.wav"), "not audio");
        var path = Path.Combine(_output, "broken.mp4");

        await Assert.ThrowsAsync<FfmpegException>(() =>
            new FfmpegRenderer(tools, capabilities).RenderAsync(_session, manifest, new FinishChoices(OutputFormat.Mp4, ["mic"], path), null, CancellationToken.None));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_output, "*", new EnumerationOptions { AttributesToSkip = 0 }));
        Assert.True(File.Exists(Path.Combine(_session, "video.mkv")));
    }

    private async Task<(FfmpegTools, FfmpegCapabilities, RecordingManifest)?> PrepareAsync()
    {
        var tools = FfmpegTools.Locate();
        if (tools is null)
        {
            return null;
        }

        var capabilities = await FfmpegCapabilities.ProbeAsync(tools);
        // A synthetic staging video and two distinguishable tones stand in for a real recording.
        await FfmpegProcess.CaptureOutputAsync(tools.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=2",
             "-c:v", "libx264rgb", "-preset", "ultrafast", "-qp", "0", "-pix_fmt", "bgr0", Path.Combine(_session, "video.mkv")], default);
        await FfmpegProcess.CaptureOutputAsync(tools.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
             "-ac", "2", "-c:a", "pcm_f32le", Path.Combine(_session, "system.wav")], default);
        await FfmpegProcess.CaptureOutputAsync(tools.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=2",
             "-c:a", "pcm_s16le", Path.Combine(_session, "mic.wav")], default);
        var manifest = new RecordingManifest
        {
            Id = "synthetic",
            StartedAt = DateTimeOffset.UnixEpoch,
            Region = new(0, 0, 320, 240),
            FrameRate = 30,
            DurationSeconds = 2,
            AudioTracks =
            {
                new RecordedAudioTrack { Id = "system", Kind = AudioSourceKind.System, Label = "System audio", FileName = "system.wav", DurationSeconds = 2 },
                new RecordedAudioTrack { Id = "mic", Kind = AudioSourceKind.Microphone, Label = "Microphone", FileName = "mic.wav", OffsetSeconds = 0.2, DurationSeconds = 2 },
            },
        };
        return (tools, capabilities, manifest);
    }
}
