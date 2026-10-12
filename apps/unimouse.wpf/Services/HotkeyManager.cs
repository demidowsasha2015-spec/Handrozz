using System.Runtime.InteropServices;
using UniMouse.Core.Model;

namespace UniMouse.Wpf.Services;

/// <summary>
/// Manages per-macro START hotkeys using the documented RegisterHotKey API.
/// Same safety posture as <see cref="EmergencyStopHotkey"/>: passive shortcut
/// registration only — no input hooks, no drivers, no keylogging. A hotkey
/// merely triggers playback of a macro the user explicitly saved and selected.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int FirstId = 0xC0FFEE;   // distinct from emergency-stop id

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _byId = [];
    private int _nextId = FirstId;

    public HotkeyManager(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Registers <paramref name="callback"/> for a combo. Returns the assigned
    /// id, or -1 when registration failed (combo already taken by another app).
    /// </summary>
    public int TryRegister(int keyCode, HotkeyModifiers modifiers, Action callback)
    {
        if (modifiers == HotkeyModifiers.None)
            throw new ArgumentException("Macro hotkey must have at least one modifier.", nameof(modifiers));
        if (_nextId == int.MaxValue)
            return -1;

        int id = _nextId++;
        bool ok = RegisterHotKey(_hwnd, id, (uint)modifiers, (uint)keyCode);
        if (!ok)
        {
            _nextId--;
            return -1;
        }

        _byId[id] = callback;
        return id;
    }

    public void Unregister(int id)
    {
        if (_byId.Remove(id))
            UnregisterHotKey(_hwnd, id);
    }

    /// <summary>Call from the WPF message hook on WM_HOTKEY.</summary>
    public void NotifyHotkey(int id)
    {
        if (_byId.TryGetValue(id, out var cb))
            cb();
    }

    public void Dispose()
    {
        // Snapshot + clear first so a second call is a no-op.
        var ids = _byId.Keys.ToArray();
        _byId.Clear();
        foreach (var id in ids)
            UnregisterHotKey(_hwnd, id);
    }
}
