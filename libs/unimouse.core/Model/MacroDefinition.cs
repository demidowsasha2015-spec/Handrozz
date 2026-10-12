using System.Text.Json.Serialization;

namespace UniMouse.Core.Model;

/// <summary>Modifier flags used by hotkeys. Matches Win32 MOD_* semantics.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global hotkey binding (RegisterHotKey-compatible).</summary>
public sealed record Hotkey(
    [property: JsonPropertyName("keyCode")] int KeyCode,
    [property: JsonPropertyName("modifiers")] HotkeyModifiers Modifiers = HotkeyModifiers.None);

/// <summary>
/// A named, replayable macro. Serialized as <c>.unimacro.json</c> files that are
/// shared 1:1 with the Python service schema (see shared/schemas/macro.schema.json).
/// </summary>
public sealed class MacroDefinition
{
    public const int SchemaVersionValue = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SchemaVersionValue;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>User-authored intent, e.g. "Fill daily report form". Sent to Qwen for refinement.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = "unknown";

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("steps")]
    public IReadOnlyList<MacroStep> Steps { get; init; } = [];

    /// <summary>0 = run once; N&gt;0 = repeat N times; -1 = loop until stopped.</summary>
    [JsonPropertyName("repeatCount")]
    public int RepeatCount { get; init; } = 1;

    /// <summary>Extra pause between full passes, ms. 0..60000.</summary>
    [JsonPropertyName("repeatDelayMs")]
    public int RepeatDelayMs { get; init; } = 500;

    /// <summary>
    /// Safety: maximum total wall-clock execution time. The runner aborts beyond it.
    /// Default 120 s, hard cap enforced by validator: 15 min.
    /// </summary>
    [JsonPropertyName("maxDurationMs")]
    public int MaxDurationMs { get; init; } = 120_000;

    /// <summary>Hotkey that starts playback (virtual-key code + modifiers bitmask).</summary>
    [JsonPropertyName("startHotkey")]
    public Hotkey? StartHotkey { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];
}
