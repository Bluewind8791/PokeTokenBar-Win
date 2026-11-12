namespace PokeTokenBar.Core;

/// <summary>Port of <c>UsageProvider</c> (UsageProvider.swift). New sources implement this.</summary>
public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool ReportsCost => true;

    /// <summary>Today's total (critical path): the menu number and the stale check. Null when no usage today.</summary>
    Task<DailyUsage?> FetchDaily();

    /// <summary>Block / week / month details (best effort).</summary>
    Task<ProviderEnrichment> FetchEnrichment();
}

/// <summary>Port of <c>ProviderEnrichment</c>. *OK false means collection failed → keep the previous value.</summary>
public sealed record ProviderEnrichment
{
    public BlockUsage? ActiveBlock { get; init; }
    public bool BlocksOK { get; init; }
    public PeriodUsage? WeekTotal { get; init; }
    public PeriodUsage? MonthTotal { get; init; }
    /// <summary>Month start → today, empty days as zeros. Gated by <see cref="PeriodsOK"/>.</summary>
    public List<DailyUsage>? MonthDaily { get; init; }
    /// <summary>Rolling day-by-day totals (21 days → today, empty days as zeros). Gated by <see cref="PeriodsOK"/>.</summary>
    public List<DailyUsage>? RecentDaily { get; init; }
    public bool PeriodsOK { get; init; }
    /// <summary>Newest entry with tokens; keeps the tab of a provider used recently but not today (#336).</summary>
    public DateTimeOffset? LastUsage { get; init; }

    /// <summary>The single assembly site for every local provider (active block, week, month, month series).</summary>
    public static ProviderEnrichment Local(IReadOnlyList<UsageEntry> entries, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var weekStart = LocalUsageReader.StartOfWeek(now, timeZone);
        var monthStart = LocalUsageReader.StartOfMonth(now, timeZone);
        var today = LocalUsageReader.LocalDay(now, timeZone);
        var withTokens = entries.Where(e => e.Total > 0).ToList();
        return new ProviderEnrichment
        {
            ActiveBlock = LocalUsageReader.ActiveBlock(entries, now),
            BlocksOK = true,
            WeekTotal = LocalUsageReader.Period(entries, LocalUsageReader.LocalDay(weekStart, timeZone),
                LocalUsageReader.LocalDay(weekStart, timeZone), today),
            MonthTotal = LocalUsageReader.Period(entries, LocalUsageReader.MonthKey(now, timeZone),
                LocalUsageReader.LocalDay(monthStart, timeZone), today),
            MonthDaily = LocalUsageReader.MonthDailySeries(entries, now, timeZone),
            RecentDaily = LocalUsageReader.RollingDailySeries(entries, LocalUsageReader.MaxDailyTrendWindowDays, now, timeZone),
            PeriodsOK = true,
            // Zero-token <synthetic> records are not use: an installed-only provider stays hidden (#56).
            LastUsage = withTokens.Count == 0 ? null : withTokens.Max(e => e.Date),
        };
    }
}

/// <summary>Port of <c>LocalClaudeProvider</c> (LocalUsageProvider.swift).</summary>
public sealed class LocalClaudeProvider(LocalUsageCache? cache = null, Func<DateTimeOffset>? now = null) : IUsageProvider
{
    private readonly LocalUsageCache _cache = cache ?? LocalUsageCache.Shared;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public string Id => "claude_code";
    public string DisplayName => "Claude Code";

    public Task<DailyUsage?> FetchDaily() => Task.Run(() =>
    {
        var now = _now();
        var entries = _cache.ClaudeEntries(LocalUsageReader.StartOfDay(now));
        return LocalUsageReader.Daily(entries, LocalUsageReader.LocalDay(now));
    });

    public Task<ProviderEnrichment> FetchEnrichment() => Task.Run(() =>
    {
        var now = _now();
        // One scan yields block, week, month and the daily series: the floor is the earliest window start.
        var entries = _cache.ClaudeEntries(LocalUsageReader.EnrichmentScanStart(now));
        return ProviderEnrichment.Local(entries, now);
    });
}
