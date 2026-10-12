using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Core.Validation;

namespace UniMouse.Wpf.ViewModels;

/// <summary>
/// Editable wrapper around one immutable <see cref="MacroStep"/>. All setters
/// validate eagerly and surface problems via <see cref="Error"/>, so the grid
/// can show red rows before anyone presses Save. Parsing of friendly key names
/// ("Enter", "a", "0x1A") happens here through <see cref="VirtualKey"/>.
/// </summary>
public partial class StepRow : ObservableObject
{
    private static readonly string[] KindNames =
        ["key", "text", "mouseMove", "mouseClick", "mouseScroll", "delay"];
    private static readonly string[] ButtonNames = ["left", "right", "middle"];
    private static readonly string[] ScrollNames = ["up", "down"];

    public int Index { get; internal set; }

    [ObservableProperty] private StepKind _kind;
    [ObservableProperty] private string _delayBeforeText = "0";
    [ObservableProperty] private string _holdText = "0";
    [ObservableProperty] private string _keyName = string.Empty;   // parsed -> KeyCode
    [ObservableProperty] private string _text = string.Empty;      // Text steps
    [ObservableProperty] private string _xText = string.Empty;
    [ObservableProperty] private string _yText = string.Empty;
    [ObservableProperty] private string _button = "left";          // raw lowercase token
    [ObservableProperty] private int _clicks = 1;
    [ObservableProperty] private string _scrollDirection = "down"; // raw lowercase token
    [ObservableProperty] private int _scrollAmount = 1;            // notches, ±1..5

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>1-based row number for the grid's "#" column.</summary>
    public string DisplayNumber => (Index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Schema token ("key"/"delay"/…) bound to the Kind combo box.</summary>
    public string KindToken
    {
        get => TokenOf(Kind);
        set { if (TryFromToken(value, out var k)) Kind = k; }
    }

    private static string TokenOf(StepKind kind) => kind switch
    {
        StepKind.Key => "key",
        StepKind.Text => "text",
        StepKind.MouseMove => "mouseMove",
        StepKind.MouseClick => "mouseClick",
        StepKind.MouseScroll => "mouseScroll",
        _ => "delay",
    };

    private static bool TryFromToken(string token, out StepKind kind)
    {
        var idx = Array.FindIndex(KindNames,
            a => string.Equals(a, token?.Trim(), StringComparison.OrdinalIgnoreCase));
        kind = idx < 0 ? StepKind.Delay : (StepKind)Enum.GetValues<StepKind>().GetValue(idx);
        return idx >= 0;
    }

    // Field-relevance flags driving column visibility in the grid.
    public bool ShowKey => Kind == StepKind.Key;
    public bool ShowText => Kind == StepKind.Text;
    public bool ShowCoords => Kind is StepKind.MouseMove or StepKind.MouseClick;
    public bool ShowHold => Kind is StepKind.Key or StepKind.MouseClick;
    public bool ShowClick => Kind == StepKind.MouseClick;
    public bool ShowScroll => Kind == StepKind.MouseScroll;

    partial void OnKindChanged(StepKind value)
    {
        OnPropertyChanged(nameof(ShowKey));
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(ShowCoords));
        OnPropertyChanged(nameof(ShowHold));
        OnPropertyChanged(nameof(ShowClick));
        OnPropertyChanged(nameof(ShowScroll));
        OnPropertyChanged(nameof(KindToken));
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public static StepRow From(MacroStep s)
    {
        var notches = Math.Clamp(Math.Abs(s.ScrollAmount) / MacroLimits.ScrollWheelDelta,
                                 1, MacroLimits.MaxScrollNotches);
        var row = new StepRow
        {
            DelayBeforeText = s.DelayBeforeMs.ToString(CultureInfo.InvariantCulture),
            HoldText = s.HoldMs.ToString(CultureInfo.InvariantCulture),
            Text = s.Text ?? string.Empty,
            XText = s.X?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            YText = s.Y?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Button = s.Button?.ToString().ToLowerInvariant() ?? "left",
            Clicks = Math.Clamp(s.Clicks, 1, MacroLimits.MaxClicks),
            ScrollDirection = s.Scroll?.ToString().ToLowerInvariant() ?? "down",
            ScrollAmount = s.Scroll == ScrollDirection.Up ? notches : -notches,
            KeyName = s.KeyCode is { } vk ? VirtualKey.Name(vk) : string.Empty,
        };
        // Set Kind last so the derived Show* flags reflect the final state.
        row.Kind = s.Kind;
        return row;
    }

    public static StepRow CreateDefault(StepKind kind)
    {
        var row = new StepRow
        {
            DelayBeforeText = "100",
            HoldText = kind switch { StepKind.Key or StepKind.MouseClick => "50", _ => "0" },
            KeyName = kind == StepKind.Key ? "Enter" : string.Empty,
            Text = kind == StepKind.Text ? "hello" : string.Empty,
            XText = kind is StepKind.MouseMove ? "0" : string.Empty,
            YText = kind is StepKind.MouseMove ? "0" : string.Empty,
        };
        row.Kind = kind; // after init — keeps derived flags consistent
        return row;
    }

    /// <summary>
    /// Converts the row back to an immutable step. Throws with a user-friendly
    /// message on the first unrecoverable problem (also cached in Error).
    /// </summary>
    public MacroStep ToStep()
    {
        var step = Convert();
        // Per-step sanity check using the same validator the runner uses.
        var probe = new MacroDefinition
        {
            Id = "editor.probe", Name = "probe", Steps = [step],
        };
        var result = MacroValidator.Validate(probe);
        if (!result.IsValid)
        {
            var msg = result.Errors[0].Message;
            Error = msg;
            throw new InvalidDataException($"Step {Index + 1}: {msg}");
        }
        Error = null;
        return step;
    }

    private MacroStep Convert()
    {
        var delay = ParseInt(DelayBeforeText, nameof(DelayBeforeMs), allowNegative: false);
        var hold = ParseInt(HoldText, nameof(HoldMs), allowNegative: false);

        switch (Kind)
        {
            case StepKind.Delay:
                return new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = delay };

            case StepKind.Key:
                if (!VirtualKey.TryParse(KeyName, out var vk))
                    Throw($"Unknown key name '{KeyName}'. Use e.g. Enter, Tab, F5, a, 7 or 0x1A.");
                return new MacroStep
                {
                    Kind = StepKind.Key, DelayBeforeMs = delay, HoldMs = hold, KeyCode = vk,
                };

            case StepKind.Text:
                if (string.IsNullOrEmpty(Text))
                    Throw("Text step needs content.");
                return new MacroStep
                {
                    Kind = StepKind.Text, DelayBeforeMs = delay, Text = Text,
                };

            case StepKind.MouseMove:
                return new MacroStep
                {
                    Kind = StepKind.MouseMove, DelayBeforeMs = delay,
                    X = ParseInt(XText, "X", allowNegative: true),
                    Y = ParseInt(YText, "Y", allowNegative: true),
                };

            case StepKind.MouseClick:
                return new MacroStep
                {
                    Kind = StepKind.MouseClick, DelayBeforeMs = delay, HoldMs = hold,
                    Button = ParseEnum<MouseButton>(Button, ButtonNames, nameof(Button)),
                    Clicks = Math.Clamp(Clicks, MacroLimits.MinClicks, MacroLimits.MaxClicks),
                };

            case StepKind.MouseScroll:
                var dir = ParseEnum<ScrollDirection>(ScrollDirection, ScrollNames, nameof(ScrollDirection));
                if (Math.Abs(ScrollAmount) is < 1 or > MacroLimits.MaxScrollNotches)
                    Throw($"Scroll notches must be between 1 and {MacroLimits.MaxScrollNotches}.");
                var notches = Math.Abs(ScrollAmount);
                return new MacroStep
                {
                    Kind = StepKind.MouseScroll, DelayBeforeMs = delay,
                    Scroll = dir, ScrollAmount = (dir == ScrollDirection.Up ? 1 : -1) * notches * MacroLimits.ScrollWheelDelta,
                };

            default:
                Throw($"Unsupported step kind '{Kind}'.");
                throw Unreachable();
        }
    }

    private int ParseInt(string s, string field, bool allowNegative)
    {
        if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            || (v < 0 && !allowNegative))
            Throw($"{field} must be {(allowNegative ? "an integer" : "a non-negative integer")} (got '{s}').");
        return v;
    }

    private T ParseEnum<T>(string token, string[] allowed, string field) where T : struct
    {
        var idx = Array.FindIndex(allowed, a => string.Equals(a, token?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (idx < 0) Throw($"{field} must be one of: {string.Join(", ", allowed)}.");
        return (T)Enum.GetValues<T>().GetValue(idx);
    }

    [DoesNotReturn]
    private static Exception Unreachable() =>
        throw new InvalidOperationException("Unreachable.");

    [DoesNotReturn]
    private void Throw(string message)
    {
        Error = message;
        throw new InvalidDataException($"Step {Index + 1}: {message}");
    }
}

/// <summary>
/// Inline step editor bound by the WPF DataGrid. Holds an ObservableCollection
/// of rows; every mutation re-indexes rows so error messages say "Step N".
/// Saving goes through MacroValidator — an invalid macro can never be persisted.
/// </summary>
public partial class StepEditorViewModel : ObservableObject
{
    public string SourceFileName { get; }
    public ObservableCollection<StepRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private MacroDefinition _macro;

    [ObservableProperty] private string? _saveResult;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isReadOnly;

    /// <summary>Window title.</summary>
    public string Title => IsReadOnly
        ? $"Steps of '{Macro.Name}' (read-only)"
        : $"Edit steps — {Macro.Name}";

    /// <summary>Toolbar buttons enabled only when editable.</summary>
    public bool CanEdit => !IsReadOnly;

    public StepEditorViewModel(MacroDefinition macro, string sourceFileName)
    {
        Macro = macro;
        SourceFileName = sourceFileName;
        Rows.CollectionChanged += (_, e) =>
        {
            Reindex();
            if (e.NewItems is not null)
                foreach (StepRow r in e.NewItems)
                    r.PropertyChanged += (_, _) => MarkDirty();
        };
        foreach (var s in macro.Steps)
            Rows.Add(StepRow.From(s));
        if (Rows.Count == 0)
            Rows.Add(StepRow.CreateDefault(StepKind.Delay));
    }

    /// <summary>Raised once after a successful save (window forwards to the library).</summary>
    public event Action? SavedOnce;

    /// <summary>True when rows differ from the last saved/persisted definition.</summary>
    public bool IsDirty { get; private set; }

    private void Reindex()
    {
        for (var i = 0; i < Rows.Count; i++)
            Rows[i].Index = i;
    }

    private void MarkDirty()
    {
        if (!IsDirty)
        {
            IsDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public void AddStep(StepKind kind)
    {
        if (IsReadOnly) return;
        Rows.Add(StepRow.CreateDefault(kind));
        SaveResult = null;
        MarkDirty();
    }

    public void RemoveAt(int index)
    {
        if (IsReadOnly || index < 0 || index >= Rows.Count) return;
        Rows.RemoveAt(index);
        SaveResult = null;
        MarkDirty();
    }

    public void MoveUp(int index)
    {
        if (IsReadOnly || index <= 0 || index >= Rows.Count) return;
        Rows.Move(index, index - 1);
        SaveResult = null;
        MarkDirty();
    }

    public void MoveDown(int index)
    {
        if (IsReadOnly || index < 0 || index >= Rows.Count - 1) return;
        Rows.Move(index, index + 1);
        SaveResult = null;
        MarkDirty();
    }

    /// <summary>
    /// Builds a new immutable definition from the current rows. Throws
    /// InvalidDataException with the first problematic step's message.
    /// </summary>
    public MacroDefinition BuildUpdated(out ValidationResult validation)
    {
        var steps = new List<MacroStep>(Rows.Count);
        foreach (var row in Rows)
            steps.Add(row.ToStep()); // throws on bad input

        var updated = new MacroDefinition
        {
            SchemaVersion = Macro.SchemaVersion,
            Id = Macro.Id,
            Name = Macro.Name,
            Description = Macro.Description,
            Author = Macro.Author,
            CreatedAtUtc = Macro.CreatedAtUtc,
            Steps = steps,
            RepeatCount = Macro.RepeatCount,
            RepeatDelayMs = Macro.RepeatDelayMs,
            MaxDurationMs = Macro.MaxDurationMs,
            StartHotkey = Macro.StartHotkey,
            Tags = Macro.Tags,
        };
        validation = MacroValidator.Validate(updated);
        return updated;
    }

    /// <summary>Persist through the store after full validation. Returns false + reason on failure.</summary>
    public bool TrySave(Func<MacroDefinition, string, bool> saveAction)
    {
        if (IsReadOnly)
        {
            SaveResult = "Read-only.";
            return false;
        }
        try
        {
            var updated = BuildUpdated(out var validation);
            if (!validation.IsValid)
            {
                SaveResult = "Cannot save — invalid macro: " +
                             string.Join("; ", validation.Errors.Select(e =>
                                 e.StepIndex is int i ? $"step {i + 1}: {e.Message}" : e.Message));
                return false;
            }
            if (!saveAction(updated, SourceFileName))
            {
                SaveResult = "Save was refused by the store.";
                return false;
            }
            Macro = updated;
            SaveResult = $"Saved {Rows.Count} step(s).";
            IsDirty = false;
            OnPropertyChanged(nameof(IsDirty));
            SavedOnce?.Invoke();
            return true;
        }
        catch (InvalidDataException ex)
        {
            SaveResult = "Cannot save — " + ex.Message;
            return false;
        }
    }
}
