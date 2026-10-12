using System.Windows;
using System.Windows.Interop;
using UniMouse.Wpf.Services;
using UniMouse.Wpf.ViewModels;

namespace UniMouse.Wpf.Views;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private readonly EmergencyStopHotkey? _stopHotkey;

    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

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

            // Route WM_HOTKEY messages to the hotkey object.
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
            viewModel.Dispose();
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
            _stopHotkey?.NotifyHotkey(wParam.ToInt32());
        return IntPtr.Zero;
    }
}
