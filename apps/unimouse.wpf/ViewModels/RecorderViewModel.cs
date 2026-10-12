using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Core.Validation;
using UniMouse.Input;
using UniMouse.Wpf.Services;

namespace UniMouse.Wpf.ViewModels;

/// <summary>
/// Modal dialog VM for a recording session: explicit start/stop UX, live step
/// counter, and a mandatory review-before-save gate (the recorded macro opens
/// in the step editor instead of silently landing in the library).
/// </summary>
public partial class RecorderViewModel : ObservableObject, IDisposable
{
    private InputCapture? _capture;

    [ObservableProperty] private string _macroName = "Recorded macro";
    [ObservableProperty] private int _stepCount;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _statusText =
        "Press Start. Recording captures keyboard/mouse ONLY while this dialog is open — hooks are removed on Stop.";

    public event Action<IReadOnlyList<MacroStep>>? ReviewRequested;

    [RelayCommand]
    private void Start()
    {
        if (IsRecording) return;

        _capture = new InputCapture(new RecorderOptions());
        // Marshal to the UI thread defensively: if a message pump other than
        // the WPF dispatcher ever drives the hook, Application.Current may be null.
        var dispatcher = Dispatcher.CurrentDispatcher;
        _capture.StepsChanged += count =>
            dispatcher.BeginInvoke(() => StepCount = count);
        try
        {
            _capture.Start();
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message; // e.g. secure desktop active
            _capture.Dispose();
            _capture = null;
            return;
        }
        IsRecording = true;
        StepCount = 0;
        StatusText = "● REC — performing your actions now will be captured…";
    }

    private bool CanStop() => IsRecording;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_capture is null) return;
        var steps = _capture.Stop();
        _capture.Dispose();
        _capture = null;
        IsRecording = false;
        StatusText = $"Stopped. {steps.Count} step(s) captured.";
        if (steps.Count > 0)
            ReviewRequested?.Invoke(steps);
    }

    /// <summary>Saves after user edited/reviewed; returns false when validation rejects.</summary>
    public bool TrySave(MacroStore store, IReadOnlyList<MacroStep> steps, out string message)
    {
        var name = string.IsNullOrWhiteSpace(MacroName) ? "Recorded macro" : MacroName.Trim();
        var id = "recorded-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var macro = new MacroDefinition
        {
            Id = id,
            Name = name.Length <= MacroLimits.MaxNameLength ? name : name[..MacroLimits.MaxNameLength],
            Description = "Created with the built-in recorder.",
            Author = Environment.UserName,
            Steps = steps,
        };

        var result = MacroValidator.Validate(macro);
        if (!result.IsValid)
        {
            message = string.Join("\n", result.Errors.Take(8).Select(e => e.Message));
            return false;
        }

        store.Save(macro);
        message = $"Saved '{macro.Name}' ({macro.Steps.Count} steps).";
        return true;
    }

    public void Dispose()
    {
        _capture?.Dispose(); // hooks ALWAYS come off, even on window close mid-recording
        _capture = null;
    }
}
