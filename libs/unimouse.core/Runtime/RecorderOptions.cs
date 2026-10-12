using UniMouse.Core.Model;

namespace UniMouse.Core.Runtime;

/// <summary>
/// Tunables for turning raw <see cref="RecordedEvent"/>s into a
/// <see cref="MacroDefinition"/>. All limits are clamped against
/// <see cref="Validation.MacroLimits"/> so the recorder can never emit a
/// macro that the validator would reject on timing/size grounds.
/// </summary>
public sealed record RecorderOptions
{
    /// <summary>Hard cap on total recording length (ms). Stops capture beyond it.</summary>
    public int MaxRecordingMs { get; init; } = 5 * 60_000; // 5 minutes

    /// <summary>Consecutive mouse moves closer than this (time) collapse into one.</summary>
    public int MoveMinIntervalMs { get; init; } = 40;

    /// <summary>Moves smaller than this distance (px) are ignored entirely.</summary>
    public int MoveMinDistancePx { get; init; } = 3;

    /// <summary>Wheel deltas are quantized to multiples of WHEEL_DELTA (120).</summary>
    public int WheelQuantum { get; init; } = MacroLimits.ScrollWheelDelta;

    /// <summary>Holds longer than this stay "hold" clicks instead of plain quick clicks (ms).</summary>
    public int ClickMergeMaxHoldMs { get; init; } = 700;

    /// <summary>Down-to-down window for double/triple-click merging (Win default ~500 ms).</summary>
    public int DoubleClickWindowMs { get; init; } = 500;

    /// <summary>
    /// Recorder-specific budget: tighter than the validator's absolute cap so a
    /// recording always leaves headroom for the trailing Delay step the UI adds.
    /// Clamped to [1..MacroLimits.MaxSteps].
    /// </summary>
    public int MaxRecordedSteps { get; init; } = 1_500;

    /// <summary>Maximum recorded steps before capture stops (validator hard limit).</summary>
    public int MaxSteps => Math.Clamp(MaxRecordedSteps, 1, MacroLimits.MaxSteps);

    /// <summary>Clamp invalid/negative values to sane ranges.</summary>
    public RecorderOptions Sanitized() => this with
    {
        MaxRecordingMs = Math.Clamp(MaxRecordingMs, 1_000, MacroLimits.MaxDurationMs),
        MoveMinIntervalMs = Math.Clamp(MoveMinIntervalMs, 0, 1_000),
        MoveMinDistancePx = Math.Clamp(MoveMinDistancePx, 0, 100),
        WheelQuantum = Math.Clamp(WheelQuantum, 1, MacroLimits.MaxScrollAmount),
        ClickMergeMaxHoldMs = Math.Clamp(ClickMergeMaxHoldMs, 0, MacroLimits.MaxHoldMs),
        DoubleClickWindowMs = Math.Clamp(DoubleClickWindowMs, 100, 2_000),
        MaxRecordedSteps = Math.Clamp(MaxRecordedSteps, 1, MacroLimits.MaxSteps),
    };
}
