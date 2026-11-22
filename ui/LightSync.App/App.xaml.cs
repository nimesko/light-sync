using Microsoft.UI.Xaml;

namespace LightSync.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        // Background work: logged; an unobserved task never takes the app down.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        // Last resort: the process is going away, but at least the reason is kept.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(e.IsTerminating ? "Fatal unhandled exception" : "Unhandled exception (AppDomain)", e.ExceptionObject);
    }

    /// <summary>
    /// An exception reached the XAML dispatcher. Log it; unless the process is in a state it can't recover
    /// from, keep running and show a notice instead of closing.
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        var fatal = ex is OutOfMemoryException or AccessViolationException or StackOverflowException
            or InvalidProgramException or BadImageFormatException or System.Runtime.InteropServices.SEHException;
        CrashLog.Write(fatal ? "Unhandled (fatal)" : "Unhandled (recovered)", $"{e.Message}\n{ex}");
        if (fatal)
        {
            return;
        }
        e.Handled = true;
        try
        {
            CrashLog.Notify?.Invoke("app", ex);
        }
        catch (Exception failure) when (Errors.Recoverable(failure))
        {
            // Logged already.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (!SingleInstance.Claim())
            {
                Exit();
                return;
            }
            _window = new MainWindow();
            var window = _window;
            SingleInstance.OnShowRequested(() => window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    window.BringToFront();
                }
                catch (Exception e) when (Errors.Recoverable(e))
                {
                    CrashLog.Write("BringToFront", e);
                }
            }));
            _window.Activate();
            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)))
            {
                _window.AppWindow.Hide();
            }
        }
        catch (Exception e) when (Errors.Recoverable(e))
        {
            CrashLog.Write("OnLaunched", e);
            throw;
        }
    }
}
