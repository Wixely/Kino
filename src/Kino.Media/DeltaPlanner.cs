using System.Runtime.InteropServices;

namespace Kino.Media;

/// <summary>A top-down, tightly packed BGRA frame.</summary>
public sealed record RawFrame(int Width, int Height, byte[] Bgra)
{
    public int Stride => Width * 4;
}

/// <summary>Moves a rectangle of the base frame to a new position (a scroll's motion vector).</summary>
public readonly record struct CopyOperation(PixelRect Source, PixelPoint Destination);

/// <summary>What changed between two frames of the same size: an optional copy, then rectangles to
/// repaint.</summary>
public sealed record DeltaPlan(CopyOperation? Copy, IReadOnlyList<PixelRect> Dirty, double DirtyFraction)
{
    public bool IsEmpty => Copy is null && Dirty.Count == 0;
}

/// <summary>
/// Finds changed tiles between two BGRA frames and detects a dominant vertical or horizontal shift
/// (scrolling) inside the changed area, so a scroll costs a copy plus the newly exposed strip.
/// </summary>
/// <remarks>Adapted from PORTÅL's delta planner, by the same author.</remarks>
public static class DeltaPlanner
{
    public const int DefaultTileSize = 64;
    private const int MinimumScrollVotes = 8;
    private const double MinimumScrollShare = 0.3;
    private const int RepeatedLineLimit = 3;

    /// <param name="previous">The frame the plan applies to.</param>
    /// <param name="current">The frame the plan produces.</param>
    /// <param name="damage">Where the capture backend says the frame changed, or null to compare
    /// everything. Only tiles touching a damaged rectangle are compared; the comparison still decides
    /// what is really dirty.</param>
    /// <param name="tileSize">The comparison tile size in pixels.</param>
    public static DeltaPlan Plan(RawFrame previous, RawFrame current, IReadOnlyList<PixelRect>? damage = null, int tileSize = DefaultTileSize)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
        {
            throw new ArgumentException("Frames must be the same size.", nameof(current));
        }

        if (damage is { Count: 0 })
        {
            return new DeltaPlan(null, [], 0);
        }

        var candidates = damage is null ? null : Candidates(damage, current.Width, current.Height, tileSize);
        var plain = DirtyTiles(previous.Bgra, current.Bgra, current.Width, current.Height, tileSize, candidates);
        if (plain.Count == 0)
        {
            return new DeltaPlan(null, [], 0);
        }

        var plainArea = Area(plain);
        if (Bounds(plain) is { } box && DetectShift(previous, current, box) is { } copy)
        {
            var predicted = (byte[])previous.Bgra.Clone();
            Apply(predicted, previous.Bgra, current.Width, copy);
            // The copy lands inside the changed box, so the same candidate tiles cover every difference.
            var residual = DirtyTiles(predicted, current.Bgra, current.Width, current.Height, tileSize, candidates);
            var residualArea = Area(residual);
            // Keep the copy only when it saves real work; a wrong guess is simply discarded.
            if (residualArea < plainArea * 0.75)
            {
                return new DeltaPlan(copy, residual, residualArea / (double)(current.Width * current.Height));
            }
        }

        return new DeltaPlan(null, plain, plainArea / (double)(current.Width * current.Height));
    }

    /// <summary>Applies a copy to <paramref name="target"/>, reading from the unmodified
    /// <paramref name="source"/> frame.</summary>
    public static void Apply(byte[] target, byte[] source, int width, CopyOperation copy)
    {
        var stride = width * 4;
        var rowBytes = copy.Source.Width * 4;
        for (var row = 0; row < copy.Source.Height; row++)
        {
            var from = ((copy.Source.Y + row) * stride) + (copy.Source.X * 4);
            var to = ((copy.Destination.Y + row) * stride) + (copy.Destination.X * 4);
            Buffer.BlockCopy(source, from, target, to, rowBytes);
        }
    }

    /// <summary>Copies a rectangle out of a frame as its own tightly packed frame.</summary>
    public static RawFrame Extract(RawFrame frame, PixelRect rect)
    {
        var pixels = new byte[rect.Width * rect.Height * 4];
        for (var row = 0; row < rect.Height; row++)
        {
            Buffer.BlockCopy(frame.Bgra, (((rect.Y + row) * frame.Width) + rect.X) * 4, pixels, row * rect.Width * 4, rect.Width * 4);
        }

        return new RawFrame(rect.Width, rect.Height, pixels);
    }

    /// <summary>Changed tiles merged into rectangles: runs along each tile row, then identical runs down
    /// columns.</summary>
    internal static List<PixelRect> DirtyTiles(byte[] previous, byte[] current, int width, int height, int tileSize, bool[,]? candidates = null)
    {
        var columns = (width + tileSize - 1) / tileSize;
        var rows = (height + tileSize - 1) / tileSize;
        var stride = width * 4;
        var dirty = new bool[rows, columns];
        for (var tileRow = 0; tileRow < rows; tileRow++)
        {
            var top = tileRow * tileSize;
            var bottom = Math.Min(height, top + tileSize);
            for (var tileColumn = 0; tileColumn < columns; tileColumn++)
            {
                if (candidates is not null && !candidates[tileRow, tileColumn])
                {
                    continue;
                }

                var left = tileColumn * tileSize * 4;
                var span = Math.Min(width - (tileColumn * tileSize), tileSize) * 4;
                for (var y = top; y < bottom; y++)
                {
                    var offset = (y * stride) + left;
                    if (!previous.AsSpan(offset, span).SequenceEqual(current.AsSpan(offset, span)))
                    {
                        dirty[tileRow, tileColumn] = true;
                        break;
                    }
                }
            }
        }

        var open = new List<(int Left, int Right, int Top, int Bottom)>();
        var rects = new List<PixelRect>();
        for (var tileRow = 0; tileRow < rows; tileRow++)
        {
            var runs = new List<(int Left, int Right)>();
            for (var column = 0; column < columns; column++)
            {
                if (!dirty[tileRow, column])
                {
                    continue;
                }

                var start = column;
                while (column + 1 < columns && dirty[tileRow, column + 1])
                {
                    column++;
                }

                runs.Add((start, column + 1));
            }

            var next = new List<(int Left, int Right, int Top, int Bottom)>();
            foreach (var run in runs)
            {
                var index = open.FindIndex(o => o.Left == run.Left && o.Right == run.Right && o.Bottom == tileRow);
                if (index >= 0)
                {
                    next.Add(open[index] with { Bottom = tileRow + 1 });
                    open.RemoveAt(index);
                }
                else
                {
                    next.Add((run.Left, run.Right, tileRow, tileRow + 1));
                }
            }

            rects.AddRange(open.Select(o => ToPixels(o, tileSize, width, height)));
            open = next;
        }

        rects.AddRange(open.Select(o => ToPixels(o, tileSize, width, height)));
        return rects;
    }

    /// <summary>The tiles touched by any damaged rectangle.</summary>
    private static bool[,] Candidates(IReadOnlyList<PixelRect> damage, int width, int height, int tileSize)
    {
        var columns = (width + tileSize - 1) / tileSize;
        var rows = (height + tileSize - 1) / tileSize;
        var candidates = new bool[rows, columns];
        foreach (var rect in damage)
        {
            var left = Math.Max(0, rect.X) / tileSize;
            var top = Math.Max(0, rect.Y) / tileSize;
            var right = Math.Min(columns, (Math.Min(width, rect.Right) + tileSize - 1) / tileSize);
            var bottom = Math.Min(rows, (Math.Min(height, rect.Bottom) + tileSize - 1) / tileSize);
            for (var row = top; row < bottom; row++)
            {
                for (var column = left; column < right; column++)
                {
                    candidates[row, column] = true;
                }
            }
        }

        return candidates;
    }

    private static PixelRect ToPixels((int Left, int Right, int Top, int Bottom) tiles, int tileSize, int width, int height)
    {
        var x = tiles.Left * tileSize;
        var y = tiles.Top * tileSize;
        return new PixelRect(x, y, Math.Min(width, tiles.Right * tileSize) - x, Math.Min(height, tiles.Bottom * tileSize) - y);
    }

    private static PixelRect? Bounds(List<PixelRect> rects)
    {
        if (rects.Count == 0)
        {
            return null;
        }

        var left = rects.Min(r => r.X);
        var top = rects.Min(r => r.Y);
        return new PixelRect(left, top, rects.Max(r => r.Right) - left, rects.Max(r => r.Bottom) - top);
    }

    private static long Area(List<PixelRect> rects) => rects.Sum(r => (long)r.Width * r.Height);

    /// <summary>The best of a vertical and a horizontal shift inside the changed box, if either is
    /// convincing.</summary>
    private static CopyOperation? DetectShift(RawFrame previous, RawFrame current, PixelRect box)
    {
        var vertical = BestShift(LineHashes(previous, box, rows: true), LineHashes(current, box, rows: true));
        var horizontal = BestShift(LineHashes(previous, box, rows: false), LineHashes(current, box, rows: false));
        if (vertical is null && horizontal is null)
        {
            return null;
        }

        if (horizontal is null || (vertical is { } v && v.Votes >= horizontal.Value.Votes))
        {
            var dy = vertical!.Value.Shift;
            var height = box.Height - Math.Abs(dy);
            return new CopyOperation(
                new PixelRect(box.X, dy > 0 ? box.Y : box.Y - dy, box.Width, height),
                new PixelPoint(box.X, dy > 0 ? box.Y + dy : box.Y));
        }

        var dx = horizontal.Value.Shift;
        var width = box.Width - Math.Abs(dx);
        return new CopyOperation(
            new PixelRect(dx > 0 ? box.X : box.X - dx, box.Y, width, box.Height),
            new PixelPoint(dx > 0 ? box.X + dx : box.X, box.Y));
    }

    /// <summary>Votes for the offset that best explains where each current line came from. Lines whose
    /// content repeats (flat backgrounds) are ignored because they match everywhere and say
    /// nothing.</summary>
    private static (int Shift, int Votes)? BestShift(ulong[] previous, ulong[] current)
    {
        var lines = new Dictionary<ulong, List<int>>();
        for (var index = 0; index < previous.Length; index++)
        {
            if (!lines.TryGetValue(previous[index], out var list))
            {
                lines[previous[index]] = list = [];
            }

            list.Add(index);
        }

        var votes = new Dictionary<int, int>();
        for (var index = 0; index < current.Length; index++)
        {
            if (!lines.TryGetValue(current[index], out var sources) || sources.Count > RepeatedLineLimit)
            {
                continue;
            }

            foreach (var source in sources)
            {
                var shift = index - source;
                if (shift != 0)
                {
                    votes[shift] = votes.GetValueOrDefault(shift) + 1;
                }
            }
        }

        if (votes.Count == 0)
        {
            return null;
        }

        var best = votes.MaxBy(pair => pair.Value);
        var needed = Math.Max(MinimumScrollVotes, (int)(current.Length * MinimumScrollShare));
        return best.Value >= needed && Math.Abs(best.Key) < current.Length ? (best.Key, best.Value) : null;
    }

    /// <summary>A 64-bit FNV-style hash of each row (or column) of the box.</summary>
    private static ulong[] LineHashes(RawFrame frame, PixelRect box, bool rows)
    {
        const ulong Offset = 0xcbf29ce484222325;
        const ulong Prime = 0x100000001b3;
        var pixels = MemoryMarshal.Cast<byte, uint>(frame.Bgra.AsSpan());
        if (rows)
        {
            var hashes = new ulong[box.Height];
            for (var row = 0; row < box.Height; row++)
            {
                var line = pixels.Slice(((box.Y + row) * frame.Width) + box.X, box.Width);
                var hash = Offset;
                foreach (var pixel in line)
                {
                    hash = (hash ^ pixel) * Prime;
                }

                hashes[row] = hash;
            }

            return hashes;
        }

        var columns = new ulong[box.Width];
        Array.Fill(columns, Offset);
        for (var row = 0; row < box.Height; row++)
        {
            var line = pixels.Slice(((box.Y + row) * frame.Width) + box.X, box.Width);
            for (var column = 0; column < line.Length; column++)
            {
                columns[column] = (columns[column] ^ line[column]) * Prime;
            }
        }

        return columns;
    }
}
