using SkiaSharp;

namespace Kino.Platform.Windows;

/// <summary>Copies screen pixels with GDI into a reusable top-down BGRA buffer. Coordinates are
/// physical virtual-screen pixels, which requires a per-monitor DPI-aware process.</summary>
public sealed unsafe class GdiFrameGrabber : IDisposable
{
    private readonly nint _screenDc;
    private readonly nint _memoryDc;
    private readonly nint _bitmap;
    private readonly nint _previousBitmap;
    private readonly byte* _bits;

    public GdiFrameGrabber(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _screenDc = Native.GetDC(0);
        _memoryDc = Native.CreateCompatibleDC(_screenDc);
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)sizeof(Native.BitmapInfoHeader),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_screenDc, &header, Native.DibRgbColors, out var bits, 0, 0);
        if (_bitmap == 0 || bits == 0)
        {
            Native.DeleteDC(_memoryDc);
            _ = Native.ReleaseDC(0, _screenDc);
            throw new InvalidOperationException($"Windows could not allocate a {width} × {height} capture buffer.");
        }

        _bits = (byte*)bits;
        _previousBitmap = Native.SelectObject(_memoryDc, _bitmap);
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    /// <summary>Captures the region whose top-left corner is (<paramref name="x"/>, <paramref name="y"/>).
    /// The returned span is valid until the next call.</summary>
    public ReadOnlySpan<byte> Grab(int x, int y, bool includeCursor)
    {
        // CAPTUREBLT includes layered windows. Kino's own overlays opt out of capture through their
        // display affinity, so they are not copied.
        if (!Native.BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, x, y, Native.SrcCopy | Native.CaptureBlt) &&
            !GrabPerMonitor(x, y))
        {
            throw new InvalidOperationException("Windows refused to copy the screen. The desktop may be locked or on a secure screen.");
        }

        if (includeCursor)
        {
            DrawCursor(x, y);
        }

        _ = Native.GdiFlush();
        return new ReadOnlySpan<byte>(_bits, FrameBytes);
    }

    /// <summary>Captures into a new image with opaque alpha.</summary>
    public CapturedImage GrabImage(int x, int y, bool includeCursor)
    {
        var pixels = Grab(x, y, includeCursor).ToArray();
        // GDI leaves the alpha byte undefined; PNG and clipboard consumers need it opaque.
        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
        }

        return new CapturedImage(Width, Height, pixels);
    }

    // A region that reaches into a gap between monitors of different sizes cannot be copied in one
    // call. Each monitor's part is copied separately and the gaps are left black.
    private bool GrabPerMonitor(int x, int y)
    {
        new Span<byte>(_bits, FrameBytes).Clear();
        var region = new PixelRect(x, y, Width, Height);
        var copied = false;
        foreach (var monitor in Native.MonitorBounds())
        {
            var part = region.Intersect(monitor);
            if (!part.IsEmpty)
            {
                copied |= Native.BitBlt(_memoryDc, part.X - x, part.Y - y, part.Width, part.Height, _screenDc, part.X, part.Y,
                    Native.SrcCopy | Native.CaptureBlt);
            }
        }

        return copied;
    }

    private void DrawCursor(int originX, int originY)
    {
        var info = new Native.CursorInfo { Size = (uint)sizeof(Native.CursorInfo) };
        if (!Native.GetCursorInfo(ref info) || (info.Flags & Native.CursorShowing) == 0 || info.Cursor == 0)
        {
            return;
        }

        var hotspotX = 0;
        var hotspotY = 0;
        if (Native.GetIconInfo(info.Cursor, out var icon))
        {
            hotspotX = (int)icon.XHotspot;
            hotspotY = (int)icon.YHotspot;
            if (icon.MaskBitmap != 0) Native.DeleteObject(icon.MaskBitmap);
            if (icon.ColorBitmap != 0) Native.DeleteObject(icon.ColorBitmap);
        }

        _ = Native.DrawIconEx(_memoryDc, info.ScreenPos.X - originX - hotspotX, info.ScreenPos.Y - originY - hotspotY,
            info.Cursor, 0, 0, 0, 0, Native.DiNormal);
    }

    public void Dispose()
    {
        Native.SelectObject(_memoryDc, _previousBitmap);
        Native.DeleteObject(_bitmap);
        Native.DeleteDC(_memoryDc);
        _ = Native.ReleaseDC(0, _screenDc);
    }
}

/// <summary>Copies a screen region scaled down to a small size in one GDI call, for previews. The
/// buffer grows to the largest size requested and is reused.</summary>
internal sealed unsafe class ScaledScreenGrabber : IDisposable
{
    private readonly nint _screenDc = Native.GetDC(0);
    private readonly nint _memoryDc;
    private nint _bitmap;
    private nint _previousBitmap;
    private byte* _bits;
    private int _width;
    private int _height;

    public ScaledScreenGrabber()
    {
        _memoryDc = Native.CreateCompatibleDC(_screenDc);
        // Halftone averages source pixels instead of dropping them, so text stays legible when shrunk.
        _ = Native.SetStretchBltMode(_memoryDc, Native.Halftone);
        _ = Native.SetBrushOrgEx(_memoryDc, 0, 0, 0);
    }

    /// <summary>Copies <paramref name="region"/> scaled to <paramref name="width"/> × <paramref name="height"/>.
    /// The pixels are top-down BGRA with the returned stride and stay valid until the next call.</summary>
    public (nint Pixels, int Stride) Grab(PixelRect region, int width, int height)
    {
        Ensure(width, height);
        if (!Native.StretchBlt(_memoryDc, 0, 0, width, height, _screenDc, region.X, region.Y, region.Width, region.Height,
                Native.SrcCopy | Native.CaptureBlt))
        {
            // The region reaches into a gap between monitors; scale each monitor's part on its own
            // and leave the gaps black.
            for (var row = 0; row < height; row++)
            {
                new Span<byte>(_bits + ((long)row * _width * 4), width * 4).Clear();
            }

            foreach (var monitor in Native.MonitorBounds())
            {
                var part = region.Intersect(monitor);
                if (part.IsEmpty)
                {
                    continue;
                }

                var left = (int)((long)(part.X - region.X) * width / region.Width);
                var top = (int)((long)(part.Y - region.Y) * height / region.Height);
                var right = (int)((long)(part.Right - region.X) * width / region.Width);
                var bottom = (int)((long)(part.Bottom - region.Y) * height / region.Height);
                _ = Native.StretchBlt(_memoryDc, left, top, Math.Max(1, right - left), Math.Max(1, bottom - top), _screenDc,
                    part.X, part.Y, part.Width, part.Height, Native.SrcCopy | Native.CaptureBlt);
            }
        }

        _ = Native.GdiFlush();
        // GDI leaves the alpha byte zero, which compositing would treat as transparent.
        for (var row = 0; row < height; row++)
        {
            var pixel = _bits + ((long)row * _width * 4) + 3;
            for (var column = 0; column < width; column++, pixel += 4)
            {
                *pixel = 255;
            }
        }

        return ((nint)_bits, _width * 4);
    }

    public void Dispose()
    {
        Release();
        Native.DeleteDC(_memoryDc);
        _ = Native.ReleaseDC(0, _screenDc);
    }

    private void Ensure(int width, int height)
    {
        if (width <= _width && height <= _height)
        {
            return;
        }

        Release();
        _width = Math.Max(width, _width);
        _height = Math.Max(height, _height);
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)sizeof(Native.BitmapInfoHeader),
            Width = _width,
            Height = -_height,
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_screenDc, &header, Native.DibRgbColors, out var bits, 0, 0);
        if (_bitmap == 0 || bits == 0)
        {
            _width = _height = 0;
            throw new InvalidOperationException("Windows could not allocate the preview buffer.");
        }

        _bits = (byte*)bits;
        _previousBitmap = Native.SelectObject(_memoryDc, _bitmap);
    }

    private void Release()
    {
        if (_bitmap != 0)
        {
            Native.SelectObject(_memoryDc, _previousBitmap);
            Native.DeleteObject(_bitmap);
            _bitmap = 0;
        }
    }
}

public static class PngEncoder
{
    public static byte[] Encode(CapturedImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var skImage = SKImage.FromPixelCopy(info, image.Pixels, image.Width * 4)
            ?? throw new InvalidOperationException("The captured pixels could not be prepared for encoding.");
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>Writes images and text to the Windows clipboard.</summary>
public sealed unsafe class WindowsClipboard
{
    private static readonly uint PngFormat = Native.RegisterClipboardFormat("PNG");
    private readonly Func<nint> _owner;

    public WindowsClipboard(Func<nint> owner)
    {
        _owner = owner;
    }

    /// <summary>Places the image as a device-independent bitmap and as PNG, so both classic and
    /// browser-based applications can paste it.</summary>
    public void SetImage(CapturedImage image, byte[] png)
    {
        var dib = CreateDib(image);
        WithClipboard(() =>
        {
            SetData(Native.CfDib, dib);
            SetData(PngFormat, png);
        });
    }

    /// <summary>Which of the formats Kino writes are on the clipboard now, for self-tests.</summary>
    public static (bool Dib, bool Png, bool Text) AvailableFormats() =>
        (Native.IsClipboardFormatAvailable(Native.CfDib), Native.IsClipboardFormatAvailable(PngFormat), Native.IsClipboardFormatAvailable(Native.CfUnicodeText));

    public void SetText(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, bytes);
        WithClipboard(() => SetData(Native.CfUnicodeText, bytes));
    }

    /// <summary>A bottom-up 32-bit DIB, the layout most clipboard readers expect.</summary>
    public static byte[] CreateDib(CapturedImage image)
    {
        var headerSize = sizeof(Native.BitmapInfoHeader);
        var stride = image.Width * 4;
        var dib = new byte[headerSize + (stride * image.Height)];
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)headerSize,
            Width = image.Width,
            Height = image.Height,
            Planes = 1,
            BitCount = 32,
            SizeImage = (uint)(stride * image.Height),
        };
        fixed (byte* destination = dib)
        {
            *(Native.BitmapInfoHeader*)destination = header;
        }

        for (var row = 0; row < image.Height; row++)
        {
            image.Pixels.AsSpan(row * stride, stride).CopyTo(dib.AsSpan(headerSize + ((image.Height - 1 - row) * stride), stride));
        }

        return dib;
    }

    private void WithClipboard(Action write)
    {
        // Another application can hold the clipboard briefly; retry rather than fail at once.
        var opened = false;
        for (var attempt = 0; attempt < 20 && !(opened = Native.OpenClipboard(_owner())); attempt++)
        {
            Thread.Sleep(25);
        }

        if (!opened)
        {
            throw new InvalidOperationException("Another application is holding the clipboard.");
        }

        try
        {
            _ = Native.EmptyClipboard();
            write();
        }
        finally
        {
            _ = Native.CloseClipboard();
        }
    }

    private static void SetData(uint format, byte[] data)
    {
        var memory = Native.GlobalAlloc(Native.GmemMoveable, (nuint)data.Length);
        if (memory == 0)
        {
            throw new InvalidOperationException("The clipboard data could not be allocated.");
        }

        var pointer = Native.GlobalLock(memory);
        new ReadOnlySpan<byte>(data).CopyTo(new Span<byte>((void*)pointer, data.Length));
        _ = Native.GlobalUnlock(memory);
        if (Native.SetClipboardData(format, memory) == 0)
        {
            // Ownership passes to the clipboard only on success.
            _ = Native.GlobalFree(memory);
            throw new InvalidOperationException("Windows refused the clipboard data.");
        }
    }
}

/// <summary>The Windows implementation of the capture workflow's screen and clipboard access.</summary>
public sealed class WindowsCapturePlatform : ICapturePlatform
{
    private readonly WindowsClipboard _clipboard;
    private readonly ShellThread _shell;

    public WindowsCapturePlatform(ShellThread shell)
    {
        _shell = shell;
        _clipboard = new WindowsClipboard(() => shell.WindowHandle);
    }

    public PixelRect VirtualScreen => Native.VirtualScreen();

    public PixelPoint PointerPosition() => Native.GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;

    public CapturedImage Capture(PixelRect region, bool includeCursor)
    {
        using var grabber = new GdiFrameGrabber(region.Width, region.Height);
        return grabber.GrabImage(region.X, region.Y, includeCursor);
    }

    public byte[] EncodePng(CapturedImage image) => PngEncoder.Encode(image);

    // The shell window owns the clipboard, so writes run on its thread.
    public void CopyImage(CapturedImage image, byte[] png) => _shell.Invoke(() => _clipboard.SetImage(image, png));

    public void CopyText(string text) => _shell.Invoke(() => _clipboard.SetText(text));
}
