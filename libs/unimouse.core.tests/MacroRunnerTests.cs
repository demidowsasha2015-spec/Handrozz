using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using Xunit;

namespace UniMouse.Core.Tests;

/// <summary>Records every executor call; optionally blocks to test cancellation.</summary>
public sealed class FakeInputExecutor : IInputExecutor
{
    public List<string> Calls { get; } = [];
    public TaskCompletionSource<bool>? Gate { get; set; } // if set, steps await it

    private async Task MaybeGateAsync(CancellationToken ct)
    {
        if (Gate is not null)
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task PressKeyAsync(int keyCode, CancellationToken ct)
    { await MaybeGateAsync(ct); Calls.Add($"press:{keyCode}"); }

    public async Task ReleaseKeyAsync(int keyCode, CancellationToken ct)
    { await MaybeGateAsync(ct); Calls.Add($"release:{keyCode}"); }

    public async Task TypeTextAsync(string text, CancellationToken ct)
    { await MaybeGateAsync(ct); Calls.Add($"type:{text}"); }

    public async Task MoveMouseAsync(int x, int y, CancellationToken ct)
    { await MaybeGateAsync(ct); Calls.Add($"move:{x},{y}"); }

    public async Task ClickMouseAsync(MouseButton button, int clicks, CancellationToken ct)
    { await MaybeGateAsync(ct); Calls.Add($"click:{button}x{clicks}"); }

    public async Task ScrollMouseAsync(ScrollDirection direction, int amount, CancellationToken ct)
    { await MaybeGateAsync(ct); Calls.Add($"scroll:{direction}:{amount}"); }

    public Task ReleaseAllAsync()
    { Calls.Add("release-all"); return Task.CompletedTask; }
}

public class MacroRunnerTests
{
    private static MacroDefinition Macro(params MacroStep[] steps) => new()
    {
        Id = "run.test",
        Name = "runner",
        MaxDurationMs = 5_000,
        Steps = steps,
    };

    [Fact]
    public async Task Runs_All_Step_Kinds_In_Order()
    {
        var fake = new FakeInputExecutor();
        var runner = new MacroRunner(fake);
        var macro = Macro(
            new MacroStep { Kind = StepKind.Key, KeyCode = 0x41 },                       // A
            new MacroStep { Kind = StepKind.Text, Text = "hi" },
            new MacroStep { Kind = StepKind.MouseMove, X = 10, Y = 20 },
            new MacroStep { Kind = StepKind.MouseClick, Button = MouseButton.Right, Clicks = 2 },
            new MacroStep { Kind = StepKind.MouseScroll, Scroll = ScrollDirection.Down, ScrollAmount = 3 },
            new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1 }
        );

        var state = await runner.RunAsync(macro);

        Assert.Equal(RunnerState.Completed, state);
        Assert.Equal(
        [
            "press:65", "release:65",
            "type:hi",
            "move:10,20",
            "click:Rightx2",
            "scroll:Down:3",
            "release-all",
        ], fake.Calls);
    }

    [Fact]
    public async Task Refuses_Invalid_Macro_And_Touches_No_Input()
    {
        var fake = new FakeInputExecutor();
        var runner = new MacroRunner(fake);
        var bad = new MacroDefinition { Id = "bad id!", Name = "x", Steps = [] };

        var state = await runner.RunAsync(bad);

        Assert.Equal(RunnerState.FailedValidation, state);
        // No step was ever executed; only the safety release-all ran.
        Assert.Equal(["release-all"], fake.Calls);
    }

    [Fact]
    public async Task Emergency_Stop_Cancels_And_Releases_Keys()
    {
        var fake = new FakeInputExecutor { Gate = new TaskCompletionSource<bool>() };
        var runner = new MacroRunner(fake);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(Macro(new MacroStep { Kind = StepKind.Key, KeyCode = 0x41 }), cts.Token);

        // let the first press start awaiting the gate, then cancel
        await Task.Delay(50);
        cts.Cancel();

        var state = await task;

        Assert.Equal(RunnerState.AbortedByUser, state);
        Assert.Contains("release-all", fake.Calls);
    }

    [Fact]
    public async Task Times_Out_When_Exceeding_MaxDuration()
    {
        var fake = new FakeInputExecutor { Gate = new TaskCompletionSource<bool>() };
        var runner = new MacroRunner(fake);
        var macro = new MacroDefinition
        {
            Id = "slow", Name = "slow", MaxDurationMs = 80,
            Steps = [new MacroStep { Kind = StepKind.Key, KeyCode = 0x41 }],
        };

        var state = await runner.RunAsync(macro);

        Assert.Equal(RunnerState.TimedOut, state);
        Assert.Contains("release-all", fake.Calls);
    }

    [Fact]
    public async Task Repeat_Count_Runs_Multiple_Passes()
    {
        var fake = new FakeInputExecutor();
        var runner = new MacroRunner(fake);
        var macro = new MacroDefinition
        {
            Id = "rep", Name = "rep", RepeatCount = 3, RepeatDelayMs = 1, MaxDurationMs = 5_000,
            Steps = [new MacroStep { Kind = StepKind.Text, Text = "x" }],
        };

        await runner.RunAsync(macro);

        Assert.Equal(3, fake.Calls.Count(c => c == "type:x"));
    }

    [Fact]
    public void TryStart_Prevents_Concurrent_Playback()
    {
        var runner = new MacroRunner(new FakeInputExecutor());
        Assert.True(runner.TryStart());
        Assert.False(runner.TryStart());
        runner.Stop();
        Assert.True(runner.TryStart());
    }

    [Fact]
    public async Task Reports_Progress_For_Each_Step()
    {
        var runner = new MacroRunner(new FakeInputExecutor());
        var seen = new List<RunnerProgress>();
        runner.Progress += p => seen.Add(p);

        await runner.RunAsync(Macro(
            new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 1 },
            new MacroStep { Kind = StepKind.Text, Text = "z" }));

        Assert.Contains(RunnerState.Running, s => seen.Any(p => p.State == s));
        Assert.Equal(RunnerState.Completed, seen[^1].State);
    }
}
