using UniMouse.Core.Validation;

namespace UniMouse.Core.Runtime;

/// <summary>Playback state machine states.</summary>
public enum RunnerState
{
    Idle,
    Running,
    Stopping,
    Completed,
    AbortedByUser,
    FailedValidation,
    TimedOut,
}

/// <summary>Progress event raised on the runner's synchronization context.</summary>
public sealed record RunnerProgress(RunnerState State, int StepIndex, int TotalSteps, int PassCompleted, string? Message = null);

/// <summary>
/// Executes a validated <see cref="MacroDefinition"/> against an <see cref="IInputExecutor"/>.
/// Safety properties (unit-tested in UniMouse.Core.Tests):
///  * refuses to run macros that fail <see cref="MacroValidator"/>;
///  * enforces MaxDurationMs with a hard timeout;
///  * honors cooperative cancellation (emergency stop) — always calls ReleaseAllAsync;
///  * never runs re-entrantly: one macro at a time per process (TryStart guard).
/// </summary>
public sealed class MacroRunner
{
    private readonly IInputExecutor _executor;
    private int _running; // 0 = free, 1 = busy (Interlocked)

    public event Action<RunnerProgress>? Progress;

    public MacroRunner(IInputExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>Returns false if another playback is already running.</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public void Stop() => Interlocked.Exchange(ref _running, 0);

    public async Task<RunnerState> RunAsync(MacroDefinition macro, CancellationToken ct = default)
    {
        var validation = MacroValidator.Validate(macro);
        if (!validation.IsValid)
        {
            Report(new RunnerProgress(RunnerState.FailedValidation, -1, macro.Steps.Count, 0,
                string.Join("; ", validation.Errors.Select(e => e.Message))));
            return RunnerState.FailedValidation;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(macro.MaxDurationMs);

        try
        {
            var passes = macro.RepeatCount switch { < 0 => int.MaxValue, 0 => 1, var n => n };
            for (var pass = 0; pass < passes; pass++)
            {
                for (var i = 0; i < macro.Steps.Count; i++)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    Report(new RunnerProgress(RunnerState.Running, i, macro.Steps.Count, pass));
                    await ExecuteStepAsync(macro.Steps[i], timeoutCts.Token).ConfigureAwait(false);
                }

                if (pass + 1 < passes)
                    await Task.Delay(macro.RepeatDelayMs, timeoutCts.Token).ConfigureAwait(false);
            }

            Report(new RunnerProgress(RunnerState.Completed, macro.Steps.Count, macro.Steps.Count, passes));
            return RunnerState.Completed;
        }
        catch (OperationCanceledException)
        {
            var timedOut = !ct.IsCancellationRequested;
            var state = timedOut ? RunnerState.TimedOut : RunnerState.AbortedByUser;
            Report(new RunnerProgress(state, -1, macro.Steps.Count, 0,
                timedOut ? "Aborted: max duration exceeded." : "Aborted by user."));
            return state;
        }
        finally
        {
            // Always leave the keyboard/mouse in a clean state.
            await _executor.ReleaseAllAsync().ConfigureAwait(false);
            Stop();
        }
    }

    private async Task ExecuteStepAsync(Model.MacroStep step, CancellationToken ct)
    {
        if (step.DelayBeforeMs > 0)
            await Task.Delay(step.DelayBeforeMs, ct).ConfigureAwait(false);

        switch (step.Kind)
        {
            case Model.StepKind.Delay:
                break; // delay already applied above

            case Model.StepKind.Key when step.KeyCode is not null:
                await _executor.PressKeyAsync(step.KeyCode.Value, ct).ConfigureAwait(false);
                if (step.HoldMs > 0) await Task.Delay(step.HoldMs, ct).ConfigureAwait(false);
                await _executor.ReleaseKeyAsync(step.KeyCode.Value, ct).ConfigureAwait(false);
                break;

            case Model.StepKind.Text when !string.IsNullOrEmpty(step.Text):
                await _executor.TypeTextAsync(step.Text!, ct).ConfigureAwait(false);
                break;

            case Model.StepKind.MouseMove when step.X is not null && step.Y is not null:
                await _executor.MoveMouseAsync(step.X!.Value, step.Y!.Value, ct).ConfigureAwait(false);
                break;

            case Model.StepKind.MouseClick when step.Button is not null:
                await _executor.ClickMouseAsync(step.Button.Value, step.Clicks, ct).ConfigureAwait(false);
                break;

            case Model.StepKind.MouseScroll when step.Scroll is not null:
                await _executor.ScrollMouseAsync(step.Scroll.Value, Math.Abs(step.ScrollAmount), ct).ConfigureAwait(false);
                break;
        }
    }

    private void Report(RunnerProgress p) => Progress?.Invoke(p);
}
