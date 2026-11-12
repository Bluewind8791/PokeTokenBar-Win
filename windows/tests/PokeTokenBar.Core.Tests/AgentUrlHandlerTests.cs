using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Windows counterpart of AgentURLHandlerTests.swift (#457). macOS reads the URL out of the GetURL Apple Event; Windows
/// registers a URL protocol whose open command starts the exe with the URL, and the second process hands it to the
/// running instance over a named pipe. Registry tests use a throwaway HKCU key and pipe tests a unique pipe name, so
/// neither touches the real protocol key nor a running app.
/// </summary>
public class AgentUrlHandlerTests
{
    // MARK: Protocol registration

    private const string TestKeyParent = @"Software\PokeTokenBarTests.AgentUrl";

    [Fact]
    public void CommandQuotesThePathAndTheUrl() =>
        Assert.Equal("\"C:\\Program Files\\PokeTokenBar\\PokeTokenBar.exe\" \"%1\"",
            AgentUrlProtocol.Command(@"C:\Program Files\PokeTokenBar\PokeTokenBar.exe"));

    [Fact]
    public void TheRealKeyIsThePerUserClassesKey() =>
        Assert.Equal(@"Software\Classes\poketokenbar", AgentUrlProtocol.KeyPath);

    /// <summary>Written when missing or pointing at another exe, left alone when it already points here.</summary>
    [Fact]
    public void RegistersOnlyWhenMissingOrStale()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Not under Software\PokeTokenBarTests: LoginItemTests deletes that whole tree and may run at the same time.
        var root = TestKeyParent + @"\" + Guid.NewGuid().ToString("N");
        var keyPath = root + @"\poketokenbar";
        const string exe = @"C:\Apps\PokeTokenBar.exe";
        try
        {
            Assert.False(AgentUrlProtocol.IsRegisteredAt(keyPath, exe));
            Assert.True(AgentUrlProtocol.EnsureRegisteredAt(keyPath, exe));   // missing → written
            Assert.True(AgentUrlProtocol.IsRegisteredAt(keyPath, exe));
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Equal("", key.GetValue("URL Protocol"));
                using var command = key.OpenSubKey(@"shell\open\command")!;
                Assert.Equal(AgentUrlProtocol.Command(exe), command.GetValue(null));
            }

            // Up to date → not written again: an edited description survives.
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
                key.SetValue(null, "edited");
            Assert.False(AgentUrlProtocol.EnsureRegisteredAt(keyPath, exe));
            Assert.False(AgentUrlProtocol.EnsureRegisteredAt(keyPath, exe.ToUpperInvariant()));   // paths compare case-insensitively
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
                Assert.Equal("edited", key.GetValue(null));

            // Another copy of the app → rewritten to this one.
            const string moved = @"D:\Elsewhere\PokeTokenBar.exe";
            Assert.False(AgentUrlProtocol.IsRegisteredAt(keyPath, moved));
            Assert.True(AgentUrlProtocol.EnsureRegisteredAt(keyPath, moved));
            Assert.True(AgentUrlProtocol.IsRegisteredAt(keyPath, moved));
            Assert.False(AgentUrlProtocol.IsRegisteredAt(keyPath, exe));

            // A command without the URL Protocol marker is not a protocol: rewritten.
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
                key.DeleteValue("URL Protocol");
            Assert.False(AgentUrlProtocol.IsRegisteredAt(keyPath, moved));
            Assert.True(AgentUrlProtocol.EnsureRegisteredAt(keyPath, moved));

            Assert.False(AgentUrlProtocol.EnsureRegisteredAt(keyPath, null));   // unknown exe path: nothing to register
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            try { Registry.CurrentUser.DeleteSubKey(TestKeyParent, throwOnMissingSubKey: false); }
            catch (InvalidOperationException) { }   // another run's key is still there
        }
    }

    // MARK: Command line

    [Fact]
    public void OnlyTheFirstArgumentIsTakenAsTheUrl()
    {
        Assert.Equal("poketokenbar://agent?state=done", AgentUrlChannel.UrlArgument(["poketokenbar://agent?state=done"]));
        Assert.Equal("POKETOKENBAR://agent?state=done", AgentUrlChannel.UrlArgument(["POKETOKENBAR://agent?state=done"]));
        Assert.Null(AgentUrlChannel.UrlArgument([]));
        Assert.Null(AgentUrlChannel.UrlArgument(["--render", "poketokenbar://agent?state=done"]));
        Assert.Null(AgentUrlChannel.UrlArgument(["https://agent?state=done"]));
        // A quote inside "%1" splits the URL into several arguments; the rest is never read as a switch.
        Assert.Equal("poketokenbar://agent?x=", AgentUrlChannel.UrlArgument(["poketokenbar://agent?x=", "--render", "home", "out.png"]));
    }

    [Fact]
    public void AcceptDropsOversizedAndForeignInput()
    {
        Assert.Equal(AgentActivity.Done("ok"), AgentUrlChannel.Accept("poketokenbar://agent?state=done&message=ok")?.Activity);
        Assert.Null(AgentUrlChannel.Accept(null));
        Assert.Null(AgentUrlChannel.Accept("poketokenbar://settings?state=done"));
        var padded = "poketokenbar://agent?state=done&x=" + new string('a', AgentUrlChannel.MaxPayloadBytes);
        Assert.NotNull(AgentActivityEvent.Parse(padded));   // precondition: only the size makes it fail
        Assert.Null(AgentUrlChannel.Accept(padded));
        var fits = "poketokenbar://agent?state=done&x=";
        fits += new string('a', AgentUrlChannel.MaxPayloadBytes - fits.Length);
        Assert.NotNull(AgentUrlChannel.Accept(fits));   // exactly at the cap is accepted
    }

    /// <summary>The URL the PowerShell hook builds for a folder with spaces, Hangul and parentheses (its
    /// <c>[Uri]::EscapeDataString</c> keeps the parentheses) comes back as the folder name.</summary>
    [Fact]
    public void TheHookScriptsUrlCarriesTheFolderName()
    {
        var e = AgentUrlChannel.Accept("poketokenbar://agent?state=done&label=%ED%8F%AC%EC%BC%93%20%EC%95%B1%20(beta)");
        Assert.Equal(AgentActivity.Done(null), e?.Activity);
        Assert.Equal("포켓 앱 (beta)", e?.Label);
        Assert.Null(AgentUrlChannel.Accept("poketokenbar://agent?state=waiting&label=")?.Label);
    }

    // MARK: Pipe

    private static string UniquePipe() => "PokeTokenBarTests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void PipeNameIsPerSession()
    {
        Assert.Equal("PokeTokenBar.SingleInstance.1", AgentUrlChannel.PipeName(1));
        Assert.NotEqual(AgentUrlChannel.PipeName(1), AgentUrlChannel.PipeName(2));
        Assert.StartsWith("PokeTokenBar.SingleInstance.", AgentUrlChannel.CurrentPipeName());
    }

    [Fact]
    public async Task ReadPayloadRejectsOversizedInvalidAndSlowInput()
    {
        Assert.Equal("poketokenbar://agent?state=done",
            await AgentUrlChannel.ReadPayload(new MemoryStream(Encoding.UTF8.GetBytes("poketokenbar://agent?state=done")), TimeSpan.FromSeconds(1)));
        Assert.NotNull(await AgentUrlChannel.ReadPayload(new MemoryStream(new byte[AgentUrlChannel.MaxPayloadBytes]), TimeSpan.FromSeconds(1)));
        Assert.Null(await AgentUrlChannel.ReadPayload(new MemoryStream(new byte[AgentUrlChannel.MaxPayloadBytes + 1]), TimeSpan.FromSeconds(1)));
        Assert.Null(await AgentUrlChannel.ReadPayload(new MemoryStream([0x70, 0xC3, 0x28]), TimeSpan.FromSeconds(1)));   // invalid UTF-8
        // A writer that connects and never finishes is cut off at the timeout.
        var name = UniquePipe();
        await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(2000));
        // The pipe has no buffer: the write completes as the reader takes the bytes.
        var write = client.WriteAsync(Encoding.UTF8.GetBytes("poketokenbar://agent?state=done")).AsTask();
        var started = DateTime.UtcNow;
        Assert.Null(await AgentUrlChannel.ReadPayload(server, TimeSpan.FromMilliseconds(300)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        await write;
    }

    [Fact]
    public void SendFailsQuicklyWithoutARunningInstance()
    {
        var started = DateTime.UtcNow;
        Assert.False(AgentUrlChannel.Send(UniquePipe(), "poketokenbar://agent?state=done", TimeSpan.FromMilliseconds(200)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.False(AgentUrlChannel.Send(UniquePipe(), "poketokenbar://agent?state=done&x=" + new string('a', AgentUrlChannel.MaxPayloadBytes),
            TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>End to end over a real pipe: the listener delivers valid URLs and drops oversized, invalid and foreign
    /// payloads without stopping.</summary>
    [Fact]
    public async Task ListenerDeliversValidUrlsAndDropsTheRest()
    {
        var name = UniquePipe();
        var received = new List<AgentActivityEvent>();
        var gate = new SemaphoreSlim(0);
        using (new AgentUrlListener(name, e => { lock (received) received.Add(e); gate.Release(); }))
        {
            var timeout = TimeSpan.FromSeconds(5);
            Assert.True(AgentUrlChannel.Send(name, "poketokenbar://agent?state=waiting&label=web&message=Pick%20one", timeout));
            Assert.True(await gate.WaitAsync(timeout));

            // Oversized (written raw, past Send's own check), invalid UTF-8 and foreign payloads are dropped.
            await RawSend(name, Encoding.UTF8.GetBytes("poketokenbar://agent?state=done&x=" + new string('a', AgentUrlChannel.MaxPayloadBytes)));
            await RawSend(name, [0x70, 0xC3, 0x28]);
            Assert.True(AgentUrlChannel.Send(name, "poketokenbar://settings?state=done", timeout));
            Assert.True(AgentUrlChannel.Send(name, "not a url", timeout));

            // The loop is still serving.
            Assert.True(AgentUrlChannel.Send(name, "poketokenbar://agent?state=done", timeout));
            Assert.True(await gate.WaitAsync(timeout));
            Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(300)));
        }
        Assert.Equal(
            [new AgentActivityEvent(AgentActivity.Waiting("Pick one"), "web"), new AgentActivityEvent(AgentActivity.Done(null), null)],
            received);
    }

    [Fact]
    public async Task DisposingTheListenerStopsIt()
    {
        var name = UniquePipe();
        var listener = new AgentUrlListener(name, _ => { });
        Assert.True(AgentUrlChannel.Send(name, "poketokenbar://agent?state=done", TimeSpan.FromSeconds(5)));
        listener.Dispose();
        Assert.True(listener.Running.IsCompleted);
        Assert.False(AgentUrlChannel.Send(name, "poketokenbar://agent?state=done", TimeSpan.FromMilliseconds(200)));
        await Task.CompletedTask;
    }

    private static async Task RawSend(string name, byte[] bytes)
    {
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        try { await client.WriteAsync(bytes); }
        catch (IOException) { }   // the server may hang up once it has read past the cap
    }
}
