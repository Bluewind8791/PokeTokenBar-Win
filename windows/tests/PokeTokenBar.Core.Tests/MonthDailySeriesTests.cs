using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of the series / enrichment cases in MonthDailyTrendTests.swift.</summary>
public class MonthDailySeriesTests
{
    private static DateTimeOffset Day(int y, int m, int d, int hour = 12, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var local = new DateTime(y, m, d, hour, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    private static UsageEntry E(string id, DateTimeOffset at, long output) => new()
    {
        Id = id, Date = at, LocalDay = LocalUsageReader.LocalDay(at), Model = "claude-opus-4-8", Output = output,
    };

    [Fact]
    public void CrossMonthSessionDoesNotLeakLastMonthIntoTheSeries()
    {
        var now = Day(2026, 7, 10);
        var lastMonthTurn = Day(2026, 6, 29, 23);
        var thisMonthTurn = Day(2026, 7, 8, 1);
        using var dir = new TempDir();
        var file = dir.Write([
            Fixtures.ClaudeLine("june", "r1", "claude-opus-4-8", Iso8601Parser.Format(lastMonthTurn), 0, 700, 0, 0),
            Fixtures.ClaudeLine("july", "r2", "claude-opus-4-8", Iso8601Parser.Format(thisMonthTurn), 0, 300, 0, 0),
        ], "straddle.jsonl");
        File.SetLastWriteTimeUtc(file, thisMonthTurn.UtcDateTime);

        var cache = new LocalUsageCache([dir.Path], Path.Combine(dir.Path, "..", Guid.NewGuid() + ".json"), () => now);
        var entries = cache.ClaudeEntries(LocalUsageReader.EnrichmentScanStart(now));

        var loadedDays = entries.Select(e => e.LocalDay).ToHashSet();
        Assert.Contains(LocalUsageReader.LocalDay(lastMonthTurn), loadedDays);   // the trigger really happens
        Assert.Contains(LocalUsageReader.LocalDay(thisMonthTurn), loadedDays);

        var series = LocalUsageReader.MonthDailySeries(entries, now);
        Assert.All(series, d => Assert.StartsWith("2026-07", d.Date));
        Assert.Equal("2026-07-01", series[0].Date);
        Assert.Equal("2026-07-10", series[^1].Date);
        Assert.Equal(300, series.Single(d => d.Date == "2026-07-08").OutputTokens);

        var month = LocalUsageReader.Period(entries, LocalUsageReader.MonthKey(now),
            LocalUsageReader.LocalDay(LocalUsageReader.StartOfMonth(now)), LocalUsageReader.LocalDay(now));
        Assert.Equal(month.TotalTokens, series.Sum(d => d.TotalTokens));
    }

    [Fact]
    public void EnrichmentSeriesAndMonthTotalStayInAgreement()
    {
        var now = Day(2026, 7, 10);
        var enrichment = ProviderEnrichment.Local(
            [E("june", Day(2026, 6, 29), 700), E("july-3", Day(2026, 7, 3), 100), E("july-10", Day(2026, 7, 10), 40)], now);
        var series = enrichment.MonthDaily!;
        Assert.All(series, d => Assert.StartsWith("2026-07", d.Date));
        Assert.Equal(enrichment.MonthTotal!.TotalTokens, series.Sum(d => d.TotalTokens));
        Assert.Equal(enrichment.MonthTotal.TotalCost, series.Sum(d => d.TotalCost), 9);
    }

    [Fact]
    public void EnrichmentLastUsageIgnoresZeroTokenEntries()
    {
        var now = Day(2026, 7, 10);
        var enrichment = ProviderEnrichment.Local([E("real", Day(2026, 7, 6), 300), E("synthetic", Day(2026, 7, 9), 0)], now);
        Assert.Equal(Day(2026, 7, 6), enrichment.LastUsage);
        Assert.Null(ProviderEnrichment.Local([E("synthetic", Day(2026, 7, 9), 0)], now).LastUsage);
    }

    [Fact]
    public void UnusedDaysAreExplicitZerosSoTheAxisCannotShift()
    {
        var series = LocalUsageReader.MonthDailySeries([E("only", Day(2026, 7, 3), 500)], Day(2026, 7, 10));
        Assert.Equal(Enumerable.Range(1, 10).Select(d => $"2026-07-{d:00}"), series.Select(d => d.Date));
        Assert.Equal(["2026-07-03"], series.Where(d => d.TotalTokens > 0).Select(d => d.Date));
    }

    [Fact]
    public void SeriesStopsAtTodayAndNeverRunsToTheEndOfTheMonth()
    {
        var now = Day(2026, 7, 10);
        var series = LocalUsageReader.MonthDailySeries([], now);
        Assert.Equal(10, series.Count);
        Assert.Equal(LocalUsageReader.LocalDay(now), series[^1].Date);
    }

    [Theory]
    [InlineData("Asia/Seoul")]
    [InlineData("America/Los_Angeles")]
    [InlineData("Australia/Lord_Howe")]
    [InlineData("Pacific/Chatham")]
    [InlineData("Europe/Berlin")]
    public void AxisLengthMatchesTheDayOfMonthInEveryMonthAndAcrossDSTTimeZones(string zoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var cases = Enumerable.Range(1, 12).Select(m => (2026, m, 27)).Append((2028, 2, 29));
        foreach (var (y, m, d) in cases)
        {
            var now = Day(y, m, d, 12, tz);
            var series = LocalUsageReader.MonthDailySeries([], now, tz);
            Assert.Equal(d, series.Count);
            Assert.Equal(LocalUsageReader.LocalDay(now, tz), series[^1].Date);
            Assert.Equal($"{y:0000}-{m:00}-01", series[0].Date);
            Assert.Equal(series.Count, series.Select(s => s.Date).Distinct().Count());
        }
    }

    [Fact]
    public void StartOfMonthAndWeekSurviveDstTransitions()
    {
        // Midnight does not exist on some DST-start days (e.g. America/Santiago). Start of day must
        // still land on that calendar day rather than throw or slide to the previous one.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        for (var day = new DateTime(2026, 1, 1); day.Year == 2026; day = day.AddDays(1))
        {
            var noon = Day(day.Year, day.Month, day.Day, 12, tz);
            var start = LocalUsageReader.StartOfDay(noon, tz);
            Assert.Equal(day.Date, TimeZoneInfo.ConvertTime(start, tz).Date);
        }
    }

    [Fact]
    public void EstimatedCostAndCoverageAgreeAcrossSeriesAndPeriods()
    {
        var now = Day(2026, 7, 10);
        var priced = ProviderEnrichment.Local([E("t", Day(2026, 7, 5), 5_000)], now);
        Assert.Equal(priced.MonthTotal!.TotalCost, priced.MonthDaily!.Sum(d => d.TotalCost));
        Assert.Equal(CostCoverage.Estimate, priced.MonthTotal.CostCoverage);
        Assert.Equal(CostCoverage.Estimate, priced.MonthDaily.First(d => d.TotalTokens > 0).CostCoverage);
    }

    [Fact]
    public void WeekStartFollowsTheCultureFirstDay()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var wednesday = Day(2026, 7, 8, 12, tz);   // 2026-07-08 is a Wednesday
        Assert.Equal("2026-07-05", LocalUsageReader.LocalDay(LocalUsageReader.StartOfWeek(wednesday, tz, DayOfWeek.Sunday), tz));
        Assert.Equal("2026-07-06", LocalUsageReader.LocalDay(LocalUsageReader.StartOfWeek(wednesday, tz, DayOfWeek.Monday), tz));
    }

    /// <summary>
    /// Late on the DST fall-back day (2026-11-01 23:59 PST in Los Angeles), 21 calendar days span more than
    /// 21 × 24 h because 2026-11-01 is 25 hours long. The first chart day (2026-10-12) starts at 00:00 PDT
    /// (07:00Z), whereas now − 21 × 24 h lands at 00:59 PDT (07:59Z). A session modified at 00:30 PDT (07:30Z)
    /// must be in the scan (direct and cached) and in the 21-day series.
    /// </summary>
    [Fact]
    public void TwentyOneDayScanIncludesEarlyMorningTranscriptOnFirstDayAcrossDstFallBack()
    {
        var la = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var now = new DateTimeOffset(2026, 11, 2, 7, 59, 0, TimeSpan.Zero);              // 2026-11-01 23:59 PST
        var transcriptTime = new DateTimeOffset(2026, 10, 12, 7, 30, 0, TimeSpan.Zero);   // 2026-10-12 00:30 PDT
        var firstChartDayStart = new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero);

        // Precondition: a fixed 21 × 24 h offset would start at 07:59Z and miss the 07:30Z transcript.
        Assert.True(now - TimeSpan.FromDays(21) > transcriptTime);

        var scanStart = LocalUsageReader.EnrichmentScanStart(now, la);
        Assert.Equal(firstChartDayStart, scanStart);
        Assert.True(scanStart <= transcriptTime);

        using var dir = new TempDir();
        var file = dir.Write([
            Fixtures.ClaudeLine("oct12-early", "r1", "claude-opus-4-8", Iso8601Parser.Format(transcriptTime), 0, 123, 0, 0),
        ], "dst-fallback-first-day.jsonl");
        File.SetLastWriteTimeUtc(file, transcriptTime.UtcDateTime);

        var full = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path], la);
        var selected = LocalUsageReader.ClaudeEntries(scanStart, [dir.Path], la);
        var cache = new LocalUsageCache([dir.Path], Path.Combine(dir.Path, "..", Guid.NewGuid() + ".json"), () => now, la);
        var cached = cache.ClaudeEntries(scanStart);
        Assert.Equal(123, full.Sum(e => e.Total));
        Assert.Equal(123, selected.Sum(e => e.Total));
        Assert.Equal(123, cached.Sum(e => e.Total));

        var recent = ProviderEnrichment.Local(cached, now, la).RecentDaily!;
        Assert.Equal(21, recent.Count);
        Assert.Equal("2026-10-12", recent[0].Date);
        Assert.Equal(123, recent[0].TotalTokens);
        Assert.Equal("2026-11-01", recent[^1].Date);
    }
}
