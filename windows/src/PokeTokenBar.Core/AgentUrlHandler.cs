using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace PokeTokenBar.Core;

/// <summary>
/// The Windows half of macOS's <c>CFBundleURLTypes</c> entry: the per-user <c>poketokenbar</c> URL protocol under
/// <c>HKCU\Software\Classes</c>, whose open command starts this executable with the URL as its only argument.
/// Refreshed at every start like the Run value of <see cref="LoginItem"/>: written only when missing or pointing at
/// another executable.
/// </summary>
public static class AgentUrlProtocol
{
    public const string KeyPath = @"Software\Classes\" + AgentActivityEvent.Scheme;
    private const string CommandSubKey = @"shell\open\command";

    /// <summary>The open command: both quoted, so a path with spaces and the URL are one argument each.</summary>
    public static string Command(string executablePath) => $"\"{executablePath}\" \"%1\"";

    /// <summary>Registers this executable when the protocol is missing or stale. True when it wrote. Throws on
    /// registry failure (the caller logs it).</summary>
    public static bool EnsureRegistered() => EnsureRegisteredAt(KeyPath, Environment.ProcessPath);

    /// <param name="keyPath">The protocol key under HKCU; tests pass a throwaway key.</param>
    internal static bool IsRegisteredAt(string keyPath, string? executablePath)
    {
        if (!OperatingSystem.IsWindows() || executablePath == null) return false;
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        using var command = key?.OpenSubKey(CommandSubKey);
        return key?.GetValue("URL Protocol") != null
            && command?.GetValue(null) is string value
            && string.Equals(value, Command(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool EnsureRegisteredAt(string keyPath, string? executablePath)
    {
        if (!OperatingSystem.IsWindows() || executablePath == null || IsRegisteredAt(keyPath, executablePath)) return false;
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(null, "URL:PokeTokenBar agent activity");
        key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(CommandSubKey);
        command.SetValue(null, Command(executablePath));
        return true;
    }
}

/// <summary>
/// Hands a <c>poketokenbar://</c> URL from the process the protocol launch started to the instance already running
/// (macOS delivers it to the running app as a GetURL Apple Event). The second process writes the URL to a named pipe
/// and exits; the running instance treats the bytes as untrusted: at most <see cref="MaxPayloadBytes"/> of valid UTF-8,
/// read within <see cref="ReadTimeout"/>, then parsed by <see cref="AgentActivityEvent.Parse"/>. Anything else is
/// dropped. The pipe is per user session (like the single-instance mutex) and current-user only.
/// </summary>
public static class AgentUrlChannel
{
    /// <summary>A real agent URL is under 1 KB (an 80-character message, percent-encoded); the cap bounds what
    /// another process can make the app read.</summary>
    public const int MaxPayloadBytes = 4096;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Named after the single-instance mutex (<c>Local\PokeTokenBar.SingleInstance</c>); pipe names are
    /// machine-wide, so the session id stands in for <c>Local\</c>.</summary>
    public static string PipeName(int sessionId) => $"PokeTokenBar.SingleInstance.{sessionId}";

    public static string CurrentPipeName()
    {
        using var process = Process.GetCurrentProcess();
        return PipeName(process.SessionId);
    }

    /// <summary>The URL a protocol launch passes (<c>"exe" "%1"</c>): the first argument, when it has our scheme.
    /// Only the first argument is read, so a URL cannot smuggle in other switches.</summary>
    public static string? UrlArgument(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].StartsWith(AgentActivityEvent.Scheme + ":", StringComparison.OrdinalIgnoreCase) ? args[0] : null;

    /// <summary>Untrusted text from the pipe or the command line → an event; null when oversized or not an agent URL.</summary>
    public static AgentActivityEvent? Accept(string? payload) =>
        payload == null || Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes ? null : AgentActivityEvent.Parse(payload);

    /// <summary>Writes the URL to the running instance. False when nothing answers in time or the URL is oversized.</summary>
    public static bool Send(string pipeName, string url, TimeSpan timeout)
    {
        var bytes = Encoding.UTF8.GetBytes(url);
        if (bytes.Length > MaxPayloadBytes) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads one payload until the writer closes: null when it is over <see cref="MaxPayloadBytes"/>, not
    /// valid UTF-8, or not complete within <paramref name="timeout"/>.</summary>
    public static async Task<string?> ReadPayload(Stream stream, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var buffer = new byte[MaxPayloadBytes + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            cancellation.ThrowIfCancellationRequested();
            return null;
        }
        if (length > MaxPayloadBytes) return null;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>
/// The running instance's end of <see cref="AgentUrlChannel"/>: one client at a time, each payload validated, accepted
/// events handed to <c>onEvent</c> on a thread-pool thread (the caller moves them to the UI thread). Waits on the pipe
/// with no timer; stops on dispose.
/// </summary>
public sealed class AgentUrlListener : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public Task Running { get; }

    public AgentUrlListener(string pipeName, Action<AgentActivityEvent> onEvent) =>
        Running = Task.Run(() => Listen(pipeName, onEvent, _stop.Token));

    private static async Task Listen(string pipeName, Action<AgentActivityEvent> onEvent, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"agent urls: cannot listen on the pipe: {e.Message}");
                return;
            }
            await using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                    var payload = await AgentUrlChannel.ReadPayload(server, AgentUrlChannel.ReadTimeout, stop).ConfigureAwait(false);
                    if (AgentUrlChannel.Accept(payload) is { } e) onEvent(e);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException)
                {
                    // The client went away mid-connection; serve the next one.
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { Running.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _stop.Dispose();
    }
}
