using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Kino.Platform.Windows;

/// <summary>A Win32 window owned by managed code. Messages for created windows are routed to the
/// owning instance; a window must be created, used and destroyed on one thread.</summary>
internal abstract unsafe class NativeWindow : IDisposable
{
    private static readonly ConcurrentDictionary<nint, NativeWindow> Windows = new();
    private static readonly ConcurrentDictionary<string, bool> RegisteredClasses = new(StringComparer.Ordinal);

    protected NativeWindow(string className, uint exStyle, uint style, nint background = 0)
    {
        EnsureClass(className, background);
        Handle = Native.CreateWindowEx(exStyle, className, "Kino", style, 0, 0, 0, 0, 0, 0, Native.GetModuleHandle(0), 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException($"Windows refused to create a {className} window (error {Marshal.GetLastPInvokeError()}).");
        }

        Windows[Handle] = this;
    }

    public nint Handle { get; private set; }

    protected virtual nint WndProc(uint message, nuint wParam, nint lParam) =>
        Native.DefWindowProc(Handle, message, wParam, lParam);

    public virtual void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        Windows.TryRemove(Handle, out _);
        _ = Native.DestroyWindow(Handle);
        Handle = 0;
        GC.SuppressFinalize(this);
    }

    private static void EnsureClass(string className, nint background)
    {
        if (!RegisteredClasses.TryAdd(className, true))
        {
            return;
        }

        fixed (char* name = className)
        {
            var windowClass = new Native.WndClassEx
            {
                Size = (uint)sizeof(Native.WndClassEx),
                WndProc = &Route,
                Instance = Native.GetModuleHandle(0),
                ClassName = name,
                Background = background,
                Cursor = Native.LoadCursor(0, 32512),
            };
            if (Native.RegisterClassEx(&windowClass) == 0)
            {
                RegisteredClasses.TryRemove(className, out _);
                throw new InvalidOperationException($"Windows refused to register the {className} window class.");
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint Route(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            return Windows.TryGetValue(window, out var owner)
                ? owner.WndProc(message, wParam, lParam)
                : Native.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            // An exception cannot cross the native callback; report it and keep the window alive.
            System.Diagnostics.Trace.WriteLine($"Kino window procedure failed: {exception}");
            return Native.DefWindowProc(window, message, wParam, lParam);
        }
    }
}
