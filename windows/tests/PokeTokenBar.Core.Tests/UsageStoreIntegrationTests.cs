using System.Net.Http;
using PokeTokenBar.Core;
using static PokeTokenBar.Core.Tests.StoreFixtures;

namespace PokeTokenBar.Core.Tests;

// Store-level parts of RareCandyTests.swift (RareCandyGrantIntegrationTests), UsageRecapTests.swift (ledger
// recording), MonthDailyTrendTests.swift (monthDailyTotals), ZeroUsageHomeRenderingTests.swift (store values, no
// rendering), LocalizationErrorsTests.swift (stored errors) and LimitProgressRenderingTests.swift (display-mode
// logic). Codex / Antigravity / Gemini limit branches are not ported (Claude Code only); a non-Claude provider id is
// still used where it is merely a generic aggregation fixture.

file static class IT
{
    public static FakeUsageProvider ClaudeProvider(long tokens = 1_000) => new("claude_code", "Claude Code", TodayDaily(tokens));

    public static LimitStatus Claude(double? fiveHour = null, double? sevenDay = null, double? opus = null, double? sonnet = null) =>
        new()
        {
            FiveHour = fiveHour is { } f ? new LimitWindow { Utilization = f } : null,
            SevenDay = sevenDay is { } s ? new LimitWindow { Utilization = s } : null,
            SevenDayOpus = opus is { } o ? new LimitWindow { Utilization = o } : null,
            SevenDaySonnet = sonnet is { } n ? new LimitWindow { Utilization = n } : null,
        };

    public static DailyUsage Usage(string date, long tokens, double cost = 0, CostCoverage coverage = default) =>
        new(date, tokens, 0, 0, 0, tokens, cost, CostCoverage: coverage);
}

/// <summary>Today from a fixed value, the month series from the enrichment (UsageRecapTests' LedgerProvider,
/// MonthDailyTrendTests' TrendProvider, ZeroUsageHome's QuietHomeProvider).</summary>
file sealed class SeriesProvider(string id, DailyUsage? daily, ProviderEnrichment enrichment, bool reportsCost = true)
    : IUsageProvider
{
    public string Id => id;
    public string DisplayName => id;
    public bool ReportsCost => reportsCost;
    public Task<DailyUsage?> FetchDaily() => Task.FromResult(daily);
    public Task<ProviderEnrichment> FetchEnrichment() => Task.FromResult(enrichment);
}

file sealed class OfflineUsageProvider : IUsageProvider
{
    public string Id => "test";
    public string DisplayName => "Test";
    public Task<DailyUsage?> FetchDaily() => Task.FromException<DailyUsage?>(new HttpRequestException("offline"));
    public Task<ProviderEnrichment> FetchEnrichment() => Task.FromResult(new ProviderEnrichment());
}

public class RareCandyGrantUsageStoreIntegrationTests
{
    private readonly AppSettings _settings = new(null);

    private UsageStore Usage(LimitStatus? claude = null, IReadOnlyList<IUsageProvider>? providers = null) =>
        Stores.Make(_settings, providers ?? [IT.ClaudeProvider()], claude: new FakeClaudeLimits(claude));

    private static CompanionStore Companion() => StateFiles.Store(Lines.Linear3, 7);

    [Fact]
    public void EligibleWindowsClaudeSessionAndWeekly() => MainActor.Run(async () =>
    {
        var store = Usage(IT.Claude(fiveHour: 100, sevenDay: 100));
        await store.Refresh(scheduleEmptyRetry: false);
        var byKey = store.CandyEligibleWindows.ToDictionary(w => w.Key);
        Assert.Equal(WindowClass.Session, byKey["claude.fiveHour"].Kind);
        Assert.Equal(100, byKey["claude.fiveHour"].Utilization);
        Assert.Equal(WindowClass.Weekly, byKey["claude.sevenDay"].Kind);
        Assert.Equal(2, store.CandyEligibleWindows.Count);
    });

    /// <summary>Opus/Sonnet weekly windows are not candy windows (sub-windows of the headline ones).</summary>
    [Fact]
    public void EligibleWindowsExcludeOpusSonnet() => MainActor.Run(async () =>
    {
        var store = Usage(IT.Claude(fiveHour: 100, opus: 100, sonnet: 100));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(["claude.fiveHour"], store.CandyEligibleWindows.Select(w => w.Key).ToHashSet());
    });

    /// <summary>A provider without official limits adds no candy window.</summary>
    [Fact]
    public void EligibleWindowsExcludeProvidersWithoutLimits() => MainActor.Run(async () =>
    {
        var store = Usage(IT.Claude(fiveHour: 100),
            [new FakeUsageProvider("gemini", "Gemini", TodayDaily(9_000)), IT.ClaudeProvider()]);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.DoesNotContain(store.CandyEligibleWindows, w => w.Key.Contains("gemini"));
        Assert.Equal(["claude.fiveHour"], store.CandyEligibleWindows.Select(w => w.Key));
    });

    [Fact]
    public void LimitsReadyReflectsLoad() => MainActor.Run(async () =>
    {
        var store = Usage(IT.Claude(fiveHour: 50));
        Assert.False(store.LimitsReady, "before refresh");
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.LimitsReady, "after the limits load");
    });

    /// <summary>[Core] Seeded, Claude 5h reaches 100% → one session candy.</summary>
    [Fact]
    public void ClaudeSessionLimitGrantsOneCandy() => MainActor.Run(async () =>
    {
        var c = Companion();
        c.GrantCandies([], limitsReady: true);   // seed (no window at 100% now)
        var store = Usage(IT.Claude(fiveHour: 100));
        await store.Refresh(scheduleEmptyRetry: false);
        c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(1, c.RareCandyCount);
    });

    [Fact]
    public void ClaudeWeeklyLimitGrantsFive() => MainActor.Run(async () =>
    {
        var c = Companion();
        c.GrantCandies([], limitsReady: true);
        var store = Usage(IT.Claude(sevenDay: 100));
        await store.Refresh(scheduleEmptyRetry: false);
        c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(RareCandy.WeeklyGrant, c.RareCandyCount);
    });

    [Fact]
    public void SessionAndWeeklyTogetherGrantSix() => MainActor.Run(async () =>
    {
        var c = Companion();
        c.GrantCandies([], limitsReady: true);
        var store = Usage(IT.Claude(fiveHour: 100, sevenDay: 100));
        await store.Refresh(scheduleEmptyRetry: false);
        c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(1 + RareCandy.WeeklyGrant, c.RareCandyCount);
    });

    /// <summary>99.9% grants nothing (strictly 100% or more).</summary>
    [Fact]
    public void JustUnder100NoGrant() => MainActor.Run(async () =>
    {
        var c = Companion();
        c.GrantCandies([], limitsReady: true);
        var store = Usage(IT.Claude(fiveHour: 99.9));
        await store.Refresh(scheduleEmptyRetry: false);
        c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(0, c.RareCandyCount);
    });

    /// <summary>First run (not seeded) at 100% → seeds only, no retroactive grant.</summary>
    [Fact]
    public void FirstRunAt100SeedsNoGrant() => MainActor.Run(async () =>
    {
        var c = Companion();
        var store = Usage(IT.Claude(fiveHour: 100));
        await store.Refresh(scheduleEmptyRetry: false);
        c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(0, c.RareCandyCount);
        Assert.True(c.State.CandyFeatureSeeded);
        c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(0, c.RareCandyCount);
    });

    /// <summary>Edge-triggered: the same 100% window evaluated many times grants once.</summary>
    [Fact]
    public void RepeatedRefreshGrantsOnce() => MainActor.Run(async () =>
    {
        var c = Companion();
        c.GrantCandies([], limitsReady: true);
        var store = Usage(IT.Claude(fiveHour: 100));
        await store.Refresh(scheduleEmptyRetry: false);
        for (var i = 0; i < 5; i++) c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(1, c.RareCandyCount);
    });

    /// <summary>The window names the grant notification body uses, in Korean whatever the machine locale.</summary>
    [Fact]
    public void EligibleWindowNamesForNotificationBody() => MainActor.Run(async () =>
    {
        var store = Usage(IT.Claude(fiveHour: 100, sevenDay: 100));
        store.LocalizationLanguage = AppLanguage.Ko;
        await store.Refresh(scheduleEmptyRetry: false);
        var names = store.CandyEligibleWindows.ToDictionary(w => w.Key, w => w.Name);
        Assert.Equal("Claude 5시간 세션", names["claude.fiveHour"]);
        Assert.Equal("Claude 주간", names["claude.sevenDay"]);
    });

    /// <summary>five_hour present without a utilization is not a candy window.</summary>
    [Fact]
    public void EligibleWindowsSkipNilUtilization() => MainActor.Run(async () =>
    {
        var store = Usage(LimitStatus.Decode("""{"five_hour":{}}"""u8));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.NotNull(store.Limits?.FiveHour);
        Assert.Null(store.Limits!.FiveHour!.Utilization);
        Assert.Empty(store.CandyEligibleWindows);
    });

    /// <summary>Candy (100) and alert (crit 95) thresholds stay separate: 97% warns but grants nothing.</summary>
    [Fact]
    public void CandyThresholdSeparateFromAlertThreshold() => MainActor.Run(async () =>
    {
        var c = Companion();
        c.GrantCandies([], limitsReady: true);
        var store = Usage(IT.Claude(fiveHour: 97));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.IsLimitWarning, "97% ≥ crit 95");
        c.GrantCandies(store.CandyEligibleWindows, store.LimitsReady);
        Assert.Equal(0, c.RareCandyCount);
    });

    /// <summary>Windows port of the AppDelegate's onStoreRefreshed: the store feeds the companion and grants.</summary>
    [Fact]
    public void FeedCompanionAfterRefreshGrantsFromFreshLimits() => MainActor.Run(async () =>
    {
        var c = Companion();
        c.GrantCandies([], limitsReady: true);
        var store = Usage(IT.Claude(fiveHour: 100));
        store.OnRefresh = () => store.FeedCompanionAfterRefresh(c);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(1, c.RareCandyCount);
    });
}

public class LedgerRecordingUsageStoreIntegrationTests
{
    private static ProviderEnrichment Series(params DailyUsage[] days) => new() { PeriodsOK = true, MonthDaily = [.. days] };

    /// <summary>Regression: the ledger used to be written right after phase 1, where the month series is still
    /// null, so the first refresh after an install recorded nothing.</summary>
    [Fact]
    public void TheFirstRefreshAlreadyFillsTheLedger() => MainActor.Run(async () =>
    {
        var today = DayKey(0);
        var yesterday = LocalUsageReader.LocalDay(DateTimeOffset.Now.AddSeconds(-86_400));
        var settings = new AppSettings(null);
        var store = Stores.Make(settings, [new SeriesProvider("claude_code", IT.Usage(today, 300),
            Series(IT.Usage(yesterday, 900), IT.Usage(today, 300)))]);

        await store.Refresh(scheduleEmptyRetry: false);

        Assert.Equal(300, store.DailyLedger.Tokens(today));
        Assert.Equal(900, store.DailyLedger.Tokens(yesterday));
        Assert.Equal(yesterday, store.DailyLedger.CoveredSince);
        Assert.Equal(store.DailyLedger, UsageLedger.Load(settings));   // survives a relaunch
    });

    /// <summary>The year view compares against last year: keep all of it, and nothing older.</summary>
    [Fact]
    public void ARefreshKeepsLastYearAndDropsTheYearBefore() => MainActor.Run(async () =>
    {
        var calendar = RecapCalendar.Current();
        var lastYear = new RecapPeriod(RecapScope.Year, DateTimeOffset.Now, calendar).Shifted(-1, calendar);
        var lastYearStart = LocalUsageReader.LocalDay(lastYear.Start);
        var lastDayTwoYearsAgo = LocalUsageReader.LocalDay(lastYear.Start.AddHours(-12));
        var today = DayKey(0);
        var settings = new AppSettings(null);
        var seeded = new UsageLedger();
        seeded.Merge([IT.Usage(lastDayTwoYearsAgo, 70), IT.Usage(lastYearStart, 80)]);
        seeded.Save(settings);
        var store = Stores.Make(settings, [new SeriesProvider("claude_code", IT.Usage(today, 5), Series(IT.Usage(today, 5)))]);

        await store.Refresh(scheduleEmptyRetry: false);

        Assert.Equal(80, store.DailyLedger.Tokens(lastYearStart));
        Assert.Equal(0, store.DailyLedger.Tokens(lastDayTwoYearsAgo));
        Assert.Equal(lastYearStart, store.DailyLedger.CoveredSince);
    });

    /// <summary>The recap reads the ledger a previous launch saved, before any refresh.</summary>
    [Fact]
    public void TheStoreLoadsTheSavedLedgerAtLaunch()
    {
        var settings = new AppSettings(null);
        var saved = new UsageLedger();
        saved.Merge([IT.Usage("2026-08-01", 0), IT.Usage("2026-09-17", 12_345)]);
        saved.Save(settings);

        var store = Stores.Make(settings);

        Assert.Equal(12_345, store.DailyLedger.Tokens("2026-09-17"));
        Assert.Equal("2026-08-01", store.DailyLedger.CoveredSince);
    }
}

public class MonthDailyUsageStoreIntegrationTests
{
    /// <summary>A provider without a series drops out while the rest still add up. Cost follows the month total:
    /// tokens from everyone, amounts only from providers that report cost.</summary>
    [Fact]
    public void ProvidersWithoutASeriesDropOutWhileTheRestStillAggregate() => MainActor.Run(async () =>
    {
        var today = DayKey(0);
        var yesterday = DayKey(-1);
        DailyUsage Daily(long tokens) => IT.Usage(today, tokens);
        var store = Stores.Make(new AppSettings(null), [
            new SeriesProvider("priced", Daily(20),
                new ProviderEnrichment { PeriodsOK = true, MonthDaily = [IT.Usage(yesterday, 100, 1.0), IT.Usage(today, 20, 0.5)] }),
            new SeriesProvider("flat", Daily(7),
                new ProviderEnrichment { PeriodsOK = true, MonthDaily = [IT.Usage(today, 7, 99.0)] }, reportsCost: false),
            new SeriesProvider("blind", Daily(3), new ProviderEnrichment { PeriodsOK = true, MonthDaily = null }),
        ]);
        await store.Refresh(scheduleEmptyRetry: false);

        var totals = store.MonthDailyTotals;
        Assert.Equal([yesterday, today], totals.Select(d => d.Date));
        Assert.Equal(100, totals[0].TotalTokens);
        Assert.Equal(27, totals[1].TotalTokens);
        Assert.Equal(1.0, totals[0].TotalCost, 9);
        Assert.Equal(0.5, totals[1].TotalCost, 9);   // the flat-rate amount stays out
    });
}

public class ZeroUsageHomeUsageStoreIntegrationTests
{
    /// <summary>The real refresh pipeline on a quiet day (no usage today): today is 0 while the month keeps its
    /// history, with source-reported or unavailable period costs. The popover rendering itself is UI.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, "source")]
    [InlineData(true, "unavailable")]
    public void QuietDayKeepsHistoryAndZeroToday(bool history, string? costs) => MainActor.Run(async () =>
    {
        CostCoverage? coverage = costs switch
        {
            "source" => CostCoverage.Source,
            "unavailable" => CostCoverage.Unavailable,
            _ => null,
        };
        var now = DateTimeOffset.Now;
        var monthStart = LocalUsageReader.StartOfMonth(now);
        var day = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Day;
        var series = Enumerable.Range(0, day).Select(offset =>
        {
            var tokens = history && offset < day - 1 ? (offset % 6 + 1) * 230_000L : 0;
            return IT.Usage(LocalUsageReader.LocalDay(monthStart.AddDays(offset)), tokens);
        }).ToList();
        var weekKey = LocalUsageReader.LocalDay(LocalUsageReader.StartOfWeek(now));
        var seriesTotal = series.Sum(d => d.TotalTokens);
        var enrichment = new ProviderEnrichment
        {
            BlocksOK = true,
            WeekTotal = coverage is { } c1
                ? new PeriodUsage(weekKey, 4_800_000, costs == "unavailable" ? 0 : 31.64, c1)
                : new PeriodUsage(weekKey, series.Where(d => string.CompareOrdinal(d.Date, weekKey) >= 0).Sum(d => d.TotalTokens), 0),
            MonthTotal = coverage is { } c2
                ? new PeriodUsage(LocalUsageReader.MonthKey(now), 17_900_000, costs == "unavailable" ? 0 : 121.30, c2)
                : new PeriodUsage(LocalUsageReader.MonthKey(now), seriesTotal, 0),
            MonthDaily = series,
            PeriodsOK = true,
        };
        var store = Stores.Make(new AppSettings(null),
            [new SeriesProvider("fixture", null, enrichment, reportsCost: coverage != null)]);

        await store.Refresh(scheduleEmptyRetry: false);

        Assert.Equal(0, store.TodayTotalTokens);
        Assert.Equal(coverage != null ? 17_900_000 : seriesTotal, store.MonthTotalTokens);
        Assert.Empty(store.TodayTokensByProvider);
        if (costs == "unavailable") Assert.True(store.MonthUsageCost.IsUnavailable);
        if (costs == "source") Assert.Equal(121.30, store.MonthCostTotal, 9);
        // An all-zero history with no period tokens leaves nothing to show.
        Assert.Equal(history || coverage != null, store.HasUsageData);
    });
}

public class LocalizationErrorsUsageStoreIntegrationTests
{
    /// <summary>Stored errors are rendered at read time, so they follow a language change; the raw diagnostic is kept.</summary>
    [Fact]
    public void StoredErrorsFollowLanguageChangesAndKeepDiagnostics() => MainActor.Run(async () =>
    {
        var settings = new AppSettings(null);
        settings.Set("refreshInterval", 0.0);
        settings.Set("statusChecksEnabled", false);
        var store = Stores.Make(settings, [new OfflineUsageProvider()],
            claude: new SequenceClaudeLimits([LimitsError.Of(LimitsErrorKind.SessionKeyInvalid), LimitsError.Of(LimitsErrorKind.SessionKeyInvalid),
                LimitsError.Of(LimitsErrorKind.SessionKeyInvalid)]));
        Assert.Null(store.LastErrorMessage(new L(AppLanguage.Ko)));
        store.LocalizationLanguage = AppLanguage.Ko;
        await store.SaveSessionKey("not-a-key");
        await store.RefreshLimitTokenFromKeychain();
        Assert.Equal(new L(AppLanguage.Ko).SessionKeyMalformedError, store.SessionKeyError);
        Assert.Equal(new L(AppLanguage.Ko).SessionKeyExpiredError, store.LimitTokenRefreshError);
        store.LocalizationLanguage = AppLanguage.Ja;
        Assert.Equal(new L(AppLanguage.Ja).SessionKeyMalformedError, store.SessionKeyError);
        Assert.Equal(new L(AppLanguage.Ja).SessionKeyExpiredError, store.LimitTokenRefreshError);
        await store.Refresh(scheduleEmptyRetry: false);
        var diagnostic = store.LastErrorDescription!;
        Assert.Equal(new L(AppLanguage.Ko).UsageRefreshError + "\n" + diagnostic, store.LastErrorMessage(new L(AppLanguage.Ko)));
        Assert.Equal(new L(AppLanguage.De).UsageRefreshError + "\n" + diagnostic, store.LastErrorMessage(new L(AppLanguage.De)));
        Assert.Equal(diagnostic, store.LastErrorDescription);
    });
}

public class LimitProgressUsageStoreIntegrationTests
{
    /// <summary>The bar fill follows the store's display mode: remaining reverses it and clamps an exhausted quota.
    /// (The macOS test reads the rendered NSProgressIndicator; the WPF bar binds the same value.)</summary>
    [Fact]
    public void RemainingModeReversesFillAndClampsExhaustedQuota()
    {
        var store = Stores.Make(new AppSettings(null));
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Used;
        Assert.Equal(25, store.LimitDisplayPercent(25), 2);
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Remaining;
        Assert.Equal(75, store.LimitDisplayPercent(25), 2);
        Assert.Equal(0, store.LimitDisplayPercent(125), 2);
        Assert.Equal(100, store.LimitDisplayPercent(0), 2);
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Used;
        Assert.Equal(25, store.LimitDisplayPercent(25), 2);
    }

    /// <summary>The pace marker goes through the same transform as the fill (#286): mirrored in remaining mode.</summary>
    [Fact]
    public void PaceMarkerMirrorsInRemainingMode()
    {
        var store = Stores.Make(new AppSettings(null));
        Assert.Null(LimitPace.MarkerFraction(null, store.LimitDisplayMode));
        Assert.Equal(0.25, LimitPace.MarkerFraction(0.25, store.LimitDisplayMode)!.Value, 6);
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Remaining;
        Assert.Equal(0.75, LimitPace.MarkerFraction(0.25, store.LimitDisplayMode)!.Value, 6);
        store.LimitDisplayMode = LimitPace.LimitDisplayMode.Used;
        Assert.Equal(0, LimitPace.MarkerFraction(0, store.LimitDisplayMode)!.Value, 6);
        Assert.Equal(1, LimitPace.MarkerFraction(1, store.LimitDisplayMode)!.Value, 6);
    }
}
