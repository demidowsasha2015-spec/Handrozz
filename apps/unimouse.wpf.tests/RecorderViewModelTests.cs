using System.IO;
using System.Linq;
using UniMouse.Core.Model;
using UniMouse.Core.Runtime;
using UniMouse.Wpf.Services;
using UniMouse.Wpf.ViewModels;
using Xunit;

namespace UniMouse.Wpf.Tests;

/// <summary>
/// Headless tests for the recorder view model's save gate. The Win32 hook
/// capture itself (InputCapture) is manual-test only; everything that decides
/// whether a recording may land in the library lives here.
/// </summary>
public class RecorderViewModelTests : IDisposable
{
    private List<MacroDefinition> LoadAll(MacroStore store) =>
        store.ListIds().Select(id => store.Load(id)!).ToList();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unimouse-rec-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static MacroStep Key(int vk, int delayMs = 50) =>
        new() { Kind = StepKind.Key, KeyCode = vk, DelayBeforeMs = delayMs };

    [Fact]
    public void TrySave_ValidSteps_PersistsMacroToStore()
    {
        var store = new MacroStore(_root);
        var vm = new RecorderViewModel { MacroName = "My recording" };

        // 'A' key press + trailing delay — passes validation (non-degenerate).
        var steps = new List<MacroStep> { Key(0x41), Key(0x42, 60) };

        Assert.True(vm.TrySave(store, steps, out var message));
        Assert.Contains("Saved", message);

        var saved = LoadAll(store);
        Assert.Single(saved);
        Assert.Equal("My recording", saved[0].Name);
        Assert.Equal(2, saved[0].Steps.Count);
        Assert.StartsWith("recorded-", saved[0].Id);
    }

    [Fact]
    public void TrySave_EmptySteps_RejectedWithValidatorError()
    {
        var store = new MacroStore(_root);
        var vm = new RecorderViewModel();

        Assert.False(vm.TrySave(store, [], out var message));
        Assert.NotEmpty(message);
        Assert.Empty(LoadAll(store)); // nothing persisted on rejection
    }

    [Fact]
    public void TrySave_BlankName_FallsBackToDefault()
    {
        var store = new MacroStore(_root);
        var vm = new RecorderViewModel { MacroName = "   " };

        Assert.True(vm.TrySave(store, [Key(0x41), Key(0x42, 60)], out _));
        Assert.Equal("Recorded macro", LoadAll(store)[0].Name);
    }

    [Fact]
    public void TrySave_ForbiddenKey_RejectedNothingPersisted()
    {
        var store = new MacroStore(_root);
        var vm = new RecorderViewModel();

        // VK_LWIN (0x5B) is forbidden by MacroValidator policy.
        Assert.False(vm.TrySave(store, [Key(0x5B), Key(0x42, 60)], out var message));
        Assert.Contains("forbidden", message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(LoadAll(store));
    }

    [Fact]
    public void Dispose_BeforeAnyStart_IsNoOpAndSafeTwice()
    {
        // Capture was never started (no hooks installed): Dispose must not throw,
        // and must be idempotent because both Stop path and window Closed fire it.
        var vm = new RecorderViewModel();
        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public void Stop_WithoutStart_ProducesNoReviewEvent()
    {
        var vm = new RecorderViewModel();
        var fired = false;
        vm.ReviewRequested += _ => fired = true;

        vm.StopCommand.Execute(null); // CanExecute false when not recording → no-op

        Assert.False(fired);
        Assert.False(vm.IsRecording);
    }
}
