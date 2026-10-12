using UniMouse.Core.Model;

namespace UniMouse.Core.Runtime;

/// <summary>
/// Pure aggregation logic that turns a chronological stream of raw
/// <see cref="RecordedEvent"/>s into safe, replayable <see cref="MacroStep"/>s.
/// No Win32 here — the WPF recorder feeds events captured by passive
/// low-level hooks into <see cref="Consume"/>, and <see cref="Build"/> on stop.
///
/// Safety posture:
///  * Forbidden key codes (Win/CapsLock per MacroLimits) are dropped at capture time.
///  * Every emitted step is range-checked against MacroLimits before Build returns.
///  * The caller must still run <see cref="Validation.MacroValidator"/> on the result.
/// </summary>
public sealed class EventAggregator
{
    private readonly RecorderOptions _options;
    private readonly List<MacroStep> _steps = [];

    // Coalescing state for mouse moves.
    private long _lastMoveTs = -1_000_000;
    private int _lastMoveX, _lastMoveY;
    private bool _hasPendingMove;

    // Pending press info for click/hold merging. Key: button.
    private readonly Dictionary<MouseButton, PressInfo> _pressed = new();

    // Held keys awaiting release (key-down/up pairing).
    private readonly Dictionary<int, long> _keysDown = new();

    private long _lastEventTs;
    /// <summary>Completed-but-deferred click per button (for multi-click merging).</summary>
    /// <summary>Deferred click: DownTs anchors timing/position of the LAST press,
    /// FirstDownTs anchors the double-click window start.</summary>
    private sealed record PendingClick(long DownTs, long FirstDownTs, int X, int Y, int Clicks, int HoldMs);
    private readonly Dictionary<MouseButton, PendingClick> _pendingClick = new();
    private bool _saturated;


    private sealed record PressInfo(long DownTs, int X, int Y);

    public EventAggregator(RecorderOptions? options = null)
    {
        _options = (options ?? new RecorderOptions()).Sanitized();
    }

    /// <summary>True once the MaxSteps budget is exhausted; further events are ignored.</summary>
    public bool IsSaturated => _saturated;

    /// <summary>Total steps produced so far.</summary>
    public int StepCount => _steps.Count;

    /// <summary>Feed one raw event (must arrive in non-decreasing timestamp order).</summary>
    public void Consume(RecordedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_saturated) return;
        if (evt.TimestampMs < _lastEventTs) return; // out-of-order: ignore, never rewind time
        _lastEventTs = evt.TimestampMs;

        switch (evt.Kind)
        {
            case RecordedEventKind.MouseMove: AddMouseMove(evt); break;
            case RecordedEventKind.MouseDown: OnButtonDown(evt); break;
            case RecordedEventKind.MouseUp: OnButtonUp(evt); break;
            case RecordedEventKind.MouseWheel: OnWheel(evt); break;
            case RecordedEventKind.KeyDown: OnKeyDown(evt); break;
            case RecordedEventKind.KeyUp: OnKeyUp(evt); break;
        }
    }

    /// <summary>
    /// Returns the final immutable step list: committed steps plus a snapshot-level
    /// flush of deferred clicks and the pending mouse move. Keys still held when
    /// recording stopped are emitted as zero-hold presses so playback ends with
    /// nothing depressed. The internal state is not mutated destructively for the
    /// move/click flush — except that flushing a deferred click removes it from
    /// the pending map (a second Build() call therefore stays consistent).
    /// </summary>
    public IReadOnlyList<MacroStep> Build()
    {
        var flushed = new List<MacroStep>(_steps);

        // Pending move is committed into the snapshot (source list untouched → idempotent).
        if (_hasPendingMove)
            flushed.Add(PendingMoveStep());

        // Deferred clicks never followed by a repeat → flush them now.
        foreach (var (button, p) in _pendingClick.ToList())
            FlushPendingClick(button, p);

        // Keys still held at stop-time → emit zero-hold key steps so playback
        // never leaves a key pressed.
        foreach (var (code, downTs) in _keysDown)
        {
            flushed.Add(ClampStep(new MacroStep
            {
                Kind = StepKind.Key,
                KeyCode = code,
                DelayBeforeMs = ClampDelay(downTs),
                HoldMs = 0,
            }));
        }

        // Buttons still pressed at stop-time → their MouseUp never arrived, so the
        // pending-click entry was never created. Nothing to flush: playback of the
        // already-emitted steps cannot leave any button down (each emitted click
        // is a complete press+release pair produced by FlushPendingClick).

        return flushed;
    }

    // ---------- mouse ----------

    private void AddMouseMove(RecordedEvent evt)
    {
        if (!_hasPendingMove)
        {
            var dx = Math.Abs(evt.X - _lastMoveX);
            var dy = Math.Abs(evt.Y - _lastMoveY);
            if (dx <= _options.MoveMinDistancePx && dy <= _options.MoveMinDistancePx)
                return; // jitter below threshold
        }

        if (_hasPendingMove && evt.TimestampMs - _lastMoveTs < _options.MoveMinIntervalMs)
        {
            // Coalesce: keep latest position, earlier timestamp.
            _lastMoveX = evt.X;
            _lastMoveY = evt.Y;
            return;
        }

        // Commit previous pending move if any, then start a new one.
        if (_hasPendingMove)
            CommitPendingMove();

        _hasPendingMove = true;
        _lastMoveTs = evt.TimestampMs;
        _lastMoveX = evt.X;
        _lastMoveY = evt.Y;
    }

    private void CommitPendingMove()
    {
        if (!_hasPendingMove) return;
        Append(ClampStep(new MacroStep
        {
            Kind = StepKind.MouseMove,
            X = _lastMoveX,
            Y = _lastMoveY,
            DelayBeforeMs = ClampDelay(_lastMoveTs),
        }));
        _hasPendingMove = false;
    }

    /// <summary>Snapshot-level flush of the pending move without touching state.</summary>
    private MacroStep PendingMoveStep() => ClampStep(new MacroStep
    {
        Kind = StepKind.MouseMove,
        X = _lastMoveX,
        Y = _lastMoveY,
        DelayBeforeMs = ClampDelay(_lastMoveTs),
    });

    private void OnButtonDown(RecordedEvent evt)
    {
        // Double/triple-click detection: a new press of the same button arriving
        // within the OS double-click window after the PREVIOUS press started
        // (GetDoubleClickTime semantics measure down-to-down). We defer emitting
        // the click step until its release; while deferred we bump Clicks in place.
        if (_pendingClick.TryGetValue(evt.Button, out var pending))
        {
            // A down-to-down repeat of the same button within the double-click
            // window merges into the deferred click step. The window is anchored
            // at the FIRST press of the burst (matching Win32 semantics closely
            // enough), so at most DoubleClickWindowMs worth of clicks merge —
            // later repeats flush as a fresh step instead.
            if (evt.TimestampMs - pending.FirstDownTs <= _options.DoubleClickWindowMs
                && pending.Clicks < MacroLimits.MaxClicks)
            {
                _pendingClick[evt.Button] = pending with { Clicks = pending.Clicks + 1 };
                return;
            }

            FlushPendingClick(evt.Button, pending); // window expired → separate click
        }

        CommitPendingMove(); // ordering: a move before this click stays before it
        _pressed[evt.Button] = new PressInfo(evt.TimestampMs, evt.X, evt.Y);
    }

    private void OnButtonUp(RecordedEvent evt)
    {
        if (!_pressed.Remove(evt.Button, out var info))
            return; // stray release without press — ignore

        var hold = evt.TimestampMs - info.DownTs;
        _pendingClick[evt.Button] = new PendingClick(
            info.DownTs, info.DownTs, info.X, info.Y, 1,
            hold <= _options.ClickMergeMaxHoldMs ? 0 : (int)Math.Min(hold, MacroLimits.MaxHoldMs));
    }

    private void FlushPendingClick(MouseButton button, PendingClick p)
    {
        _pendingClick.Remove(button);
        Append(ClampStep(new MacroStep
        {
            Kind = StepKind.MouseClick,
            Button = button,
            Clicks = p.Clicks,
            X = p.X,
            Y = p.Y,
            DelayBeforeMs = ClampDelay(p.DownTs),
            HoldMs = p.HoldMs,
        }));
    }

    private void OnWheel(RecordedEvent evt)
    {
        foreach (var (b, p) in _pendingClick.ToList())
            FlushPendingClick(b, p); // a wheel event ends any click burst's merge window
        CommitPendingMove();

        var notches = (int)Math.Round((double)evt.Delta / _options.WheelQuantum);
        if (notches == 0) return;

        // Convention across the codebase: ScrollAmount carries a SIGNED pixel
        // delta (runner takes Math.Abs and uses Scroll for direction; the step
        // editor round-trips sign the same way). Clamped to ±MaxScrollAmount.
        var magnitude = Math.Min(Math.Abs(notches) * _options.WheelQuantum, MacroLimits.MaxScrollAmount);
        if (magnitude == 0) return;
        var amount = notches > 0 ? magnitude : -magnitude;
        Append(ClampStepSigned(new MacroStep
        {
            Kind = StepKind.MouseScroll,
            Scroll = notches > 0 ? ScrollDirection.Up : ScrollDirection.Down,
            ScrollAmount = amount,
            X = evt.X,
            Y = evt.Y,
            DelayBeforeMs = ClampDelay(evt.TimestampMs),
        }));
    }

    // ---------- keyboard ----------

    private void OnKeyDown(RecordedEvent evt)
    {
        if (MacroLimits.ForbiddenKeyCodes.Contains(evt.KeyCode))
            return; // defense-in-depth even though capture layer filters first

        foreach (var (b, p) in _pendingClick.ToList())
            FlushPendingClick(b, p); // keyboard activity ends the mouse burst
        CommitPendingMove();

        // Auto-repeat: repeated downs without an up are ignored (one logical press).
        if (_keysDown.ContainsKey(evt.KeyCode)) return;
        _keysDown[evt.KeyCode] = evt.TimestampMs;
    }

    private void OnKeyUp(RecordedEvent evt)
    {
        if (!_keysDown.Remove(evt.KeyCode, out var downTs)) return;

        var hold = evt.TimestampMs - downTs;
        Append(ClampStep(new MacroStep
        {
            Kind = StepKind.Key,
            KeyCode = evt.KeyCode,
            DelayBeforeMs = ClampDelay(downTs),
            HoldMs = (int)Math.Min(hold, MacroLimits.MaxHoldMs),
        }));
    }

    // ---------- helpers ----------

    private void Append(MacroStep step)
    {
        if (_steps.Count >= _options.MaxSteps)
        {
            _saturated = true;
            return;
        }
        _steps.Add(step);
    }

    private static int ClampDelay(long ts) =>
        (int)Math.Clamp(ts, 0, MacroLimits.MaxDelayMs);

    private static MacroStep ClampStep(MacroStep s) => s with
    {
        DelayBeforeMs = Math.Clamp(s.DelayBeforeMs, 0, MacroLimits.MaxDelayMs),
        HoldMs = Math.Clamp(s.HoldMs, 0, MacroLimits.MaxHoldMs),
        Clicks = Math.Clamp(s.Clicks, MacroLimits.MinClicks, MacroLimits.MaxClicks),
        ScrollAmount = Math.Clamp(s.ScrollAmount, -MacroLimits.MaxScrollAmount, MacroLimits.MaxScrollAmount),
    };

    /// <summary>Alias kept for call-site clarity where the value is intentionally signed.</summary>
    private static MacroStep ClampStepSigned(MacroStep s) => ClampStep(s);
}
