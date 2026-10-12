using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// <summary>Raised after a successful save, with the real file name persisted.</summary>
    public event Action<string>? Saved;

    private readonly MacroStore _store;

    public StepEditorWindow() => InitializeComponent();

    public StepEditorWindow(StepEditorViewModel vm, MacroStore store) : this()
    {
        DataContext = vm;
        _store = store;
        Owner = Application.Current.MainWindow;
        vm.SavedOnce += fileName => Saved?.Invoke(fileName);
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
        Vm.TrySave((macro, file, reportActualName) =>
        {
            try
            {
                // Drafts (e.g. recorder review-before-save) are not backed by a
                // library file yet. Pass fileName = null so the store derives a
                // fresh, sanitized name from the macro id and CREATES a new file
                // instead of overwriting an existing macro with the same name.
                var actual = _store.Save(macro, Vm.IsNew ? null : file);
                reportActualName(actual);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();

    // -----------------------------------------------------------------
    // Drag & drop row reordering.
    //
    // WPF gotcha: DataGridCell captures the mouse on left-button down, so a
    // plain Mouse.Move += DoDragDrop never fires. The standard workaround is
    // to handle PreviewMouseLeftButtonDown (tunneling, runs before the cell
    // capture), remember the pressed row, and start the drag from
    // PreviewMouseMove once the pointer travels beyond SystemParameters
    // drag thresholds. Editing a cell still works: we only claim the mouse
    // when the press lands on a non-editing row and the user actually drags.
    // -----------------------------------------------------------------

    private Point _dragStart;
    private int _dragSourceIndex = -1;

    private void StepsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragSourceIndex = -1;
        if (Vm.IsReadOnly) return;

        // Commit an in-flight cell edit instead of starting a drag over it.
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is { IsEditing: true }) return;

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not null)
        {
            _dragStart = e.GetPosition(null);
            _dragSourceIndex = row.GetIndex();
        }
    }

    private void StepsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSourceIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = _dragSourceIndex;
        _dragSourceIndex = -1; // single-shot: avoid restarting after a failed drop

        // Snapshot the data object; the live collection may change mid-drag.
        DragDrop.DoDragDrop(StepsGrid, new DataObject(typeof(int), source), DragDropEffects.Move);
    }

    private void StepsGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(int)) && !Vm.IsReadOnly
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void StepsGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int))) return;
        var from = (int)e.Data.GetData(typeof(int));

        // Resolve the target row under the drop point; dropping below all rows
        // appends at the end.
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        var to = row is not null ? row.GetIndex() : Vm.Rows.Count - 1;

        Vm.MoveTo(from, to);
        StepsGrid.SelectedIndex = to;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T)
            current = VisualTreeHelper.GetParent(current);
        return current as T;
    }

    private StepEditorViewModel Vm => (StepEditorViewModel)DataContext;
}

/// <summary>Static list providers for XAML bindings (combo boxes).</summary>
public static class EditorLists
{
    public static IReadOnlyList<string> Kinds => ["key", "text", "mouseMove", "mouseClick", "mouseScroll", "delay"];
    public static IReadOnlyList<string> Buttons => ["left", "right", "middle"];
    public static IReadOnlyList<string> Scrolls => ["up", "down"];
}
