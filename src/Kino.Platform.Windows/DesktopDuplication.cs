using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kino.Media;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Kino.Platform.Windows;

/// <summary>
/// Captures a desktop region through DXGI Desktop Duplication. Windows hands over a new desktop image
/// only when something was presented, together with the rectangles it redrew and moved, so a still
/// screen costs almost nothing and only changed areas need comparing. The pointer is not included.
/// </summary>
/// <remarks>
/// Adapted from PORTÅL's capture backend, by the same author, for capture at the region's native size.
/// Throws <see cref="NotSupportedException"/> for regions it cannot serve (rotated outputs, outputs on
/// different adapters) and other exceptions when duplication is lost (secure desktop, mode change).
/// Not thread-safe.
/// </remarks>
internal sealed class DesktopDuplicationCapture : IDisposable
{
    private const uint FirstFrameTimeoutMilliseconds = 500;
    private readonly IDXGIFactory1 _factory;
    private readonly List<DuplicatedOutput> _outputs = [];
    private readonly List<PixelRect> _desktopDamage = [];
    private bool _damageUnknown;
    private IDXGIAdapter1? _deviceAdapter;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private (int Width, int Height) _stagingSize;
    private RawFrame? _last;
    private PixelRect _lastRegion;

    public DesktopDuplicationCapture()
    {
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        try
        {
            for (uint adapterIndex = 0; _factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                var kept = false;
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                {
                    var description = output.Description;
                    if (!description.AttachedToDesktop)
                    {
                        output.Dispose();
                        continue;
                    }

                    var r = description.DesktopCoordinates;
                    _outputs.Add(new DuplicatedOutput(adapter, output.QueryInterface<IDXGIOutput1>(), new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), description.Rotation));
                    output.Dispose();
                    kept = true;
                }

                if (!kept)
                {
                    adapter.Dispose();
                }
            }

            if (_outputs.Count == 0)
            {
                throw new NotSupportedException("No display output can be duplicated.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Captures <paramref name="region"/>. When nothing was presented since the previous call
    /// for the same region, returns the previous frame instance with empty damage.</summary>
    /// <returns>The frame and where it changed, or null damage when unknown.</returns>
    public (RawFrame Frame, IReadOnlyList<PixelRect>? Damage) Capture(PixelRect region)
    {
        var needed = _outputs.Where(o => !o.Bounds.Intersect(region).IsEmpty).ToList();
        if (needed.Count == 0)
        {
            throw new NotSupportedException("The region is outside every duplicated output.");
        }

        if (needed.Any(o => o.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified)))
        {
            throw new NotSupportedException("Rotated outputs are captured with GDI.");
        }

        if (needed.Any(o => o.Adapter.Description1.Luid != needed[0].Adapter.Description1.Luid))
        {
            throw new NotSupportedException("The region spans outputs on different graphics adapters.");
        }

        EnsureDevice(needed[0].Adapter);
        var sameView = _last is not null && region == _lastRegion;
        if (!sameView)
        {
            _damageUnknown = true;
            _desktopDamage.Clear();
        }

        var presented = false;
        foreach (var output in needed)
        {
            presented |= Pull(output);
        }

        if (sameView && !presented)
        {
            return (_last!, []);
        }

        var pixels = Read(region, needed);
        IReadOnlyList<PixelRect>? damage = null;
        if (!_damageUnknown)
        {
            var dirty = new List<PixelRect>(_desktopDamage.Count);
            foreach (var rect in _desktopDamage)
            {
                var part = rect.Intersect(region);
                if (!part.IsEmpty)
                {
                    dirty.Add(part with { X = part.X - region.X, Y = part.Y - region.Y });
                }
            }

            damage = dirty;
        }

        _damageUnknown = false;
        _desktopDamage.Clear();
        _last = new RawFrame(region.Width, region.Height, pixels);
        _lastRegion = region;
        return (_last, damage);
    }

    public void Dispose()
    {
        ReleaseDevice();
        foreach (var adapter in _outputs.Select(o => o.Adapter).Distinct())
        {
            adapter.Dispose();
        }

        foreach (var output in _outputs)
        {
            output.Output.Dispose();
        }

        _outputs.Clear();
        _factory.Dispose();
    }

    /// <summary>Takes the output's newest desktop image if one was presented. Returns whether it
    /// changed.</summary>
    private bool Pull(DuplicatedOutput output)
    {
        if (output.Duplication is null)
        {
            output.Duplication = output.Output.DuplicateOutput(_device!);
            output.HasImage = false;
        }

        var result = output.Duplication.AcquireNextFrame(output.HasImage ? 0 : FirstFrameTimeoutMilliseconds, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
        {
            if (!output.HasImage)
            {
                throw new InvalidOperationException("Desktop Duplication produced no first frame.");
            }

            return false;
        }

        result.CheckError();
        try
        {
            // A pointer-only update carries no new image.
            if (info.LastPresentTime == 0)
            {
                return false;
            }

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            if (output.Copy is null)
            {
                var description = texture.Description;
                output.Copy = _device!.CreateTexture2D(new Texture2DDescription(
                    description.Format, description.Width, description.Height, 1, 1, BindFlags.None, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            }

            _context!.CopyResource(output.Copy, texture);
            if (!output.HasImage)
            {
                output.HasImage = true;
                _damageUnknown = true;
            }
            else if (!_damageUnknown)
            {
                CollectDamage(output, info);
            }

            return true;
        }
        finally
        {
            resource?.Dispose();
            output.Duplication.ReleaseFrame();
        }
    }

    /// <summary>Records the redrawn and moved rectangles of this frame in desktop coordinates.</summary>
    private void CollectDamage(DuplicatedOutput output, OutduplFrameInfo info)
    {
        if (info.TotalMetadataBufferSize == 0)
        {
            _damageUnknown = true;
            return;
        }

        try
        {
            var moves = new OutduplMoveRect[(info.TotalMetadataBufferSize / Unsafe.SizeOf<OutduplMoveRect>()) + 1];
            output.Duplication!.GetFrameMoveRects((uint)(moves.Length * Unsafe.SizeOf<OutduplMoveRect>()), moves, out var moveBytes).CheckError();
            for (var index = 0; index < moveBytes / Unsafe.SizeOf<OutduplMoveRect>(); index++)
            {
                AddDesktopDamage(output, moves[index].DestinationRect);
            }

            var dirty = new RawRect[(info.TotalMetadataBufferSize / Unsafe.SizeOf<RawRect>()) + 1];
            output.Duplication.GetFrameDirtyRects((uint)(dirty.Length * Unsafe.SizeOf<RawRect>()), dirty, out var dirtyBytes).CheckError();
            for (var index = 0; index < dirtyBytes / Unsafe.SizeOf<RawRect>(); index++)
            {
                AddDesktopDamage(output, dirty[index]);
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            _damageUnknown = true;
        }
    }

    private void AddDesktopDamage(DuplicatedOutput output, RawRect rect) =>
        _desktopDamage.Add(new PixelRect(output.Bounds.X + rect.Left, output.Bounds.Y + rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));

    /// <summary>Copies each output's part of the region into a CPU-readable texture and reads it back as
    /// opaque BGRA. Parts of the region that no output covers stay black.</summary>
    private byte[] Read(PixelRect region, List<DuplicatedOutput> needed)
    {
        EnsureStaging(region.Width, region.Height);
        foreach (var output in needed)
        {
            var part = output.Bounds.Intersect(region);
            _context!.CopySubresourceRegion(
                _staging!, 0, (uint)(part.X - region.X), (uint)(part.Y - region.Y), 0,
                output.Copy!, 0,
                new Box(part.X - output.Bounds.X, part.Y - output.Bounds.Y, 0, part.Right - output.Bounds.X, part.Bottom - output.Bounds.Y, 1));
        }

        var pixels = new byte[region.Width * region.Height * 4];
        var mapped = _context!.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var row = 0; row < region.Height; row++)
            {
                Marshal.Copy(mapped.DataPointer + (row * (int)mapped.RowPitch), pixels, row * region.Width * 4, region.Width * 4);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = 0xff;
        }

        return pixels;
    }

    private void EnsureDevice(IDXGIAdapter1 adapter)
    {
        if (_device is not null && ReferenceEquals(adapter, _deviceAdapter))
        {
            return;
        }

        ReleaseDevice();
        D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device,
            out _context).CheckError();
        _deviceAdapter = adapter;
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging is not null && _stagingSize == (width, height))
        {
            return;
        }

        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        _stagingSize = (width, height);
    }

    private void ReleaseDevice()
    {
        foreach (var output in _outputs)
        {
            output.ReleaseDuplication();
        }

        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _staging = null;
        _context = null;
        _device = null;
        _deviceAdapter = null;
        _last = null;
    }

    private sealed class DuplicatedOutput(IDXGIAdapter1 adapter, IDXGIOutput1 output, PixelRect bounds, ModeRotation rotation)
    {
        public IDXGIAdapter1 Adapter { get; } = adapter;
        public IDXGIOutput1 Output { get; } = output;
        public PixelRect Bounds { get; } = bounds;
        public ModeRotation Rotation { get; } = rotation;
        public IDXGIOutputDuplication? Duplication { get; set; }
        public ID3D11Texture2D? Copy { get; set; }
        public bool HasImage { get; set; }

        public void ReleaseDuplication()
        {
            Duplication?.Dispose();
            Copy?.Dispose();
            Duplication = null;
            Copy = null;
            HasImage = false;
        }
    }
}

/// <summary>Draws the mouse pointer onto a captured frame with GDI, which handles every cursor kind
/// (colour, monochrome and inverting). Only the pointer's own box is copied through GDI.</summary>
internal sealed unsafe class CursorPainter : IDisposable
{
    private readonly nint _screenDc = Native.GetDC(0);
    private readonly nint _memoryDc;
    private nint _bitmap;
    private nint _previousBitmap;
    private byte* _bits;
    private int _size;

    public CursorPainter()
    {
        _memoryDc = Native.CreateCompatibleDC(_screenDc);
    }

    /// <summary>Paints the pointer onto <paramref name="frame"/>, which shows <paramref name="region"/>
    /// of the desktop. Returns the painted box in frame pixels, or null when the pointer is hidden or
    /// outside the frame.</summary>
    public PixelRect? Paint(RawFrame frame, PixelRect region)
    {
        var info = new Native.CursorInfo { Size = (uint)sizeof(Native.CursorInfo) };
        if (!Native.GetCursorInfo(ref info) || (info.Flags & Native.CursorShowing) == 0 || info.Cursor == 0 ||
            !Native.GetIconInfo(info.Cursor, out var icon))
        {
            return null;
        }

        int width;
        int height;
        try
        {
            var description = default(Native.BitmapDescription);
            var source = icon.ColorBitmap != 0 ? icon.ColorBitmap : icon.MaskBitmap;
            if (Native.GetObject(source, sizeof(Native.BitmapDescription), &description) == 0)
            {
                return null;
            }

            width = description.Width;
            // A monochrome cursor stacks its AND and XOR masks in one bitmap of double height.
            height = icon.ColorBitmap != 0 ? description.Height : description.Height / 2;
        }
        finally
        {
            if (icon.MaskBitmap != 0) Native.DeleteObject(icon.MaskBitmap);
            if (icon.ColorBitmap != 0) Native.DeleteObject(icon.ColorBitmap);
        }

        var left = info.ScreenPos.X - (int)icon.XHotspot - region.X;
        var top = info.ScreenPos.Y - (int)icon.YHotspot - region.Y;
        var box = new PixelRect(left, top, width, height).Intersect(new PixelRect(0, 0, frame.Width, frame.Height));
        if (box.IsEmpty)
        {
            return null;
        }

        Ensure(Math.Max(box.Width, box.Height));
        var rowBytes = box.Width * 4;
        for (var row = 0; row < box.Height; row++)
        {
            new ReadOnlySpan<byte>(frame.Bgra, (((box.Y + row) * frame.Width) + box.X) * 4, rowBytes).CopyTo(new Span<byte>(_bits + ((long)row * _size * 4), rowBytes));
        }

        _ = Native.DrawIconEx(_memoryDc, left - box.X, top - box.Y, info.Cursor, 0, 0, 0, 0, Native.DiNormal);
        _ = Native.GdiFlush();
        for (var row = 0; row < box.Height; row++)
        {
            var target = new Span<byte>(frame.Bgra, (((box.Y + row) * frame.Width) + box.X) * 4, rowBytes);
            new ReadOnlySpan<byte>(_bits + ((long)row * _size * 4), rowBytes).CopyTo(target);
            for (var index = 3; index < rowBytes; index += 4)
            {
                target[index] = 0xff;
            }
        }

        return box;
    }

    public void Dispose()
    {
        Release();
        Native.DeleteDC(_memoryDc);
        _ = Native.ReleaseDC(0, _screenDc);
    }

    private void Ensure(int size)
    {
        if (size <= _size)
        {
            return;
        }

        Release();
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)sizeof(Native.BitmapInfoHeader),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_screenDc, &header, Native.DibRgbColors, out var bits, 0, 0);
        if (_bitmap == 0 || bits == 0)
        {
            throw new InvalidOperationException("Windows could not allocate the pointer buffer.");
        }

        _bits = (byte*)bits;
        _size = size;
        _previousBitmap = Native.SelectObject(_memoryDc, _bitmap);
    }

    private void Release()
    {
        if (_bitmap != 0)
        {
            Native.SelectObject(_memoryDc, _previousBitmap);
            Native.DeleteObject(_bitmap);
            _bitmap = 0;
            _size = 0;
        }
    }
}

/// <summary>
/// Frames for the JPEG recording modes: Desktop Duplication with its change reports, falling back to
/// GDI when duplication cannot serve the region or is lost, and retrying duplication later. Each call
/// returns a frame the caller may keep: one it receives is never changed afterwards.
/// </summary>
internal sealed class RegionFrameSource : IDisposable
{
    private const string DuplicationName = "Desktop Duplication";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private readonly PixelRect _region;
    private readonly bool _includeCursor;
    private readonly CursorPainter? _cursor;
    private DesktopDuplicationCapture? _duplication;
    private bool _duplicationUnsupported;
    private DateTime _retryAt;
    private GdiFrameGrabber? _gdi;
    private RawFrame? _last;
    private PixelRect? _lastCursor;

    public RegionFrameSource(PixelRect region, bool includeCursor)
    {
        _region = region;
        _includeCursor = includeCursor;
        _cursor = includeCursor ? new CursorPainter() : null;
    }

    /// <summary>The backend that produced the latest frame, for diagnostics.</summary>
    public string Backend { get; private set; } = "none";

    /// <summary>Frames that came from GDI because duplication was unavailable.</summary>
    public long GdiFrames { get; private set; }

    /// <summary>The next frame and where it changed (null when unknown).</summary>
    public (RawFrame Frame, IReadOnlyList<PixelRect>? Damage) Next()
    {
        if (TryDuplication() is { } captured)
        {
            // After GDI frames, duplication's change report describes changes since its own last
            // frame, not since the frame the caller holds.
            var damage = Backend == DuplicationName ? captured.Damage : null;
            Backend = DuplicationName;
            return WithCursor(captured.Frame, damage);
        }

        Backend = "GDI";
        GdiFrames++;
        _gdi ??= new GdiFrameGrabber(_region.Width, _region.Height);
        RawFrame frame;
        try
        {
            frame = new RawFrame(_region.Width, _region.Height, _gdi.Grab(_region.X, _region.Y, _includeCursor).ToArray());
        }
        catch (InvalidOperationException) when (_last is not null)
        {
            // The secure desktop (UAC, lock screen) cannot be copied; hold the last image.
            return (_last, []);
        }

        _lastCursor = null;
        _last = frame;
        return (frame, null);
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _gdi?.Dispose();
        _cursor?.Dispose();
    }

    private (RawFrame Frame, IReadOnlyList<PixelRect>? Damage)? TryDuplication()
    {
        if (_duplicationUnsupported || DateTime.UtcNow < _retryAt)
        {
            return null;
        }

        try
        {
            _duplication ??= new DesktopDuplicationCapture();
            return _duplication.Capture(_region);
        }
        catch (NotSupportedException)
        {
            _duplicationUnsupported = true;
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException or COMException)
        {
            // Lost to a desktop switch, display change or driver reset. GDI covers the gap.
        }

        _duplication?.Dispose();
        _duplication = null;
        _retryAt = DateTime.UtcNow + RetryInterval;
        return null;
    }

    private (RawFrame Frame, IReadOnlyList<PixelRect>? Damage) WithCursor(RawFrame frame, IReadOnlyList<PixelRect>? damage)
    {
        if (_cursor is null)
        {
            _last = frame;
            return (frame, damage);
        }

        // Duplication frames are shared with the capture, so the pointer goes on a copy.
        var painted = frame with { Bgra = (byte[])frame.Bgra.Clone() };
        var box = _cursor.Paint(painted, _region);
        if (damage is not null)
        {
            var merged = new List<PixelRect>(damage);
            if (_lastCursor is { } previous) merged.Add(previous);
            if (box is { } current) merged.Add(current);
            damage = merged;
        }

        _lastCursor = box;
        _last = painted;
        return (painted, damage);
    }
}
