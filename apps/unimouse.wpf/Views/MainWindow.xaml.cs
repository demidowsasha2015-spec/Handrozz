using System.Windows;
using System.Windows.Interop;
using UniMouse.Wpf.Services;
using UniMouse.Wpf.ViewModels;

namespace UniMouse.Wpf.Views;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private readonly EmergencyStopHotkey? _stopHotkey;
    private HotkeyManager? _macroHotkeys;
    private readonly MacroStore _store;

    public MainWindow(MainViewModel viewModel, MacroStore store)
    {
        DataContext = viewModel;
        _store = store;
        InitializeComponent();
        viewModel.EditRequested += OpenEditor;

        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _stopHotkey = new EmergencyStopHotkey(hwnd);
            if (!_stopHotkey.Register())
            {
                viewModel.StatusText = "Warning: emergency-stop hotkey (Ctrl+Alt+F12) is already taken.";
                return;
            }
            _stopHotkey.Triggered += viewModel.EmergencyStop;

            // Per-macro START hotkeys (documented RegisterHotKey only).
            _macroHotkeys = new HotkeyManager(hwnd);
            foreach (var m in viewModel.Macros)
            {
                if (m.Definition.StartHotkey is { } hk)
                {
                    var captured = m;
                    _macroHotkeys.TryRegister(hk.KeyCode, hk.Modifiers, () => viewModel.PlayById(captured.Id));
                }
            }

            // Route WM_HOTKEY messages to the hotkey objects.
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };

        Closed += (_, _) =>
        {
            if (_stopHotkey is not null)
            {
                var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                src?.RemoveHook(WndProc);
                _stopHotkey.Dispose();
            }
            _macroHotkeys?.Dispose();
            viewModel.Dispose();
        };
    }

    private void MacroList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedMacro is { } m)
            vm.EditSelectedCommand.Execute(null);
    }

    private void OpenEditor(MacroListItem item)
    {
        var vm = (MainViewModel)DataContext;
        var editorVm = new StepEditorViewModel(item.Definition, item.FileName);
        var win = new StepEditorWindow(editorVm, _store) { Owner = this };
        win.Saved += () => vm.NotifyMacroSaved(item.FileName);
        win.ShowDialog();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            _stopHotkey?.NotifyHotkey(wParam.ToInt32());
            _macroHotkeys?.NotifyHotkey(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }
}
