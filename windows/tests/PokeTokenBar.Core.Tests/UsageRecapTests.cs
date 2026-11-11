using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Fixtures: a fixed UTC calendar whose weeks start on Monday, so nothing depends on the machine.</summary>
internal static class RecapFixtures
{
    public static RecapCalendar Calendar(DayOfWeek first = DayOfWeek.Monday) => new(TimeZoneInfo.Utc, first);

    public static DateTimeOffset Date(int year, int month, int day, int hour = 12) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>Friday 18 September 2026, noon: the week runs Monday 14 to Sunday 20.</summary>
    public static readonly DateTimeOffset Now = Date(2026, 9, 18);

    public static string Key(int year, int month, int day) => $"{year:D4}-{month:D2}-{day:D2}";

    public static DailyUsage Usage(string date, long tokens) =>
        new(date, 0, 0, 0, 0, tokens, 0, null, CostCoverage.Source);

    /// <summary>A ledger recording since <paramref name="since"/>, with usage on the given days.</summary>
    public static UsageLedger Ledger(string since, params (string Day, long Tokens)[] days)
    {
        var ledger = new UsageLedger();
        ledger.Merge(new[] { Usage(since, 0) }.Concat(days.Select(d => Usage(d.Day, d.Tokens))));
        return ledger;
    }

    public static UsageRecap Recap(UsageLedger ledger, RecapScope scope, int offset = 0, IEnumerable<IRecapGraduate>? dex = null)
    {
        var calendar = Calendar();
        var period = new RecapPeriod(scope, Now, calendar).Shifted(offset, calendar);
        return UsageRecap.Make(ledger, dex ?? [], period, Now, calendar);
    }

    public sealed record Graduate(string Id, DateTimeOffset? CaughtAt, bool IsReleased = false) : IRecapGraduate;
}

/// <summary>Port of UsageLedgerTests.</summary>
public sealed class UsageLedgerTests
{
    [Fact]
    public void ADayNeverGoesDown()
    {
        var l = new UsageLedger();
        l.Merge([RecapFixtures.Usage("2026-09-10", 500), RecapFixtures.Usage("2026-09-11", 300)]);
        l.Merge([RecapFixtures.Usage("2026-09-10", 120), RecapFixtures.Usage("2026-09-11", 900)]);
        Assert.Equal(500, l.Tokens("2026-09-10"));
        Assert.Equal(900, l.Tokens("2026-09-11"));
    }

    [Fact]
    public void TheSeriesVouchesForItsEmptyDaysToo()
    {
        var l = new UsageLedger();
        l.Merge([RecapFixtures.Usage("2026-09-01", 0), RecapFixtures.Usage("2026-09-02", 40)]);
        Assert.Equal(new Dictionary<string, long> { ["2026-09-02"] = 40 }, l.TokensByDay);
        Assert.Equal("2026-09-01", l.CoveredSince);
        Assert.True(l.Covers("2026-09-01"));
        Assert.False(l.Covers("2026-08-31"));

        l.Merge([RecapFixtures.Usage("2026-09-05", 10)]);
        Assert.Equal("2026-09-01", l.CoveredSince);
    }

    [Fact]
    public void PruningForgetsTheCoverageItDrops()
    {
        var l = RecapFixtures.Ledger("2025-06-01", ("2025-06-10", 100), ("2026-02-01", 200));
        l.Prune("2026-01-01");
        Assert.Equal(new Dictionary<string, long> { ["2026-02-01"] = 200 }, l.TokensByDay);
        Assert.Equal("2026-01-01", l.CoveredSince);
    }

    [Fact]
    public void JunkIsDroppedOnLoad()
    {
        var settings = new AppSettings(null);
        settings.SetNode(UsageLedger.DefaultsKey,
            new JsonObject { ["2026-09-10"] = 400, ["nope"] = 10, ["0000-00-00"] = 10, ["2026-09-11"] = 0 });
        settings.Set(UsageLedger.CoverageKey, "0000-00-00");

        var loaded = UsageLedger.Load(settings);
        Assert.Equal(new Dictionary<string, long> { ["2026-09-10"] = 400 }, loaded.TokensByDay);
        Assert.Null(loaded.CoveredSince);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var l = RecapFixtures.Ledger("2026-09-01", ("2026-09-10", 700));
        l.Save(new AppSettings(path));
        Assert.Equal(l, UsageLedger.Load(new AppSettings(path)));
    }
}

/// <summary>Port of RecapPeriodTests.</summary>
public sealed class RecapPeriodTests
{
    [Fact]
    public void AWeekStartsOnTheRegionsFirstWeekday()
    {
        var monday = new RecapPeriod(RecapScope.Week, RecapFixtures.Now, RecapFixtures.Calendar(DayOfWeek.Monday));
        Assert.Equal(RecapFixtures.Date(2026, 9, 14, 0), monday.Start);
        Assert.Equal(RecapFixtures.Date(2026, 9, 21, 0), monday.End);
        var sunday = new RecapPeriod(RecapScope.Week, RecapFixtures.Now, RecapFixtures.Calendar(DayOfWeek.Sunday));
        Assert.Equal(RecapFixtures.Date(2026, 9, 13, 0), sunday.Start);
    }

    [Fact]
    public void SteppingBackLandsOnWholePeriods()
    {
        var cal = RecapFixtures.Calendar();
        var february = new RecapPeriod(RecapScope.Month, RecapFixtures.Date(2026, 3, 31), cal).Shifted(-1, cal);
        Assert.Equal(RecapFixtures.Date(2026, 2, 1, 0), february.Start);
        Assert.Equal(RecapFixtures.Date(2026, 3, 1, 0), february.End);

        var lastYear = new RecapPeriod(RecapScope.Year, RecapFixtures.Now, cal).Shifted(-1, cal);
        Assert.Equal(RecapFixtures.Date(2025, 1, 1, 0), lastYear.Start);
        Assert.Equal(RecapFixtures.Date(2026, 1, 1, 0), lastYear.End);
    }

    /// <summary>A DST zone: the period still starts at local midnight.</summary>
    [Fact]
    public void PeriodsStartAtLocalMidnightAcrossDaylightSaving()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var cal = new RecapCalendar(berlin, DayOfWeek.Monday);
        var october = new RecapPeriod(RecapScope.Month, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(2)), cal);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), october.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(1)), october.End);
        var recap = UsageRecap.Make(new UsageLedger(), [], october, october.Start.AddDays(40), cal);
        Assert.Equal(31, recap.Buckets.Count);
    }
}

/// <summary>Port of UsageRecapTests (the rendering cases belong to the UI port).</summary>
public sealed class UsageRecapTests
{
    private static string K(int y, int m, int d) => RecapFixtures.Key(y, m, d);

    [Fact]
    public void EachScopeDrawsItsOwnMeters()
    {
        var l = RecapFixtures.Ledger(K(2026, 1, 1), (K(2026, 9, 2), 10), (K(2026, 9, 17), 20), (K(2026, 8, 30), 5));
        var week = RecapFixtures.Recap(l, RecapScope.Week);
        Assert.Equal(K(2026, 9, 14), week.Buckets[0].Key);
        Assert.Equal(7, week.Buckets.Count);
        Assert.Equal([K(2026, 9, 18)], week.Buckets.Where(b => b.IsCurrent).Select(b => b.Key));

        Assert.Equal(30, RecapFixtures.Recap(l, RecapScope.Month).Buckets.Count);
        Assert.Equal(28, RecapFixtures.Recap(l, RecapScope.Month, offset: -7).Buckets.Count);

        var year = RecapFixtures.Recap(l, RecapScope.Year);
        Assert.Equal(12, year.Buckets.Count);
        Assert.Equal(30, year.Buckets[8].Tokens);
        Assert.Equal(5, year.Buckets[7].Tokens);
        Assert.True(year.Buckets[8].IsCurrent);
        Assert.Equal(35, year.Total);
    }

    [Fact]
    public void FutureAndUnrecordedDaysAreNotZeros()
    {
        var week = RecapFixtures.Recap(RecapFixtures.Ledger(K(2026, 9, 16), (K(2026, 9, 17), 50)), RecapScope.Week);
        Assert.Equal([false, false, true, true, true, false, false], week.Buckets.Select(b => b.HasData));
        Assert.Equal(1, week.ActiveDays);
        Assert.Equal(3, week.CountedDays);

        var year = RecapFixtures.Recap(RecapFixtures.Ledger(K(2026, 9, 1)), RecapScope.Year);
        Assert.Equal(Enumerable.Repeat(false, 8).Append(true).Concat(Enumerable.Repeat(false, 3)), year.Buckets.Select(b => b.HasData));
    }

    [Fact]
    public void ARunningPeriodIsComparedOverTheSameDays()
    {
        var l = RecapFixtures.Ledger(K(2026, 8, 1), (K(2026, 9, 10), 100), (K(2026, 8, 18), 40), (K(2026, 8, 19), 1_000));
        var month = RecapFixtures.Recap(l, RecapScope.Month);
        Assert.True(month.IsInProgress);
        Assert.Equal(40, month.PreviousTotal);
        Assert.Equal(1.5, month.Delta!.Value, 4);
    }

    [Fact]
    public void AFinishedPeriodIsComparedWhole()
    {
        var l = RecapFixtures.Ledger(K(2026, 7, 1), (K(2026, 8, 5), 300), (K(2026, 7, 31), 100));
        var august = RecapFixtures.Recap(l, RecapScope.Month, offset: -1);
        Assert.False(august.IsInProgress);
        Assert.Equal(100, august.PreviousTotal);
        Assert.Equal(2.0, august.Delta!.Value, 4);
    }

    [Fact]
    public void AHalfCoveredPreviousPeriodIsNotAComparison()
    {
        var week = RecapFixtures.Recap(RecapFixtures.Ledger(K(2026, 9, 9), (K(2026, 9, 15), 150), (K(2026, 9, 10), 50)), RecapScope.Week);
        Assert.Null(week.PreviousTotal);
        Assert.Null(week.Delta);
    }

    [Fact]
    public void TheBestStreakStaysInsideThePeriod()
    {
        var days = new[] { 11, 12, 13, 14, 15, 16, 18 }.Select(d => (K(2026, 9, d), 5L)).ToArray();
        Assert.Equal(3, RecapFixtures.Recap(RecapFixtures.Ledger(K(2026, 9, 1), days), RecapScope.Week).BestStreak);
        Assert.Equal(6, RecapFixtures.Recap(RecapFixtures.Ledger(K(2026, 9, 1), days), RecapScope.Month).BestStreak);
    }

    [Fact]
    public void GraduationsInThePeriodOnly()
    {
        var dex = new IRecapGraduate[]
        {
            new RecapFixtures.Graduate("this-week", RecapFixtures.Date(2026, 9, 17)),
            new RecapFixtures.Graduate("monday", RecapFixtures.Date(2026, 9, 14, 0)),
            new RecapFixtures.Graduate("let-go", RecapFixtures.Date(2026, 9, 16), IsReleased: true),
            new RecapFixtures.Graduate("last-week", RecapFixtures.Date(2026, 9, 13, 23)),
        };
        var l = RecapFixtures.Ledger(K(2026, 9, 1));
        Assert.Equal(["this-week", "monday"],
            RecapFixtures.Recap(l, RecapScope.Week, dex: dex).Graduated.Cast<RecapFixtures.Graduate>().Select(g => g.Id));
        Assert.Equal(3, RecapFixtures.Recap(l, RecapScope.Month, dex: dex).Graduated.Count);
    }

    [Fact]
    public void GoingBackStopsWhereTheLedgerStarts()
    {
        var l = RecapFixtures.Ledger(K(2026, 9, 1), (K(2026, 9, 2), 10));
        Assert.False(RecapFixtures.Recap(l, RecapScope.Month).CanGoBack);
        Assert.True(RecapFixtures.Recap(l, RecapScope.Week, offset: -1).CanGoBack);
        Assert.False(RecapFixtures.Recap(l, RecapScope.Week, offset: -2).CanGoBack);
        Assert.False(RecapFixtures.Recap(l, RecapScope.Year).CanGoBack);
    }

    [Fact]
    public void AnEmptyLedgerStillDrawsThePeriod()
    {
        var week = RecapFixtures.Recap(new UsageLedger(), RecapScope.Week);
        Assert.Equal(7, week.Buckets.Count);
        Assert.DoesNotContain(week.Buckets, b => b.HasData);
        Assert.Equal(0, week.Total);
        Assert.Null(week.BestDay);
        Assert.Null(week.PreviousTotal);
        Assert.False(week.CanGoBack);
    }

    [Fact]
    public void TheBestDayIsTheFirstOfEqualMaxima()
    {
        var week = RecapFixtures.Recap(RecapFixtures.Ledger(K(2026, 9, 1), (K(2026, 9, 15), 9), (K(2026, 9, 16), 9)), RecapScope.Week);
        Assert.Equal(new UsageRecap.Day(K(2026, 9, 15), 9), week.BestDay);
    }
}
