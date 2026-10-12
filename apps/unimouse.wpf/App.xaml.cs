using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UniMouse.Wpf.Services;
using UniMouse.Wpf.ViewModels;
using UniMouse.Wpf.Views;

namespace UniMouse.Wpf;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    // Held for the whole process lifetime — disposing it early would let a
    // second instance start while the first is still running.
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard: two playback engines racing each other is a UX/safety hazard.
        _singleInstanceMutex = new Mutex(true, @"Local\UniMouseMacroAI.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("UniMouse Macro AI is already running.", "UniMouse",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var services = new ServiceCollection();

        var apiBase = new Uri(Environment.GetEnvironmentVariable("UNIMOUSE_SERVICE_URL")
                              ?? "http://127.0.0.1:8777");
        var apiKey = Environment.GetEnvironmentVariable("UNIMOUSE_API_KEY"); // optional shared secret

        services.AddSingleton(new MacroStore());
        services.AddSingleton(_ => new QwenApiClient(apiBase, apiKey));
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        Services = services.BuildServiceProvider();

        var window = Services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Best-effort: never leave synthetic keys pressed after the app closes.
        try
        {
            var vm = Services.GetService<MainViewModel>();
            vm?.Dispose();
        }
        catch { /* shutdown must not throw */ }
        base.OnExit(e);
    }
}
