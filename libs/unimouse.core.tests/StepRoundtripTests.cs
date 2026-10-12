using System.Text.Json;
using UniMouse.Core.Model;
using Xunit;

namespace UniMouse.Core.Tests;

/// <summary>
/// The WPF step editor round-trips steps through .unimacro.json via these very
/// STJ attributes. These tests pin that contract so the editor can never write
/// a file the Python service would reject.
/// </summary>
public class StepRoundtripTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = null,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Every_step_kind_survives_serialize_deserialize()
    {
        var macro = new MacroDefinition
        {
            Id = "test.roundtrip", Name = "roundtrip", Description = "all kinds",
            RepeatCount = 2, RepeatDelayMs = 300, MaxDurationMs = 60_000,
            StartHotkey = new Hotkey(0x41, HotkeyModifiers.Control | HotkeyModifiers.Alt),
            Tags = ["a", "b"],
            Steps =
            [
                new MacroStep { Kind = StepKind.Key, KeyCode = 0x0D, DelayBeforeMs = 50, HoldMs = 20 },
                new MacroStep { Kind = StepKind.Text, Text = "hello world", DelayBeforeMs = 10 },
                new MacroStep { Kind = StepKind.MouseMove, X = 800, Y = 600, DelayBeforeMs = 5 },
                new MacroStep { Kind = StepKind.MouseClick, Button = MouseButton.Right, Clicks = 2, HoldMs = 30 },
                new MacroStep { Kind = StepKind.MouseScroll, Scroll = ScrollDirection.Up, ScrollAmount = 240 },
                new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1000 },
            ],
        };

        var json = JsonSerializer.Serialize(macro, Opts);
        var back = JsonSerializer.Deserialize<MacroDefinition>(json, Opts)!;

        Assert.Equal(macro.Id, back.Id);
        Assert.Equal(macro.Steps.Count, back.Steps.Count);
        for (var i = 0; i < macro.Steps.Count; i++)
            Assert.Equal(macro.Steps[i], back.Steps[i]);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, back.StartHotkey!.Modifiers);
        Assert.Equal(0x41, back.StartHotkey.KeyCode);
    }

    [Fact]
    public void Kind_tokens_match_schema_camel_case()
    {
        var json = JsonSerializer.Serialize(
            new MacroStep { Kind = StepKind.MouseScroll, Scroll = ScrollDirection.Down, ScrollAmount = -120 }, Opts);
        Assert.Contains("\"mouseScroll\"", json);
        Assert.Contains("\"down\"", json);
    }
}
