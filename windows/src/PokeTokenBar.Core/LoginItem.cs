using Microsoft.Win32;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>LoginItem.swift</c>: launch at sign-in through the per-user
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value. The macOS launchd agent also relaunched the
/// app after a crash (KeepAlive); a Run value has no watchdog, so that half is not ported, nor the one-time
/// migration from the legacy macOS login item.
/// </summary>
public static class LoginItem
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PokeTokenBar";

    /// <summary>Whether the Run value exists and points at this executable.</summary>
    public static bool IsEnabled => IsEnabledAt(RunKeyPath, Environment.ProcessPath);

    /// <summary>On: writes the Run value for this executable. Off: removes it. Throws on registry failure
    /// (the caller shows it), like the Swift <c>setEnabled</c>.</summary>
    public static void SetEnabled(bool on) => SetEnabledAt(RunKeyPath, on, Environment.ProcessPath);

    /// <summary>The Run value's command: the quoted path, so a path with spaces is one argument.</summary>
    public static string Command(string executablePath) => $"\"{executablePath}\"";

    internal static bool IsEnabledAt(string keyPath, string? executablePath)
    {
        if (!OperatingSystem.IsWindows() || executablePath == null) return false;
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName) is string value
            && string.Equals(value, Command(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabledAt(string keyPath, bool on, string? executablePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Run key is Windows-only");
        if (on)
        {
            if (executablePath == null) throw new InvalidOperationException("The executable path is unknown");
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(ValueName, Command(executablePath));
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
