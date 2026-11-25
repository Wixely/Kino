using System.Collections.Concurrent;

namespace Kino.Platform.Windows;

/// <summary>
/// A dedicated UI thread with a hidden window. It receives global hotkeys and tray messages, owns the
/// clipboard while Kino writes to it, and hosts the selection preview and recording frame windows.
/// Everything that touches those windows runs here, through <see cref="Post"/> or <see cref="Invoke{T}"/>.
/// </summary>
public sealed class ShellThread : IDisposable
{
    private const uint InvokeMessage = Native.WmApp + 1;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<nuint, Action> _timers = [];
    private HiddenWindow? _window;
    private nuint _nextTimer = 100;

    public ShellThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Kino shell" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Raised on this thread with the id passed to <see cref="RegisterHotkey"/>.</summary>
    public event Action<int>? HotkeyPressed;

    internal event Func<uint, nuint, nint, nint?>? MessageReceived;

    public nint WindowHandle => _window?.Handle ?? 0;
    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public void Post(Action work)
    {
        _work.Enqueue(work);
        _ = Native.PostMessage(WindowHandle, InvokeMessage, 0, 0);
    }

    public T Invoke<T>(Func<T> work)
    {
        if (IsCurrent)
        {
            return work();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action work) => Invoke(() =>
    {
        work();
        return true;
    });

    /// <summary>Registers a global hotkey. Must run on this thread. Returns false when another
    /// application already owns the combination.</summary>
    public bool RegisterHotkey(int id, ShortcutBinding binding)
    {
        var modifiers = Native.ModNoRepeat;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Control)) modifiers |= Native.ModControl;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers |= Native.ModAlt;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers |= Native.ModShift;
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Windows)) modifiers |= Native.ModWin;
        return Native.RegisterHotKey(WindowHandle, id, modifiers, VirtualKey(binding.Key));
    }

    public void UnregisterHotkey(int id) => Native.UnregisterHotKey(WindowHandle, id);

    /// <summary>Starts a repeating timer on this thread and returns its id.</summary>
    public nuint StartTimer(uint milliseconds, Action tick)
    {
        var id = _nextTimer++;
        _timers[id] = tick;
        _ = Native.SetTimer(WindowHandle, id, milliseconds, 0);
        return id;
    }

    public void StopTimer(nuint id)
    {
        if (_timers.Remove(id))
        {
            _ = Native.KillTimer(WindowHandle, id);
        }
    }

    public void Dispose()
    {
        if (_window is null)
        {
            return;
        }

        Post(() =>
        {
            _window?.Dispose();
            _window = null;
            Native.PostQuitMessage(0);
        });
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    public static uint VirtualKey(ShortcutKey key) => key switch
    {
        >= ShortcutKey.Numpad0 and <= ShortcutKey.Numpad9 => 0x60u + (uint)(key - ShortcutKey.Numpad0),
        >= ShortcutKey.D0 and <= ShortcutKey.D9 => 0x30u + (uint)(key - ShortcutKey.D0),
        >= ShortcutKey.F1 and <= ShortcutKey.F12 => 0x70u + (uint)(key - ShortcutKey.F1),
        ShortcutKey.PrintScreen => 0x2C,
        ShortcutKey.Escape => 0x1B,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private void Run()
    {
        try
        {
            _window = new HiddenWindow(this);
        }
        catch (Exception exception)
        {
            _ready.SetException(exception);
            return;
        }

        _ready.SetResult();
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            _ = Native.TranslateMessage(message);
            _ = Native.DispatchMessage(message);
        }
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case InvokeMessage:
                while (_work.TryDequeue(out var work))
                {
                    try
                    {
                        work();
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Trace.WriteLine($"Kino shell work failed: {exception}");
                    }
                }

                return 0;
            case Native.WmHotkey:
                HotkeyPressed?.Invoke((int)wParam);
                return 0;
            case Native.WmTimer when _timers.TryGetValue(wParam, out var tick):
                tick();
                return 0;
        }

        if (MessageReceived is { } handlers)
        {
            foreach (Func<uint, nuint, nint, nint?> handler in handlers.GetInvocationList())
            {
                if (handler(message, wParam, lParam) is { } result)
                {
                    return result;
                }
            }
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private sealed class HiddenWindow(ShellThread owner) : NativeWindow("KinoShellWindow", Native.WsExToolWindow, Native.WsPopup)
    {
        protected override nint WndProc(uint message, nuint wParam, nint lParam) => owner.HandleMessage(Handle, message, wParam, lParam);
    }
}
