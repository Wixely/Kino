using SkiaSharp;

namespace Kino.Media;

/// <summary>JPEG encoding of frame rectangles and decoding straight back into a frame, both without
/// intermediate copies.</summary>
public static unsafe class JpegCodec
{
    /// <summary>Encodes <paramref name="rect"/> of <paramref name="frame"/>.</summary>
    public static byte[] Encode(RawFrame frame, PixelRect rect, int quality)
    {
        var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        fixed (byte* pixels = frame.Bgra)
        {
            // Opaque makes Skia ignore whatever alpha the capture left.
            using var pixmap = new SKPixmap(info, (nint)(pixels + (((long)rect.Y * frame.Stride) + ((long)rect.X * 4))), frame.Stride);
            using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, quality)
                ?? throw new InvalidOperationException("The frame could not be encoded as JPEG.");
            return data.ToArray();
        }
    }

    /// <summary>Decodes a JPEG of exactly <paramref name="rect"/>'s size into that rectangle of a
    /// frame <paramref name="targetWidth"/> pixels wide.</summary>
    public static void DecodeInto(ReadOnlySpan<byte> jpeg, byte[] target, int targetWidth, PixelRect rect)
    {
        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("A recorded image could not be read.");
        if (codec.Info.Width != rect.Width || codec.Info.Height != rect.Height)
        {
            throw new InvalidDataException($"A recorded image is {codec.Info.Width} × {codec.Info.Height}, expected {rect.Width} × {rect.Height}.");
        }

        var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var stride = targetWidth * 4;
        fixed (byte* pixels = target)
        {
            var result = codec.GetPixels(info, (nint)(pixels + (((long)rect.Y * stride) + ((long)rect.X * 4))), stride, SKCodecOptions.Default);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new InvalidDataException($"A recorded image could not be decoded ({result}).");
            }
        }
    }
}
