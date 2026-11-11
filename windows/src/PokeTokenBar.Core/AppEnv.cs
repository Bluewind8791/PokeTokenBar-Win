namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>AppEnv.swift</c> / <c>AppStatePaths.swift</c>.
/// macOS gates user-location writes on "running as the bundled .app". The Windows app sets
/// <see cref="IsInstalledApp"/> at startup instead, so tests never read or write the user's real state.
/// </summary>
public static class AppEnv
{
    /// <summary>Set by the WPF app on launch. False under tests.</summary>
    public static bool IsInstalledApp { get; set; }

    public static bool PersistsToUserLocation(string? injectedFilePath) =>
        injectedFilePath != null || IsInstalledApp;

    /// <summary>
    /// %LOCALAPPDATA%\PokeTokenBar — the counterpart of ~/Library/Application Support/PokeTokenBar.
    /// <c>PTB_STATE_DIR</c> overrides it, as on macOS.
    /// </summary>
    public static string StateDirectory()
    {
        var overrideDir = (Environment.GetEnvironmentVariable("PTB_STATE_DIR") ?? "").Trim();
        var dir = overrideDir.Length > 0
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeTokenBar");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>Port of <c>UsageEnvironment.swift</c>. Windows GUI apps inherit user environment
/// variables, so the macOS login-shell lookup is not needed.</summary>
public static class UsageEnvironment
{
    public static string? Value(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}

/// <summary>Port of <c>AppLog.swift</c>: a size-rotated line log, written only by the installed app.</summary>
public static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Message, DateTimeOffset At)> LastByKey = new();

    public static string LogFilePath => Path.Combine(AppEnv.StateDirectory(), "PokeTokenBar.log");

    public static void Write(string message)
    {
        if (!AppEnv.IsInstalledApp) return;
        lock (Gate)
        {
            try
            {
                var path = LogFilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = Path.ChangeExtension(path, "old.log");
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}] {message}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes only when the message for <paramref name="key"/> changed or an hour passed.</summary>
    public static void WriteIfChanged(string key, string message, TimeSpan? repeatAfter = null)
    {
        if (!AppEnv.IsInstalledApp) return;
        var now = DateTimeOffset.UtcNow;
        lock (Gate)
        {
            if (LastByKey.TryGetValue(key, out var last) && last.Message == message
                && now - last.At < (repeatAfter ?? TimeSpan.FromHours(1))) return;
            LastByKey[key] = (message, now);
        }
        Write(message);
    }
}
