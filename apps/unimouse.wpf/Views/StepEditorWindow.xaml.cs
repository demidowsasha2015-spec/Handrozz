using System.Windows;
using UniMouse.Core.Model;
using UniMouse.Wpf.Services;
using UniMouse.Wpf.ViewModels;

namespace UniMouse.Wpf.Views;

/// <summary>
/// Inline step editor window. Pure view: all state lives in StepEditorViewModel,
/// which re-validates through MacroValidator on save — an invalid macro can
/// never be persisted from here.
/// </summary>
public partial class StepEditorWindow : Window
{
    /// <summary>Raised after a successful save so the library list can refresh.</summary>
    public event Action? Saved;

    private readonly MacroStore _store;

    public StepEditorWindow() => InitializeComponent();

    public StepEditorWindow(StepEditorViewModel vm, MacroStore store) : this()
    {
        DataContext = vm;
        _store = store;
        Owner = Application.Current.MainWindow;
        vm.SavedOnce += () => Saved?.Invoke();
    }

    private void AddKey_Click(object s, RoutedEventArgs e)    => Vm.AddStep(StepKind.Key);
    private void AddText_Click(object s, RoutedEventArgs e)   => Vm.AddStep(StepKind.Text);
    private void AddMove_Click(object s, RoutedEventArgs e)   => Vm.AddStep(StepKind.MouseMove);
    private void AddClick_Click(object s, RoutedEventArgs e)  => Vm.AddStep(StepKind.MouseClick);
    private void AddScroll_Click(object s, RoutedEventArgs e) => Vm.AddStep(StepKind.MouseScroll);
    private void AddDelay_Click(object s, RoutedEventArgs e)  => Vm.AddStep(StepKind.Delay);

    private void MoveUp_Click(object s, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedIndex >= 0) Vm.MoveUp(StepsGrid.SelectedIndex);
    }

    private void MoveDown_Click(object s, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedIndex >= 0) Vm.MoveDown(StepsGrid.SelectedIndex);
    }

    private void Delete_Click(object s, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedIndex >= 0) Vm.RemoveAt(StepsGrid.SelectedIndex);
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        // Commit any cell still in edit mode before converting rows.
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        Vm.TrySave((macro, file) =>
        {
            try
            {
                _store.Save(macro, file);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private StepEditorViewModel Vm => (StepEditorViewModel)DataContext;
}

/// <summary>Static list providers for XAML bindings (combo boxes).</summary>
public static class EditorLists
{
    public static IReadOnlyList<string> Kinds => ["key", "text", "mouseMove", "mouseClick", "mouseScroll", "delay"];
    public static IReadOnlyList<string> Buttons => ["left", "right", "middle"];
    public static IReadOnlyList<string> Scrolls => ["up", "down"];
}
