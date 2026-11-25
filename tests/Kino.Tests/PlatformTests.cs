using System.Diagnostics;
using Kino.Platform.Windows;
using NAudio.Wave;

namespace Kino.Tests;

public sealed class AudioTrackRecorderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kino-audio-").FullName;
    private long _now = 1_000_000;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public async Task A_source_that_delivers_nothing_becomes_silence_of_the_full_length()
    {
        // Loopback capture delivers no packets while nothing plays.
        var capture = new FakeCapture(new WaveFormat(48000, 16, 2));
        var path = Path.Combine(_folder, "system.wav");
        using var recorder = new AudioTrackRecorder(capture, path, () => _now);
        recorder.Start();

        var duration = await recorder.StopAsync(recorder.Origin + Seconds(2));

        Assert.Equal(2.0, duration, precision: 3);
        using var reader = new WaveFileReader(path);
        Assert.Equal(2.0, reader.TotalTime.TotalSeconds, precision: 3);
    }

    [Fact]
    public async Task A_gap_in_delivery_is_filled_so_later_audio_keeps_its_time()
    {
        var format = new WaveFormat(1000, 16, 1);
        var capture = new FakeCapture(format);
        var path = Path.Combine(_folder, "gap.wav");
        using var recorder = new AudioTrackRecorder(capture, path, () => _now);
        recorder.Start();
        var origin = _now;

        // 100 ms of audio arriving on time.
        _now = origin + Seconds(0.1);
        capture.Deliver(Tone(100, 1000));
        // Nothing for a second, then 100 ms that ends at 1.2 s.
        _now = origin + Seconds(1.2);
        capture.Deliver(Tone(100, 2000));

        var duration = await recorder.StopAsync(origin + Seconds(1.2));

        Assert.Equal(1.2, duration, precision: 3);
        var samples = ReadSamples(path);
        Assert.Equal(1200, samples.Length);
        Assert.Equal(1000, samples[50]);
        Assert.Equal(0, samples[600]);
        Assert.Equal(2000, samples[1150]);
    }

    [Fact]
    public async Task Ordinary_jitter_does_not_insert_silence()
    {
        var capture = new FakeCapture(new WaveFormat(1000, 16, 1));
        var path = Path.Combine(_folder, "jitter.wav");
        using var recorder = new AudioTrackRecorder(capture, path, () => _now);
        recorder.Start();
        var origin = _now;

        // Packets arrive up to 20 ms late, which is inside the tolerance.
        _now = origin + Seconds(0.12);
        capture.Deliver(Tone(100, 500));
        _now = origin + Seconds(0.22);
        capture.Deliver(Tone(100, 500));

        await recorder.StopAsync(origin + Seconds(0.2));

        var samples = ReadSamples(path);
        Assert.Equal(200, samples.Length);
        Assert.All(samples, sample => Assert.Equal(500, sample));
    }

    private static byte[] Tone(int samples, short value)
    {
        var bytes = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2), value);
        }

        return bytes;
    }

    private static short[] ReadSamples(string path)
    {
        using var reader = new WaveFileReader(path);
        var bytes = new byte[reader.Length];
        _ = reader.Read(bytes, 0, bytes.Length);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private sealed class FakeCapture(WaveFormat format) : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = format;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void Deliver(byte[] buffer) => DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, buffer.Length));
        public void Dispose() { }
    }
}

public sealed class ClipboardFormatTests
{
    [Fact]
    public void Dib_is_bottom_up_with_a_32_bit_header()
    {
        // Two rows: blue on top, red below, in BGRA order.
        byte[] pixels = [255, 0, 0, 255, 0, 0, 255, 255];
        var dib = WindowsClipboard.CreateDib(new CapturedImage(1, 2, pixels));

        Assert.Equal(40 + 8, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(1, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));
        // The bottom row comes first.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, dib[40..44]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, dib[44..48]);
    }

    [Fact]
    public void Png_encoding_round_trips_the_pixels()
    {
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255];
        var png = PngEncoder.Encode(new CapturedImage(2, 2, pixels));

        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(2, bitmap.Width);
        Assert.Equal(new SkiaSharp.SKColor(30, 20, 10), bitmap.GetPixel(0, 0));
        Assert.Equal(new SkiaSharp.SKColor(120, 110, 100), bitmap.GetPixel(1, 1));
    }
}

public sealed class ScreenCaptureTests
{
    [Fact]
    public void A_region_reaching_outside_every_monitor_still_captures()
    {
        var screen = new PixelRect(
            GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        using var grabber = new GdiFrameGrabber(64, 64);

        // Straddles the virtual screen's top-left corner, which is off-screen on most layouts.
        var image = grabber.GrabImage(screen.X - 32, screen.Y - 32, includeCursor: false);

        Assert.Equal(64 * 64 * 4, image.Pixels.Length);
    }

    [Fact]
    public void Scaled_grab_at_full_size_matches_a_direct_capture()
    {
        // The primary monitor's top-left is usually static desktop content.
        var region = new PixelRect(0, 0, 48, 32);
        using var direct = new GdiFrameGrabber(region.Width, region.Height);
        var expected = direct.Grab(region.X, region.Y, includeCursor: false).ToArray();
        using var scaled = new ScaledScreenGrabber();

        var (pixels, stride) = scaled.Grab(region, region.Width, region.Height);

        var differing = 0;
        var actual = new byte[region.Width * 4];
        for (var row = 0; row < region.Height; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels + (row * stride), actual, 0, actual.Length);
            for (var column = 0; column < region.Width * 4; column++)
            {
                if ((column & 3) != 3 && Math.Abs(actual[column] - expected[(row * region.Width * 4) + column]) > 8)
                {
                    differing++;
                }
            }
        }

        Assert.True(differing < region.Width * region.Height / 20, $"{differing} channel values differ.");
    }

    [Theory]
    [InlineData(256, 128, 32, 16)]
    [InlineData(800, 450, 336, 189)]
    public void Scaled_grab_shrinks_like_a_direct_capture(int regionWidth, int regionHeight, int width, int height)
    {
        // Shrinking must keep the average colour of the region, not wash it out.
        var region = new PixelRect(0, 0, regionWidth, regionHeight);
        using var direct = new GdiFrameGrabber(region.Width, region.Height);
        var expected = AverageColour(direct.Grab(region.X, region.Y, includeCursor: false).ToArray(), region.Width, region.Height, region.Width * 4);
        using var scaled = new ScaledScreenGrabber();

        var (pixels, stride) = scaled.Grab(region, width, height);

        var buffer = new byte[stride * height];
        System.Runtime.InteropServices.Marshal.Copy(pixels, buffer, 0, buffer.Length);
        var actual = AverageColour(buffer, width, height, stride);
        // Compositing treats GDI's zero alpha as transparent, which showed as a blank preview.
        Assert.All(Enumerable.Range(0, width * height), index => Assert.Equal(255, buffer[((index / width) * stride) + ((index % width) * 4) + 3]));
        for (var channel = 0; channel < 3; channel++)
        {
            Assert.True(Math.Abs(expected[channel] - actual[channel]) < 12, $"Channel {channel}: expected {expected[channel]:0}, got {actual[channel]:0}.");
        }
    }

    private static double[] AverageColour(byte[] pixels, int width, int height, int stride)
    {
        var sums = new double[3];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    sums[channel] += pixels[(row * stride) + (column * 4) + channel];
                }
            }
        }

        return [.. sums.Select(sum => sum / (width * height))];
    }

    [Fact]
    public void Corner_preview_sits_at_the_bottom_right_of_the_primary_monitor_and_shows_the_selection()
    {
        using var preview = new CornerPreview(null!);
        preview.Configure(SelectionKind.Image, new PixelPoint(0, 0), "Ctrl+Num2 at the far corner copies");

        var (bitmap, bounds) = preview.Compose(new PixelPoint(799, 449));
        using (bitmap)
        {
            var (work, _) = Native.PrimaryMonitor();
            Assert.True(bounds.Right <= work.Right && bounds.Bottom <= work.Bottom, $"{bounds} is outside {work}.");
            Assert.True(bounds.Right > work.Right - 64 && bounds.Bottom > work.Bottom - 64, $"{bounds} is not at the bottom-right of {work}.");
            if (Environment.GetEnvironmentVariable("KINO_PREVIEW_PNG") is { Length: > 0 } output)
            {
                using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(output, data.ToArray());
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
