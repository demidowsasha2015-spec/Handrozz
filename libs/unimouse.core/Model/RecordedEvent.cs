namespace UniMouse.Core.Model;

/// <summary>Category of a raw captured input event (recorder pipeline).</summary>
public enum RecordedEventKind
{
    KeyDown,
    KeyUp,
    MouseDown,
    MouseUp,
    MouseMove,
    MouseWheel,
}

/// <summary>
/// A single raw input event produced by the recorder's passive capture layer
/// (WH_KEYBOARD_LL / WH_MOUSE_LL in the WPF app). The model itself is
/// platform-neutral so aggregation logic stays unit-testable everywhere.
/// </summary>
/// <param name="TimestampMs">Monotonic time since recording start, ms.</param>
/// <param name="X">Absolute virtual-screen X (mouse events).</param>
/// <param name="Y">Absolute virtual-screen Y (mouse events).</param>
/// <param name="Delta">Wheel delta in Win32 units (+120 = scroll up) for MouseWheel.</param>
public sealed record RecordedEvent(
    RecordedEventKind Kind,
    long TimestampMs,
    int KeyCode = 0,
    MouseButton Button = MouseButton.Left,
    int X = 0,
    int Y = 0,
    int Delta = 0);
