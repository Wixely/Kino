namespace Kino;

/// <summary>A key a shortcut can press, in a platform-neutral form. The Windows host maps these to
/// virtual-key codes.</summary>
public enum ShortcutKey
{
    Numpad0,
    Numpad1,
    Numpad2,
    Numpad3,
    Numpad4,
    Numpad5,
    Numpad6,
    Numpad7,
    Numpad8,
    Numpad9,
    D0,
    D1,
    D2,
    D3,
    D4,
    D5,
    D6,
    D7,
    D8,
    D9,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    PrintScreen,
    Escape,
}

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>The keys Kino listens for. Each key has one action; keys that do not apply to the
/// current workflow state are ignored. See <see cref="CaptureWorkflow"/>.</summary>
public enum ShortcutSlot
{
    /// <summary>Marks the first corner of a screenshot.</summary>
    ImageCorner,

    /// <summary>Marks the second corner of a screenshot, captures it and copies the image.</summary>
    ImageCapture,

    /// <summary>Saves the last screenshot to a file and copies its path.</summary>
    SaveImage,

    /// <summary>Marks the first corner of a video.</summary>
    VideoCorner,

    /// <summary>Marks the second corner of a video and starts recording.</summary>
    VideoStart,

    /// <summary>Stops recording and opens the finishing prompt.</summary>
    StopRecording,
}

public sealed record ShortcutBinding(ShortcutModifiers Modifiers, ShortcutKey Key)
{
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        parts.Add(Key switch
        {
            >= ShortcutKey.Numpad0 and <= ShortcutKey.Numpad9 => $"Num{Key - ShortcutKey.Numpad0}",
            >= ShortcutKey.D0 and <= ShortcutKey.D9 => $"{Key - ShortcutKey.D0}",
            ShortcutKey.PrintScreen => "PrtSc",
            ShortcutKey.Escape => "Esc",
            _ => Key.ToString(),
        });
        return string.Join('+', parts);
    }
}

public static class DefaultShortcuts
{
    public static IReadOnlyDictionary<ShortcutSlot, ShortcutBinding> Create() => new Dictionary<ShortcutSlot, ShortcutBinding>
    {
        [ShortcutSlot.ImageCorner] = new(ShortcutModifiers.Control, ShortcutKey.Numpad1),
        [ShortcutSlot.ImageCapture] = new(ShortcutModifiers.Control, ShortcutKey.Numpad2),
        [ShortcutSlot.SaveImage] = new(ShortcutModifiers.Control, ShortcutKey.Numpad3),
        [ShortcutSlot.VideoCorner] = new(ShortcutModifiers.Control, ShortcutKey.Numpad4),
        [ShortcutSlot.VideoStart] = new(ShortcutModifiers.Control, ShortcutKey.Numpad5),
        [ShortcutSlot.StopRecording] = new(ShortcutModifiers.Control, ShortcutKey.Numpad6),
    };

    /// <summary>The alternative for keyboards without a numeric keypad: Ctrl+Alt with the top-row
    /// digits.</summary>
    public static IReadOnlyDictionary<ShortcutSlot, ShortcutBinding> CreateCompact() => new Dictionary<ShortcutSlot, ShortcutBinding>
    {
        [ShortcutSlot.ImageCorner] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutKey.D1),
        [ShortcutSlot.ImageCapture] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutKey.D2),
        [ShortcutSlot.SaveImage] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutKey.D3),
        [ShortcutSlot.VideoCorner] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutKey.D4),
        [ShortcutSlot.VideoStart] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutKey.D5),
        [ShortcutSlot.StopRecording] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutKey.D6),
    };

    /// <summary>Slots that share a binding. A shared binding would make one key perform two
    /// unrelated actions, so the host refuses to register it.</summary>
    public static IReadOnlyList<(ShortcutSlot First, ShortcutSlot Second)> FindConflicts(
        IReadOnlyDictionary<ShortcutSlot, ShortcutBinding> bindings)
    {
        var conflicts = new List<(ShortcutSlot, ShortcutSlot)>();
        var entries = bindings.OrderBy(pair => pair.Key).ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            for (var j = i + 1; j < entries.Length; j++)
            {
                if (entries[i].Value == entries[j].Value)
                {
                    conflicts.Add((entries[i].Key, entries[j].Key));
                }
            }
        }

        return conflicts;
    }
}
