using System.Threading;
using System.Windows;

namespace EatonUsbController.App;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance enforcement — Local prefix is session-scoped (safe for user-session tray app)
        try
        {
            _mutex = new Mutex(true, @"Local\EatonUsbController_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                Shutdown();
                Environment.Exit(0);
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Another session/account holds the mutex — treat as already running
            Shutdown();
            Environment.Exit(0);
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

