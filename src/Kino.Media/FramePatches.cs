using System.Buffers.Binary;

namespace Kino.Media;

/// <summary>One encoded rectangle of a patch frame.</summary>
public sealed record ImagePatch(PixelRect Rect, ReadOnlyMemory<byte> Data);

/// <summary>
/// The payload of a patch frame: copies (scroll motion vectors), applied first against the base frame,
/// then encoded rectangles painted on top.
/// </summary>
/// <remarks>Adapted from PORTÅL's patch format, by the same author.</remarks>
public sealed record FramePatches(IReadOnlyList<CopyOperation> Copies, IReadOnlyList<ImagePatch> Images)
{
    private const byte CopyTag = 1;
    private const byte ImageTag = 2;
    private const int MaximumOperations = 4096;

    public byte[] Encode()
    {
        var length = 2 + (Copies.Count * 25) + Images.Sum(image => 21 + image.Data.Length);
        var buffer = new byte[length];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, checked((ushort)(Copies.Count + Images.Count)));
        var offset = 2;
        foreach (var copy in Copies)
        {
            span[offset] = CopyTag;
            WriteRect(span[(offset + 1)..], copy.Source);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 17)..], copy.Destination.X);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 21)..], copy.Destination.Y);
            offset += 25;
        }

        foreach (var image in Images)
        {
            span[offset] = ImageTag;
            WriteRect(span[(offset + 1)..], image.Rect);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 17)..], image.Data.Length);
            image.Data.Span.CopyTo(span[(offset + 21)..]);
            offset += 21 + image.Data.Length;
        }

        return buffer;
    }

    /// <summary>Parses and bounds-checks patch data against the frame size.</summary>
    public static FramePatches Decode(ReadOnlyMemory<byte> data, int width, int height)
    {
        var span = data.Span;
        if (span.Length < 2)
        {
            throw new InvalidDataException("The patch data is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(span);
        if (count > MaximumOperations)
        {
            throw new InvalidDataException("The patch has too many operations.");
        }

        var bounds = new PixelRect(0, 0, width, height);
        var copies = new List<CopyOperation>();
        var images = new List<ImagePatch>();
        var offset = 2;
        for (var index = 0; index < count; index++)
        {
            if (offset + 21 > span.Length)
            {
                throw new InvalidDataException("The patch data is truncated.");
            }

            var tag = span[offset];
            var rect = ReadRect(span[(offset + 1)..]);
            switch (tag)
            {
                case CopyTag:
                    if (offset + 25 > span.Length)
                    {
                        throw new InvalidDataException("The patch data is truncated.");
                    }

                    var destination = new PixelPoint(
                        BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 17)..]),
                        BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 21)..]));
                    if (!Inside(rect, bounds) || !Inside(new PixelRect(destination.X, destination.Y, rect.Width, rect.Height), bounds))
                    {
                        throw new InvalidDataException("A patch copy lies outside the frame.");
                    }

                    copies.Add(new CopyOperation(rect, destination));
                    offset += 25;
                    break;
                case ImageTag:
                    var length = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 17)..]);
                    if (length < 0 || offset + 21 + length > span.Length)
                    {
                        throw new InvalidDataException("The patch data is truncated.");
                    }

                    if (!Inside(rect, bounds))
                    {
                        throw new InvalidDataException("A patch image lies outside the frame.");
                    }

                    images.Add(new ImagePatch(rect, data.Slice(offset + 21, length)));
                    offset += 21 + length;
                    break;
                default:
                    throw new InvalidDataException($"Unknown patch operation {tag}.");
            }
        }

        if (offset != span.Length)
        {
            throw new InvalidDataException("The patch has trailing data.");
        }

        return new FramePatches(copies, images);
    }

    private static bool Inside(PixelRect rect, PixelRect bounds) =>
        rect.X >= bounds.X && rect.Y >= bounds.Y && rect.Right <= bounds.Right && rect.Bottom <= bounds.Bottom;

    private static void WriteRect(Span<byte> span, PixelRect rect)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span, rect.X);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], rect.Y);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], rect.Width);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], rect.Height);
    }

    private static PixelRect ReadRect(ReadOnlySpan<byte> span)
    {
        var width = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("A patch rectangle is empty.");
        }

        return new PixelRect(BinaryPrimitives.ReadInt32LittleEndian(span), BinaryPrimitives.ReadInt32LittleEndian(span[4..]), width, height);
    }
}
