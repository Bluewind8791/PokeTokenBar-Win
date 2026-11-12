using PokeTokenBar.Core;
using static PokeTokenBar.Core.Tests.StoreFixtures;

namespace PokeTokenBar.Core.Tests;

// Port of UsageStoreTests.swift: the refresh pipeline and the derived values, with injected fakes (no real
// logs, credentials or network). Codex / Antigravity / Cursor limit tests are not ported (the Windows port
// follows Claude Code only); tests that use other provider ids only as generic usage sources stay.
// Not ported either: the disableKeychainAccess tests (no Keychain on Windows), the statuspage parse tests
// (ProviderStatusAndUpdateTests.cs) and the OAuthCredentialData test (OAuthLimitsTests.cs).

public sealed class UsageStoreTests
{
    private readonly AppSettings _settings = new(null);

    private UsageStore MakeStore(IReadOnlyList<IUsageProvider> providers, LimitStatus? claude = null,
        ManualScheduler? scheduler = null, Func<bool>? displaysAsleep = null) =>
        Stores.Make(_settings, providers, new FakeClaudeLimits(claude), scheduler: scheduler, displaysAsleep: displaysAsleep);

    private static FakeUsageProvider Claude(long? tokens, double cost = 0) =>
        new("claude_code", "Claude Code", tokens is { } t ? TodayDaily(t, cost) : null);

    private static FakeUsageProvider Other(string id, long? tokens, bool reportsCost = true) =>
        new(id, id, tokens is { } t ? TodayDaily(t) : null, reportsCost);

    private static ProviderEnrichment Periods(long week, long month, BlockUsage? block = null, bool blocksOK = true,
        List<DailyUsage>? monthDaily = null, DateTimeOffset? lastUsage = null) => new()
    {
        ActiveBlock = block, BlocksOK = blocksOK,
        WeekTotal = new PeriodUsage("w", week, 0), MonthTotal = new PeriodUsage("m", month, 0),
        MonthDaily = monthDaily, PeriodsOK = true, LastUsage = lastUsage,
    };

    // MARK: Refresh coalescing

    /// <summary>[Regression] A refresh during an in-flight one runs once afterwards instead of being dropped.</summary>
    [Fact]
    public void ConcurrentRefreshIsCoalescedNotDropped() => MainActor.Run(async () =>
    {
        var p = new GatedUsageProvider(TodayDaily(1_000));
        var store = MakeStore([p]);
        var a = store.Refresh(scheduleEmptyRetry: false);
        Assert.True(await MainActor.WaitUntil(() => p.DailyCalls >= 1));
        Assert.True(store.IsRefreshing, "A must be in flight");
        await store.Refresh(scheduleEmptyRetry: false);   // B: queued, returns at once
        p.Release();
        await a;
        Assert.True(await MainActor.WaitUntil(() => p.DailyCalls >= 2), "the overlapping refresh must run once afterwards");
    });

    // MARK: Display sleep polling suspension (#350)

    [Fact]
    public void SuspendedPollingProbeResumesWhenDisplaysAreAwake() => MainActor.Run(async () =>
    {
        var asleep = true;
        var store = MakeStore([Claude(1_000)], displaysAsleep: () => asleep);
        store.SuspendPolling();
        Assert.True(store.IsPollingSuspended);
        Assert.False(store.IsPollingTimerScheduled, "no polling timer while suspended (battery)");
        Assert.True(store.IsSuspendedProbeScheduled, "the recovery probe runs while suspended");

        asleep = false;   // the display is on, but no wake event came
        await store.RunSuspendedPollingProbe();

        Assert.False(store.IsPollingSuspended);
        Assert.True(store.IsPollingTimerScheduled);
        Assert.False(store.IsSuspendedProbeScheduled);
        Assert.Equal(1_000, store.TodayTotalTokens);
    });

    [Fact]
    public void SuspendedPollingProbeStaysSuspendedWhileDisplaysSleep() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_000)], displaysAsleep: () => true);
        store.SuspendPolling();
        await store.RunSuspendedPollingProbe();
        Assert.True(store.IsPollingSuspended);
        Assert.False(store.IsPollingTimerScheduled);
        Assert.True(store.IsSuspendedProbeScheduled);
    });

    [Fact]
    public void SystemWakeResumesSuspendedPollingWhenDisplaysAreAwake() => MainActor.Run(async () =>
    {
        var asleep = true;
        var store = MakeStore([Claude(1_000)], displaysAsleep: () => asleep);
        store.SuspendPolling();
        asleep = false;
        await store.HandleSystemWake();
        Assert.False(store.IsPollingSuspended);
        Assert.True(store.IsPollingTimerScheduled);
        Assert.Equal(1_000, store.TodayTotalTokens);
    });

    [Fact]
    public void RefreshWhileDisplaysAwakeClearsAStaleSuspension() => MainActor.Run(async () =>
    {
        var asleep = true;
        var store = MakeStore([Claude(1_000)], displaysAsleep: () => asleep);
        store.SuspendPolling();
        asleep = false;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(store.IsPollingSuspended);
        Assert.True(store.IsPollingTimerScheduled);
        Assert.False(store.IsSuspendedProbeScheduled);
    });

    [Fact]
    public void RefreshWhileDisplaysAsleepKeepsPollingSuspended() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_000)], displaysAsleep: () => true);
        store.SuspendPolling();
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.IsPollingSuspended);
        Assert.False(store.IsPollingTimerScheduled);
    });

    /// <summary>Windows host path: ResumePolling (display on) restores the timer and refreshes once.</summary>
    [Fact]
    public void ResumePollingRestoresTheTimerAndRefreshes() => MainActor.Run(async () =>
    {
        var scheduler = new ManualScheduler();
        var store = MakeStore([Claude(1_000)], scheduler: scheduler);
        store.SuspendPolling();
        store.ResumePolling();
        Assert.False(store.IsPollingSuspended);
        Assert.True(store.IsPollingTimerScheduled);
        Assert.True(await MainActor.WaitUntil(() => store.TodayTotalTokens == 1_000));
        Assert.Single(scheduler.Active, e => e.Repeats && e.Interval == TimeSpan.FromSeconds(120));
    });

    [Fact]
    public void RefreshIntervalReschedulesThePollingTimer() => MainActor.Run(() =>
    {
        var scheduler = new ManualScheduler();
        var store = MakeStore([], scheduler: scheduler);
        Assert.Single(scheduler.Active, e => e.Repeats && e.Interval == TimeSpan.FromSeconds(120));
        store.RefreshInterval = 0;   // manual
        Assert.False(store.IsPollingTimerScheduled);
        Assert.DoesNotContain(scheduler.Active, e => e.Repeats);
        store.RefreshInterval = 300;
        Assert.Single(scheduler.Active, e => e.Repeats && e.Interval == TimeSpan.FromSeconds(300));
    });

    // MARK: Manual vs automatic limits path

    /// <summary>Automatic polls use allowKeychainPrompt=false; only the user's button passes true (on Windows it
    /// makes the OAuth path re-read the credentials file instead of its in-memory token).</summary>
    [Fact]
    public void AutoRefreshUsesNoPromptPathManualUsesPromptPath() => MainActor.Run(async () =>
    {
        var limits = new RecordingClaudeLimits(ClaudeLimits(10));
        var store = Stores.Make(_settings, [Claude(1_000)], limits);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.NotEmpty(limits.PromptFlags);
        Assert.All(limits.PromptFlags, Assert.False);

        await store.RefreshLimitTokenFromKeychain();
        Assert.True(limits.PromptFlags[^1]);
    });

    // MARK: Floating pet settings

    [Fact]
    public void FloatingPetSettingsDefaultAndPersistence()
    {
        var store = MakeStore([]);
        Assert.False(store.FloatingPetEnabled);
        Assert.Equal(96, store.FloatingPetSize);
        Assert.True(store.FloatingPetBubbleAlerts);
        Assert.True(store.FloatingPetAgentActivity);   // inert until an agent hook sends a URL
        Assert.True(store.FloatingPetAgentRun);   // macOS behaviour by default

        store.FloatingPetEnabled = true;
        store.FloatingPetSize = 144;
        store.FloatingPetBubbleAlerts = false;
        store.FloatingPetAgentActivity = false;
        store.FloatingPetAgentRun = false;

        var reloaded = MakeStore([]);
        Assert.True(reloaded.FloatingPetEnabled);
        Assert.Equal(144, reloaded.FloatingPetSize);
        Assert.False(reloaded.FloatingPetBubbleAlerts);
        Assert.False(reloaded.FloatingPetAgentActivity);
        Assert.False(reloaded.FloatingPetAgentRun);
    }

    /// <summary>Always facing back is a display preference: default off, persisted like the floating pet settings.</summary>
    [Fact]
    public void AlwaysFacesBackDefaultsOffAndPersists()
    {
        var store = MakeStore([Claude(1_000)]);
        Assert.False(store.AlwaysFacesBack);
        store.AlwaysFacesBack = true;
        Assert.True(MakeStore([Claude(1_000)]).AlwaysFacesBack);   // persists across restarts
    }

    /// <summary>The store mirrors the preference into the companion with every update.</summary>
    [Fact]
    public void UpdateCompanionMirrorsAlwaysFacesBack() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_000)]);
        var companion = StateFiles.Store(Lines.NoEvo);
        await companion.Hatch(20);
        store.AlwaysFacesBack = true;
        store.UpdateCompanion(companion);
        Assert.True(companion.FacesBack);
        store.AlwaysFacesBack = false;
        store.UpdateCompanion(companion);
        Assert.False(companion.FacesBack);
    });

    /// <summary>
    /// Sulk input: unknown only before the first complete refresh; afterwards unreadable providers are ignored (not a
    /// veto) and the newest <c>LastUsage</c> among the rest decides.
    /// </summary>
    [Fact]
    public void TimeSinceLastUsageIsUnknownOnlyBeforeTheFirstRefresh() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var older = Other("codex", 1);
        var newer = Claude(1);
        static ProviderEnrichment Enrichment(DateTimeOffset? lastUsage, bool periodsOK = true) => new()
        {
            BlocksOK = true, WeekTotal = new PeriodUsage("w", 1, 0), MonthTotal = new PeriodUsage("m", 1, 0),
            MonthDaily = [TodayDaily(1)], PeriodsOK = periodsOK, LastUsage = lastUsage,
        };
        older.Enrichment = Enrichment(now.AddHours(-50));
        newer.Enrichment = Enrichment(now.AddHours(-30));
        var store = MakeStore([older, newer]);
        Assert.Null(store.TimeSinceLastUsage(now));   // nothing scanned yet: unknown, never idle

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(30 * 3600, store.TimeSinceLastUsage(now)!.Value.TotalSeconds, 0);

        // The newer provider becomes unreadable: its carried-over time is ignored, the older one decides.
        newer.Enrichment = Enrichment(null, periodsOK: false);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(50 * 3600, store.TimeSinceLastUsage(now)!.Value.TotalSeconds, 0);   // no veto

        // One provider fails while the other reported 4 h ago: that is idle enough to sulk.
        older.Enrichment = Enrichment(now.AddHours(-4));
        await store.Refresh(scheduleEmptyRetry: false);
        var idle = store.TimeSinceLastUsage(now)!.Value;
        Assert.Equal(4 * 3600, idle.TotalSeconds, 0);
        Assert.True(idle >= PokemonBalance.SulkAfterIdle);

        // Every provider fails after the first refresh: nothing reports, so it is long idle.
        older.Enrichment = Enrichment(null, periodsOK: false);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(LocalUsageReader.RecentUseWindow, store.TimeSinceLastUsage(now));

        // A complete scan that found no usage at all means the same.
        older.Enrichment = Enrichment(null);
        newer.Enrichment = Enrichment(null);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(LocalUsageReader.RecentUseWindow, store.TimeSinceLastUsage(now));
    });

    [Fact]
    public void BubbleAlertPicksHighestSeverityThenUtilization()
    {
        var warnLow = new LimitAlert("a", "A", false, 81);
        var warnHigh = new LimitAlert("b", "B", false, 90);
        var critLow = new LimitAlert("c", "C", true, 95);
        var critHigh = new LimitAlert("d", "D", true, 99);
        Assert.Null(UsageStore.BubbleAlert([]));
        Assert.Equal(warnHigh, UsageStore.BubbleAlert([warnLow, warnHigh]));
        Assert.Equal(critLow, UsageStore.BubbleAlert([warnHigh, critLow]));
        Assert.Equal(critHigh, UsageStore.BubbleAlert([critLow, warnHigh, critHigh]));
    }

    [Fact]
    public void BubbleDismissUsesTTL()
    {
        var shown = DateTimeOffset.FromUnixTimeSeconds(1_000);
        Assert.False(UsageStore.ShouldDismissBubble(shown, shown.AddSeconds(5.9)));
        Assert.True(UsageStore.ShouldDismissBubble(shown, shown.AddSeconds(6)));
        Assert.True(UsageStore.ShouldDismissBubble(shown, shown.AddSeconds(6), TimeSpan.FromSeconds(6)));
        Assert.False(UsageStore.ShouldDismissBubble(shown, shown.AddSeconds(3), TimeSpan.FromSeconds(6)));
    }

    /// <summary>(#56 display) The hover tooltip must not show Claude's limit when Claude was unused today.</summary>
    [Fact]
    public void HighestLimitUtilizationIgnoresClaudeUnusedToday() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(null), Other("future_tool", 500_000)], ClaudeLimits(90));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Null(store.HighestLimitUtilization);

        var used = MakeStore([Claude(1_000)], ClaudeLimits(90));
        await used.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(90, used.HighestLimitUtilization!.Value, 2);
    });

    // MARK: Provider status

    private UsageStore MakeStatusStore(FakeStatusProvider stub) =>
        Stores.Make(_settings, [Claude(1_000)], status: stub);

    [Fact]
    public void ProviderStatusKeepsPreviousOnFailure() => MainActor.Run(async () =>
    {
        var stub = new FakeStatusProvider(new() { ["claude_code"] = new ProviderStatus(ProviderStatusIndicator.Minor, "deg") });
        var store = MakeStatusStore(stub);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(ProviderStatusIndicator.Minor, store.ProviderStatus("claude_code")?.Indicator);
        stub.Result = [];
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(ProviderStatusIndicator.Minor, store.ProviderStatus("claude_code")?.Indicator);
        stub.Result = new() { ["claude_code"] = new ProviderStatus(ProviderStatusIndicator.Operational, "ok") };
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(ProviderStatusIndicator.Operational, store.ProviderStatus("claude_code")?.Indicator);
    });

    [Fact]
    public void ProviderStatusDisabledClears() => MainActor.Run(async () =>
    {
        var stub = new FakeStatusProvider(new() { ["claude_code"] = new ProviderStatus(ProviderStatusIndicator.Major, "x") });
        var store = MakeStatusStore(stub);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(ProviderStatusIndicator.Major, store.ProviderStatus("claude_code")?.Indicator);
        store.StatusChecksEnabled = false;
        Assert.Null(store.ProviderStatus("claude_code"));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.Statuses);
    });

    // MARK: Limit alerts — edge triggered

    private static List<LimitAlert> Eval(Dictionary<string, int> tiers, double warn, double crit,
        params (string Key, string Name, double Utilization)[] windows) =>
        UsageStore.EvaluateLimitAlerts(windows, null, warn, crit, tiers);

    /// <summary>(user report) A warning at 80% repeated at 80·81·84·90·94: now once per tier.</summary>
    [Fact]
    public void LimitAlertFiresOncePerTierNotEveryRefresh()
    {
        var tiers = new Dictionary<string, int>();
        List<LimitAlert> E(double u) => Eval(tiers, 80, 95, ("주간", "주간", u));
        Assert.Equal([new LimitAlert("주간", "주간", false, 80)], E(80));
        Assert.Empty(E(81));
        Assert.Empty(E(84));
        Assert.Empty(E(90));
        Assert.Empty(E(94));
        Assert.Equal([new LimitAlert("주간", "주간", true, 95)], E(95));
        Assert.Empty(E(96));
        Assert.Empty(E(99));
    }

    [Fact]
    public void LimitAlertDoesNotRefireOnRepeatedSameUtilization()
    {
        var tiers = new Dictionary<string, int>();
        Assert.Single(Eval(tiers, 80, 95, ("주간", "주간", 90)));
        Assert.Empty(Eval(tiers, 80, 95, ("주간", "주간", 90)));
        Assert.Empty(Eval(tiers, 80, 95, ("주간", "주간", 90)));
    }

    [Fact]
    public void LimitAlertRearmsAfterDroppingBelowWarn()
    {
        var tiers = new Dictionary<string, int>();
        Eval(tiers, 80, 95, ("주간", "주간", 82));
        Assert.Empty(Eval(tiers, 80, 95, ("주간", "주간", 40)));
        Assert.Equal([new LimitAlert("주간", "주간", false, 85)], Eval(tiers, 80, 95, ("주간", "주간", 85)));
    }

    [Fact]
    public void LimitAlertTracksWindowsIndependently()
    {
        var tiers = new Dictionary<string, int>();
        Assert.Equal([new LimitAlert("5시간", "5시간", true, 96)],
            Eval(tiers, 80, 95, ("5시간", "5시간", 96), ("주간", "주간", 50)));
        Assert.Equal([new LimitAlert("주간", "주간", false, 82)],
            Eval(tiers, 80, 95, ("5시간", "5시간", 97), ("주간", "주간", 82)));
    }

    /// <summary>(#61) Two windows with the same display name are tracked by key, each alerting once.</summary>
    [Fact]
    public void LimitAlertKeyDisambiguatesDuplicateDisplayNames()
    {
        var tiers = new Dictionary<string, int>();
        var alerts = Eval(tiers, 80, 95, ("claude.sevenDayOpus", "Claude Weekly Opus", 90),
            ("claude.scoped.weekly_scoped.Opus.0", "Claude Weekly Opus", 92));
        Assert.Equal(2, alerts.Count);
        Assert.Equal(1, tiers["claude.sevenDayOpus"]);
        Assert.Equal(1, tiers["claude.scoped.weekly_scoped.Opus.0"]);
        Assert.Empty(Eval(tiers, 80, 95, ("claude.sevenDayOpus", "Claude Weekly Opus", 91),
            ("claude.scoped.weekly_scoped.Opus.0", "Claude Weekly Opus", 93)));
    }

    /// <summary>Windows seam: an alert crossing raises one toast event (critical with sound) and one bubble, which
    /// the TTL timer clears.</summary>
    [Fact]
    public void RefreshRaisesLimitNotificationsAndBubble() => MainActor.Run(async () =>
    {
        var scheduler = new ManualScheduler();
        var limits = new FakeClaudeLimits(ClaudeLimits(96));
        var store = Stores.Make(_settings, [Claude(1_000)], limits, scheduler: scheduler);
        store.LocalizationLanguage = AppLanguage.En;
        store.FloatingPetEnabled = true;
        var posted = new List<LimitNotification>();
        store.LimitNotificationPosted += posted.Add;

        await store.Refresh(scheduleEmptyRetry: false);
        var notification = Assert.Single(posted);
        Assert.Equal("claude.fiveHour-critical", notification.Identifier);
        Assert.Equal(new L(AppLanguage.En).NotifCritical, notification.Title);
        Assert.Equal(new L(AppLanguage.En).NotifBody(new L(AppLanguage.En).ClaudeFiveHour, "96%"), notification.Body);
        Assert.True(notification.PlaySound);
        Assert.Equal("claude.fiveHour", store.CurrentBubbleAlert?.Key);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Single(posted);   // same tier: no repeat

        scheduler.FireAfter(UsageStore.BubbleTTL);
        Assert.Null(store.CurrentBubbleAlert);

        store.LimitNotifications = false;
        limits.Status = ClaudeLimits(10);
        await store.Refresh(scheduleEmptyRetry: false);   // re-arms
        limits.Status = ClaudeLimits(85);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Single(posted);   // toasts off
        Assert.False(store.CurrentBubbleAlert?.IsCritical);   // the bubble has its own gate
    });

    // MARK: Menu bar lines

    /// <summary>(user report) A provider unused today must not show its limit in the menu bar.</summary>
    [Fact]
    public void MenuBarLimitHidesClaudeUnusedToday() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(null), Other("future_tool", 1_000_000)], ClaudeLimits(40, "2099-01-01T00:00:00Z"));
        store.ShowTokensInMenu = false;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.DoesNotContain("Claude", store.MenuTitle);
    });

    [Fact]
    public void MenuBarLimitShowsClaudeUsedToday() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(500_000)], ClaudeLimits(85));
        store.ShowTokensInMenu = false;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Contains("Claude", store.MenuTitle);
    });

    /// <summary>A plan without a 5-hour session limit, as oauth/usage sends it: <c>five_hour</c> is null and
    /// <c>limits[]</c> has no session entry. <paramref name="fiveHour"/> adds a session window back.</summary>
    private static LimitStatus ClaudeWeekly(double utilization, TimeSpan resetsIn, DateTimeOffset now, double? fiveHour = null)
    {
        var weeklyReset = Iso8601Parser.Format(now + resetsIn);
        var session = fiveHour is { } f
            ? $"{{\"utilization\":{f},\"resets_at\":\"{Iso8601Parser.Format(now + TimeSpan.FromHours(2.5))}\"}}"
            : "null";
        return LimitStatus.Decode(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"five_hour\":{session},\"seven_day\":{{\"utilization\":{utilization},\"resets_at\":\"{weeklyReset}\"}}," +
            $"\"limits\":[{{\"kind\":\"weekly_all\",\"group\":\"weekly\",\"percent\":{utilization},\"resets_at\":\"{weeklyReset}\"}}]}}"));
    }

    /// <summary>Plans without a session limit (Ultra) send <c>five_hour: null</c>: the menu bar falls back to the
    /// weekly window instead of dropping Claude, and the pace comes from the 7-day span (#464).</summary>
    [Fact]
    public void MenuShowsTheWeeklyLimitWhenThePlanHasNoSessionLimit() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var store = MakeStore([Claude(1_000)], ClaudeWeekly(66, TimeSpan.FromDays(3.5), now));
        store.ShowTokensInMenu = false;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        await store.Refresh(scheduleEmptyRetry: false);

        var parts = store.MenuLimitParts(now);
        Assert.Equal(["Claude 66%"], parts.Select(p => p.Text));
        Assert.Equal(0.5, parts[0].Pace!.Value, 2);
        Assert.Equal("Claude 66%", store.MenuTitle);

        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Remaining;
        Assert.Equal(["Claude 34%"], store.MenuLimitParts(now).Select(p => p.Text));
    });

    /// <summary>With both windows, the menu bar keeps showing the 5-hour session.</summary>
    [Fact]
    public void MenuPrefersTheSessionLimitOverTheWeeklyOne() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var store = MakeStore([Claude(1_000)], ClaudeWeekly(66, TimeSpan.FromDays(3.5), now, fiveHour: 40));
        store.ShowTokensInMenu = false;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(["Claude 40%"], store.MenuLimitParts(now).Select(p => p.Text));
    });

    [Fact]
    public void HeadlineWindowPicksTheSessionThenTheWeeklyWindow()
    {
        var now = DateTimeOffset.Now;
        var both = ClaudeWeekly(66, TimeSpan.FromHours(1), now, fiveHour: 40).HeadlineWindow!.Value;
        Assert.Equal(40, both.Window.Utilization);
        Assert.Equal(LimitWindowSpan.FiveHour, both.Span);

        var weeklyOnly = ClaudeWeekly(66, TimeSpan.FromHours(1), now).HeadlineWindow!.Value;
        Assert.Equal(66, weeklyOnly.Window.Utilization);
        Assert.Equal(LimitWindowSpan.SevenDay, weeklyOnly.Span);

        var empty = LimitStatus.Decode("{\"five_hour\":null,\"seven_day\":null}"u8);
        Assert.Null(empty.HeadlineWindow);
    }

    [Fact]
    public void MenuLinesStacksUsageAndLimitsSeparately() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_200_000)], ClaudeLimits(40, "2099-01-01T00:00:00Z"));
        store.ShowTokensInMenu = true;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(2, store.MenuLines.Count);
        Assert.Contains("Claude", store.MenuLines[1]);
    });

    [Fact]
    public void MenuLinesTokenAndCostStackVertically() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_200_000, 3.45)]);
        store.ShowTokensInMenu = true;
        store.ShowCostInMenu = true;
        store.ShowLimitInMenu = false;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(2, store.MenuLines.Count);
        Assert.DoesNotContain(" · ", store.MenuLines[0]);
        Assert.Contains("$", store.MenuLines[1]);
    });

    /// <summary>Every toggle combination: up to two items → one line each; all three → tokens · cost + limit.</summary>
    [Fact]
    public void MenuLinesAllCombinations() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_200_000, 3.45)], ClaudeLimits(40, "2099-01-01T00:00:00Z"));
        await store.Refresh(scheduleEmptyRetry: false);
        IReadOnlyList<string> Lines(bool t, bool c, bool l)
        {
            store.ShowTokensInMenu = t;
            store.ShowCostInMenu = c;
            store.ShowLimitInMenu = l;
            return store.MenuLines;
        }
        Assert.Empty(Lines(false, false, false));
        Assert.Single(Lines(true, false, false));
        Assert.Single(Lines(false, true, false));
        Assert.Single(Lines(false, false, true));
        var tc = Lines(true, true, false);
        Assert.Equal(2, tc.Count);
        Assert.DoesNotContain(" · ", tc[0]);
        Assert.Contains("$", tc[1]);
        Assert.Equal(2, Lines(true, false, true).Count);
        Assert.Equal(2, Lines(false, true, true).Count);
        var three = Lines(true, true, true);
        Assert.Equal(2, three.Count);
        Assert.Contains(" · ", three[0]);
        Assert.DoesNotContain("Claude", three[0]);
        Assert.Contains("Claude", three[1]);
    });

    [Fact]
    public void MenuLinesBeforeFirstRefreshShowADash()
    {
        Assert.Equal(["—"], MakeStore([]).MenuLines);
    }

    /// <summary>
    /// Port of <c>testMenuLinesWithGrowthAllCombinations</c> (#388), Windows layout: every combination keeps the usage
    /// and limit lines exactly as without growth, and the growth is its own last line. The tray tooltip is cut from
    /// the end, so the growth is cut before the lines it was added next to (macOS joins it into the usage line to stay
    /// within two menu-bar lines).
    /// </summary>
    [Fact]
    public void MenuLinesWithGrowthAllCombinations() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_200_000, 3.45)], ClaudeLimits(40, "2099-01-01T00:00:00Z"));
        await store.Refresh(scheduleEmptyRetry: false);
        var token = TokenFormatter.Compact(store.TodayTotalTokens);
        var cost = store.TodayUsageCost.Text(new L(store.LocalizationLanguage), compact: true);
        for (var mask = 0; mask < 16; mask++)
        {
            store.ShowTokensInMenu = (mask & 1) != 0;
            store.ShowCostInMenu = (mask & 2) != 0;
            store.ShowLimitInMenu = (mask & 4) != 0;
            var growth = (mask & 8) != 0 ? "Growth 42%" : null;
            var lines = store.MenuLinesWithGrowth(growth);
            var expected = new List<string>();
            if (store.ShowTokensInMenu) expected.Add(token);
            if (store.ShowCostInMenu) expected.Add(cost);
            if (store.ShowLimitInMenu) expected.Add("Claude 40%");
            if (growth != null) expected.Add(growth);
            Assert.Equal(expected, lines.SelectMany(l => l.Split(" · ")).ToList());
            Assert.Equal(store.MenuLines.Concat(growth != null ? [growth] : Array.Empty<string>()), lines);
            if (growth != null) Assert.Equal(growth, lines[^1]);
        }
    });

    /// <summary>Port of <c>testGrowthSettingPersistsAndSavedGrowthIsAvailableBeforeUsageLoads</c>.</summary>
    [Fact]
    public void GrowthSettingPersistsAndSavedGrowthIsAvailableBeforeUsageLoads()
    {
        var store = MakeStore([]);
        Assert.False(store.ShowGrowthInMenu);
        Assert.Equal(["—"], store.MenuLines);
        Assert.Equal(["Hatch 42%"], store.MenuLinesWithGrowth("Hatch 42%"));
        store.ShowGrowthInMenu = true;
        Assert.True(MakeStore([]).ShowGrowthInMenu);
        store.ShowGrowthInMenu = false;
        Assert.False(MakeStore([]).ShowGrowthInMenu);
    }

    // MARK: Limit display mode

    [Fact]
    public void LimitDisplayPercentModes()
    {
        Assert.Equal(40, UsageStore.DisplayPercent(40, LimitPace.LimitDisplayMode.Used));
        Assert.Equal(60, UsageStore.DisplayPercent(40, LimitPace.LimitDisplayMode.Remaining));
        Assert.Equal(100, UsageStore.DisplayPercent(0, LimitPace.LimitDisplayMode.Remaining));
        Assert.Equal(0, UsageStore.DisplayPercent(100, LimitPace.LimitDisplayMode.Remaining));
        Assert.Equal(0, UsageStore.DisplayPercent(120, LimitPace.LimitDisplayMode.Remaining));
        Assert.Equal(59.5, UsageStore.DisplayPercent(40.5, LimitPace.LimitDisplayMode.Remaining));
    }

    [Fact]
    public void MenuBarLimitRemainingModeInvertsPercents() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(10_000_000)], ClaudeLimits(40, "2099-01-01T00:00:00Z"));
        store.ShowTokensInMenu = false;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Remaining;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Contains("Claude 60%", store.MenuTitle);
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Used;
        Assert.Contains("Claude 40%", store.MenuTitle);
    });

    [Fact]
    public void LimitDisplayModePersistsAcrossRestart()
    {
        var s1 = MakeStore([]);
        Assert.Equal(LimitPace.LimitDisplayMode.Used, s1.LimitDisplayMode);
        s1.LimitDisplayMode = LimitPace.LimitDisplayMode.Remaining;
        Assert.Equal("remaining", _settings.GetString("limitDisplayMode"));
        Assert.Equal(LimitPace.LimitDisplayMode.Remaining, MakeStore([]).LimitDisplayMode);
    }

    [Fact]
    public void AnimationQualityPersistsAcrossRestart()
    {
        var s1 = MakeStore([]);
        Assert.Equal(AnimationQuality.PowerSaver, s1.AnimationQuality);
        s1.AnimationQuality = AnimationQuality.Smooth;
        Assert.Equal("smooth", _settings.GetString("animationQuality"));
        Assert.Equal(AnimationQuality.Smooth, MakeStore([]).AnimationQuality);
    }

    /// <summary>A 0 floor would be native fps, an idle-wakeup regression. Low power slows to the power-saver floor.</summary>
    [Fact]
    public void NoAnimationQualityPresetDisablesTheCap()
    {
        foreach (var quality in Enum.GetValues<AnimationQuality>())
        {
            Assert.True(quality.FrameFloor() > 0, quality.ToString());
            Assert.Equal(quality, AnimationQualities.FromRawValue(quality.RawValue()));
            Assert.Equal(Math.Max(quality.FrameFloor(), 0.4), quality.EffectiveFrameFloor(lowPower: true));
            Assert.Equal(quality.FrameFloor(), quality.EffectiveFrameFloor(lowPower: false));
        }
    }

    // MARK: Aggregation

    [Fact]
    public void AggregatesTodayTokensAcrossProviders() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(100_000_000), Other("codex", 50_000_000)]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(150_000_000, store.TodayTotalTokens);
        Assert.Equal(new Dictionary<string, long> { ["claude_code"] = 100_000_000, ["codex"] = 50_000_000 },
            store.TodayTokensByProvider);
        Assert.NotNull(store.LastUpdated);
        Assert.Null(store.LastErrorDescription);
    });

    /// <summary>[#115] A new provider's snapshot reaches today / week / month / burn.</summary>
    [Fact]
    public void NewProviderSnapshotFlowsThroughTodayWeekMonthAndBurn() => MainActor.Run(async () =>
    {
        var kiro = Other("kiro", 50_000_000, reportsCost: false);
        kiro.Enrichment = Periods(90_000_000, 300_000_000, Block(200_000));
        var store = MakeStore([Claude(100_000_000), kiro]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(150_000_000, store.TodayTotalTokens);
        Assert.Equal(50_000_000, store.TodayTokensByProvider["kiro"]);
        Assert.Equal(90_000_000, store.WeekTotalTokens);
        Assert.Equal(300_000_000, store.MonthTotalTokens);
        Assert.Equal(BurnTier.Fast, store.BurnTier);
        Assert.Equal("kiro", store.Snapshot("kiro")?.ProviderID);
    });

    [Fact]
    public void OtherProviderOnlyWhenClaudeHasNoData() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(null), Other("codex", 50_000_000)]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(50_000_000, store.TodayTotalTokens);
        Assert.True(store.HasUsageData);
        Assert.Equal("codex", Assert.Single(store.Snapshots).ProviderID);
        Assert.Equal(new Dictionary<string, long> { ["codex"] = 50_000_000 }, store.TodayTokensByProvider);
    });

    [Fact]
    public void StaleDatedSnapshotExcludedFromTodayTotal() => MainActor.Run(async () =>
    {
        var old = new FakeUsageProvider("codex", "Codex", new DailyUsage("2000-01-01", 0, 0, 0, 0, 999, 0));
        var store = MakeStore([Claude(100_000_000), old]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(100_000_000, store.TodayTotalTokens);
        Assert.Equal(new Dictionary<string, long> { ["claude_code"] = 100_000_000 }, store.TodayTokensByProvider);
    });

    /// <summary>[Regression] A provider that drops out keeps the others' values, and returns under its own id.</summary>
    [Fact]
    public void RefreshPreservesProviderIdentityAcrossPartialSnapshotLossAndRecovery() => MainActor.Run(async () =>
    {
        var claude = Claude(1_000);
        var codex = Other("codex", 500);
        var store = MakeStore([claude, codex]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(new Dictionary<string, long> { ["claude_code"] = 1_000, ["codex"] = 500 }, store.TodayTokensByProvider);

        codex.Daily = null;
        claude.Daily = TodayDaily(1_200);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(new Dictionary<string, long> { ["claude_code"] = 1_200 }, store.TodayTokensByProvider);
        Assert.Equal(["claude_code"], store.Snapshots.Select(s => s.ProviderID));

        codex.Daily = TodayDaily(700);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(new Dictionary<string, long> { ["claude_code"] = 1_200, ["codex"] = 700 }, store.TodayTokensByProvider);
    });

    [Fact]
    public void ProviderFailureKeepsPreviousTodayValue() => MainActor.Run(async () =>
    {
        var claude = Claude(100_000_000);
        var store = MakeStore([claude]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(100_000_000, store.TodayTotalTokens);
        claude.FailDaily = true;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(100_000_000, store.TodayTotalTokens);
        Assert.NotNull(store.LastErrorDescription);
    });

    // MARK: Menu bar title

    [Fact]
    public void MenuTitleReflectsToggles() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(100_000_000, 12.5)]);
        store.ShowTokensInMenu = true;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = false;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal("100M", store.MenuTitle);
        store.ShowCostInMenu = true;
        Assert.Equal("100M · $12.5", store.MenuTitle);
    });

    [Fact]
    public void MenuTitleShowsLimitPercent() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(10_000_000)], ClaudeLimits(42));
        store.ShowTokensInMenu = false;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal("Claude 42%", store.MenuTitle);
    });

    // MARK: Limit warning

    [Fact]
    public void LimitWarningWhenClaudeOverCritical() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(10_000_000)], ClaudeLimits(96));
        store.CritThreshold = 95;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.IsLimitWarning);
        Assert.NotNull(store.Limits);
    });

    [Fact]
    public void NoLimitWarningWhenUnderCritical() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(10_000_000)], ClaudeLimits(50));
        store.CritThreshold = 95;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(store.IsLimitWarning);
    });

    /// <summary>The weekly window at 100% warns even when the 5h window is calm.</summary>
    [Fact]
    public void LimitWarningFromWeeklyWindow() => MainActor.Run(async () =>
    {
        var limits = ClaudeLimits(10);
        limits.SevenDay = new LimitWindow { Utilization = 100 };
        var store = MakeStore([Claude(10_000_000)], limits);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.IsLimitWarning);
    });

    [Fact]
    public void LimitWarningFromForecastAtFullUtilization() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(10_000_000)], ClaudeLimits(100, "2099-01-01T00:00:00Z"));
        store.CritThreshold = 101;   // the threshold branch is off: only the forecast can warn
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.IsLimitWarning);
    });

    [Fact]
    public void ForecastDepletionGuards()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        Assert.Null(UsageStore.ForecastDepletion(1_000_000, 50_000, 4, now));     // < 5% unstable
        Assert.Null(UsageStore.ForecastDepletion(1_000_000, 9_999, 50, now));     // slow burn
        Assert.Null(UsageStore.ForecastDepletion(0, 50_000, 50, now));
        // 1M tokens at 50% → 1M left at 50k/min = 20 minutes.
        Assert.Equal(now.AddMinutes(20), UsageStore.ForecastDepletion(1_000_000, 50_000, 50, now));
    }

    /// <summary>The Settings sliders overlap: moving one past the other pushes the other along.</summary>
    [Fact]
    public void WarningStaysBelowCritical()
    {
        var store = MakeStore([]);
        store.CritThreshold = 80;
        store.WarnThreshold = 95;
        Assert.Equal(95, store.WarnThreshold);
        Assert.Equal(100, store.CritThreshold);

        store.CritThreshold = 80;
        Assert.Equal(80, store.CritThreshold);
        Assert.Equal(75, store.WarnThreshold);

        store.WarnThreshold = 60;
        Assert.Equal(80, store.CritThreshold);
        Assert.Equal(60, _settings.GetDouble("warnThreshold"));
        Assert.Equal(80, _settings.GetDouble("critThreshold"));
    }

    [Fact]
    public void StoredThresholdsLoadInOrder()
    {
        _settings.Set("warnThreshold", 95.0);
        _settings.Set("critThreshold", 80.0);
        var store = MakeStore([]);
        Assert.True(store.WarnThreshold < store.CritThreshold);
        Assert.Equal(80, store.CritThreshold);
        Assert.Equal(75, store.WarnThreshold);
        var alerts = UsageStore.EvaluateLimitAlerts([("k", "5h", 77)], null, store.WarnThreshold, store.CritThreshold, []);
        Assert.Equal([false], alerts.Select(a => a.IsCritical));
    }

    [Fact]
    public void SettingsDefaultsMatchMacOS()
    {
        var store = MakeStore([]);
        Assert.Equal(120, store.RefreshInterval);
        Assert.Equal(80, store.WarnThreshold);
        Assert.Equal(95, store.CritThreshold);
        Assert.True(store.ShowTokensInMenu);
        Assert.False(store.ShowCostInMenu);
        Assert.False(store.ShowLimitInMenu);
        Assert.True(store.LimitNotifications);
        Assert.True(store.CompanionNotifications);
        Assert.True(store.UpdateNotificationsEnabled);
        Assert.True(store.StatusChecksEnabled);
        Assert.Equal(MenuLimitColorMode.Gauge, store.MenuLimitColorMode);
        Assert.Equal(ClaudeTrackedAccountMode.Automatic, store.ClaudeTrackedAccountMode);
        Assert.Equal("", store.AdditionalClaudeConfigDirs);
    }

    // MARK: Burn tier

    [Fact]
    public void BurnTierThresholds() => MainActor.Run(async () =>
    {
        async Task<BurnTier> Tier(double tpm)
        {
            var p = Claude(10_000_000);
            p.Enrichment = new ProviderEnrichment { ActiveBlock = Block(tpm), BlocksOK = true };
            var store = MakeStore([p]);
            await store.Refresh(scheduleEmptyRetry: false);
            return store.BurnTier;
        }
        Assert.Equal(BurnTier.Idle, await Tier(500));
        Assert.Equal(BurnTier.Normal, await Tier(50_000));
        Assert.Equal(BurnTier.Fast, await Tier(200_000));
        Assert.Equal(BurnTier.Blazing, await Tier(500_000));
    });

    [Fact]
    public void BurnTierFromNonClaudeProvider() => MainActor.Run(async () =>
    {
        var other = Other("codex", 10_000_000);
        other.Enrichment = new ProviderEnrichment { ActiveBlock = Block(200_000), BlocksOK = true };
        var store = MakeStore([other]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(BurnTier.Fast, store.BurnTier);
    });

    /// <summary>Right after midnight: no tokens today, but the active 5h block makes a carrier snapshot.</summary>
    [Fact]
    public void MidnightCarrierSnapshotFromActiveBlock() => MainActor.Run(async () =>
    {
        var claude = Claude(null);
        claude.Enrichment = Periods(90_000_000, 300_000_000, Block(200_000));
        var store = MakeStore([claude]);
        await store.Refresh(scheduleEmptyRetry: false);
        var snap = store.Snapshot("claude_code");
        Assert.Equal("claude_code", snap?.ProviderID);
        Assert.Null(snap?.Today);
        Assert.Equal(90_000_000, store.WeekTotalTokens);
        Assert.Equal(BurnTier.Fast, store.BurnTier);
    });

    [Fact]
    public void CarrierForWeekMonthOnlyWithoutActiveBlock() => MainActor.Run(async () =>
    {
        var other = Other("codex", null);
        other.Enrichment = Periods(50_000_000, 80_000_000);
        var store = MakeStore([other]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Contains(store.Snapshots, s => s.ProviderID == "codex");
        Assert.Equal(0, store.TodayTotalTokens);
        Assert.Empty(store.TodayTokensByProvider);
        Assert.Equal(50_000_000, store.WeekTotalTokens);
        Assert.Equal(80_000_000, store.MonthTotalTokens);
        Assert.Equal(BurnTier.Idle, store.BurnTier);
    });

    [Fact]
    public void IdleHistoryPersistsAcrossRefreshAndEnrichmentFailure() => MainActor.Run(async () =>
    {
        var provider = Other("future_tool", null);
        var history = TodayDaily(12_000) with { Date = DayKey(-1) };
        provider.Enrichment = Periods(12_000, 12_000, monthDaily: [history, TodayDaily(0)]);
        var store = MakeStore([provider]);

        for (var i = 0; i < 2; i++)
        {
            await store.Refresh(scheduleEmptyRetry: false);
            Assert.Equal([12_000L, 0L], store.MonthDailyTotals.Select(d => d.TotalTokens));
            Assert.Equal(12_000, store.DailyLedger.Tokens(history.Date));
            Assert.Equal(0, store.TodayTotalTokens);
            Assert.Empty(store.TodayTokensByProvider);
        }

        provider.Enrichment = new ProviderEnrichment();   // failed read: keep history
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(12_000, store.WeekTotalTokens);
        Assert.Equal(12_000, store.MonthTotalTokens);
        Assert.Equal([12_000L, 0L], store.MonthDailyTotals.Select(d => d.TotalTokens));

        // A successful empty scan is authoritative: no ghost provider.
        provider.Enrichment = new ProviderEnrichment { BlocksOK = true, PeriodsOK = true };
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.Snapshots);
        Assert.Equal(12_000, store.DailyLedger.Tokens(history.Date));
    });

    [Fact]
    public void IdleCarrierRequiresPositiveHistoricalValue() => MainActor.Run(async () =>
    {
        foreach (var source in new[] { "week", "month", "series", "empty", "failed" })
        {
            var provider = Other("future_tool", null);
            var day = TodayDaily(source == "series" ? 12_000 : 0) with { Date = DayKey(-1) };
            provider.Enrichment = new ProviderEnrichment
            {
                BlocksOK = true,
                WeekTotal = new PeriodUsage("w", source == "week" ? 12_000 : 0, 0),
                MonthTotal = new PeriodUsage("m", source == "month" ? 12_000 : 0, 0),
                MonthDaily = [day], PeriodsOK = source != "failed",
            };
            var store = Stores.Make(new AppSettings(null), [provider]);
            await store.Refresh(scheduleEmptyRetry: false);
            Assert.True((source is "empty" or "failed" ? 0 : 1) == store.Snapshots.Count, source);
            Assert.Equal(0, store.TodayTotalTokens);
        }
    });

    /// <summary>A zero-token parser artifact must not become a visible tab.</summary>
    [Fact]
    public void NoCarrierForZeroTokenActiveBlock() => MainActor.Run(async () =>
    {
        var claude = Claude(null);
        claude.Enrichment = Periods(0, 0, new BlockUsage("synthetic", "2026-09-18T15:54:00Z", "2026-09-18T20:54:00Z", true, 0, 0, 0));
        var store = MakeStore([claude]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.DoesNotContain(store.Snapshots, s => s.ProviderID == "claude_code");
    });

    /// <summary>[#336] A provider used three days ago keeps its tab (and limits) across week/month boundaries.</summary>
    [Fact]
    public void CarrierForProviderUsedWithinRecentWindowAcrossCalendarBoundaries() => MainActor.Run(async () =>
    {
        var claude = Claude(null);
        claude.Enrichment = Periods(0, 0, monthDaily: [TodayDaily(0)], lastUsage: DateTimeOffset.Now.AddDays(-3));
        var store = MakeStore([claude], ClaudeLimits(81));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Contains(store.Snapshots, s => s.ProviderID == "claude_code");
        Assert.Equal(0, store.TodayTotalTokens);
        Assert.Empty(store.TodayTokensByProvider);
        Assert.Equal(BurnTier.Idle, store.BurnTier);

        await store.Refresh(scheduleEmptyRetry: false);   // phase 1 starts from the carrier and keeps it
        Assert.Contains(store.Snapshots, s => s.ProviderID == "claude_code");
    });

    [Fact]
    public void NoCarrierForProviderIdleBeyondRecentWindow() => MainActor.Run(async () =>
    {
        var claude = Claude(null);
        claude.Enrichment = Periods(0, 0, lastUsage: DateTimeOffset.Now - LocalUsageReader.RecentUseWindow - TimeSpan.FromHours(1));
        var store = MakeStore([claude]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.Snapshots);
    });

    [Fact]
    public void SnapshotOrderFollowsProviderRegistrationOrder() => MainActor.Run(async () =>
    {
        var claude = Claude(null);
        claude.Enrichment = Periods(0, 0, lastUsage: DateTimeOffset.Now.AddDays(-1));
        var store = MakeStore([claude, Other("codex", 5_000)]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(["claude_code", "codex"], store.Snapshots.Select(s => s.ProviderID));
    });

    [Fact]
    public void BurnTierCombinesProviders() => MainActor.Run(async () =>
    {
        var claude = Claude(10_000_000);
        claude.Enrichment = new ProviderEnrichment { ActiveBlock = Block(60_000), BlocksOK = true };
        var other = Other("codex", 10_000_000);
        other.Enrichment = new ProviderEnrichment { ActiveBlock = Block(60_000), BlocksOK = true };
        var store = MakeStore([claude, other]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(BurnTier.Fast, store.BurnTier);
    });

    /// <summary>Extension rule: any future provider id flows through every generic aggregate.</summary>
    [Fact]
    public void UnknownFutureProviderFlowsThroughAllAggregation() => MainActor.Run(async () =>
    {
        var future = Other("future_tool_xyz", 42_000_000);
        future.Enrichment = Periods(90_000_000, 300_000_000, Block(200_000));
        var store = MakeStore([future]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(42_000_000, store.TodayTotalTokens);
        Assert.Equal(90_000_000, store.WeekTotalTokens);
        Assert.Equal(300_000_000, store.MonthTotalTokens);
        Assert.Equal(BurnTier.Fast, store.BurnTier);
        Assert.Equal("future_tool_xyz", store.Snapshot("future_tool_xyz")?.ProviderID);
    });

    /// <summary>The default registry (Claude Code only on Windows) is non-empty with unique ids. Only the
    /// providers are left at their default; nothing is refreshed, so no log is read.</summary>
    [Fact]
    public void DefaultProviderRegistryHasUniqueIds()
    {
        using var store = new UsageStore(claudeLimitsProvider: new FakeClaudeLimits(),
            additionalClaudeLimitsProvider: _ => new FakeClaudeLimits(), statusProvider: new FakeStatusProvider(),
            sessionKeys: new StubSessionKeys(), accountSessionKeys: _ => new StubSessionKeys(),
            autoRefresh: false, settings: _settings, scheduler: new ManualScheduler());
        var ids = store.RegisteredProviderIDs;
        Assert.Equal(["claude_code"], ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EmptyUsageSchedulesOneRetry() => MainActor.Run(async () =>
    {
        var scheduler = new ManualScheduler();
        var claude = Claude(null);
        var store = MakeStore([claude], scheduler: scheduler);
        await store.Refresh();
        Assert.Single(scheduler.Active, e => !e.Repeats && e.Interval == UsageStore.EmptyUsageRetryDelay);

        claude.Daily = TodayDaily(1_000);
        scheduler.FireAfter(UsageStore.EmptyUsageRetryDelay);
        Assert.True(await MainActor.WaitUntil(() => store.TodayTotalTokens == 1_000));
        // The retry runs without scheduling another one.
        Assert.DoesNotContain(scheduler.Active, e => !e.Repeats && e.Interval == UsageStore.EmptyUsageRetryDelay);
    });

    // MARK: Session key

    [Fact]
    public void SaveSessionKeyValidatesAndPicksOrgWithUsage() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(organizations: [SessionOrg("org-idle", false), SessionOrg("org-active", true)]);
        var store = Stores.Make(_settings, [Claude(1_000)], new FakeClaudeLimits(ClaudeLimits(7)), sessionKeys: keys);
        await store.SaveSessionKey("  sk-ant-sid02-" + new string('a', 60) + "\n");
        Assert.Null(store.SessionKeyError);
        Assert.True(store.SessionKeyConfigured);
        Assert.Equal("org-active", store.SessionKeySelectedOrgID);
        Assert.EndsWith("aaa", keys.Saves[^1].Key);
        Assert.Equal(2, store.SessionKeyOrganizations.Count);
    });

    [Fact]
    public void SaveSessionKeyRejectsMalformedInputWithoutStoring() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(organizations: [SessionOrg("org", true)]);
        var store = Stores.Make(_settings, [Claude(1_000)], sessionKeys: keys);
        store.LocalizationLanguage = AppLanguage.Ko;
        await store.SaveSessionKey("not-a-session-key");
        Assert.False(store.SessionKeyConfigured);
        Assert.Empty(keys.Saves);
        Assert.Equal(new L(AppLanguage.Ko).SessionKeyMalformedError, store.SessionKeyError);
    });

    [Fact]
    public void SaveSessionKeyRejectsKeyWithNoVisibleOrganization() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys();
        var store = Stores.Make(_settings, [Claude(1_000)], sessionKeys: keys);
        store.LocalizationLanguage = AppLanguage.Ko;
        await store.SaveSessionKey("sk-ant-sid02-" + new string('a', 60));
        Assert.False(store.SessionKeyConfigured);
        Assert.Empty(keys.Saves);
        Assert.Equal(new L(AppLanguage.Ko).SessionKeyNoOrgError, store.SessionKeyError);
    });

    [Fact]
    public void ClearSessionKeyResetsStateAndStorage() => MainActor.Run(() =>
    {
        var keys = new StubSessionKeys(new SessionKeyCredential("sk", "org-active"), [SessionOrg("org-active", true)]);
        var store = Stores.Make(_settings, [Claude(1_000)], new FakeClaudeLimits(ClaudeLimits(7)), sessionKeys: keys);
        Assert.True(store.SessionKeyConfigured);
        store.ClearSessionKey();
        Assert.True(keys.Cleared);
        Assert.False(store.SessionKeyConfigured);
        Assert.Null(store.SessionKeySelectedOrgID);
        Assert.Empty(store.SessionKeyOrganizations);
    });

    [Fact]
    public void SelectSessionOrganizationSwitchesOrgKeepingTheKey() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(new SessionKeyCredential("sk-ant-sid02-abc", "org-a"));
        var store = Stores.Make(_settings, [Claude(1_000)], new FakeClaudeLimits(ClaudeLimits(7)), sessionKeys: keys);
        await store.SelectSessionOrganization("org-b");
        Assert.Equal("org-b", keys.Saves[^1].OrganizationID);
        Assert.Equal("sk-ant-sid02-abc", keys.Saves[^1].Key);
        Assert.Equal("org-b", store.SessionKeySelectedOrgID);
    });

    [Fact]
    public void RefreshSessionOrganizationsRepopulatesAfterRestart() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(new SessionKeyCredential("sk-ant-sid02-abc", "org-b"),
            [SessionOrg("org-a", false), SessionOrg("org-b", true)]);
        var store = Stores.Make(_settings, [Claude(1_000)], new FakeClaudeLimits(ClaudeLimits(7)), sessionKeys: keys);
        Assert.Empty(store.SessionKeyOrganizations);
        await store.RefreshSessionOrganizations();
        Assert.Equal(["org-a", "org-b"], store.SessionKeyOrganizations.Select(o => o.Id));
        Assert.Equal("org-b", store.SessionKeySelectedOrgID);
    });

    [Fact]
    public void RefreshSessionOrganizationsDoesNothingWithoutKey() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(organizations: [SessionOrg("org-a", true)]);
        var store = Stores.Make(_settings, [Claude(1_000)], sessionKeys: keys);
        await store.RefreshSessionOrganizations();
        Assert.Empty(store.SessionKeyOrganizations);
    });

    // MARK: Expired credential UX

    private UsageStore ExpiryStore(IClaudeLimitsProviding limits, ISessionKeyManaging? keys = null) =>
        Stores.Make(_settings, [Claude(10_000_000)], limits, sessionKeys: keys);

    [Fact]
    public void LimitsAuthExpiredSetOn401AndClearedOnSuccess() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Http(401)], ClaudeLimits(12, "2099-01-01T00:00:00Z")));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.LimitsAuthExpired);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(store.LimitsAuthExpired);
    });

    /// <summary>Windows: an expired token in the credentials file reads like a rejected OAuth token.</summary>
    [Fact]
    public void ExpiredCredentialsFileIsAnOAuthExpiry() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Of(LimitsErrorKind.CredentialExpired)]));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(LimitsAuthExpiry.OAuth, store.LimitsAuthExpiry);
        Assert.False(store.LimitsAvailable);
    });

    [Fact]
    public void SessionKeyExpiryIsDistinguishedFromOAuthExpiry() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Of(LimitsErrorKind.SessionKeyInvalid)]));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(LimitsAuthExpiry.SessionKey, store.LimitsAuthExpiry);
        Assert.True(store.LimitsAuthExpired);
    });

    [Fact]
    public void OAuthExpiryIsNotReportedAsSessionKeyExpiry() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Http(401)]));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(LimitsAuthExpiry.OAuth, store.LimitsAuthExpiry);
    });

    [Fact]
    public void SessionKeyExpiryClearsOnSuccess() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Of(LimitsErrorKind.SessionKeyInvalid)],
            ClaudeLimits(12, "2099-01-01T00:00:00Z")));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(LimitsAuthExpiry.SessionKey, store.LimitsAuthExpiry);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Null(store.LimitsAuthExpiry);
    });

    [Fact]
    public void SessionKeyExpiredIsVisibleWhileKeyIsStillStored() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Of(LimitsErrorKind.SessionKeyInvalid)]),
            new StubSessionKeys(new SessionKeyCredential("sk", "org")));
        Assert.False(store.SessionKeyExpired);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.SessionKeyConfigured);
        Assert.True(store.SessionKeyExpired);
    });

    [Fact]
    public void OAuthExpiryDoesNotMarkSessionKeyExpired() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Http(401)]), new StubSessionKeys());
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(store.SessionKeyExpired);
    });

    [Fact]
    public void LimitsAuthExpiredNotSetOnNon401() => MainActor.Run(async () =>
    {
        var store = ExpiryStore(new SequenceClaudeLimits([LimitsError.Http(500)]));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(store.LimitsAuthExpired);
    });

    /// <summary>A 429 pauses the automatic polls (no fetch during the backoff); the user's button bypasses it.</summary>
    [Fact]
    public void RateLimitBacksOffAutomaticPollsButNotTheManualRefresh() => MainActor.Run(async () =>
    {
        var limits = new SequenceClaudeLimits([LimitsError.RateLimited(null)], ClaudeLimits(12));
        var store = ExpiryStore(limits);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(1, limits.Call);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(1, limits.Call);   // backing off
        await store.RefreshLimitTokenFromKeychain();
        Assert.Equal(2, limits.Call);
        Assert.NotNull(store.Limits);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(3, limits.Call);   // success reset the backoff
    });

    [Fact]
    public void NextLimitsBackoffDoublesUpToAnHour()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), UsageStore.NextLimitsBackoff(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(600), UsageStore.NextLimitsBackoff(TimeSpan.FromSeconds(300)));
        Assert.Equal(TimeSpan.FromSeconds(3600), UsageStore.NextLimitsBackoff(TimeSpan.FromSeconds(2400)));
    }

    [Fact]
    public void IsStaleBeforeFirstRefreshThenFreshAfter() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(10_000_000)]);
        Assert.True(store.IsStale);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(store.IsStale);
    });

    // MARK: Provider tab selection

    [Fact]
    public void SnapshotPreferringSelection() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_000), Other("gemini", 2_000)]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal("gemini", store.Snapshot("gemini")?.ProviderID);
        Assert.Equal("claude_code", store.Snapshot(null)?.ProviderID);
        Assert.Equal("claude_code", store.Snapshot("cursor")?.ProviderID);
    });

    /// <summary>The phase-1 rebuild carries week/month over, so a skipped enrichment does not blink the rows.</summary>
    [Fact]
    public void WeekMonthPersistAcrossRefreshWhenEnrichmentSkips() => MainActor.Run(async () =>
    {
        var claude = Claude(1_000);
        claude.Enrichment = new ProviderEnrichment
        {
            BlocksOK = false, PeriodsOK = true,
            WeekTotal = new PeriodUsage("2026-06-28", 7_000, 0), MonthTotal = new PeriodUsage("2026-06", 30_000, 0),
        };
        var store = MakeStore([claude]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(7_000, store.WeekTotalTokens);
        Assert.Equal(30_000, store.MonthTotalTokens);

        claude.Enrichment = new ProviderEnrichment();
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(7_000, store.WeekTotalTokens);
        Assert.Equal(30_000, store.MonthTotalTokens);
    });

    // MARK: friendlyLimitError

    [Fact]
    public void FriendlyLimitErrorMapping()
    {
        var l = new L(AppLanguage.En);
        Assert.Equal(l.LimitRefreshHTTPError(401), UsageStore.FriendlyLimitError(LimitsError.Http(401), l));
        Assert.Equal(l.LimitRefreshHTTPError(500), UsageStore.FriendlyLimitError(LimitsError.Http(500), l));
        Assert.Equal(l.LimitRefreshNoCredential, UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.CredentialFormat), l));
        Assert.Equal(l.LimitRefreshNoCredential, UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.NoCredential), l));
        Assert.Equal(l.ClaudeAuthExpiredHint, UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.CredentialExpired), l));
        Assert.Equal(l.LimitRefreshGeneric, UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.LiveFetchNotPermitted), l));
        Assert.Equal(l.LimitRefreshGeneric, UsageStore.FriendlyLimitError(new StubException(), l));
        Assert.Equal(l.LimitRefreshRateLimited, UsageStore.FriendlyLimitError(LimitsError.RateLimited(null), l));
        Assert.Equal(l.LimitRefreshReauthNeeded,
            UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.CredentialMissingAccountOAuth), l));
        Assert.NotEqual(l.LimitRefreshReauthNeeded, l.LimitRefreshNoCredential);
        Assert.Equal(l.SessionKeyMalformedError, UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.SessionKeyMalformed), l));
        Assert.Equal(l.SessionKeyExpiredError, UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.SessionKeyInvalid), l));
        Assert.Equal(l.SessionKeyNoOrgError, UsageStore.FriendlyLimitError(LimitsError.Of(LimitsErrorKind.SessionKeyNoOrganization), l));
    }

    // MARK: Tokens-only providers (the Swift Cursor aggregation tests; any reportsCost=false provider)

    [Fact]
    public void TokensOnlyProviderFlowsThroughAggregation() => MainActor.Run(async () =>
    {
        var tool = Other("cursor", 8_000);
        tool.Enrichment = Periods(50_000, 200_000);
        var store = MakeStore([tool]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(8_000, store.TodayTotalTokens);
        Assert.Equal(50_000, store.WeekTotalTokens);
        Assert.Equal(200_000, store.MonthTotalTokens);
        Assert.Equal("cursor", store.Snapshot("cursor")?.ProviderID);
    });

    [Fact]
    public void TokensOnlyProviderContributesZeroToTodayCostTotal() => MainActor.Run(async () =>
    {
        var tool = new FakeUsageProvider("cursor", "Cursor", TodayDaily(50_000, 9.99), reportsCost: false);
        var store = MakeStore([Claude(100_000, 1.25), tool]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(150_000, store.TodayTotalTokens);
        Assert.Equal(1.25, store.TodayCostTotal, 6);
        Assert.False(store.Snapshot("cursor")?.ReportsCost);
        Assert.True(store.Snapshot("claude_code")?.ReportsCost);
    });

    [Fact]
    public void TokensOnlyProviderDoesNotSurfaceZeroDollarCostInMenu() => MainActor.Run(async () =>
    {
        var tool = new FakeUsageProvider("cursor", "Cursor", TodayDaily(50_000, 9.99), reportsCost: false);
        var store = MakeStore([tool]);
        store.ShowTokensInMenu = true;
        store.ShowCostInMenu = true;
        store.ShowLimitInMenu = false;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.CostingSnapshots);
        Assert.False(store.ShowsCost);
        Assert.Equal([TokenFormatter.Compact(50_000)], store.MenuLines);
    });

    [Fact]
    public void UnavailableCostRendersAsDash()
    {
        var l = new L(AppLanguage.En);
        var unavailable = new UsageCost(0, CostCoverage.Unavailable);
        Assert.Equal("$—", unavailable.Text(l, compact: true));
        Assert.Equal(l.CostUnavailable, unavailable.Text(l));
        Assert.Equal("$9.5", new UsageCost(9.5, CostCoverage.Source).Text(l, compact: true));
        Assert.Equal("$9.50", new UsageCost(9.5, CostCoverage.Source).Text(l));
    }

    // MARK: Menu bar limit colors

    /// <summary>Half of the 5-hour window has passed: Claude 40% is on pace (−10).</summary>
    private async Task<UsageStore> HalfwayStore(DateTimeOffset now, double utilization = 40)
    {
        var store = MakeStore([Claude(1_000)], ClaudeFiveHour(utilization, TimeSpan.FromHours(2.5), now));
        store.ShowTokensInMenu = false;
        store.ShowCostInMenu = false;
        store.ShowLimitInMenu = true;
        await store.Refresh(scheduleEmptyRetry: false);
        return store;
    }

    [Fact]
    public void MenuLimitPartsCarryThePaceOfTheirOwnWindow() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var store = await HalfwayStore(now);
        var part = Assert.Single(store.MenuLimitParts(now));
        Assert.Equal("Claude 40%", part.Text);
        Assert.Equal(0.5, part.Pace!.Value, 2);
        Assert.Equal("Claude 40%", store.MenuTitle);
    });

    [Fact]
    public void MenuLimitColorRunsFollowTheMode() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var calm = await HalfwayStore(now);
        Assert.Equal(MenuLimitColorMode.Gauge, calm.MenuLimitColorMode);
        Assert.Equal([new MenuLimitColorRun("Claude 40%", PaceTier.OnPace)], calm.MenuLimitColorRuns(now));
        calm.MenuLimitColorMode = MenuLimitColorMode.Attention;
        Assert.Empty(calm.MenuLimitColorRuns(now));   // on pace keeps the system color

        var fast = await HalfwayStore(now, 70);   // +20 over pace
        fast.MenuLimitColorMode = MenuLimitColorMode.Attention;
        Assert.Equal([new MenuLimitColorRun("Claude 70%", PaceTier.Over)], fast.MenuLimitColorRuns(now));
        fast.MenuLimitColorMode = MenuLimitColorMode.Off;
        Assert.Empty(fast.MenuLimitColorRuns(now));
    });

    /// <summary>Without a reset time there is no pace: the absolute threshold colors apply (warn 80, crit 95).</summary>
    [Fact]
    public void MenuLimitItemsWithoutAPaceUseTheThresholdColors() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        foreach (var (utilization, expected) in new[] { (50.0, PaceTier.OnPace), (85, PaceTier.Over), (96, PaceTier.WayOver) })
        {
            var store = Stores.Make(new AppSettings(null), [Claude(1_000)], new FakeClaudeLimits(ClaudeFiveHour(utilization, null, now)));
            store.ShowLimitInMenu = true;
            await store.Refresh(scheduleEmptyRetry: false);
            Assert.Null(store.MenuLimitParts(now)[0].Pace);
            Assert.Equal([expected], store.MenuLimitColorRuns(now).Select(r => r.Tier));
        }
    });

    [Fact]
    public void MenuLimitColorIsJudgedFromUsageInRemainingMode() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var store = await HalfwayStore(now, 70);
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Remaining;
        Assert.Equal([new MenuLimitColorRun("Claude 30%", PaceTier.Over)], store.MenuLimitColorRuns(now));
    });

    [Fact]
    public void MenuLimitColorModePersists()
    {
        MakeStore([]).MenuLimitColorMode = MenuLimitColorMode.Attention;
        Assert.Equal("attention", _settings.GetString("menuLimitColorMode"));
        Assert.Equal(MenuLimitColorMode.Attention, MakeStore([]).MenuLimitColorMode);
    }

    // MARK: Daily trend window (#430)

    private static DailyUsage Usage(string date, long tokens) => new(date, 0, tokens, 0, 0, tokens, 0);

    [Fact]
    public void DailyTrendWindowDefaultsTo14DaysAndPersists()
    {
        var store = MakeStore([]);
        Assert.Equal(DailyTrendWindow.D14, store.DailyTrendWindow);

        store.DailyTrendWindow = DailyTrendWindow.D7;
        Assert.Equal(DailyTrendWindow.D7, store.DailyTrendWindow);
        Assert.Equal("7d", _settings.GetString("dailyTrendWindow"));
        Assert.Equal(DailyTrendWindow.D7, MakeStore([]).DailyTrendWindow);
    }

    [Fact]
    public void DailyTrendTotalsFollowsActiveWindow() => MainActor.Run(async () =>
    {
        var fake = Claude(100);
        fake.Enrichment = new ProviderEnrichment
        {
            RecentDaily = [Usage("2026-10-15", 100)],
            MonthDaily = [Usage("2026-10-01", 500)],
            PeriodsOK = true,
        };
        var store = MakeStore([fake]);
        await store.Refresh(scheduleEmptyRetry: false);

        store.DailyTrendWindow = DailyTrendWindow.D14;
        Assert.Equal(100, store.DailyTrendTotals[0].TotalTokens);

        store.DailyTrendWindow = DailyTrendWindow.Month;
        Assert.Equal(500, store.DailyTrendTotals[0].TotalTokens);
    });

    /// <summary>Each rolling window is the tail of the 21-day series; without one, the tail of the month series.</summary>
    [Fact]
    public void RollingTotalsTakeTheTailOfTheRecentSeriesAndFallBackToTheMonth() => MainActor.Run(async () =>
    {
        var recent = Enumerable.Range(-20, 21).Select(d => Usage(DayKey(d), 21 + d)).ToList();   // 1…21 tokens
        var withRecent = Claude(21);
        withRecent.Enrichment = Periods(1, 1) with { RecentDaily = recent };
        var store = MakeStore([withRecent]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(Enumerable.Range(15, 7).Select(t => (long)t), store.DailyTrendTotalsFor(DailyTrendWindow.D7).Select(d => d.TotalTokens));
        Assert.Equal(14, store.DailyTrendTotalsFor(DailyTrendWindow.D14).Count);
        Assert.Equal(21, store.DailyTrendTotalsFor(DailyTrendWindow.D21).Count);

        var monthOnly = Other("future_tool", 9);
        monthOnly.Enrichment = Periods(1, 1, monthDaily: [Usage(DayKey(-8), 7), Usage(DayKey(-1), 8), TodayDaily(9)]);
        var fallback = MakeStore([monthOnly]);
        await fallback.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([7L, 8L, 9L], fallback.DailyTrendTotalsFor(DailyTrendWindow.D14).Select(d => d.TotalTokens));
        Assert.Equal([8L, 9L], fallback.RollingDailyTotals(2).Select(d => d.TotalTokens));
    });

    /// <summary>A failed enrichment keeps the previous rolling series, like the month series (phase-1 carry-over).</summary>
    [Fact]
    public void RecentDailySurvivesAFailedEnrichment() => MainActor.Run(async () =>
    {
        var provider = Claude(5);
        provider.Enrichment = Periods(5, 5, monthDaily: [TodayDaily(5)]) with
        {
            RecentDaily = [Usage(DayKey(-15), 40), TodayDaily(5)],
        };
        var store = MakeStore([provider]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([40L, 5L], store.DailyTrendTotalsFor(DailyTrendWindow.D21).Select(d => d.TotalTokens));

        provider.Enrichment = new ProviderEnrichment();   // failed read
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([40L, 5L], store.DailyTrendTotalsFor(DailyTrendWindow.D21).Select(d => d.TotalTokens));
    });

    /// <summary>
    /// Used 15 days ago, across a month boundary: no week/month total and no use within 7 days, so only the rolling
    /// series keeps the provider (and its 14/21-day trend) on screen.
    /// </summary>
    [Fact]
    public void RecentDailyAloneKeepsTheProviderAndItsTrendHistory() => MainActor.Run(async () =>
    {
        var provider = Other("future_tool", null);
        provider.Enrichment = Periods(0, 0, monthDaily: [TodayDaily(0)]) with
        {
            RecentDaily = [Usage(DayKey(-15), 40), TodayDaily(0)],
        };
        var store = MakeStore([provider]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Contains(store.Snapshots, snap => snap.ProviderID == "future_tool");
        Assert.True(store.HasDailyTrendHistory);
        Assert.Equal(40, store.DailyTrendTotalsFor(DailyTrendWindow.D21).Sum(d => d.TotalTokens));
        Assert.Equal(0, store.DailyTrendTotalsFor(DailyTrendWindow.Month).Sum(d => d.TotalTokens));

        provider.Enrichment = Periods(0, 0, monthDaily: [TodayDaily(0)]) with { RecentDaily = [TodayDaily(0)] };
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.Snapshots);
        Assert.False(store.HasDailyTrendHistory);
    });

    // MARK: Windows seams

    [Fact]
    public void ChangedFiresDuringRefreshAndOnSettings() => MainActor.Run(async () =>
    {
        var store = MakeStore([Claude(1_000)]);
        var changes = 0;
        store.Changed += () => changes++;
        var refreshed = 0;
        store.OnRefresh = () => refreshed++;
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(changes >= 2);
        Assert.Equal(1, refreshed);
        var before = changes;
        store.ShowCostInMenu = true;
        Assert.Equal(before + 1, changes);
        Assert.True(_settings.GetBool("showCostInMenu"));
    });

    [Fact]
    public void DayChangeTimerIsArmedForTheNextMidnight()
    {
        var now = new DateTimeOffset(2026, 11, 4, 23, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 4, 23, 0, 0)));
        var scheduler = new ManualScheduler();
        using var store = Stores.Make(_settings, [], scheduler: scheduler, now: () => now);
        var next = LocalUsageReader.StartOfDay(now).AddDays(1);
        Assert.Contains(scheduler.Active, e => !e.Repeats && e.Interval == next - now + TimeSpan.FromSeconds(1));
    }
}

public sealed class LoginItemTests
{
    [Fact]
    public void CommandQuotesThePath() =>
        Assert.Equal("\"C:\\Program Files\\PokeTokenBar\\PokeTokenBar.exe\"",
            LoginItem.Command(@"C:\Program Files\PokeTokenBar\PokeTokenBar.exe"));

    /// <summary>Round trip under a throwaway HKCU test key — never the real Run key.</summary>
    [Fact]
    public void SetEnabledWritesAndRemovesTheRunValue()
    {
        if (!OperatingSystem.IsWindows()) return;
        var keyPath = @"Software\PokeTokenBarTests\" + Guid.NewGuid().ToString("N");
        const string exe = @"C:\Apps\PokeTokenBar.exe";
        try
        {
            Assert.False(LoginItem.IsEnabledAt(keyPath, exe));
            LoginItem.SetEnabledAt(keyPath, true, exe);
            Assert.True(LoginItem.IsEnabledAt(keyPath, exe));
            Assert.False(LoginItem.IsEnabledAt(keyPath, @"C:\Elsewhere\PokeTokenBar.exe"));
            LoginItem.SetEnabledAt(keyPath, false, exe);
            Assert.False(LoginItem.IsEnabledAt(keyPath, exe));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\PokeTokenBarTests", throwOnMissingSubKey: false);
        }
    }
}
