using System.Windows;
using UniMouse.Wpf.ViewModels;

namespace UniMouse.Wpf.Views;

/// <summary>
/// Modal recorder dialog. Owns the RecorderViewModel lifetime: hooks are
/// guaranteed to come off when the window closes, even mid-recording.
/// </summary>
public partial class RecorderWindow : Window
{
    private readonly RecorderViewModel _vm;

    public RecorderWindow(RecorderViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        // After Stop() hands the captured steps to the review gate, close this
        // dialog so the step editor can take focus.
        vm.ReviewRequested += _ => Dispatcher.BeginInvoke(Close);

        Closed += (_, _) => _vm.Dispose(); // idempotent — safe if already stopped
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
