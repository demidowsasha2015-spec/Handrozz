using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Core.Validation;
using UniMouse.Input;
using UniMouse.Wpf.Services;

namespace UniMouse.Wpf.ViewModels;

/// <summary>
/// Main window view model: macro library, AI generation, editing, playback.
/// Threading rule: playback runs on the thread pool (MacroRunner), all UI
/// mutations are marshalled back via the Progress event raised on capture.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Raised when the user asks to edit a macro; MainWindow opens StepEditorWindow.</summary>
    public event Action<MacroListItem>? EditRequested;

    private readonly MacroStore _store;
    private readonly QwenApiClient _api;
    private readonly MacroRunner _runner;
    private CancellationTokenSource? _playbackCts;

    public ObservableCollection<MacroListItem> Macros { get; } = [];

    [ObservableProperty] private MacroListItem? _selectedMacro;

    /// <summary>
    /// Drives IsEnabled of the Edit/Delete buttons. CommunityToolkit only raises
    /// CanExecuteChanged for commands annotated with NotifyCanExecuteChangedFor,
    /// so we refresh them explicitly when the selection changes — otherwise the
    /// buttons would stay greyed out after clicking a list row.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _hasLibrarySelection;

    public bool HasSelection => SelectedMacro is not null;

    partial void OnSelectedMacroChanged(MacroListItem? value)
    {
        HasLibrarySelection = value is not null;
        EditSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] private string _aiPrompt = string.Empty;
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayMacroCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopMacroCommand))]
    private bool _isPlaying;

    [ObservableProperty] private double _progress;          // 0..1 within current pass
    [ObservableProperty] private int _currentStepIndex;
    [ObservableProperty] private int _totalSteps;
    [ObservableProperty] private bool _serviceOnline;

    public MainViewModel(MacroStore store, QwenApiClient api)
    {
        _store = store;
        _api = api;
        _runner = new MacroRunner(new Win32InputExecutor());
        ReloadLibrary();
        _ = CheckServiceAsync();
    }

    // ---------------------------------------------------------------- library

    [RelayCommand]
    private void ReloadLibrary()
    {
        Macros.Clear();
        foreach (var file in _store.ListIds())
        {
            var macro = _store.Load(file);
            if (macro is not null)
                Macros.Add(new MacroListItem(file, macro));
        }
        StatusText = $"Loaded {Macros.Count} macro(s).";
    }

    private bool CanActOnSelection() => SelectedMacro is not null;

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void DeleteSelected()
    {
        if (SelectedMacro is null) return;
        _store.Delete(SelectedMacro.FileName);
        ReloadLibrary();
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void EditSelected()
    {
        if (SelectedMacro is not null)
            EditRequested?.Invoke(SelectedMacro);
    }

    /// <summary>Called by MainWindow after a successful save from the step editor.</summary>
    public void NotifyMacroSaved(string fileName)
    {
        ReloadLibrary();
        var again = Macros.FirstOrDefault(m => m.FileName == fileName);
        if (again is not null) SelectedMacro = again;
        StatusText = $"Macro '{fileName}' updated and re-validated.";
    }

    [RelayCommand]
    private void NewBlankMacro()
    {
        var macro = new MacroDefinition
        {
            Id = $"user.{DateTime.Now:yyyyMMdd-HHmmss}",
            Name = "New macro",
            Steps = [new MacroStep { Kind = StepKind.Delay, DelayBeforeMs = 500 }],
        };
        var validation = MacroValidator.Validate(macro);
        if (!validation.IsValid) return; // never persist invalid macros
        _store.Save(macro);
        ReloadLibrary();
    }

    // ------------------------------------------------------------------- AI

    [RelayCommand]
    private async Task GenerateWithAiAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(AiPrompt))
        {
            StatusText = "Type an intent first, e.g. 'type \"good morning\" then press Enter'.";
            return;
        }

        StatusText = "Asking local Qwen service…";
        try
        {
            var result = await _api.GenerateAsync(AiPrompt.Trim(), ct: ct);

            if (result.Rejected)
            {
                StatusText = $"REFUSED by safety policy: {result.RejectReason}";
                return;
            }
            if (result.Macro is null)
            {
                StatusText = "No macro produced. " +
                             string.Join(" | ", result.Warnings.Take(3));
                return;
            }

            _store.Save(result.Macro);
            ReloadLibrary();
            AiPrompt = string.Empty;
            StatusText = $"AI generated '{result.Macro.Name}' " +
                         $"({result.Macro.Steps.Count} steps). Review before running. {result.Explanation}";
        }
        catch (HttpRequestException)
        {
            StatusText = "Qwen service unreachable. Start it: uvicorn qwen_service.main:app --host 127.0.0.1 --port 8777";
            ServiceOnline = false;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Generation cancelled.";
        }
    }

    private async Task CheckServiceAsync()
    {
        ServiceOnline = await Task.Run(() => _api.HealthyAsync());
    }

    // -------------------------------------------------------------- playback

    private bool CanPlay() => !IsPlaying && SelectedMacro is not null;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task PlayMacroAsync() => PlayAsync(SelectedMacro);

    /// <summary>Entry point for per-macro start hotkeys (HotkeyManager).</summary>
    public void PlayById(string id)
    {
        var item = Macros.FirstOrDefault(m => m.Id == id);
        if (item is null)
        {
            StatusText = $"No macro registered for hotkey target '{id}'.";
            return;
        }
        SelectedMacro = item;
        // Fire-and-forget on the UI thread; playback itself runs on pool.
        _ = PlayAsync(item);
    }

    private async Task PlayAsync(MacroListItem? selected)
    {
        if (selected is null || IsPlaying || !_runner.TryStart())
        {
            StatusText = "A macro is already running.";
            return;
        }

        // Pre-flight local validation — the runner validates again, but this
        // gives instant feedback without touching input devices.
        var validation = MacroValidator.Validate(selected.Definition);
        if (!validation.IsValid)
        {
            _runner.Stop();
            StatusText = "Validation failed: " +
                         string.Join("; ", validation.Errors.Select(e => e.Message));
            return;
        }

        IsPlaying = true;
        TotalSteps = selected.Definition.Steps.Count;
        _playbackCts = new CancellationTokenSource();

        // Marshal progress events back to this VM (UI binds directly).
        Action<RunnerProgress>? handler = p =>
        {
            CurrentStepIndex = p.StepIndex + 1;
            Progress = p.TotalSteps == 0 ? 0 : (double)p.StepIndex / p.TotalSteps;
            StatusText = p.State switch
            {
                RunnerState.Running => $"Running step {p.StepIndex + 1}/{p.TotalSteps} (pass {p.PassCompleted + 1})…",
                RunnerState.Completed => "Completed.",
                RunnerState.TimedOut => $"ABORTED: {p.Message}",
                RunnerState.AbortedByUser => "Stopped by user.",
                RunnerState.FailedValidation => $"REJECTED: {p.Message}",
                _ => StatusText,
            };
        };
        _runner.Progress += handler;

        try
        {
            await _runner.RunAsync(selected.Definition, _playbackCts.Token);
        }
        finally
        {
            _runner.Progress -= handler;
            IsPlaying = false;
            Progress = 0;
            _playbackCts.Dispose();
            _playbackCts = null;
        }
    }

    private bool CanStop() => IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopMacro()
    {
        _playbackCts?.Cancel();
        StatusText = "Stopping… releasing all keys.";
    }

    /// <summary>Wired to EmergencyStopHotkey.Triggered (Ctrl+Alt+F12).</summary>
    public void EmergencyStop() => StopMacro();

    public void Dispose()
    {
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _api.Dispose();
    }
}

/// <summary>Row in the macro library list.</summary>
public sealed record MacroListItem(string FileName, MacroDefinition Definition)
{
    public string Id => string.IsNullOrWhiteSpace(Definition.Id) ? FileName : Definition.Id;
    public string DisplayName => $"{Definition.Name}  ({Definition.Steps.Count} steps)";
}
