using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Counterpart of <c>AppDelegate</c> (PokeTokenBarApp.swift): a tray-only app with no main window.
/// </summary>
public partial class App : Application
{
    private SingleInstanceLock? _singleInstance;
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
        // A poketokenbar:// launch (the URL protocol's open command) carries the URL as the first argument.
        var agentUrl = AgentUrlChannel.UrlArgument(e.Args);
        // Set before the single-instance check: the log writes only for the installed app, and a yielding launch must
        // leave its line, or "the app does not start" cannot be told from a crash.
        AppEnv.IsInstalledApp = true;
        // A second launch yields to the instance already running (SingleInstance.swift).
        _singleInstance = SingleInstanceLock.TryAcquire();
        if (_singleInstance == null)
        {
            // An agent URL is handed over (dropped when it is not taken in time) and the process exits quietly.
            if (agentUrl != null)
            {
                if (!AgentUrlChannel.Send(AgentUrlChannel.CurrentPipeName(), agentUrl, AgentUrlChannel.SendTimeout))
                    AppLog.Write("agent urls: the running instance did not take a URL in time; dropped");
            }
            else AppLog.Write("duplicate instance: yielding to the instance already running");
            Shutdown();
            return;
        }
        Theme.Install(this);
        CrashReporter.ObserveUnhandledExceptions();
        CrashReporter.Install(UpdateChecker.EntryVersion());
        AppSettings.Current = AppSettings.ForInstalledApp();
        _app = new AppController();
        // No instance was running: this process is the app now and handles its own URL once the pet exists.
        if (agentUrl != null) _app.HandleAgentUrl(agentUrl);
    }

    /// <summary>
    /// Windows logoff, shutdown or restart (WM_QUERYENDSESSION). WPF answers it with an asynchronous
    /// <see cref="Application.Shutdown()"/>, and Windows may end the process right after WM_ENDSESSION,
    /// before that shutdown reaches <see cref="OnExit"/>. The clean mark therefore happens here,
    /// synchronously; otherwise every reboot is logged as a crash at the next launch.
    /// WPF shuts the app down after this even when another app vetoes the session end, so the mark holds.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (_app != null && !e.Cancel) CrashReporter.MarkClean(reason: $"session ending: {e.ReasonSessionEnding}");
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
