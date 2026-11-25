using Kino.Media;
using Kino.Media.FFmpeg;

namespace Kino.Tests;

/// <summary>The delta planner, ported with PORTÅL's tests.</summary>
public sealed class DeltaPlannerTests
{
    [Fact]
    public void Identical_frames_need_nothing()
    {
        var frame = Synthetic.Document(320, 240, offset: 0);

        Assert.True(DeltaPlanner.Plan(frame, Synthetic.Copy(frame)).IsEmpty);
    }

    [Fact]
    public void A_small_change_repaints_only_its_tile()
    {
        var before = Synthetic.Document(320, 240, offset: 0);
        var after = Synthetic.Copy(before);
        Synthetic.Paint(after, new PixelRect(70, 10, 5, 5), 0xff00ff00);

        var plan = DeltaPlanner.Plan(before, after);

        Assert.Null(plan.Copy);
        Assert.Equal([new PixelRect(64, 0, 64, 64)], plan.Dirty);
    }

    [Fact]
    public void Adjacent_changed_tiles_merge_into_one_rectangle()
    {
        var before = Synthetic.Document(320, 240, offset: 0);
        var after = Synthetic.Copy(before);
        Synthetic.Paint(after, new PixelRect(10, 10, 120, 100), 0xff00ff00);

        Assert.Equal([new PixelRect(0, 0, 192, 128)], DeltaPlanner.Plan(before, after).Dirty);
    }

    [Fact]
    public void Vertical_scroll_becomes_a_copy_plus_the_exposed_strip()
    {
        // A text-like page scrolled up by 37 px with a static 64 px sidebar.
        var before = Synthetic.Document(640, 480, offset: 0, sidebar: 64);
        var after = Synthetic.Document(640, 480, offset: 37, sidebar: 64);

        var plan = DeltaPlanner.Plan(before, after);

        var copy = Assert.IsType<CopyOperation>(plan.Copy);
        Assert.Equal(copy.Source.Y - 37, copy.Destination.Y);
        Assert.Equal(copy.Source.X, copy.Destination.X);
        Assert.True(plan.DirtyFraction < 0.25, $"Residual {plan.DirtyFraction:P0} should be the exposed strip only.");
        AssertReconstructs(before, after, plan);
    }

    [Fact]
    public void Horizontal_scroll_is_detected_too()
    {
        var before = Synthetic.Columns(640, 240, offset: 0);
        var after = Synthetic.Columns(640, 240, offset: 50);

        var plan = DeltaPlanner.Plan(before, after);

        Assert.Equal(Assert.IsType<CopyOperation>(plan.Copy).Source.X - 50, plan.Copy.Value.Destination.X);
        AssertReconstructs(before, after, plan);
    }

    [Fact]
    public void Reported_damage_limits_what_is_compared()
    {
        var before = Synthetic.Document(320, 240, offset: 0);
        var after = Synthetic.Copy(before);
        Synthetic.Paint(after, new PixelRect(10, 10, 5, 5), 0xff00ff00);
        Synthetic.Paint(after, new PixelRect(200, 150, 5, 5), 0xff00ff00);

        Assert.Equal([new PixelRect(192, 128, 64, 64)], DeltaPlanner.Plan(before, after, [new PixelRect(198, 148, 10, 10)]).Dirty);
        Assert.True(DeltaPlanner.Plan(before, after, []).IsEmpty);
    }

    [Fact]
    public void A_flat_background_does_not_fake_a_scroll()
    {
        var before = Synthetic.Solid(320, 240, 0xff202020);
        var after = Synthetic.Copy(before);
        Synthetic.Paint(after, new PixelRect(0, 100, 320, 40), 0xffffffff);

        Assert.Null(DeltaPlanner.Plan(before, after).Copy);
    }

    [Fact]
    public void Patches_round_trip_and_reject_out_of_bounds_rectangles()
    {
        var patches = new FramePatches(
            [new CopyOperation(new PixelRect(0, 40, 100, 50), new PixelPoint(0, 0))],
            [new ImagePatch(new PixelRect(0, 50, 100, 40), new byte[] { 1, 2, 3 })]);

        var decoded = FramePatches.Decode(patches.Encode(), 100, 100);

        Assert.Equal(patches.Copies, decoded.Copies);
        Assert.Equal(patches.Images[0].Rect, decoded.Images[0].Rect);
        Assert.Equal([1, 2, 3], decoded.Images[0].Data.ToArray());
        Assert.Throws<InvalidDataException>(() => FramePatches.Decode(patches.Encode(), 100, 80));
    }

    private static void AssertReconstructs(RawFrame before, RawFrame after, DeltaPlan plan)
    {
        var rebuilt = (byte[])before.Bgra.Clone();
        if (plan.Copy is { } copy)
        {
            DeltaPlanner.Apply(rebuilt, before.Bgra, before.Width, copy);
        }

        foreach (var rect in plan.Dirty)
        {
            var patch = DeltaPlanner.Extract(after, rect);
            for (var row = 0; row < rect.Height; row++)
            {
                Buffer.BlockCopy(patch.Bgra, row * rect.Width * 4, rebuilt, (((rect.Y + row) * after.Width) + rect.X) * 4, rect.Width * 4);
            }
        }

        Assert.Equal(after.Bgra, rebuilt);
    }
}

public sealed class FrameFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kino-frames-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Delta_mode_stores_changes_only_and_rebuilds_every_frame_closely()
    {
        var path = Path.Combine(_folder, FrameFile.FileName);
        var frames = Scene(320, 240);
        using (var writer = new FrameStreamWriter(path, 320, 240, 30, FrameStreamMode.Delta, quality: 95))
        {
            for (var slot = 0; slot < frames.Count; slot++)
            {
                writer.Write(slot, frames[slot], damage: null);
            }

            // Frame 0 is full; 1 and 2 are unchanged; 3 changes a corner; 4 scrolls; 5 replaces everything.
            Assert.Equal(2, writer.FullFrames);
            Assert.Equal(2, writer.PatchFrames);
            Assert.Equal(2, writer.UnchangedFrames);
        }

        using var reader = FrameStreamReader.Open(path);
        var rebuilder = new FrameRebuilder(reader.Width, reader.Height);
        var next = reader.Next();
        for (var slot = 0; slot < frames.Count; slot++)
        {
            while (next is not null && next.Slot <= slot)
            {
                rebuilder.Apply(next);
                next = reader.Next();
            }

            Assert.True(MeanDifference(frames[slot].Bgra, rebuilder.Pixels) < 4, $"Frame {slot} differs by {MeanDifference(frames[slot].Bgra, rebuilder.Pixels):0.00}.");
        }
    }

    [Fact]
    public void Full_mode_stores_every_frame_whole()
    {
        var path = Path.Combine(_folder, FrameFile.FileName);
        var frames = Scene(160, 120);
        using (var writer = new FrameStreamWriter(path, 160, 120, 30, FrameStreamMode.Full, quality: 90))
        {
            for (var slot = 0; slot < frames.Count; slot++)
            {
                writer.Write(slot, frames[slot], damage: null);
            }

            Assert.Equal(frames.Count, writer.FullFrames);
            Assert.Equal(0, writer.PatchFrames);
        }

        Assert.Equal(frames.Count, FrameFile.CountSlots(path));
    }

    [Fact]
    public void Delta_mode_writes_a_full_frame_at_the_keyframe_interval()
    {
        var path = Path.Combine(_folder, FrameFile.FileName);
        var frame = Synthetic.Document(128, 128, offset: 0);
        using var writer = new FrameStreamWriter(path, 128, 128, 10, FrameStreamMode.Delta, quality: 90, keyframeSeconds: 1);
        for (var slot = 0; slot < 25; slot++)
        {
            var changed = Synthetic.Copy(frame);
            Synthetic.Paint(changed, new PixelRect(0, 0, 4, 4), 0xff000000u | (uint)slot);
            writer.Write(slot, changed, damage: null);
        }

        // Slots 0, 10 and 20 are full; the rest patch one tile.
        Assert.Equal(3, writer.FullFrames);
        Assert.Equal(22, writer.PatchFrames);
    }

    [Fact]
    public void A_truncated_file_reads_up_to_the_last_complete_record()
    {
        var path = Path.Combine(_folder, FrameFile.FileName);
        var frames = Scene(160, 120);
        using (var writer = new FrameStreamWriter(path, 160, 120, 30, FrameStreamMode.Full, quality: 90))
        {
            for (var slot = 0; slot < frames.Count; slot++)
            {
                writer.Write(slot * 2, frames[slot], damage: null);
            }
        }

        // An interrupted recording leaves half a record at the end.
        using (var file = new FileStream(path, FileMode.Open))
        {
            file.SetLength(file.Length - 100);
        }

        Assert.Equal(((frames.Count - 2) * 2) + 1, FrameFile.CountSlots(path));
    }

    [Fact]
    public async Task Writing_frames_repeats_the_previous_frame_for_slots_without_a_record()
    {
        var path = Path.Combine(_folder, FrameFile.FileName);
        var first = Synthetic.Solid(64, 32, 0xff102030);
        var second = Synthetic.Solid(64, 32, 0xff405060);
        using (var writer = new FrameStreamWriter(path, 64, 32, 30, FrameStreamMode.Delta, quality: 100))
        {
            writer.Write(0, first, null);
            writer.Write(3, second, null);
        }

        using var output = new MemoryStream();
        await FrameFile.WriteFramesAsync(path, 5, output, null, CancellationToken.None);

        var bytes = output.ToArray();
        var frameBytes = 64 * 32 * 4;
        Assert.Equal(5 * frameBytes, bytes.Length);
        foreach (var (slot, expected) in new[] { (0, first), (2, first), (3, second), (4, second) })
        {
            Assert.True(MeanDifference(expected.Bgra, bytes.AsSpan(slot * frameBytes, frameBytes)) < 2, $"Slot {slot} has the wrong image.");
        }
    }

    [Fact]
    public void A_file_that_is_not_a_frame_stream_is_refused()
    {
        var path = Path.Combine(_folder, "video.mkv");
        File.WriteAllBytes(path, new byte[64]);

        Assert.Throws<InvalidDataException>(() => FrameStreamReader.Open(path));
    }

    [Fact]
    public void The_final_render_reads_jpeg_recordings_from_stdin()
    {
        var manifest = new RecordingManifest
        {
            Id = "jpeg",
            StartedAt = DateTimeOffset.UnixEpoch,
            Region = new(10, 20, 640, 360),
            FrameRate = 24,
            VideoFormat = StagingFormat.JpegFrames,
            VideoFileName = FrameFile.FileName,
        };

        var arguments = RenderPlan.Final(@"C:\session", manifest, new FinishChoices(OutputFormat.Mp4, [], @"C:\out.mp4"), @"C:\out.partial");

        Assert.DoesNotContain("-nostdin", arguments);
        Assert.Equal(["-f", "rawvideo", "-pix_fmt", "bgra", "-s", "640x360", "-framerate", "24", "-i", "pipe:0"],
            arguments.SkipWhile(argument => argument != "rawvideo").Prepend("-f").Take(10));
    }

    /// <summary>Six frames: a document, two unchanged copies, a small change, a scroll, and a different
    /// image.</summary>
    internal static List<RawFrame> Scene(int width, int height)
    {
        var page = Synthetic.Smooth(width, height, offset: 0);
        var changed = Synthetic.Copy(page);
        Synthetic.Paint(changed, new PixelRect(4, 4, 12, 12), 0xff00c000);
        var scrolled = Synthetic.Smooth(width, height, offset: 24);
        Synthetic.Paint(scrolled, new PixelRect(4, 4, 12, 12), 0xff00c000);
        return [page, Synthetic.Copy(page), Synthetic.Copy(page), changed, scrolled, Synthetic.Solid(width, height, 0xff806040)];
    }

    internal static double MeanDifference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        long total = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            if ((index & 3) != 3)
            {
                total += Math.Abs(expected[index] - actual[index]);
            }
        }

        return total / (expected.Length * 0.75);
    }
}

public sealed class FrameFileRenderTests : IDisposable
{
    private readonly string _session = Directory.CreateTempSubdirectory("kino-jpeg-render-").FullName;
    private readonly string _output = Directory.CreateTempSubdirectory("kino-jpeg-output-").FullName;

    public void Dispose()
    {
        Directory.Delete(_session, recursive: true);
        Directory.Delete(_output, recursive: true);
    }

    [Theory]
    [InlineData(OutputFormat.Mp4)]
    [InlineData(OutputFormat.WebM)]
    public async Task A_delta_recording_renders_with_the_right_length_and_pictures(OutputFormat format)
    {
        var tools = FfmpegTools.Locate();
        if (tools is null)
        {
            return;
        }

        // 60 slots at 30 fps with records only at 0 and 30: the second second repeats a different image.
        var first = Synthetic.Smooth(320, 240, offset: 0);
        var second = Synthetic.Smooth(320, 240, offset: 80);
        using (var writer = new FrameStreamWriter(Path.Combine(_session, FrameFile.FileName), 320, 240, 30, FrameStreamMode.Delta, quality: 92))
        {
            writer.Write(0, first, null);
            writer.Write(30, second, null);
        }

        var manifest = new RecordingManifest
        {
            Id = "jpeg",
            StartedAt = DateTimeOffset.UnixEpoch,
            Region = new(0, 0, 320, 240),
            FrameRate = 30,
            DurationSeconds = 2,
            FramesWritten = 60,
            VideoFormat = StagingFormat.JpegFrames,
            VideoFileName = FrameFile.FileName,
        };
        var path = Path.Combine(_output, "delta" + OutputFormats.Extension(format));

        await new FfmpegRenderer(tools, await FfmpegCapabilities.ProbeAsync(tools))
            .RenderAsync(_session, manifest, new FinishChoices(format, [], path), null, CancellationToken.None);

        var video = Assert.Single(await Ffprobe.StreamsAsync(tools, path));
        Assert.InRange(video.DurationSeconds ?? 0, 1.9, 2.1);
        var early = await FrameAtAsync(tools, path, 0.5);
        var late = await FrameAtAsync(tools, path, 1.5);
        Assert.True(FrameFileTests.MeanDifference(first.Bgra, early) < 6, $"Early frame differs by {FrameFileTests.MeanDifference(first.Bgra, early):0.00}.");
        Assert.True(FrameFileTests.MeanDifference(second.Bgra, late) < 6, $"Late frame differs by {FrameFileTests.MeanDifference(second.Bgra, late):0.00}.");
    }

    /// <summary>Decodes the frame at <paramref name="seconds"/> back to BGRA.</summary>
    private async Task<byte[]> FrameAtAsync(FfmpegTools tools, string path, double seconds)
    {
        var raw = Path.Combine(_output, $"frame-{seconds}.bgra");
        await FfmpegProcess.CaptureOutputAsync(tools.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path,
             "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", raw], default);
        return File.ReadAllBytes(raw);
    }
}

/// <summary>Synthetic frames for planner and frame-file tests.</summary>
internal static class Synthetic
{
    /// <summary>Distinct text-like rows (so each row hashes uniquely) scrolled by
    /// <paramref name="offset"/>.</summary>
    public static RawFrame Document(int width, int height, int offset, int sidebar = 0)
    {
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            var line = y + offset;
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = x < sidebar
                    ? 0xff303030
                    : 0xff000000 | (uint)(((line * 2654435761u) + ((uint)(x / 7) * 40503u)) & 0x00ffffff);
            }
        }

        return Raw(width, height, pixels);
    }

    /// <summary>Gradients with soft stripes: content JPEG reproduces closely, unlike random noise.</summary>
    public static RawFrame Smooth(int width, int height, int offset)
    {
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            var line = y + offset;
            for (var x = 0; x < width; x++)
            {
                var red = (uint)((x * 255 / width) & 0xff);
                var green = (uint)((line * 3) & 0xff);
                var blue = (uint)(((line / 16) % 2 == 0) ? 200 : 60);
                pixels[(y * width) + x] = 0xff000000 | (red << 16) | (green << 8) | blue;
            }
        }

        return Raw(width, height, pixels);
    }

    public static RawFrame Columns(int width, int height, int offset)
    {
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = 0xff000000 | (uint)((((x + offset) * 2654435761u) + ((uint)(y / 5) * 40503u)) & 0x00ffffff);
            }
        }

        return Raw(width, height, pixels);
    }

    public static RawFrame Solid(int width, int height, uint colour) =>
        Raw(width, height, Enumerable.Repeat(colour, width * height).ToArray());

    public static RawFrame Copy(RawFrame frame) => frame with { Bgra = (byte[])frame.Bgra.Clone() };

    public static void Paint(RawFrame frame, PixelRect rect, uint colour)
    {
        var value = BitConverter.GetBytes(colour);
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            for (var x = rect.X; x < rect.Right; x++)
            {
                value.CopyTo(frame.Bgra, ((y * frame.Width) + x) * 4);
            }
        }
    }

    private static RawFrame Raw(int width, int height, uint[] pixels)
    {
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        return new RawFrame(width, height, bytes);
    }
}
