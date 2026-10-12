using System.Runtime.InteropServices;
using UniMouse.Core.Model;

namespace UniMouse.Wpf.Services;

/// <summary>
/// Global "emergency stop" hotkey via the documented RegisterHotKey API.
/// This is a passive system-wide shortcut registration — NOT an input hook,
/// NOT a driver, NOT keylogging. It only fires when the user presses the
/// configured combo and its sole purpose is to STOP playback immediately.
/// </summary>
public sealed class EmergencyStopHotkey : IDisposable
{
    private const int HotkeyId = 0xB00B1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private bool _registered;

    /// <summary>WM_HOTKEY arrives here (set by App code from its message loop).</summary>
    public event Action? Triggered;

    public EmergencyStopHotkey(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>Default: Ctrl+Alt+F12. Callers may pass any non-forbidden combo.</summary>
    public bool Register(int keyCode = 0x7B, HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt)
    {
        if (modifiers == HotkeyModifiers.None)
            throw new ArgumentException("Emergency hotkey must have at least one modifier.", nameof(modifiers));

        _registered = RegisterHotKey(_hwnd, HotkeyId, (uint)modifiers, (uint)keyCode);
        return _registered;
    }

    /// <summary>Called by the WPF message hook when WM_HOTKEY with our id arrives.</summary>
    public void NotifyHotkey(int id)
    {
        if (id == HotkeyId)
            Triggered?.Invoke();
    }

    public void Dispose()
    {
        if (!_registered)
            return;
        _registered = false;   // set first: idempotent even if Closed fires twice
        UnregisterHotKey(_hwnd, HotkeyId);
    }
}
