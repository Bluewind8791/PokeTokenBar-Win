using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

public class CrashReporterTests
{
    [Fact]
    public void FirstLaunchIsNotReportedAsAbnormalAndLeavesAMarker()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "PokeTokenBar.running");

        Assert.False(CrashReporter.Install("1.0.0", marker));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void LaunchAfterASessionWithoutCleanExitIsAbnormal()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "PokeTokenBar.running");

        CrashReporter.Install("1.0.0", marker);
        // No MarkClean: the session was killed or crashed.
        Assert.True(CrashReporter.Install("1.0.0", marker));
    }

    [Fact]
    public void LaunchAfterACleanExitIsNotAbnormal()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "PokeTokenBar.running");

        CrashReporter.Install("1.0.0", marker);
        CrashReporter.MarkClean(marker);

        Assert.False(File.Exists(marker));
        Assert.False(CrashReporter.Install("1.0.0", marker));
    }

    [Fact]
    public void SessionEndMarkThenExitMarkIsStillAClean()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "PokeTokenBar.running");

        CrashReporter.Install("1.0.0", marker);
        // Windows restart: the session-end mark, then OnExit when the process lives long enough.
        CrashReporter.MarkClean(marker, reason: "session ending: Shutdown");
        CrashReporter.MarkClean(marker);

        Assert.False(CrashReporter.Install("1.0.0", marker));
    }
}
