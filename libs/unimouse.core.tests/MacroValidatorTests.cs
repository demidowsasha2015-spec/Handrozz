using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Core.Validation;
using Xunit;

namespace UniMouse.Core.Tests;

public class MacroValidatorTests
{
    private static MacroDefinition Valid(params MacroStep[] steps) => new()
    {
        Id = "test.macro",
        Name = "Test macro",
        Steps = steps,
    };

    [Fact]
    public void Accepts_Minimal_Valid_Macro()
    {
        var result = MacroValidator.Validate(Valid(new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 10 }));
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad id with spaces")]
    [InlineData("unicode_🚀")]
    public void Rejects_Bad_Ids(string id)
    {
        var m2 = new MacroDefinition { Id = id, Name = "n", Steps = [new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1 }] };
        var result = MacroValidator.Validate(m2);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "id.invalid");
    }

    [Fact]
    public void Rejects_Empty_Steps()
    {
        var result = MacroValidator.Validate(Valid());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "steps.empty");
    }

    [Fact]
    public void Rejects_Forbidden_Win_Key()
    {
        var macro = Valid(new MacroStep { Kind = StepKind.Key, KeyCode = 0x5B }); // VK_LWIN
        var result = MacroValidator.Validate(macro);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "step.key_forbidden");
    }

    [Fact]
    public void Rejects_Control_Characters_In_Text()
    {
        var macro = Valid(new MacroStep { Kind = StepKind.Text, Text = "hello\u0007world" }); // BEL
        var result = MacroValidator.Validate(macro);
        Assert.Contains(result.Errors, e => e.Code == "step.text_control_chars");
    }

    [Fact]
    public void Allows_Tab_And_Newline_In_Text()
    {
        var macro = Valid(new MacroStep { Kind = StepKind.Text, Text = "a\tb\nc" });
        Assert.True(MacroValidator.Validate(macro).IsValid);
    }

    [Fact]
    public void Rejects_Negative_Delay()
    {
        var macro = Valid(new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = -5 });
        Assert.Contains(MacroValidator.Validate(macro).Errors, e => e.Code == "step.delay");
    }

    [Fact]
    public void Rejects_Excessive_Duration()
    {
        var macro = new MacroDefinition
        {
            Id = "a.b", Name = "n", MaxDurationMs = MacroLimits.MaxDurationMs + 1,
            Steps = [new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1 }],
        };
        Assert.Contains(MacroValidator.Validate(macro).Errors, e => e.Code == "duration.invalid");
    }

    [Theory]
    [InlineData(-1)]  // infinite loop allowed
    [InlineData(0)]   // treated as single pass
    [InlineData(100)]
    public void Accepts_Repeat_Counts(int repeat)
    {
        var macro = new MacroDefinition
        {
            Id = "a.b", Name = "n", RepeatCount = repeat,
            Steps = [new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1 }],
        };
        Assert.True(MacroValidator.Validate(macro).IsValid);
    }

    [Fact]
    public void Rejects_Invalid_Repeat_Count()
    {
        var macro = new MacroDefinition
        {
            Id = "a.b", Name = "n", RepeatCount = -2,
            Steps = [new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1 }],
        };
        Assert.Contains(MacroValidator.Validate(macro).Errors, e => e.Code == "repeat.invalid");
    }

    [Fact]
    public void Rejects_Too_Many_Clicks()
    {
        var macro = Valid(new MacroStep { Kind = StepKind.MouseClick, Button = MouseButton.Left, Clicks = 99 });
        Assert.Contains(MacroValidator.Validate(macro).Errors, e => e.Code == "step.clicks_range");
    }

    [Fact]
    public void Reports_Step_Index()
    {
        var macro = Valid(
            new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1 },
            new MacroStep { Kind = StepKind.Key } // missing KeyCode
        );
        var err = Assert.Single(MacroValidator.Validate(macro).Errors);
        Assert.Equal(1, err.StepIndex);
    }
}
