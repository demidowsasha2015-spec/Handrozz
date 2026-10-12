using System.Runtime.InteropServices;
using UniMouse.Core.Model;
using UniMouse.Core.Runtime;

namespace UniMouse.Input;

/// <summary>
/// Win32 SendInput-based executor. This is the ONLY component allowed to touch
/// user-input APIs. It uses documented, public OS automation primitives:
///   * SendInput (synthesizes input at the same level as the OS shell does)
///   * SetCursorPos / mouse_event semantics via SendInput
/// Explicitly out of scope by design and policy: kernel drivers, DLL injection,
/// reading other processes' memory, raw hooks for stealth, anti-cheat evasion.
/// </summary>
public sealed class Win32InputExecutor : IInputExecutor
{
    private readonly HashSet<int> _pressedKeys = [];
    private readonly object _keysLock = new();

    // ---- P/Invoke surface -------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short VkKeyScanW(char ch);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint MouseEventFMove = 0x0001;
    private const uint MouseEventFAbsolute = 0x0002;
    private const uint MouseEventFWheel = 0x0800;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;

    // Keys that need the extended-key flag (arrows, navigation, right Ctrl/Alt).
    private static readonly HashSet<int> ExtendedKeys =
    [
        0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x2C, // Insert..Arrows, PrintScreen
        0xA3, 0xA5, // RCONTROL, RMENU
        0x5B, 0x5C, // LWIN/RWIN (forbidden in macros but valid for completeness)
    ];

    // ---- IInputExecutor ---------------------------------------------------

    public Task PressKeyAsync(int keyCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SendKey(keyCode, up: false);
        lock (_keysLock) _pressedKeys.Add(keyCode);
        return Task.CompletedTask;
    }

    public Task ReleaseKeyAsync(int keyCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SendKey(keyCode, up: true);
        lock (_keysLock) _pressedKeys.Remove(keyCode);
        return Task.CompletedTask;
    }

    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n') { SendChar('\n', enter: true); continue; }
            if (ch == '\t') { SendVirtualKey(0x09); continue; }
            var scan = VkKeyScanW(ch);
            if (scan == -1) continue; // unrepresentable char in current layout — skip safely

            var vk = (byte)(scan & 0xFF);
            var shift = (scan & 0x0100) != 0;
            var altGr = (scan & 0x0200) != 0;

            if (altGr) { SendKey(0x12, false); SendKey(0x11, false); } // VK_MENU+VK_CONTROL = AltGr
            else if (shift) SendKey(0x10, false);                      // VK_SHIFT

            SendVirtualKey(vk);

            if (altGr) { SendKey(0x11, true); SendKey(0x12, true); }
            else if (shift) SendKey(0x10, true);
        }
        return Task.CompletedTask;
    }

    public Task MoveMouseAsync(int x, int y, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Absolute virtual-screen mapping so multi-monitor works correctly.
        var vsX = GetSystemMetrics(76);  // SM_XVIRTUALSCREEN
        var vsY = GetSystemMetrics(77);  // SM_YVIRTUALSCREEN
        var vsW = GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
        var vsH = GetSystemMetrics(79);  // SM_CYVIRTUALSCREEN

        var normX = (int)((x - vsX) * 65535.0 / Math.Max(vsW - 1, 1));
        var normY = (int)((y - vsY) * 65535.0 / Math.Max(vsH - 1, 1));

        Send(new INPUT
        {
            type = InputMouse,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    dwFlags = MouseEventFMove | MouseEventFAbsolute,
                }
            }
        });
        return Task.CompletedTask;
    }

    public Task ClickMouseAsync(MouseButton button, int clicks, CancellationToken ct)
    {
        var (down, up) = button switch
        {
            MouseButton.Left => (MouseEventLeftDown, MouseEventLeftUp),
            MouseButton.Right => (MouseEventRightDown, MouseEventRightUp),
            MouseButton.Middle => (MouseEventMiddleDown, MouseEventMiddleUp),
            _ => (MouseEventLeftDown, MouseEventLeftUp),
        };

        for (var i = 0; i < clicks && !ct.IsCancellationRequested; i++)
        {
            SendMouse(down, 0);
            Thread.Sleep(20); // natural click gap
            SendMouse(up, 0);
            if (i + 1 < clicks) Thread.Sleep(40);
        }
        return Task.CompletedTask;
    }

    public Task ScrollMouseAsync(ScrollDirection direction, int amount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var delta = (uint)(direction == ScrollDirection.Up ? amount : -amount);
        Send(new INPUT
        {
            type = InputMouse,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = delta,
                    dwFlags = MouseEventFWheel,
                }
            }
        });
        return Task.CompletedTask;
    }

    public Task ReleaseAllAsync()
    {
        int[] keys;
        lock (_keysLock) keys = [.. _pressedKeys];
        foreach (var k in keys)
        {
            try { SendKey(k, up: true); } catch { /* best effort */ }
        }
        lock (_keysLock) _pressedKeys.Clear();
        return Task.CompletedTask;
    }

    // ---- internals ----------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static void SendVirtualKey(int vk)
    {
        SendKey(vk, up: false);
        SendKey(vk, up: true);
    }

    private static void SendChar(char ch, bool enter = false)
    {
        if (enter)
        {
            SendKey(0x0D, up: false);
            SendKey(0x0D, up: true);
            return;
        }
        Send(new INPUT
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)ch, dwFlags = 0x0004 /* UNICODE */ }
            }
        });
    }

    private static void SendKey(int vk, bool up)
    {
        uint flags = 0;
        if (up) flags |= KeyEventFKeyUp;
        if (ExtendedKeys.Contains(vk)) flags |= KeyEventFExtendedKey;

        Send(new INPUT
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags }
            }
        });
    }

    private static void SendMouse(uint flags, uint data) =>
        Send(new INPUT
        {
            type = InputMouse,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } }
        });

    private static void Send(INPUT input)
    {
        var inputs = new[] { input };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != 1)
            throw new InvalidOperationException(
                $"SendInput failed ({Marshal.GetLastWin32Error()}). " +
                "Another application may be blocking synthetic input.");
    }
}
