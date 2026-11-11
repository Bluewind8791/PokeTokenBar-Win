using System.Threading;
using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Counterpart of <c>AppDelegate</c> (PokeTokenBarApp.swift): a tray-only app with no main window.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppController? _app;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--render")
        {
            // Dev tool: offscreen render with sample data, no tray, no user state.
            var code = RenderHarness.Run(e.Args);
            Shutdown(code);
            return;
        }
        Theme.Install(this);
        // A second launch yields to the instance already running (SingleInstance.swift).
        _singleInstance = new Mutex(true, @"Local\PokeTokenBar.SingleInstance", out var first);
        if (!first)
        {
            AppLog.Write("duplicate instance: yielding to the instance already running");
            Shutdown();
            return;
        }
        AppEnv.IsInstalledApp = true;
        CrashReporter.ObserveUnhandledExceptions();
        CrashReporter.Install(UpdateChecker.EntryVersion());
        AppSettings.Current = AppSettings.ForInstalledApp();
        _app = new AppController();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _app?.Dispose();
        // Only the instance that installed the marker clears it; a yielding duplicate never had one.
        if (_app != null) CrashReporter.MarkClean();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
