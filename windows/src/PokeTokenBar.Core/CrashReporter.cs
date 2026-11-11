namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>CrashReporter.swift</c>: every crash, kill or power loss leaves a trace in <see cref="AppLog"/>.
/// <list type="number">
/// <item>A running marker: written at launch, removed only on a clean exit. A marker found at the next
/// launch means the previous session ended abnormally, including deaths nothing in-process can catch
/// (Task Manager kill, native crash, power loss).</item>
/// <item>Unhandled managed exceptions are written when they happen. AppLog writes synchronously, so the
/// line is on disk before the process ends; the macOS signal-safe <c>crash.log</c> is not needed.</item>
/// </list>
/// </summary>
public static class CrashReporter
{
    public static string MarkerPath => Path.Combine(AppEnv.StateDirectory(), "PokeTokenBar.running");

    /// <summary>
    /// Call once at launch, after the single-instance check (a yielding second instance must not touch
    /// the running instance's marker). Returns true when the previous session did not exit cleanly.
    /// </summary>
    public static bool Install(string version, string? markerPath = null)
    {
        var path = markerPath ?? MarkerPath;
        var abnormal = File.Exists(path);
        if (abnormal)
            AppLog.Write("previous session did not exit cleanly (crash, kill or power loss). "
                + "For a stack, see Event Viewer > Windows Logs > Application (.NET Runtime / Application Error).");
        try { File.WriteAllText(path, ""); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        AppLog.Write($"launch: PokeTokenBar {version}");
        return abnormal;
    }

    /// <summary>Clean exit: removes the running marker so the next launch does not report a crash.</summary>
    public static void MarkClean(string? markerPath = null)
    {
        try { File.Delete(markerPath ?? MarkerPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        AppLog.Write("clean shutdown");
    }

    /// <summary>
    /// Logs exceptions nothing caught. A WPF dispatcher exception that is not handled reaches
    /// <see cref="AppDomain.UnhandledException"/> too, so one hook covers the UI thread and worker threads.
    /// An unobserved task exception does not end the process; it is logged once per distinct message.
    /// </summary>
    public static void ObserveUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write($"[CRASH] unhandled exception{(e.IsTerminating ? " (terminating)" : "")}: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            AppLog.WriteIfChanged("unobserved-task", $"unobserved task exception: {e.Exception}");
    }
}
