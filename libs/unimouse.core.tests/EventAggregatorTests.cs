using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Core.Validation;
using Xunit;

namespace UniMouse.Core.Tests;

/// <summary>
/// Headless tests for the recorder's pure aggregation layer. The Win32 capture
/// (WH_*_LL hooks) lives in UniMouse.Wpf and is exercised manually; everything
/// that decides *what steps a recording becomes* is here and platform-neutral.
/// </summary>
public class EventAggregatorTests
{
    private static RecordedEvent KeyDown(long t, int vk) =>
        new(RecordedEventKind.KeyDown, t, KeyCode: vk);

    private static RecordedEvent KeyUp(long t, int vk) =>
        new(RecordedEventKind.KeyUp, t, KeyCode: vk);

    private static RecordedEvent Move(long t, int x, int y) =>
        new(RecordedEventKind.MouseMove, t, X: x, Y: y);

    private static RecordedEvent Down(long t, MouseButton b = MouseButton.Left, int x = 10, int y = 10) =>
        new(RecordedEventKind.MouseDown, t, Button: b, X: x, Y: y);

    private static RecordedEvent Up(long t, MouseButton b = MouseButton.Left, int x = 10, int y = 10) =>
        new(RecordedEventKind.MouseUp, t, Button: b, X: x, Y: y);

    private static RecordedEvent Wheel(long t, int delta, int x = 5, int y = 5) =>
        new(RecordedEventKind.MouseWheel, t, X: x, Y: y, Delta: delta);

    // ---------- keyboard ----------

    [Fact]
    public void KeyPressRelease_ProducesSingleKeyStepWithHold()
    {
        var agg = new EventAggregator();
        agg.Consume(KeyDown(100, 0x41)); // 'A'
        agg.Consume(KeyUp(150, 0x41));

        var steps = agg.Build();
        var step = Assert.Single(steps);
        Assert.Equal(StepKind.Key, step.Kind);
        Assert.Equal(0x41, step.KeyCode);
        Assert.Equal(100, step.DelayBeforeMs);
        Assert.Equal(50, step.HoldMs);
    }

    [Fact]
    public void AutoRepeatDowns_CollapseToOnePress()
    {
        var agg = new EventAggregator();
        agg.Consume(KeyDown(0, 0x41));
        agg.Consume(KeyDown(30, 0x41));   // OS auto-repeat
        agg.Consume(KeyDown(60, 0x41));   // OS auto-repeat
        agg.Consume(KeyUp(200, 0x41));

        var step = Assert.Single(agg.Build());
        Assert.Equal(200, step.HoldMs);
    }

    [Fact]
    public void ForbiddenKeys_AreDropped()
    {
        var agg = new EventAggregator();
        agg.Consume(KeyDown(0, 0x5B));    // LWin — forbidden
        agg.Consume(KeyUp(10, 0x5B));
        agg.Consume(KeyDown(0, 0x14));    // CapsLock — forbidden
        agg.Consume(KeyUp(10, 0x14));

        Assert.Empty(agg.Build());
    }

    [Fact]
    public void StrayKeyUp_Ignored()
    {
        var agg = new EventAggregator();
        agg.Consume(KeyUp(50, 0x41)); // release without press (started mid-hold)
        Assert.Empty(agg.Build());
    }

    [Fact]
    public void HeldKeyAtStop_EmitsZeroHoldReleaseStep()
    {
        var agg = new EventAggregator();
        agg.Consume(KeyDown(100, 0x10)); // Shift still down when recording stopped
        agg.Consume(KeyDown(200, 0x41));
        agg.Consume(KeyUp(250, 0x41));

        var steps = agg.Build();
        Assert.Equal(2, steps.Count);
        Assert.Equal(StepKind.Key, steps[1].Kind);
        Assert.Equal(0x10, steps[1].KeyCode);
        Assert.Equal(0, steps[1].HoldMs); // no phantom long hold
    }

    // ---------- mouse moves ----------

    [Fact]
    public void TinyMoves_BelowDistanceThresholdIgnored()
    {
        var agg = new EventAggregator(new RecorderOptions { MoveMinDistancePx = 3 });
        agg.Consume(Move(0, 100, 100));   // first move from (0,0) origin → big, kept
        agg.Consume(Move(50, 101, 101));  // jitter ≤3px with no pending gap → ignored
        agg.Consume(Move(100, 102, 102));

        var steps = agg.Build();
        Assert.Single(steps);
        Assert.Equal(StepKind.MouseMove, steps[0].Kind);
        Assert.Equal(100, steps[0].X);
    }

    [Fact]
    public void RapidMoves_CoalesceToLatestPositionEarliestTimestamp()
    {
        var agg = new EventAggregator(new RecorderOptions
        {
            MoveMinIntervalMs = 40,
            MoveMinDistancePx = 0,
        });
        agg.Consume(Move(0, 10, 10));
        agg.Consume(Move(10, 50, 50));   // within 40ms → coalesced into pending
        agg.Consume(Move(20, 90, 90));   // still pending window (gap from ts10 = 10ms)

        var steps = agg.Build();
        var move = Assert.Single(steps);
        Assert.Equal(90, move.X);        // latest position
        Assert.Equal(10, move.DelayBeforeMs); // earliest timestamp of the burst
    }

    [Fact]
    public void SpacedMoves_EmitSeparateSteps()
    {
        var agg = new EventAggregator(new RecorderOptions { MoveMinIntervalMs = 40, MoveMinDistancePx = 0 });
        agg.Consume(Move(0, 10, 10));
        agg.Consume(Move(100, 200, 200));
        agg.Consume(Move(250, 300, 300));

        var steps = agg.Build();
        Assert.Equal(3, steps.Count);
        Assert.All(steps, s => Assert.Equal(StepKind.MouseMove, s.Kind));
    }

    // ---------- clicks ----------

    [Fact]
    public void QuickClick_HasZeroHold()
    {
        var agg = new EventAggregator();
        agg.Consume(Down(50));
        agg.Consume(Up(120)); // 70ms hold < ClickMergeMaxHoldMs

        var click = Assert.Single(agg.Build());
        Assert.Equal(StepKind.MouseClick, click.Kind);
        Assert.Equal(1, click.Clicks);
        Assert.Equal(0, click.HoldMs);
        Assert.Equal(50, click.DelayBeforeMs);
    }

    [Fact]
    public void LongPress_KeepsHoldDuration()
    {
        var agg = new EventAggregator(new RecorderOptions { ClickMergeMaxHoldMs = 700 });
        agg.Consume(Down(0, MouseButton.Right));
        agg.Consume(Up(1500, MouseButton.Right));

        var click = Assert.Single(agg.Build());
        Assert.Equal(MouseButton.Right, click.Button);
        Assert.Equal(1500, click.HoldMs);
    }

    [Fact]
    public void DoubleClickWithinWindow_MergesIntoTwoClicks()
    {
        var agg = new EventAggregator();
        agg.Consume(Down(0));
        agg.Consume(Up(50));
        agg.Consume(Down(200)); // down-to-down gap 200ms ≤ 500ms window
        agg.Consume(Up(260));

        var click = Assert.Single(agg.Build());
        Assert.Equal(2, click.Clicks);
    }

    [Fact]
    public void TripleClick_ChainsWithinWindow()
    {
        // Window is anchored at the FIRST press; all downs must land within 500 ms of it.
        var agg = new EventAggregator();
        agg.Consume(Down(0)); agg.Consume(Up(40));
        agg.Consume(Down(200)); agg.Consume(Up(240));
        agg.Consume(Down(400)); agg.Consume(Up(440));

        var click = Assert.Single(agg.Build());
        Assert.Equal(3, click.Clicks);
    }

    [Fact]
    public void ClicksOutsideWindow_StaySeparate()
    {
        var agg = new EventAggregator();
        agg.Consume(Down(0)); agg.Consume(Up(40));
        agg.Consume(Down(1000)); agg.Consume(Up(1040)); // 1000ms > 500ms window

        var steps = agg.Build();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal(1, s.Clicks));
    }

    [Fact]
    public void RapidClicks_CapAtMaxFivePerStep()
    {
        var agg = new EventAggregator();
        for (var i = 0; i < 8; i++)
        {
            agg.Consume(Down(i * 100));
            agg.Consume(Up(i * 100 + 30));
        }

        var steps = agg.Build();
        // First 6 downs fall inside the 500ms window anchored at t=0 → capped at 5 clicks;
        // the remaining rapid clicks flush as a second step (also capped). Never > MaxClicks.
        Assert.True(steps.Count >= 2);
        Assert.All(steps, st => Assert.Equal(MacroLimits.MaxClicks, st.Clicks));
    }

    [Fact]
    public void StrayMouseUp_Ignored()
    {
        var agg = new EventAggregator();
        agg.Consume(Up(100)); // recording started while button already held
        Assert.Empty(agg.Build());
    }

    // ---------- wheel ----------

    [Fact]
    public void WheelQuantizesToNotches()
    {
        var agg = new EventAggregator();
        agg.Consume(Wheel(0, -240)); // two notches down
        agg.Consume(Wheel(100, 120)); // one notch up

        var steps = agg.Build();
        Assert.Equal(2, steps.Count);
        Assert.Equal(ScrollDirection.Down, steps[0].Scroll);
        Assert.Equal(-240, steps[0].ScrollAmount); // signed convention: direction also in sign
        Assert.Equal(ScrollDirection.Up, steps[1].Scroll);
        Assert.Equal(120, steps[1].ScrollAmount);
    }

    [Fact]
    public void SubNotchDelta_RoundedAwayAndIgnored()
    {
        var agg = new EventAggregator();
        agg.Consume(Wheel(0, 40)); // rounds to 0 notches → dropped
        Assert.Empty(agg.Build());
    }

    [Fact]
    public void HugeWheel_ClampedToValidatorLimit()
    {
        var agg = new EventAggregator();
        agg.Consume(Wheel(0, 100_000));

        var step = Assert.Single(agg.Build());
        Assert.Equal(MacroLimits.MaxScrollAmount, step.ScrollAmount); // 600 px cap
    }

    // ---------- stream hygiene ----------

    [Fact]
    public void OutOfOrderEvents_Ignored()
    {
        var agg = new EventAggregator();
        agg.Consume(KeyDown(100, 0x41));
        agg.Consume(KeyUp(50, 0x41));  // rewound clock → dropped
        agg.Consume(KeyUp(200, 0x41));

        var step = Assert.Single(agg.Build());
        Assert.Equal(100, step.HoldMs);
    }

    [Fact]
    public void Saturation_StopsAcceptingMoreSteps()
    {
        var agg = new EventAggregator(new RecorderOptions { MaxRecordedSteps = 3 });
        for (var i = 0; i < 10; i++)
        {
            agg.Consume(KeyDown(i * 100, 0x41));
            agg.Consume(KeyUp(i * 100 + 10, 0x41));
        }

        Assert.True(agg.IsSaturated);
        Assert.Equal(3, agg.StepCount);
    }

    [Fact]
    public void Sanitized_ClampsNonsenseOptions()
    {
        var opts = new RecorderOptions
        {
            MaxRecordingMs = -5,
            MoveMinIntervalMs = 999_999,
            WheelQuantum = 0,
            MaxRecordedSteps = 100_000,
        }.Sanitized();

        Assert.Equal(1_000, opts.MaxRecordingMs);
        Assert.Equal(1_000, opts.MoveMinIntervalMs);
        Assert.Equal(1, opts.WheelQuantum);
        Assert.Equal(MacroLimits.MaxSteps, opts.MaxRecordedSteps);
    }

    // ---------- end-to-end safety ----------

    [Fact]
    public void RealisticRecording_AllEmittedStepsPassPerStepValidation()
    {
        // A small "fill a form" session: move, click, type-ish keys, scroll, dblclick.
        var agg = new EventAggregator();
        agg.Consume(Move(0, 400, 300));
        agg.Consume(Down(100)); agg.Consume(Up(140));
        agg.Consume(KeyDown(200, 0x48)); agg.Consume(KeyUp(230, 0x48)); // H
        agg.Consume(KeyDown(260, 0x49)); agg.Consume(KeyUp(290, 0x49)); // I
        agg.Consume(Wheel(350, -120));
        agg.Consume(Down(500)); agg.Consume(Up(540));
        agg.Consume(Down(600)); agg.Consume(Up(640));

        var steps = agg.Build();
        Assert.NotEmpty(steps);

        var macro = new MacroDefinition
        {
            Id = "recorded-test",
            Name = "Recorded test",
            Steps = steps,
        };
        var result = MacroValidator.Validate(macro);
        Assert.True(result.IsValid,
            string.Join("; ", result.Errors.Select(e => $"{e.Code}[{e.StepIndex}]: {e.Message}")));
    }
}
