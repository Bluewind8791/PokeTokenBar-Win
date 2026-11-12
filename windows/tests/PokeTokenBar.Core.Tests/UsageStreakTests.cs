using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Port of the Core cases of UsageStreakTests.swift (#451): the current streak is read from <see cref="UsageLedger"/>,
/// the recap's day-by-day history. The badge cases are App code and live in PokeTokenBar.App.Tests.
/// <para>
/// The traps are the day boundaries. A run must survive the 1st of the month (providers report the current month
/// only, which is why the ledger exists), must not break on a morning before the first prompt, and must step days
/// through the calendar — the DST case below is green in a zone without DST, so it runs in America/Los_Angeles.
/// </para>
/// </summary>
public sealed class UsageStreakTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private static RecapCalendar Calendar(TimeZoneInfo zone) => new(zone, DayOfWeek.Monday);

    private static DateTimeOffset Date(int year, int month, int day, TimeZoneInfo zone, int hour = 12)
    {
        var local = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>A ledger recording since <paramref name="since"/>, with usage on <paramref name="active"/> days and nothing on the others.</summary>
    private static UsageLedger Ledger(string since, params string[] active) =>
        RecapFixtures.Ledger(since, active.Select(d => (d, 1_000L)).ToArray());

    private static UsageStreak Streak(UsageLedger ledger, DateTimeOffset now, TimeZoneInfo zone) =>
        ledger.CurrentStreak(now, Calendar(zone));

    // MARK: Counting

    [Fact]
    public void ARunEndingTodayCountsToday()
    {
        var ledger = Ledger("2026-09-01", "2026-09-16", "2026-09-17", "2026-09-18");
        Assert.Equal(new UsageStreak(3, IncludesToday: true), Streak(ledger, Date(2026, 9, 18, Utc), Utc));
    }

    /// <summary>Before the first prompt of the day the run is intact and ends yesterday. If an unused today broke
    /// it, the badge would vanish every morning.</summary>
    [Fact]
    public void AnUnusedTodayDoesNotBreakTheRun()
    {
        var ledger = Ledger("2026-09-01", "2026-09-16", "2026-09-17");
        Assert.Equal(new UsageStreak(2, IncludesToday: false), Streak(ledger, Date(2026, 9, 18, Utc, hour: 8), Utc));
    }

    [Fact]
    public void AWholeDayWithoutUsageEndsTheRun()
    {
        var ledger = Ledger("2026-09-01", "2026-09-14", "2026-09-15", "2026-09-17", "2026-09-18");
        Assert.Equal(2, Streak(ledger, Date(2026, 9, 18, Utc), Utc).Days);   // the 16th was empty

        // Yesterday and today empty: the run is over.
        var lapsed = Ledger("2026-09-01", "2026-09-15", "2026-09-16");
        Assert.Equal(UsageStreak.None, Streak(lapsed, Date(2026, 9, 18, Utc), Utc));
    }

    /// <summary>The reason the streak reads the ledger and not the month series: on the 1st the series holds a
    /// single day, which would reset every run once a month.</summary>
    [Fact]
    public void TheRunCarriesAcrossTheFirstOfTheMonth()
    {
        var ledger = Ledger("2026-09-01", "2026-09-28", "2026-09-29", "2026-09-30", "2026-10-01");
        Assert.Equal(4, Streak(ledger, Date(2026, 10, 1, Utc), Utc).Days);
    }

    /// <summary>
    /// Stepping back a fixed 86400 seconds from midnight after the spring-forward day lands on the day before it,
    /// skipping it: an empty 8 March would be jumped over and the run would wrongly continue. Checked by injecting
    /// the 86400 step (<c>LocalDate(Midnight(day) - 86400 s)</c>) — the spring assertion fails with it.
    /// </summary>
    [Fact]
    public void StepsThroughDSTWithoutSkippingADay()
    {
        var spring = Ledger("2026-03-01", "2026-03-06", "2026-03-07", "2026-03-09");
        // 8 March (23 hours long) is empty and must end the run.
        Assert.Equal(1, Streak(spring, Date(2026, 3, 9, Pacific), Pacific).Days);

        // 1 November (25 hours long) must count once.
        var fall = Ledger("2026-10-25", "2026-10-31", "2026-11-01", "2026-11-02");
        Assert.Equal(3, Streak(fall, Date(2026, 11, 2, Pacific), Pacific).Days);
    }

    /// <summary>Same day rule as the recap's best streak: a run that fits inside one week reads the same in both places.</summary>
    [Fact]
    public void AgreesWithTheRecapsBestStreak()
    {
        var now = Date(2026, 9, 18, Utc);
        var ledger = Ledger("2026-09-01", "2026-09-15", "2026-09-16", "2026-09-17", "2026-09-18");
        var week = new RecapPeriod(RecapScope.Week, now, Calendar(Utc));
        var recap = UsageRecap.Make(ledger, [], week, now, Calendar(Utc));
        Assert.Equal(recap.BestStreak, Streak(ledger, now, Utc).Days);
    }

    /// <summary>The store reads the run from its ledger with the injected clock (Swift <c>UsageStore.currentStreak</c>).</summary>
    [Fact]
    public void TheStoreReadsTheRunFromItsLedger()
    {
        var settings = new AppSettings(null);
        RecapFixtures.Ledger("2026-09-01", ("2026-09-16", 10), ("2026-09-17", 10)).Save(settings);
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 18, 12, 0, 0)));
        var store = Stores.Make(settings, now: () => now);
        Assert.Equal(new UsageStreak(2, IncludesToday: false), store.CurrentStreak);
    }

    [Fact]
    public void EveryLanguageNamesTheCount()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var l = new L(language);
            Assert.Contains("12", l.StreakDays(12));
            Assert.Contains("12", l.StreakDaysPending(12));
            Assert.NotEqual(l.StreakDays(12), l.StreakDaysPending(12));
        }
    }
}
