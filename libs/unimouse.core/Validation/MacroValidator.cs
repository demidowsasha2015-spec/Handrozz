using System.Text.RegularExpressions;
using UniMouse.Core.Model;

namespace UniMouse.Core.Validation;

/// <summary>A single validation problem with a machine-readable code.</summary>
public sealed record ValidationError(string Code, string Message, int? StepIndex = null);

/// <summary>Result of validating a macro: valid flag plus all collected errors.</summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<ValidationError> Errors)
{
    public static readonly ValidationResult Ok = new(true, []);
}

/// <summary>
/// Hard safety + sanity limits. These are the product's guardrails:
/// macros that fail validation can never reach the input executor.
/// </summary>
public static partial class MacroLimits
{
    public const int MaxSteps = 2000;
    public const int MaxDelayMs = 60_000;
    public const int MaxHoldMs = 10_000;
    public const int MaxTextLength = 10_000;
    public const int MaxRepeatCount = 10_000;      // -1 (infinite) is allowed separately
    public const int MaxRepeatDelayMs = 60_000;
    public const int MaxDurationMs = 15 * 60_000;  // 15 minutes hard cap
    public const int MinClicks = 1;
    public const int MaxClicks = 5;
    public const int MaxScrollAmount = 120;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 2_000;

    /// <summary>Virtual-key codes we refuse to synthesize (security-sensitive).</summary>
    public static readonly HashSet<int> ForbiddenKeyCodes = [];

    /// <summary>Win+L (lock), Ctrl+Alt+Del cannot be caught by hotkeys but Win combos can.</summary>
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkLock = 0x14;

    static MacroLimits()
    {
        ForbiddenKeyCodes.Add(VkLWin);
        ForbiddenKeyCodes.Add(VkRWin);
        ForbiddenKeyCodes.Add(VkLock);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    public static partial Regex IdPattern();
}

/// <summary>
/// Validates <see cref="MacroDefinition"/> against schema rules and safety limits.
/// Pure function — no I/O, no platform dependencies. Mirrored in Python
/// (apps/qwen-service/src/qwen_service/validation.py) so both sides agree.
/// </summary>
public static class MacroValidator
{
    public static ValidationResult Validate(MacroDefinition macro)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(macro.Id) || !MacroLimits.IdPattern().IsMatch(macro.Id))
            errors.Add(new("id.invalid", "Id must be non-empty and match [A-Za-z0-9._-]+."));

        if (string.IsNullOrWhiteSpace(macro.Name) || macro.Name.Length > MacroLimits.MaxNameLength)
            errors.Add(new("name.invalid", $"Name must be 1..{MacroLimits.MaxNameLength} characters."));

        if (macro.Description.Length > MacroLimits.MaxDescriptionLength)
            errors.Add(new("description.too_long", $"Description exceeds {MacroLimits.MaxDescriptionLength} characters."));

        if (macro.SchemaVersion != MacroDefinition.SchemaVersionValue)
            errors.Add(new("schema.version", $"Unsupported schemaVersion {macro.SchemaVersion}."));

        if (macro.Steps.Count == 0)
            errors.Add(new("steps.empty", "Macro must contain at least one step."));

        if (macro.Steps.Count > MacroLimits.MaxSteps)
            errors.Add(new("steps.too_many", $"Macro exceeds {MacroLimits.MaxSteps} steps."));

        if (macro.RepeatCount < -1 || macro.RepeatCount > MacroLimits.MaxRepeatCount)
            errors.Add(new("repeat.invalid", $"RepeatCount must be -1 or 0..{MacroLimits.MaxRepeatCount}."));

        if (macro.RepeatDelayMs is < 0 or > MacroLimits.MaxRepeatDelayMs)
            errors.Add(new("repeat.delay", $"RepeatDelayMs must be 0..{MacroLimits.MaxRepeatDelayMs}."));

        if (macro.MaxDurationMs is <= 0 or > MacroLimits.MaxDurationMs)
            errors.Add(new("duration.invalid", $"MaxDurationMs must be 1..{MacroLimits.MaxDurationMs}."));

        for (var i = 0; i < macro.Steps.Count; i++)
            ValidateStep(macro.Steps[i], i, errors);

        return new(errors.Count == 0, errors);
    }

    private static void ValidateStep(MacroStep step, int index, List<ValidationError> errors)
    {
        static void Err(List<ValidationError> e, string code, string msg, int i) =>
            e.Add(new ValidationError(code, msg, i));

        if (step.DelayBeforeMs is < 0 or > MacroLimits.MaxDelayMs)
            Err(errors, "step.delay", $"DelayBeforeMs must be 0..{MacroLimits.MaxDelayMs}.", index);

        if (step.HoldMs is < 0 or > MacroLimits.MaxHoldMs)
            Err(errors, "step.hold", $"HoldMs must be 0..{MacroLimits.MaxHoldMs}.", index);

        switch (step.Kind)
        {
            case StepKind.Key:
                if (step.KeyCode is null)
                    Err(errors, "step.key_code_missing", "Key step requires KeyCode.", index);
                else if (step.KeyCode < 0x01 || step.KeyCode > 0xFE)
                    Err(errors, "step.key_code_range", "KeyCode must be a valid VK code (0x01..0xFE).", index);
                else if (MacroLimits.ForbiddenKeyCodes.Contains(step.KeyCode.Value))
                    Err(errors, "step.key_forbidden", "This virtual key is forbidden for safety.", index);
                break;

            case StepKind.Text:
                if (string.IsNullOrEmpty(step.Text))
                    Err(errors, "step.text_empty", "Text step requires non-empty Text.", index);
                else if (step.Text!.Length > MacroLimits.MaxTextLength)
                    Err(errors, "step.text_too_long", $"Text exceeds {MacroLimits.MaxTextLength} characters.", index);
                else if (ContainsDisallowedControlChars(step.Text!))
                    Err(errors, "step.text_control_chars", "Text must not contain control characters other than \\t and \\n.", index);
                break;

            case StepKind.MouseMove:
                if (step.X is null || step.Y is null)
                    Err(errors, "step.coords_missing", "MouseMove requires X and Y.", index);
                break;

            case StepKind.MouseClick:
                if (step.Button is null)
                    Err(errors, "step.button_missing", "MouseClick requires Button.", index);
                if (step.Clicks is < MacroLimits.MinClicks or > MacroLimits.MaxClicks)
                    Err(errors, "step.clicks_range", $"Clicks must be {MacroLimits.MinClicks}..{MacroLimits.MaxClicks}.", index);
                break;

            case StepKind.MouseScroll:
                if (step.Scroll is null)
                    Err(errors, "step.scroll_missing", "MouseScroll requires Scroll direction.", index);
                if (Math.Abs(step.ScrollAmount) is > MacroLimits.MaxScrollAmount)
                    Err(errors, "step.scroll_range", $"|ScrollAmount| must be 0..{MacroLimits.MaxScrollAmount}.", index);
                break;

            case StepKind.Delay:
                if (step.DelayBeforeMs == 0)
                    Err(errors, "step.delay_zero", "Delay step must have DelayBeforeMs > 0.", index);
                break;
        }
    }

    private static bool ContainsDisallowedControlChars(string text) =>
        text.Any(c => char.IsControl(c) && c != '\t' && c != '\n');
}
