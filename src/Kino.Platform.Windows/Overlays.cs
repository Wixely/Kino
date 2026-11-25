using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Kino.Platform.Windows;

/// <summary>The panel shown at the bottom-right of the primary monitor between the two corner
/// shortcuts. It shows the whole region selected so far, scaled to fit, with its size and corners. It
/// never takes focus, ignores the mouse and is excluded from capture; it fades while the pointer is
/// over it so the screen beneath stays visible.</summary>
internal sealed unsafe class CornerPreview : NativeWindow
{
    private const int LogicalWidth = 360;
    private const int LogicalHeight = 284;
    private static readonly SKRect PreviewBox = SKRect.Create(12, 12, LogicalWidth - 24, 192);
    private static readonly SKColor Panel = new(0x1a, 0x1a, 0x1b, 0xf2);
    private static readonly SKColor Backdrop = new(0x0b, 0x0b, 0x0c);
    private static readonly SKColor Amber = new(0xf2, 0xa9, 0x2f);
    private static readonly SKColor Text = new(0xf2, 0xf2, 0xf0);
    private static readonly SKColor Muted = new(0xb0, 0xb0, 0xb0);
    private readonly ShellThread _shell;
    private readonly SKTypeface _typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    private readonly SKTypeface _bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
    private readonly ScaledScreenGrabber _grabber = new();
    private nuint _timer;
    private SelectionKind _kind;
    private PixelPoint _corner;
    private string _hint = "";
    private PixelRect _bounds;

    public CornerPreview(ShellThread shell)
        : base("KinoCornerPreview",
            Native.WsExLayered | Native.WsExTopmost | Native.WsExToolWindow | Native.WsExNoActivate | Native.WsExTransparent,
            Native.WsPopup)
    {
        _shell = shell;
        _ = Native.SetWindowDisplayAffinity(Handle, Native.WdaExcludeFromCapture);
    }

    public bool IsShowing => _timer != 0;

    public void Show(SelectionKind kind, PixelPoint corner, string hint)
    {
        _kind = kind;
        _corner = corner;
        _hint = hint;
        if (_timer == 0)
        {
            _timer = _shell.StartTimer(33, Render);
        }

        Render();
    }

    public void Hide()
    {
        if (_timer != 0)
        {
            _shell.StopTimer(_timer);
            _timer = 0;
        }

        _ = Native.ShowWindow(Handle, Native.SwHide);
    }

    public override void Dispose()
    {
        Hide();
        _grabber.Dispose();
        base.Dispose();
    }

    private void Render()
    {
        if (!Native.GetCursorPos(out var cursor))
        {
            return;
        }

        var pointer = new PixelPoint(cursor.X, cursor.Y);
        var (bitmap, bounds) = Compose(pointer);
        using (bitmap)
        {
            var pointerOver = pointer.X >= bounds.X && pointer.X < bounds.Right && pointer.Y >= bounds.Y && pointer.Y < bounds.Bottom;
            Present(bitmap, bounds, pointerOver ? (byte)70 : (byte)255);
        }
    }

    /// <summary>Draws the panel for a pointer position and says where on screen it goes.</summary>
    internal (SKBitmap Bitmap, PixelRect Bounds) Compose(PixelPoint pointer)
    {
        var (work, scale) = Native.PrimaryMonitor();
        var width = (int)MathF.Ceiling(LogicalWidth * scale);
        var height = (int)MathF.Ceiling(LogicalHeight * scale);
        var margin = (int)(16 * scale);
        var bounds = new PixelRect(work.Right - width - margin, work.Bottom - height - margin, width, height);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        Draw(canvas, pointer, scale);
        return (bitmap, bounds);
    }

    /// <summary>Sets what the panel describes without showing it.</summary>
    internal void Configure(SelectionKind kind, PixelPoint corner, string hint) => (_kind, _corner, _hint) = (kind, corner, hint);

    private void Draw(SKCanvas canvas, PixelPoint pointer, float scale)
    {
        using var fill = new SKPaint { Color = Panel, IsAntialias = true };
        using var border = new SKPaint { Color = Amber, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        var panel = new SKRoundRect(new SKRect(1, 1, LogicalWidth - 1, LogicalHeight - 1), 12);
        canvas.DrawRoundRect(panel, fill);
        canvas.DrawRoundRect(panel, border);
        using var backdrop = new SKPaint { Color = Backdrop };
        canvas.DrawRoundRect(new SKRoundRect(PreviewBox, 8), backdrop);

        // The whole selection so far, scaled to fit the box. GDI scales while copying, so the cost
        // depends on the box size rather than the region size.
        var region = PixelRect.FromCorners(_corner, pointer);
        var fit = Math.Min(PreviewBox.Width / region.Width, PreviewBox.Height / region.Height);
        var shown = SKRect.Create(
            PreviewBox.MidX - (region.Width * fit / 2),
            PreviewBox.MidY - (region.Height * fit / 2),
            Math.Max(1, region.Width * fit),
            Math.Max(1, region.Height * fit));
        var targetWidth = Math.Max(1, (int)MathF.Round(shown.Width * scale));
        var targetHeight = Math.Max(1, (int)MathF.Round(shown.Height * scale));
        var (pixels, stride) = _grabber.Grab(region, targetWidth, targetHeight);
        var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var image = SKImage.FromPixels(info, pixels, stride))
        {
            canvas.DrawImage(image, shown, new SKSamplingOptions(SKFilterMode.Linear));
        }

        using var outline = new SKPaint { Color = Amber, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRect(shown, outline);

        const float left = 14;
        using var titleFont = new SKFont(_bold, 12);
        using var bodyFont = new SKFont(_typeface, 13);
        using var sizeFont = new SKFont(_bold, 18);
        using var hintFont = new SKFont(_typeface, 12);
        using var amber = new SKPaint { Color = Amber, IsAntialias = true };
        using var text = new SKPaint { Color = Text, IsAntialias = true };
        using var muted = new SKPaint { Color = Muted, IsAntialias = true };
        canvas.DrawText(_kind == SelectionKind.Image ? "SCREENSHOT" : "VIDEO", left, 228, titleFont, amber);
        canvas.DrawText($"{region.Width} × {region.Height}", left + 92, 229, sizeFont, text);
        canvas.DrawText($"From {_corner} to {pointer}", left, 250, bodyFont, muted);
        canvas.DrawText($"{_hint} · Esc cancels", left, 270, hintFont, text);
    }

    private void Present(SKBitmap bitmap, PixelRect bounds, byte opacity)
    {
        var screenDc = Native.GetDC(0);
        var memoryDc = Native.CreateCompatibleDC(screenDc);
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)sizeof(Native.BitmapInfoHeader),
            Width = bitmap.Width,
            Height = -bitmap.Height,
            Planes = 1,
            BitCount = 32,
        };
        var dib = Native.CreateDIBSection(screenDc, &header, Native.DibRgbColors, out var bits, 0, 0);
        try
        {
            if (dib == 0)
            {
                return;
            }

            new ReadOnlySpan<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount).CopyTo(new Span<byte>((void*)bits, bitmap.ByteCount));
            var previous = Native.SelectObject(memoryDc, dib);
            var position = new Native.Point { X = bounds.X, Y = bounds.Y };
            var size = new Native.Size { Cx = bounds.Width, Cy = bounds.Height };
            var origin = default(Native.Point);
            var blend = new Native.BlendFunction { BlendOp = Native.AcSrcOver, SourceConstantAlpha = opacity, AlphaFormat = Native.AcSrcAlpha };
            _ = Native.UpdateLayeredWindow(Handle, screenDc, &position, &size, memoryDc, &origin, 0, &blend, Native.UlwAlpha);
            Native.SelectObject(memoryDc, previous);
            if (_bounds != bounds || !Native.IsWindowVisible(Handle))
            {
                _ = Native.SetWindowPos(Handle, Native.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SwpNoActivate | Native.SwpShowWindow);
                _bounds = bounds;
            }
        }
        finally
        {
            if (dib != 0) Native.DeleteObject(dib);
            Native.DeleteDC(memoryDc);
            _ = Native.ReleaseDC(0, screenDc);
        }
    }
}

/// <summary>A red frame drawn just outside a recording region, made of four thin windows so the
/// recorded pixels inside stay untouched. The windows are also excluded from capture.</summary>
internal sealed class RecordingFrame : IDisposable
{
    private static readonly nint RedBrush = Native.CreateSolidBrush(0x002020E0);
    private readonly FrameEdge[] _edges = [new(), new(), new(), new()];
    private bool _shown;

    /// <summary>Moves the frame if it is shown. A move that arrives after <see cref="Hide"/> is
    /// ignored.</summary>
    public void Move(PixelRect region)
    {
        if (_shown)
        {
            Show(region);
        }
    }

    public void Show(PixelRect region)
    {
        _shown = true;
        var (_, scale) = Native.MonitorAt(new PixelPoint(region.X, region.Y));
        var t = Math.Max(2, (int)MathF.Round(3 * scale));
        PixelRect[] rectangles =
        [
            new(region.X - t, region.Y - t, region.Width + (2 * t), t),
            new(region.X - t, region.Bottom, region.Width + (2 * t), t),
            new(region.X - t, region.Y, t, region.Height),
            new(region.Right, region.Y, t, region.Height),
        ];
        for (var index = 0; index < _edges.Length; index++)
        {
            var rectangle = rectangles[index];
            _ = Native.SetWindowPos(_edges[index].Handle, Native.HwndTopmost, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
                Native.SwpNoActivate | Native.SwpShowWindow);
        }
    }

    public void Hide()
    {
        _shown = false;
        foreach (var edge in _edges)
        {
            _ = Native.ShowWindow(edge.Handle, Native.SwHide);
        }
    }

    public void Dispose()
    {
        foreach (var edge in _edges)
        {
            edge.Dispose();
        }
    }

    private sealed class FrameEdge : NativeWindow
    {
        public FrameEdge()
            : base("KinoRecordingFrame",
                Native.WsExLayered | Native.WsExTopmost | Native.WsExToolWindow | Native.WsExNoActivate | Native.WsExTransparent,
                Native.WsPopup,
                RedBrush)
        {
            _ = Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LwaAlpha);
            _ = Native.SetWindowDisplayAffinity(Handle, Native.WdaExcludeFromCapture);
        }
    }
}

public sealed record TrayMenuItem(string Text, Action? Execute, bool Enabled = true, bool Checked = false)
{
    public static TrayMenuItem Separator { get; } = new("-", null);
}

/// <summary>Kino's notification-area icon and menu.</summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Native.WmApp + 7;
    private readonly ShellThread _shell;
    private readonly uint _taskbarCreated;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private nint _icon;
    private string _tip = "Kino";
    private bool _added;

    public TrayIcon(ShellThread shell, byte[] iconPng, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        _shell = shell;
        _menu = menu;
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        _shell.Invoke(() =>
        {
            _shell.MessageReceived += HandleMessage;
            _icon = CreateIcon(iconPng);
            Add();
        });
    }

    public event Action? Activated;

    public void SetState(byte[] iconPng, string tip) => _shell.Post(() =>
    {
        var previous = _icon;
        _icon = CreateIcon(iconPng);
        _tip = tip;
        Modify(Native.NifIcon | Native.NifTip, null, null, false);
        if (previous != 0) Native.DestroyIcon(previous);
    });

    public void ShowBalloon(string title, string message, bool error) => _shell.Post(() => Modify(Native.NifInfo, title, message, error));

    public void Dispose() => _shell.Invoke(() =>
    {
        _shell.MessageReceived -= HandleMessage;
        if (_added)
        {
            var data = Data(0);
            _ = Native.ShellNotifyIcon(Native.NimDelete, &data);
            _added = false;
        }

        if (_icon != 0) Native.DestroyIcon(_icon);
        _icon = 0;
    });

    /// <summary>Creates an icon handle from PNG bytes at the small-icon size.</summary>
    public static nint CreateIcon(byte[] png)
    {
        var size = Math.Max(16, Native.GetSystemMetrics(49));
        using var source = SKBitmap.Decode(png);
        using var resized = source.Resize(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        fixed (byte* pointer = bytes)
        {
            return Native.CreateIconFromResourceEx(pointer, (uint)bytes.Length, true, 0x00030000, size, size, 0);
        }
    }

    private nint? HandleMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated)
        {
            _added = false;
            Add();
            return 0;
        }

        if (message != CallbackMessage)
        {
            return null;
        }

        var mouse = (uint)(lParam & 0xffff);
        if (mouse == Native.WmLeftButtonUp)
        {
            Activated?.Invoke();
        }
        else if (mouse is Native.WmRightButtonUp or Native.WmContextMenu)
        {
            ShowMenu();
        }

        return 0;
    }

    private void ShowMenu()
    {
        var items = _menu();
        var menu = Native.CreatePopupMenu();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (ReferenceEquals(item, TrayMenuItem.Separator))
                {
                    _ = Native.AppendMenu(menu, Native.MfSeparator, 0, null);
                    continue;
                }

                var flags = Native.MfString | (item.Enabled && item.Execute is not null ? 0 : Native.MfGrayed) | (item.Checked ? Native.MfChecked : 0);
                _ = Native.AppendMenu(menu, flags, (nuint)(index + 1), item.Text);
            }

            _ = Native.GetCursorPos(out var cursor);
            // The menu closes properly only when its owner is the foreground window.
            _ = Native.SetForegroundWindow(_shell.WindowHandle);
            var chosen = Native.TrackPopupMenu(menu, Native.TpmRightButton | Native.TpmReturnCommand | Native.TpmBottomAlign,
                cursor.X, cursor.Y, 0, _shell.WindowHandle, 0);
            _ = Native.PostMessage(_shell.WindowHandle, Native.WmNull, 0, 0);
            if (chosen > 0 && chosen <= items.Count)
            {
                items[(int)chosen - 1].Execute?.Invoke();
            }
        }
        finally
        {
            _ = Native.DestroyMenu(menu);
        }
    }

    private void Add()
    {
        var data = Data(Native.NifMessage | Native.NifIcon | Native.NifTip);
        _added = Native.ShellNotifyIcon(Native.NimAdd, &data);
    }

    private void Modify(uint flags, string? title, string? message, bool error)
    {
        if (!_added)
        {
            return;
        }

        var data = Data(flags);
        if (title is not null && message is not null)
        {
            Native.CopyTo(title, data.InfoTitle, 64);
            Native.CopyTo(message, data.Info, 256);
            data.InfoFlags = error ? Native.NiifError : Native.NiifInfo;
        }

        _ = Native.ShellNotifyIcon(Native.NimModify, &data);
    }

    private Native.NotifyIconData Data(uint flags)
    {
        var data = new Native.NotifyIconData
        {
            Size = (uint)sizeof(Native.NotifyIconData),
            Window = _shell.WindowHandle,
            Id = 1,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        Native.CopyTo(_tip, data.Tip, 128);
        return data;
    }
}

/// <summary>Shows selection and recording feedback on the shell thread.</summary>
public sealed class WindowsCaptureFeedback : ICaptureFeedback, IDisposable
{
    private readonly ShellThread _shell;
    private readonly CornerPreview _preview;
    private readonly RecordingFrame _frame;
    private StrongBox<PixelRect>? _pendingMove;

    public WindowsCaptureFeedback(ShellThread shell)
    {
        _shell = shell;
        (_preview, _frame) = shell.Invoke(() => (new CornerPreview(shell), new RecordingFrame()));
    }

    public event Action<string, string>? Notified;

    public void ShowSelection(SelectionKind kind, PixelPoint firstCorner, string nextStep) =>
        _shell.Invoke(() => _preview.Show(kind, firstCorner, nextStep));

    // Synchronous: the workflow captures pixels right after this returns.
    public void HideSelection() => _shell.Invoke(_preview.Hide);

    public void ShowRecordingFrame(PixelRect region) => _shell.Invoke(() => _frame.Show(region));

    // Called every frame while following the pointer. Only the newest region is kept, so a busy shell
    // thread applies one move instead of a backlog.
    public void MoveRecordingFrame(PixelRect region)
    {
        if (Interlocked.Exchange(ref _pendingMove, new StrongBox<PixelRect>(region)) is null)
        {
            _shell.Post(() =>
            {
                if (Interlocked.Exchange(ref _pendingMove, null) is { } move)
                {
                    _frame.Move(move.Value);
                }
            });
        }
    }

    public void HideRecordingFrame() => _shell.Invoke(_frame.Hide);

    public void Notify(string title, string message) => Notified?.Invoke(title, message);

    public void Dispose() => _shell.Invoke(() =>
    {
        _preview.Dispose();
        _frame.Dispose();
    });
}
