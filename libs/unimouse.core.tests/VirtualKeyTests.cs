using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Core.Validation;
using Xunit;

namespace UniMouse.Core.Tests;

public class VirtualKeyTests
{
    [Theory]
    [InlineData("Enter", 0x0D)]
    [InlineData("enter", 0x0D)]
    [InlineData("  ENTER ", 0x0D)]
    [InlineData("page_up", 0x22)]
    [InlineData("Page-Up", 0x22)]
    [InlineData("F12", 0x7B)]
    [InlineData("a", 0x41)]
    [InlineData("Z", 0x5A)]
    [InlineData("5", 0x35)]
    [InlineData("0x1A", 0x1A)]
    [InlineData("Oem1", 0xBA)]
    [InlineData("oem1 (;:)", 0xBA)]
    public void Parses_known_names(string text, int expected)
        => Assert.True(VirtualKey.TryParse(text, out var vk) && vk == expected,
                       $"'{text}' should parse to 0x{expected:X2}, got 0x{vk:X2}");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-key")]
    [InlineData("+")]              // no bare punctuation names
    [InlineData("Win")]            // deliberately absent from the safe map
    [InlineData("LWin")]
    [InlineData("CapsLock")]       // forbidden by validator -> not offered here
    public void Rejects_unknown_or_unsafe(string text)
        => Assert.False(VirtualKey.TryParse(text, out _), $"'{text}' must not parse");

    [Fact]
    public void Name_roundtrips_through_parse()
    {
        foreach (var (code, name) in VirtualKey.AllKnown)
        {
            Assert.Equal(name, VirtualKey.Name(code));
            Assert.True(VirtualKey.TryParse(name, out var parsed));
            Assert.Equal(code, parsed);
        }
    }

    [Fact]
    public void Unknown_code_gets_hex_fallback_name()
        => Assert.Equal("VK 0xEE", VirtualKey.Name(0xEE));

    [Fact]
    public void AllKnown_excludes_forbidden_keys()
    {
        Assert.DoesNotContain(VirtualKey.AllKnown, kv => kv.Key is 0x5B or 0x5C or 0x14);
    }

    [Fact]
    public void Parsed_key_flows_into_valid_macro()
    {
        Assert.True(VirtualKey.TryParse("Ctrl", out var ctrl));
        var macro = new MacroDefinition
        {
            Id = "test.vk", Name = "vk", Steps =
            [
                new MacroStep { Kind = StepKind.Key, KeyCode = ctrl, DelayBeforeMs = 10 },
            ],
        };
        Assert.True(MacroValidator.Validate(macro).IsValid);
    }
}
