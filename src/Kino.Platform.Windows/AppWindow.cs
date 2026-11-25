using System.Runtime.InteropServices;

namespace Kino.Platform.Windows;

/// <summary>
/// Controls the CupriFace window from outside the CupriFace host. Kino starts in the tray, so the
/// window is hidden as soon as it exists, and closing it hides it again instead of ending the process.
/// Only <see cref="Quit"/> lets a close through.
/// </summary>
public static unsafe class AppWindow
{
    private static nint _window;
    private static nint _previousProcedure;
    private static volatile bool _allowClose;
    private static string _searchTitle = "";
    private static nint _found;

    public static bool IsAttached => _window != 0;

    /// <summary>True once something asked for the window, after which startup stops hiding it.</summary>
    public static bool ShowRequested { get; private set; }
    public static bool IsVisible => _window != 0 && Native.IsWindowVisible(_window);

    /// <summary>True when the window is visible but minimised to the taskbar.</summary>
    public static bool IsMinimized => IsVisible && Native.IsIconic(_window);

    /// <summary>True once <see cref="Quit"/> has closed the window for good.</summary>
    public static bool Quitting => _allowClose;

    /// <summary>Shows the window minimised on the taskbar without activating it, queued like
    /// <see cref="Minimize"/>.</summary>
    public static void ShowMinimized()
    {
        if (_window != 0)
        {
            _ = Native.ShowWindowAsync(_window, Native.SwShowMinNoActive);
        }
    }

    /// <summary>Minimises through the window's message queue. Minimising synchronously from inside a
    /// CupriFace update makes the same frame draw to a zero-size surface, which crashes the process;
    /// queued, the window handles the minimise before its next frame, as with the title-bar button.</summary>
    public static void Minimize()
    {
        if (_window != 0)
        {
            _ = Native.ShowWindowAsync(_window, Native.SwMinimize);
        }
    }

    /// <summary>Finds this process's top-level window titled <paramref name="title"/> that Kino's own
    /// platform layer did not create, and takes over its close behaviour.</summary>
    public static bool TryAttach(string title)
    {
        if (_window != 0)
        {
            return true;
        }

        _searchTitle = title;
        _found = 0;
        _ = Native.EnumWindows(&FindWindow, 0);
        if (_found == 0)
        {
            return false;
        }

        _window = _found;
        _previousProcedure = Native.SetWindowLongPtr(_window, Native.GwlpWndProc, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&Subclass);
        return true;
    }

    public static void Show()
    {
        ShowRequested = true;
        if (_window == 0)
        {
            return;
        }

        _ = Native.ShowWindow(_window, Native.SwRestore);
        _ = Native.ShowWindow(_window, Native.SwShow);
        _ = Native.SetForegroundWindow(_window);
    }

    public static void Hide()
    {
        if (_window != 0)
        {
            _ = Native.ShowWindow(_window, Native.SwHide);
        }
    }

    /// <summary>Closes the window for real, which ends the CupriFace host loop.</summary>
    public static void Quit()
    {
        _allowClose = true;
        if (_window != 0)
        {
            _ = Native.PostMessage(_window, Native.WmClose, 0, 0);
        }
    }

    /// <summary>Lets the next foreground request from this process succeed, for example after a
    /// hotkey or tray click.</summary>
    public static void AllowForeground() => _ = Native.AllowSetForegroundWindow((uint)Environment.ProcessId);

    [UnmanagedCallersOnly]
    private static int FindWindow(nint window, nint parameter)
    {
        _ = Native.GetWindowThreadProcessId(window, out var processId);
        if (processId != Environment.ProcessId)
        {
            return 1;
        }

        var buffer = stackalloc char[256];
        var classLength = Native.GetClassName(window, buffer, 256);
        if (new string(buffer, 0, classLength).StartsWith("Kino", StringComparison.Ordinal))
        {
            return 1;
        }

        var titleLength = Native.GetWindowText(window, buffer, 256);
        if (new string(buffer, 0, titleLength) != _searchTitle)
        {
            return 1;
        }

        _found = window;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint Subclass(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == Native.WmClose && !_allowClose)
        {
            _ = Native.ShowWindow(window, Native.SwHide);
            return 0;
        }

        return Native.CallWindowProc(_previousProcedure, window, message, wParam, lParam);
    }
}

/// <summary>One interactive Kino per user session. A second launch signals the first to open its
/// window, then exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Kino.Instance";
    private const string ActivateName = @"Local\Kino.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Returns the lock, or null after signalling the instance that already holds it.</summary>
    public static SingleInstance? TryAcquire(Action onActivate)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        if (!owned)
        {
            activate.Set();
            activate.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

public static class ProcessDpi
{
    /// <summary>Makes pointer positions and capture coordinates physical pixels on every monitor. Must
    /// run before any window is created.</summary>
    public static bool EnablePerMonitorV2() => Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);
}
