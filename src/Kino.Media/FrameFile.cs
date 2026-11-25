using System.Buffers.Binary;
using System.Diagnostics;

namespace Kino.Media;

/// <summary>How a <see cref="FrameStreamWriter"/> stores frames.</summary>
public enum FrameStreamMode
{
    /// <summary>Only what changed since the previous frame, as JPEG patches and scroll copies, with a
    /// full JPEG when most of the frame changed and at a fixed interval.</summary>
    Delta,

    /// <summary>Every captured frame as a full JPEG, with no comparison.</summary>
    Full,
}

public enum FrameRecordKind : byte
{
    /// <summary>A JPEG of the whole frame.</summary>
    Full = 0,

    /// <summary><see cref="FramePatches"/> to apply to the previous frame.</summary>
    Patch = 1,
}

/// <summary>One stored frame. <see cref="Slot"/> is the frame number on the recording's fixed clock;
/// slots without a record repeat the previous frame.</summary>
public sealed record FrameRecord(long Slot, FrameRecordKind Kind, byte[] Payload);

/// <summary>
/// The temporary video of a JPEG recording: a short header, then records appended as frames are
/// captured. Appending keeps everything before a crash readable; a record cut short at the end is
/// ignored when reading.
/// </summary>
/// <remarks>Layout, little-endian. Header: magic "KFRS", version (u16), reserved (u16), width, height and
/// frame rate (i32 each). Record: slot (i64), kind (u8), payload length (i32), payload.</remarks>
public static class FrameFile
{
    public const string FileName = "video.kframes";
    internal const uint Magic = 0x5352464B; // "KFRS"
    internal const ushort Version = 1;
    internal const int HeaderBytes = 20;
    internal const int RecordHeaderBytes = 13;

    /// <summary>The number of frame slots the stream covers: one past the last stored slot.</summary>
    public static long CountSlots(string path)
    {
        using var reader = FrameStreamReader.Open(path);
        var last = -1L;
        while (reader.Next() is { } record)
        {
            last = record.Slot;
        }

        return last + 1;
    }

    /// <summary>Writes <paramref name="frameCount"/> rebuilt BGRA frames to <paramref name="output"/>,
    /// one per slot, repeating the previous frame for slots without a record.</summary>
    public static async Task WriteFramesAsync(string path, long frameCount, Stream output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var reader = FrameStreamReader.Open(path);
        var rebuilder = new FrameRebuilder(reader.Width, reader.Height);
        var next = reader.Next();
        for (var slot = 0L; slot < frameCount; slot++)
        {
            while (next is not null && next.Slot <= slot)
            {
                rebuilder.Apply(next);
                next = reader.Next();
            }

            await output.WriteAsync(rebuilder.Pixels, cancellationToken).ConfigureAwait(false);
            if (slot % 30 == 0)
            {
                progress?.Report(slot / (double)frameCount);
            }
        }
    }
}

/// <summary>Writes a <see cref="FrameFile"/>. Not thread-safe; one thread calls it, while JPEG
/// encoding runs in parallel on the thread pool. Call <see cref="Complete"/> after the last frame.</summary>
public sealed class FrameStreamWriter : IDisposable
{
    // Most of the frame changed: a full JPEG is smaller and simpler than many patches.
    private const double FullFrameThreshold = 0.5;
    private static readonly long FlushInterval = Stopwatch.Frequency * 2;
    private readonly FileStream _file;
    private readonly int _width;
    private readonly int _height;
    private readonly FrameStreamMode _mode;
    private readonly int _quality;
    private readonly long _keyframeSlots;
    private readonly byte[] _recordHeader = new byte[FrameFile.RecordHeaderBytes];
    private readonly Queue<Task<(long Slot, FrameRecordKind Kind, byte[] Payload)>> _pending = new();
    private readonly int _parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
    private RawFrame? _previous;
    private long _lastFullSlot;
    private long _lastFlush = Stopwatch.GetTimestamp();

    /// <param name="path">The file to create.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="frameRate">The frame clock rate, stored for reading back.</param>
    /// <param name="mode">Whether to store changes or every frame.</param>
    /// <param name="quality">JPEG quality, 1 to 100.</param>
    /// <param name="keyframeSeconds">In delta mode, the longest run of patches before a full frame.</param>
    public FrameStreamWriter(string path, int width, int height, int frameRate, FrameStreamMode mode, int quality, double keyframeSeconds = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        _width = width;
        _height = height;
        _mode = mode;
        _quality = Math.Clamp(quality, 1, 100);
        _keyframeSlots = Math.Max(1, (long)(keyframeSeconds * frameRate));
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
        var header = new byte[FrameFile.HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, FrameFile.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), FrameFile.Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), frameRate);
        _file.Write(header);
        _file.Flush();
        Bytes = header.Length;
    }

    public long FullFrames { get; private set; }
    public long PatchFrames { get; private set; }

    /// <summary>Captured frames that matched the previous one and were not stored.</summary>
    public long UnchangedFrames { get; private set; }

    /// <summary>The file size so far, also readable after disposal.</summary>
    public long Bytes { get; private set; }

    /// <summary>Stores the frame captured for <paramref name="slot"/>. The writer keeps
    /// <paramref name="frame"/> as the base of the next comparison, so the caller must not change its
    /// pixels afterwards.</summary>
    /// <param name="slot">The frame number on the recording clock; increases with every call.</param>
    /// <param name="frame">The captured frame.</param>
    /// <param name="damage">Where the capture backend says the frame changed, or null when unknown.</param>
    public void Write(long slot, RawFrame frame, IReadOnlyList<PixelRect>? damage)
    {
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException("The frame does not match the stream size.", nameof(frame));
        }

        // Planning compares raw frames, so it runs in order here; the JPEG encoding it asks for is the
        // slow part and runs in parallel, with records appended in slot order.
        var plan = _mode == FrameStreamMode.Delta && _previous is not null ? DeltaPlanner.Plan(_previous, frame, damage) : null;
        if (plan is { IsEmpty: true })
        {
            UnchangedFrames++;
        }
        else if (plan is null || plan.DirtyFraction > FullFrameThreshold || slot - _lastFullSlot >= _keyframeSlots)
        {
            var quality = _quality;
            Enqueue(Task.Run(() => (slot, FrameRecordKind.Full, JpegCodec.Encode(frame, new PixelRect(0, 0, frame.Width, frame.Height), quality))));
            _lastFullSlot = slot;
            FullFrames++;
        }
        else
        {
            var quality = _quality;
            Enqueue(Task.Run(() => (slot, FrameRecordKind.Patch, new FramePatches(
                plan.Copy is { } copy ? [copy] : [],
                [.. plan.Dirty.Select(rect => new ImagePatch(rect, JpegCodec.Encode(frame, rect, quality)))]).Encode())));
            PatchFrames++;
        }

        _previous = frame;
        AppendFinished(waitForAll: false);
        if (Stopwatch.GetTimestamp() - _lastFlush > FlushInterval)
        {
            // Pushes buffered records to the operating system, so a crash of Kino loses little.
            _file.Flush();
            _lastFlush = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Waits for every pending encode and appends it. Throws when an encode failed.</summary>
    public void Complete()
    {
        AppendFinished(waitForAll: true);
        _file.Flush();
    }

    /// <summary>Appends what was encoded and closes the file; failures are left to
    /// <see cref="Complete"/> to report.</summary>
    public void Dispose()
    {
        try
        {
            AppendFinished(waitForAll: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // The recording is being abandoned or Complete already reported this.
        }
        finally
        {
            _file.Dispose();
        }
    }

    private void Enqueue(Task<(long Slot, FrameRecordKind Kind, byte[] Payload)> encode)
    {
        _pending.Enqueue(encode);
        // Bounded, so a long burst of changes holds the caller back instead of piling up frames.
        while (_pending.Count > _parallelism)
        {
            AppendNext();
        }
    }

    private void AppendFinished(bool waitForAll)
    {
        while (_pending.Count > 0 && (waitForAll || _pending.Peek().IsCompleted))
        {
            AppendNext();
        }
    }

    private void AppendNext()
    {
        var (slot, kind, payload) = _pending.Dequeue().GetAwaiter().GetResult();
        Append(slot, kind, payload);
    }

    private void Append(long slot, FrameRecordKind kind, byte[] payload)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_recordHeader, slot);
        _recordHeader[8] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(_recordHeader.AsSpan(9), payload.Length);
        _file.Write(_recordHeader);
        _file.Write(payload);
        Bytes += _recordHeader.Length + payload.Length;
    }
}

/// <summary>Reads a <see cref="FrameFile"/> record by record.</summary>
public sealed class FrameStreamReader : IDisposable
{
    private readonly FileStream _file;
    private readonly byte[] _recordHeader = new byte[FrameFile.RecordHeaderBytes];
    private long _lastSlot = -1;

    private FrameStreamReader(FileStream file, int width, int height, int frameRate)
    {
        _file = file;
        Width = width;
        Height = height;
        FrameRate = frameRate;
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameRate { get; }

    public static FrameStreamReader Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        try
        {
            var header = new byte[FrameFile.HeaderBytes];
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(header) != FrameFile.Magic)
            {
                throw new InvalidDataException("The temporary video is not a Kino frame stream.");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != FrameFile.Version)
            {
                throw new InvalidDataException("The temporary video was written by a different version of Kino.");
            }

            var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            var frameRate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            if (width <= 0 || height <= 0 || frameRate <= 0)
            {
                throw new InvalidDataException("The temporary video header is damaged.");
            }

            return new FrameStreamReader(file, width, height, frameRate);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>The next record, or null at the end. A record cut short by an interrupted recording
    /// counts as the end.</summary>
    public FrameRecord? Next()
    {
        if (_file.ReadAtLeast(_recordHeader, _recordHeader.Length, throwOnEndOfStream: false) < _recordHeader.Length)
        {
            return null;
        }

        var slot = BinaryPrimitives.ReadInt64LittleEndian(_recordHeader);
        var kind = _recordHeader[8];
        var length = BinaryPrimitives.ReadInt32LittleEndian(_recordHeader.AsSpan(9));
        if (slot <= _lastSlot || kind > (byte)FrameRecordKind.Patch || length < 0 || length > _file.Length - _file.Position)
        {
            // Out of order, unknown or longer than what is left: the tail is damaged or truncated.
            return null;
        }

        var payload = new byte[length];
        if (_file.ReadAtLeast(payload, length, throwOnEndOfStream: false) < length)
        {
            return null;
        }

        _lastSlot = slot;
        return new FrameRecord(slot, (FrameRecordKind)kind, payload);
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Rebuilds frames by applying records in order.</summary>
public sealed class FrameRebuilder
{
    private readonly int _width;
    private readonly int _height;

    public FrameRebuilder(int width, int height)
    {
        _width = width;
        _height = height;
        // Black until the first full frame, which a well-formed stream starts with.
        Pixels = new byte[width * height * 4];
        for (var index = 3; index < Pixels.Length; index += 4)
        {
            Pixels[index] = 255;
        }
    }

    /// <summary>The current frame, top-down BGRA.</summary>
    public byte[] Pixels { get; }

    public void Apply(FrameRecord record)
    {
        if (record.Kind == FrameRecordKind.Full)
        {
            JpegCodec.DecodeInto(record.Payload, Pixels, _width, new PixelRect(0, 0, _width, _height));
            return;
        }

        var patches = FramePatches.Decode(record.Payload, _width, _height);
        if (patches.Copies.Count > 0)
        {
            // Copies read the base frame, so they take their pixels from a snapshot of it.
            var source = (byte[])Pixels.Clone();
            foreach (var copy in patches.Copies)
            {
                DeltaPlanner.Apply(Pixels, source, _width, copy);
            }
        }

        foreach (var image in patches.Images)
        {
            JpegCodec.DecodeInto(image.Data.Span, Pixels, _width, image.Rect);
        }
    }
}
