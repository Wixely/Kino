namespace Kino;

/// <summary>A point in physical virtual-screen pixels. Coordinates can be negative on monitors placed
/// left of or above the primary display.</summary>
public readonly record struct PixelPoint(int X, int Y)
{
    public override string ToString() => $"{X}, {Y}";
}

/// <summary>A rectangle in physical virtual-screen pixels. <see cref="Right"/> and <see cref="Bottom"/>
/// are exclusive.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The rectangle spanned by two corner pixels, inclusive of both. The corners can be given
    /// in any order, so a selection made bottom-right first produces the same rectangle.</summary>
    public static PixelRect FromCorners(PixelPoint first, PixelPoint second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X, second.X);
        var bottom = Math.Max(first.Y, second.Y);
        return new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(left, top, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>Shrinks odd dimensions by one pixel. 4:2:0 video encoders need even frame sizes.</summary>
    public PixelRect ToEvenSize() => this with { Width = Width & ~1, Height = Height & ~1 };

    public override string ToString() => $"{Width} × {Height} at {X}, {Y}";
}
