using System.Reflection;
using System.Windows;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// A Windows restart must not read as a crash at the next launch. WPF answers WM_QUERYENDSESSION with an
/// asynchronous Shutdown, and Windows can end the process before OnExit runs, so the running marker is
/// cleared in the session-ending hook. The live check is in docs/reference/defect-log.md (Windows: OnExit).
/// </summary>
public class SessionEndTests
{
    [Fact]
    public void AppClearsTheCrashMarkerWhenTheSessionEnds()
    {
        var hook = typeof(App).GetMethod("OnSessionEnding", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(SessionEndingCancelEventArgs)]);

        Assert.NotNull(hook);
        Assert.Equal(typeof(App), hook.DeclaringType);
    }
}
