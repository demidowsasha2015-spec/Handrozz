using UniMouse.Core.Model;
using UniMouse.Core.Validation;
using UniMouse.Wpf.ViewModels;
using Xunit;

namespace UniMouse.Wpf.Tests;

/// <summary>
/// StepEditorViewModel is a plain ObservableObject (no Dispatcher dependency),
/// so it can be unit-tested headlessly. These tests lock in the reorder
/// semantics used by both the Move Up/Down buttons and drag &amp; drop.
/// </summary>
public class StepEditorViewModelTests
{
    // Delays are distinguished by both magnitude and preceding delay so the
    // step validator can tell rows apart (identical consecutive delays would
    // be flagged as a degenerate macro).
    private static MacroStep Delay(int index, int ms) =>
        new() { Kind = StepKind.Delay, DelayBeforeMs = ms + index };

    private static StepEditorViewModel VmWithDelays(params int[] baseDelaysMs) =>
        new(
            new MacroDefinition
            {
                Id = "editor.test",
                Name = "Editor test",
                Steps = baseDelaysMs
                    .Select((ms, i) => Delay(i, ms))
                    .ToArray(),
            },
            "editor-test.unimacro.json");

    [Fact]
    public void MoveTo_Reorders_Forward_And_Does_Not_Touch_Other_Rows()
    {
        var vm = VmWithDelays(10, 20, 30);
        vm.MoveTo(0, 2);
        Assert.Collection(vm.Rows,
            r => Assert.Equal("20", r.DelayBeforeText),
            r => Assert.Equal("30", r.DelayBeforeText),
            r => Assert.Equal("10", r.DelayBeforeText));
    }

    [Fact]
    public void MoveTo_Clamps_Target_Index()
    {
        var vm = VmWithDelays(10, 20, 30);
        vm.MoveTo(0, 99);   // clamp to last
        vm.MoveTo(2, -5);   // clamp to first
        Assert.Equal("30", vm.Rows[1].DelayBeforeText);
        Assert.Equal("20", vm.Rows[2].DelayBeforeText);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(3, 1)]
    [InlineData(0, 0)]     // same position: no-op
    public void MoveTo_Ignores_Invalid_Or_Noop_Indices(int from, int to)
    {
        var vm = VmWithDelays(10, 20, 30);
        vm.MoveTo(from, to);
        Assert.Equal(new[] { "10", "20", "30" }, vm.Rows.Select(r => r.DelayBeforeText));
        Assert.False(vm.IsDirty); // no-op must not mark unsaved changes
    }

    [Fact]
    public void MoveTo_Sets_Dirty_And_Reindexes()
    {
        var vm = VmWithDelays(10, 20);
        vm.MoveTo(1, 0);
        Assert.True(vm.IsDirty);
        Assert.Equal(0, vm.Rows[0].Index);
        Assert.Equal(1, vm.Rows[1].Index);
    }

    [Fact]
    public void MoveTo_IsBlocked_In_ReadMode()
    {
        var vm = VmWithDelays(10, 20);
        vm.IsReadOnly = true;
        vm.MoveTo(0, 1);
        Assert.Equal("10", vm.Rows[0].DelayBeforeText);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void BuildUpdated_Preserves_Step_Order_After_MoveTo()
    {
        var vm = VmWithDelays(10, 20, 30);
        vm.MoveTo(2, 0);
        var updated = vm.BuildUpdated(out var validation);
        Assert.True(validation.IsValid);
        Assert.Equal([32, 10, 21], updated.Steps.Select(s => s.DelayBeforeMs));
    }

    [Fact]
    public void TrySave_Persists_Reordered_Macro_And_Clears_Dirty()
    {
        var vm = VmWithDelays(10, 20, 30);
        vm.MoveTo(0, 2);

        MacroDefinition? saved = null;
        var ok = vm.TrySave((macro, _, report) => { saved = macro; report("x.unimacro.json"); return true; });

        Assert.True(ok);
        Assert.NotNull(saved);
        Assert.Equal([21, 32, 10], saved!.Steps.Select(s => s.DelayBeforeMs));
        Assert.False(vm.IsDirty);
        Assert.NotNull(vm.SaveResult);
    }

    [Fact]
    public void TrySave_Reports_Actual_File_Name_For_Drafts()
    {
        // Draft flow: the store derives its own file name (Vm passes a placeholder).
        // SavedOnce must carry the REAL name so the library can select the new row.
        var vm = VmWithDelays(10);
        string? reported = null;
        vm.SavedOnce += name => reported = name;

        var ok = vm.TrySave((_, _, report) => { report("recorded-20261012-153000.unimacro.json"); return true; });

        Assert.True(ok);
        Assert.Equal("recorded-20261012-153000.unimacro.json", reported);
    }

    [Fact]
    public void TrySave_Fails_When_SaveAction_Refuses()
    {
        var vm = VmWithDelays(10);
        var ok = vm.TrySave((_, _, _) => false);
        Assert.False(ok);
        Assert.Contains("refused", vm.SaveResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scroll_Rows_Map_Between_Notches_And_WheelDelta_Without_Validator_Conflict()
    {
        // Regression guard for the "UI allows 10 notches but validator rejects"
        // drift: a maximal (5-notch) scroll row must survive ToStep + validation.
        var vm = VmWithDelays(10);
        vm.AddStep(StepKind.MouseScroll);
        var row = vm.Rows[^1];
        row.ScrollDirection = "up";
        row.ScrollAmount = MacroLimits.MaxScrollNotches;

        var step = row.ToStep(); // throws if the probe validation fails
        Assert.Equal(MacroLimits.MaxScrollNotches * MacroLimits.ScrollWheelDelta, step.ScrollAmount);
        Assert.Null(row.Error);

        // And back from wheel-pixels into notch units.
        var round = StepRow.From(step);
        Assert.Equal(MacroLimits.MaxScrollNotches, Math.Abs(round.ScrollAmount));
        Assert.Equal("up", round.ScrollDirection);
    }
}
