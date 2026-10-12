namespace UniMouse.Core.Model;

/// <summary>High-level category of a macro step. Kept intentionally small and safe.</summary>
public enum StepKind
{
    /// <summary>Press/hold/release a keyboard key (SendInput, no hooks).</summary>
    Key,

    /// <summary>Type literal text via keyboard layout.</summary>
    Text,

    /// <summary>Move mouse cursor to an absolute screen coordinate.</summary>
    MouseMove,

    /// <summary>Click mouse buttons at current position.</summary>
    MouseClick,

    /// <summary>Scroll the mouse wheel.</summary>
    MouseScroll,

    /// <summary>Wait for a given duration.</summary>
    Delay,
}

/// <summary>Which physical mouse button.</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>Direction of a wheel scroll.</summary>
public enum ScrollDirection
{
    Up,
    Down,
}

/// <summary>
/// A single immutable macro step. Coordinates are absolute pixels in virtual-screen space.
/// All timing values are in milliseconds and must be non-negative.
/// </summary>
public sealed record MacroStep
{
    public required StepKind Kind { get; init; }

    /// <summary>Delay BEFORE executing this step, ms. 0..MaxDelayMs.</summary>
    public int DelayBeforeMs { get; init; }

    /// <summary>How long a key/click stays pressed, ms (Key/MouseClick only).</summary>
    public int HoldMs { get; init; }

    /// <summary>Virtual-key code (Key steps). Validated against the allowed range.</summary>
    public int? KeyCode { get; init; }

    /// <summary>Literal text (Text steps). Control characters are rejected by the validator.</summary>
    public string? Text { get; init; }

    /// <summary>Absolute X in virtual screen coordinates (mouse steps).</summary>
    public int? X { get; init; }

    /// <summary>Absolute Y in virtual screen coordinates (mouse steps).</summary>
    public int? Y { get; init; }

    public MouseButton? Button { get; init; }

    /// <summary>Number of clicks (MouseClick). 1..5.</summary>
    public int Clicks { get; init; } = 1;

    public ScrollDirection? Scroll { get; init; }

    /// <summary>Wheel notches (MouseScroll). -120..120.</summary>
    public int ScrollAmount { get; init; }
}
