using System.Diagnostics;
using System.Runtime.InteropServices;
using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Core.Validation;

namespace UniMouse.Input;

/// <summary>
/// Passive recording capture built ONLY on documented low-level hooks
/// (WH_KEYBOARD_LL / WH_MOUSE_LL via SetWindowsHookEx).
///
/// Safety posture — read carefully:
///  * The hooks are installed only while the user explicitly holds a "record"
///    session in the UI, and are removed immediately on stop/dispose. There is
///    no always-on capture, so this component can never function as a keylogger.
///  * Hooks observe input and always call CallNextHookEx — input is never
///    blocked, modified, or synthesized here.
///  * Forbidden keys (Win/CapsLock per MacroLimits) are dropped from the
///    captured stream before it reaches the aggregator.
///  * No kernel drivers, no injection, no reading of other processes' memory.
///    This mirrors exactly what commercial macro recorders do with public APIs.
/// </summary>
public sealed class InputCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly RecorderOptions _options;
    private readonly EventAggregator _aggregator;
    private readonly Stopwatch _clock = new();
    private readonly HookProc _keyboardProc; // rooted fields: prevent GC of delegates
    private readonly HookProc _mouseProc;
    private long _lastTickMs;
    private IntPtr _kbdHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <summary>Raised from the hook thread whenever a new step was committed (for live counters).</summary>
    public event Action<int>? StepsChanged;

    public InputCapture(RecorderOptions? options = null)
    {
        _options = (options ?? new RecorderOptions()).Sanitized();
        _aggregator = new EventAggregator(_options);
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public bool IsRecording => _kbdHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;

    /// <summary>
    /// Installs both hooks. LL hooks need a message loop pumping on the calling
    /// thread — call this from the WPF UI thread. Returns false (and cleans up)
    /// if either hook fails to install.
    /// </summary>
    public void Start()
    {
        if (IsRecording) throw new InvalidOperationException("Already recording.");

        var module = GetModuleHandle(null);
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_kbdHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("Failed to install input hooks (a secure desktop may be active).");
        }

        _clock.Restart();
        _lastTickMs = 0;
    }

    /// <summary>Removes hooks and returns the aggregated steps. Safe to call multiple times.</summary>
    public IReadOnlyList<MacroStep> Stop()
    {
        RemoveHooks();
        _clock.Stop();
        return _aggregator.Build();
    }

    public void Dispose() => RemoveHooks();

    private void RemoveHooks()
    {
        if (_kbdHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    // ---------- callbacks ----------

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var kind = msg switch
            {
                WM_KEYDOWN or WM_SYSKEYDOWN => RecordedEventKind.KeyDown,
                WM_KEYUP or WM_SYSKEYUP => RecordedEventKind.KeyUp,
                _ => (RecordedEventKind?)null,
            };

            if (kind is not null && !MacroLimits.ForbiddenKeyCodes.Contains((int)data.vkCode))
            {
                // Injected events (LLKHF_INJECTED) are skipped: replaying our own
                // SendInput must never feed back into a live recording.
                if ((data.flags & 0x10) == 0)
                    Enqueue(new RecordedEvent(kind.Value, Now(), KeyCode: (int)data.vkCode));
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;

            RecordedEvent? evt = msg switch
            {
                WM_MOUSEMOVE => new RecordedEvent(RecordedEventKind.MouseMove, Now(), X: data.x, Y: data.y),
                WM_LBUTTONDOWN => ButtonEvt(RecordedEventKind.MouseDown, data, MouseButton.Left),
                WM_LBUTTONUP => ButtonEvt(RecordedEventKind.MouseUp, data, MouseButton.Left),
                WM_RBUTTONDOWN => ButtonEvt(RecordedEventKind.MouseDown, data, MouseButton.Right),
                WM_RBUTTONUP => ButtonEvt(RecordedEventKind.MouseUp, data, MouseButton.Right),
                WM_MBUTTONDOWN => ButtonEvt(RecordedEventKind.MouseDown, data, MouseButton.Middle),
                WM_MBUTTONUP => ButtonEvt(RecordedEventKind.MouseUp, data, MouseButton.Middle),
                WM_MOUSEWHEEL => new RecordedEvent(RecordedEventKind.MouseWheel, Now(),
                    X: data.x, Y: data.y, Delta: unchecked((short)((data.mouseData >> 16) & 0xFFFF))),
                _ => null,
            };

            if (evt is not null) Enqueue(evt.Value);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private RecordedEvent ButtonEvt(RecordedEventKind kind, MSLLHOOKSTRUCT d, MouseButton b) =>
        new(kind, Now(), Button: b, X: d.x, Y: d.y);

    // ---------- timing ----------

    private long Now()
    {
        // Monotonic, non-decreasing: two events within the same ms share a tick.
        var t = _clock.ElapsedMilliseconds;
        if (t < _lastTickMs) t = _lastTickMs;
        else _lastTickMs = t;
        return t;
    }

    private void Enqueue(RecordedEvent evt)
    {
        // Hard duration cap: hooks come off the moment the budget is exceeded.
        if (_clock.ElapsedMilliseconds > _options.MaxRecordingMs)
        {
            RemoveHooks();
            return;
        }

        var before = _aggregator.StepCount;
        _aggregator.Consume(evt);
        if (_aggregator.StepCount != before)
            StepsChanged?.Invoke(_aggregator.StepCount);
        if (_aggregator.IsSaturated)
            RemoveHooks(); // step budget exhausted → stop capturing immediately
    }
}
