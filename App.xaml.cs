using System.Threading;
using System.Windows;

namespace OtterDesktop;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        var mutexName = e.Args.Contains("--test-instance", StringComparer.OrdinalIgnoreCase)
            ? $@"Local\OtterDesktop.Test.{Environment.ProcessId}"
            : @"Local\OtterDesktop.Singleton";
        _singleInstance = new Mutex(true, mutexName, out _ownsSingleInstance);
        if (!_ownsSingleInstance)
        {
            MessageBox.Show("Otter is already running.", "Otter Desktop", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstance)
        {
            _singleInstance?.ReleaseMutex();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
