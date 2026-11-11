using System.Net;
using System.Text;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of the statuspage cases of UsageStoreTests.swift.</summary>
public sealed class ProviderStatusTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ProviderStatusParse()
    {
        var s = StatuspageStatusProvider.Parse(B("""{"page":{"name":"Claude"},"status":{"indicator":"minor","description":"Partially Degraded Service"}}"""));
        Assert.Equal(ProviderStatusIndicator.Minor, s?.Indicator);
        Assert.Equal("Partially Degraded Service", s?.Description);
        Assert.True(s!.Indicator.HasIssue());
        var none = StatuspageStatusProvider.Parse(B("""{"status":{"indicator":"none","description":"All Systems Operational"}}"""));
        Assert.Equal(ProviderStatusIndicator.Operational, none?.Indicator);
        Assert.False(none!.Indicator.HasIssue());
        Assert.Equal(ProviderStatusIndicator.Unknown, StatuspageStatusProvider.Parse(B("""{"status":{"indicator":"potato"}}"""))?.Indicator);
        Assert.Null(StatuspageStatusProvider.Parse(B("not json")));
        Assert.Null(StatuspageStatusProvider.Parse(B("""{"nope":true}""")));
    }

    [Fact]
    public void TheComponentIgnoresAnUnrelatedGlobalIncident()
    {
        var data = B("""
            {"status":{"indicator":"minor","description":"Partial System Degradation"},
             "components":[{"name":"Claude.ai","status":"degraded_performance"},{"name":"Claude Code","status":"operational"}]}
            """);
        var status = StatuspageStatusProvider.ParseComponent(data, "claude code");
        Assert.Equal(ProviderStatusIndicator.Operational, status?.Indicator);
        Assert.Equal("Claude Code", status?.Description);
    }

    [Fact]
    public void ComponentDegradationMapsToIssue()
    {
        var data = B("""{"components":[{"name":"Claude Code","status":"degraded_performance"}]}""");
        var status = StatuspageStatusProvider.ParseComponent(data, "Claude Code");
        Assert.Equal(ProviderStatusIndicator.Minor, status?.Indicator);
        Assert.True(status!.Indicator.HasIssue());
        Assert.Null(StatuspageStatusProvider.ParseComponent(data, "Codex API"));
    }

    [Fact]
    public async Task FetchReadsClaudeCodeOnlyAndOmitsFailures()
    {
        var handler = new StubHandler(r => StubHandler.Json("""{"components":[{"name":"Claude Code","status":"major_outage"}]}"""));
        var result = await new StatuspageStatusProvider(handler.Client()).Fetch();
        Assert.Equal(["claude_code"], result.Keys);
        Assert.Equal(ProviderStatusIndicator.Critical, result["claude_code"].Indicator);
        Assert.Equal("status.claude.com", Assert.Single(handler.Requests).RequestUri!.Host);

        var failing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Empty(await new StatuspageStatusProvider(failing.Client()).Fetch());
        Assert.Empty(await new StatuspageStatusProvider().Fetch());   // gate closed under tests
    }
}

/// <summary>Port of NetworkReachabilityTests (availability is a bool on Windows).</summary>
public sealed class NetworkReachabilityTests
{
    [Fact]
    public void ColdStartDoesNotTriggerReconnect()
    {
        Assert.False(NetworkReachabilityMonitor.ShouldTriggerReconnect(null, true));
        Assert.False(NetworkReachabilityMonitor.ShouldTriggerReconnect(null, false));
    }

    [Fact]
    public void AlreadyOnlineDoesNotTriggerReconnect() =>
        Assert.False(NetworkReachabilityMonitor.ShouldTriggerReconnect(true, true));

    [Fact]
    public void OfflineTransitions()
    {
        Assert.False(NetworkReachabilityMonitor.ShouldTriggerReconnect(false, false));
        Assert.False(NetworkReachabilityMonitor.ShouldTriggerReconnect(true, false));
        Assert.True(NetworkReachabilityMonitor.ShouldTriggerReconnect(false, true));
    }

    [Fact]
    public void FlappingCooldownSuppression()
    {
        var baseDate = DateTimeOffset.FromUnixTimeSeconds(1000);
        var cooldown = TimeSpan.FromSeconds(5);
        Assert.True(NetworkReachabilityMonitor.ShouldTriggerReconnect(false, true, null, baseDate, cooldown));
        Assert.False(NetworkReachabilityMonitor.ShouldTriggerReconnect(false, true, baseDate, baseDate.AddSeconds(2), cooldown));
        Assert.True(NetworkReachabilityMonitor.ShouldTriggerReconnect(false, true, baseDate, baseDate.AddSeconds(6), cooldown));
    }

    [Fact]
    public void StatusUpdatesFireTheCallbackOnlyOnReconnect()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        var fired = 0;
        var monitor = new NetworkReachabilityMonitor(clock: () => now) { OnReconnected = () => fired++ };
        monitor.HandleStatusUpdate(true);    // first event: cold start
        monitor.HandleStatusUpdate(false);
        monitor.HandleStatusUpdate(true);
        Assert.Equal(1, fired);
        monitor.HandleStatusUpdate(false);
        monitor.HandleStatusUpdate(true);    // within the cooldown
        Assert.Equal(1, fired);
        now = now.AddSeconds(6);
        monitor.HandleStatusUpdate(false);
        monitor.HandleStatusUpdate(true);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void MonitorLifecycleAndConcurrentStartStop()
    {
        var monitor = new NetworkReachabilityMonitor();
        monitor.Start();
        monitor.Start();
        monitor.Stop();
        monitor.Stop();
        Parallel.For(0, 50, i => { if (i % 2 == 0) monitor.Start(); else monitor.Stop(); });
        monitor.Dispose();
    }
}

/// <summary>Port of UpdateCheckerTests (the Homebrew script cases are macOS-only).</summary>
public sealed class UpdateCheckerTests
{
    private const string ReleaseURL = "https://github.com/chattymin/PokeTokenBar/releases/tag/v2.5.5";

    [Theory]
    [InlineData("2.0.2", "2.0.1", true)]
    [InlineData("2.0.1", "2.0.1", false)]
    [InlineData("2.0.0", "2.0.1", false)]
    [InlineData("2.0.9", "2.1.0", false)]
    [InlineData("2.0.10", "2.0.9", true)]
    [InlineData("2.1.0", "2.0.9", true)]
    [InlineData("3.0.0", "2.9.9", true)]
    [InlineData("2.0.1", "2.0", true)]
    [InlineData("2.0", "2.0.0", false)]
    public void IsNewer(string a, string b, bool expected) => Assert.Equal(expected, UpdateChecker.IsNewer(a, b));

    [Fact]
    public async Task FailedCheckDoesNotStartTheCooldown()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var fetches = 0;
        var checker = new UpdateChecker("2.5.3", () => now, new AppSettings(null),
            () => { fetches++; return Task.FromResult<UpdateChecker.LatestRelease?>(null); });
        await checker.Check(TimeSpan.FromSeconds(1800));
        Assert.Equal(1, fetches);
        Assert.Null(checker.Available);
        now = now.AddSeconds(5);
        await checker.Check(TimeSpan.FromSeconds(1800));
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task SuccessfulCheckStartsTheCooldownAndAppliesTheRelease()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var fetches = 0;
        var checker = new UpdateChecker("2.5.3", () => now, new AppSettings(null), () =>
        {
            fetches++;
            return Task.FromResult<UpdateChecker.LatestRelease?>(new("v2.5.5", ReleaseURL));
        });
        await checker.Check(TimeSpan.FromSeconds(1800));
        Assert.Equal(1, fetches);
        Assert.Equal(new UpdateChecker.UpdateRelease("2.5.5", ReleaseURL), checker.Available);

        now = now.AddSeconds(60);
        await checker.Check(TimeSpan.FromSeconds(1800));
        Assert.Equal(1, fetches);

        now = now.AddSeconds(1800);
        await checker.Check(TimeSpan.FromSeconds(1800));
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task RejectedReleaseUrlDoesNotStartTheCooldown()
    {
        var fetches = 0;
        var checker = new UpdateChecker("2.5.3", () => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), new AppSettings(null), () =>
        {
            fetches++;
            return Task.FromResult<UpdateChecker.LatestRelease?>(new("v2.5.5", "http://evil.example/x"));
        });
        await checker.Check();
        Assert.Null(checker.Available);
        await checker.Check();
        Assert.Equal(2, fetches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("v2.5")]
    [InlineData("2.5.5.1")]
    [InlineData("v2..5")]
    [InlineData("2.5.x")]
    [InlineData("release-2.5.5")]
    [InlineData("v 2.5.5")]
    [InlineData("v2.6.0-beta.1")]
    [InlineData("2.6.0-rc1")]
    [InlineData("v2.6.0+build.5")]
    [InlineData("v2.6.0.beta")]
    [InlineData("v-2.6.0")]
    [InlineData("v1234567890.0.0")]
    [InlineData("v٢.٥.٥")]
    public async Task MalformedOrPrereleaseTagIsAFailedCheckAndDoesNotBlockTheNextOne(string bad)
    {
        var tag = bad;
        var fetches = 0;
        var checker = new UpdateChecker("2.5.3", () => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), new AppSettings(null), () =>
        {
            fetches++;
            return Task.FromResult<UpdateChecker.LatestRelease?>(new(tag, ReleaseURL));
        });
        await checker.Check();
        Assert.Null(checker.Available);
        Assert.Equal(UpdateChecker.NoticeKind.Current, checker.Notice.Kind);

        tag = "v2.5.5";
        await checker.Check();
        Assert.Equal(2, fetches);
        Assert.Equal("2.5.5", checker.Available?.Version);
    }

    [Fact]
    public void NormalizedReleaseVersion()
    {
        Assert.Equal("2.5.5", UpdateChecker.NormalizedReleaseVersion("v2.5.5"));
        Assert.Equal("2.5.10", UpdateChecker.NormalizedReleaseVersion("2.5.10"));
        Assert.Equal("2.5.10", UpdateChecker.NormalizedReleaseVersion(" V2.05.10\n"));
        Assert.Null(UpdateChecker.NormalizedReleaseVersion("v2.6.0-beta.1"));
        Assert.Null(UpdateChecker.NormalizedReleaseVersion("v2.5"));
        Assert.Null(UpdateChecker.NormalizedReleaseVersion("v1234567890.0.0"));
    }

    [Fact]
    public async Task OverlappingChecksShareOneFetch()
    {
        var fetches = 0;
        var release = new TaskCompletionSource();
        var checker = new UpdateChecker("2.5.3", settings: new AppSettings(null), fetchLatest: async () =>
        {
            fetches++;
            if (fetches == 1) await release.Task;
            return null;
        });
        var first = checker.Check();
        await checker.Check();
        Assert.Equal(1, fetches);

        release.SetResult();
        await first;
        await checker.Check();
        Assert.Equal(2, fetches);
    }

    [Fact]
    public void SkippedReleaseStaysVisibleAndANewerOneReturnsToTheBanner()
    {
        var settings = new AppSettings(null);
        var checker = new UpdateChecker("2.5.3", settings: settings);
        checker.Consider("2.5.4", "https://github.com/chattymin/PokeTokenBar/releases/tag/v2.5.4");
        Assert.Equal("2.5.4", checker.Available?.Version);
        Assert.Null(checker.Skipped);
        Assert.Equal(new UpdateChecker.SettingsNotice(UpdateChecker.NoticeKind.Offer, "2.5.4"), checker.Notice);

        checker.SkipCurrent();
        Assert.Null(checker.Available);
        Assert.Equal("2.5.4", checker.Skipped?.Version);
        Assert.Equal(new UpdateChecker.SettingsNotice(UpdateChecker.NoticeKind.Skipped, "2.5.4"), checker.Notice);
        Assert.Equal("2.5.4", settings.GetString("skippedUpdateVersion"));

        checker.Consider("v2.5.4", "https://github.com/chattymin/PokeTokenBar/releases/tag/v2.5.4");
        Assert.Null(checker.Available);
        Assert.Equal(UpdateChecker.NoticeKind.Skipped, checker.Notice.Kind);

        checker.Consider("2.5.5", ReleaseURL);
        Assert.Equal("2.5.5", checker.Available?.Version);
        Assert.Null(checker.Skipped);

        checker.Consider("2.5.3", "https://github.com/chattymin/PokeTokenBar/releases/tag/v2.5.3");
        Assert.Equal(UpdateChecker.NoticeKind.Current, checker.Notice.Kind);
    }

    [Fact]
    public void ShowAgainRestoresTheBannerAndUpdateUsesTheSkippedRelease()
    {
        var settings = new AppSettings(null);
        var checker = new UpdateChecker("2.5.3", settings: settings);
        const string url = "https://github.com/chattymin/PokeTokenBar/releases/tag/v2.5.4";
        checker.Consider("2.5.4", url);
        checker.SkipCurrent();
        Assert.Equal(url, checker.UpdateTarget?.Url);

        checker.ShowSkippedAgain();
        Assert.Equal("2.5.4", checker.Available?.Version);
        Assert.Null(checker.Skipped);
        Assert.Null(settings.GetString("skippedUpdateVersion"));
    }

    /// <summary>Releases are shared with macOS: only one with a Windows asset is offered.</summary>
    [Fact]
    public async Task OnlyAReleaseWithAWindowsAssetCounts()
    {
        const string macOnly = """{"tag_name":"v2.6.0","html_url":"https://github.com/chattymin/PokeTokenBar/releases/tag/v2.6.0","assets":[{"name":"PokeTokenBar.zip"}]}""";
        const string withWindows = """{"tag_name":"v2.6.0","html_url":"https://github.com/chattymin/PokeTokenBar/releases/tag/v2.6.0","assets":[{"name":"PokeTokenBar.zip"},{"name":"PokeTokenBar-Windows-x64.zip"}]}""";
        Assert.Null(UpdateChecker.ParseLatestRelease(Encoding.UTF8.GetBytes(macOnly)));
        Assert.Equal(new UpdateChecker.LatestRelease("v2.6.0", "https://github.com/chattymin/PokeTokenBar/releases/tag/v2.6.0"),
            UpdateChecker.ParseLatestRelease(Encoding.UTF8.GetBytes(withWindows)));
        Assert.True(UpdateChecker.IsWindowsAsset("PokeTokenBar-2.6.0-win-x64.msi"));
        Assert.False(UpdateChecker.IsWindowsAsset("PokeTokenBar-Windows.txt"));

        var handler = new StubHandler(_ => StubHandler.Json(withWindows));
        Assert.Equal("v2.6.0", (await UpdateChecker.LiveLatestRelease(UpdateChecker.Repo, handler.Client()))?.Tag);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/repos/chattymin/PokeTokenBar/releases/latest", request.RequestUri!.AbsolutePath);
        Assert.True(request.Headers.Contains("User-Agent"));
        Assert.Null(await UpdateChecker.LiveLatestRelease(UpdateChecker.Repo));   // gate closed under tests
    }
}
