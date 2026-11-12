using PokeTokenBar.App;
using PokeTokenBar.Core;

namespace PokeTokenBar.App.Tests;

/// <summary>Port of the DailyTrendMetrics / MonthDailyTrend cases of MonthDailyTrendTests.swift (#430).</summary>
public class DailyTrendUiTests
{
    private static DailyUsage U(string date, long tokens) => new(date, 0, tokens, 0, 0, tokens, 0);

    [Fact]
    public void DailyTrendColumnsByWindow()
    {
        var today = "2026-10-15";
        var series = new[] { U("2026-10-14", 100), U("2026-10-15", 200) };

        var cols7 = DailyTrendMetrics.Columns(series, today, DailyTrendWindow.D7);
        Assert.Equal(7, cols7.Count);
        Assert.Equal("2026-10-09", cols7[0].Date);
        Assert.Equal(today, cols7[^1].Date);
        Assert.All(cols7, c => Assert.False(c.IsFuture));
        Assert.Equal(200, cols7[^1].Tokens);

        var cols14 = DailyTrendMetrics.Columns(series, today, DailyTrendWindow.D14);
        Assert.Equal(14, cols14.Count);
        Assert.Equal("2026-10-02", cols14[0].Date);
        Assert.Equal(today, cols14[^1].Date);
        Assert.All(cols14, c => Assert.False(c.IsFuture));

        var cols21 = DailyTrendMetrics.Columns(series, today, DailyTrendWindow.D21);
        Assert.Equal(21, cols21.Count);
        Assert.Equal("2026-09-25", cols21[0].Date);
        Assert.Equal(today, cols21[^1].Date);
        Assert.All(cols21, c => Assert.False(c.IsFuture));

        var colsMonth = DailyTrendMetrics.Columns(series, today, DailyTrendWindow.Month);
        Assert.Equal(31, colsMonth.Count);
        Assert.Equal("2026-10-01", colsMonth[0].Date);
        Assert.Equal("2026-10-31", colsMonth[^1].Date);
        Assert.False(colsMonth[14].IsFuture);
        Assert.True(colsMonth[15].IsFuture);
    }

    [Fact]
    public void AxisLabelsByWindow()
    {
        var today = "2026-10-15";
        foreach (var win in Enum.GetValues<DailyTrendWindow>())
            Assert.Equal("15", DailyTrendMetrics.AxisLabel(today, today, win));

        Assert.Equal("12", DailyTrendMetrics.AxisLabel("2026-10-12", today, DailyTrendWindow.D7));
        Assert.Null(DailyTrendMetrics.AxisLabel("2026-10-14", today, DailyTrendWindow.D7));

        Assert.Equal("8", DailyTrendMetrics.AxisLabel("2026-10-08", today, DailyTrendWindow.D14));
        Assert.Null(DailyTrendMetrics.AxisLabel("2026-10-12", today, DailyTrendWindow.D14));

        Assert.Equal("8", DailyTrendMetrics.AxisLabel("2026-10-08", today, DailyTrendWindow.D21));
        Assert.Equal("1", DailyTrendMetrics.AxisLabel("2026-10-01", today, DailyTrendWindow.D21));
        Assert.Null(DailyTrendMetrics.AxisLabel("2026-10-05", today, DailyTrendWindow.D21));

        Assert.Equal("1", DailyTrendMetrics.AxisLabel("2026-10-01", "2026-10-10", DailyTrendWindow.D14));
        Assert.Null(DailyTrendMetrics.AxisLabel("2026-10-01", "2026-10-02", DailyTrendWindow.D14));
    }

    [Fact]
    public void AxisLabelsSuppressCollisionsNearMonthBoundary()
    {
        Assert.Equal("2", DailyTrendMetrics.AxisLabel("2026-10-02", "2026-10-02", DailyTrendWindow.D7));
        Assert.Null(DailyTrendMetrics.AxisLabel("2026-10-01", "2026-10-02", DailyTrendWindow.D7));

        var today9 = "2026-10-09";
        Assert.Equal("1", DailyTrendMetrics.AxisLabel("2026-10-01", today9, DailyTrendWindow.D14));
        Assert.Null(DailyTrendMetrics.AxisLabel("2026-10-02", today9, DailyTrendWindow.D14));
        Assert.Equal("9", DailyTrendMetrics.AxisLabel(today9, today9, DailyTrendWindow.D14));

        Assert.Equal("1", DailyTrendMetrics.AxisLabel("2026-10-01", "2026-10-04", DailyTrendWindow.D7));
        Assert.Equal("4", DailyTrendMetrics.AxisLabel("2026-10-04", "2026-10-04", DailyTrendWindow.D7));
    }

    /// <summary>Extra: the next-month-1st branch (a 7-day mark right before the 1st yields to it).</summary>
    [Fact]
    public void RegularMarkJustBeforeTheFirstYieldsToIt()
    {
        // today 11-08 (21d): 10-31 is diff 8 — not a mark; 10-25 diff 14, 11-01 diff 7 → 1st shown via dayOfMonth==1
        // today 11-06 (21d): 10-30 is diff 7 and 2 days before 11-01 (diff 5) → suppressed; 11-01 shown.
        Assert.Null(DailyTrendMetrics.AxisLabel("2026-10-30", "2026-11-06", DailyTrendWindow.D21));
        Assert.Equal("1", DailyTrendMetrics.AxisLabel("2026-11-01", "2026-11-06", DailyTrendWindow.D21));
        Assert.Equal("23", DailyTrendMetrics.AxisLabel("2026-10-23", "2026-11-06", DailyTrendWindow.D21));
    }

    [Fact]
    public void EmptySelectedWindowKeepsSelectableSurfaceWhenAnotherWindowHasHistory()
    {
        Assert.True(MonthDailyTrend.ShouldShowSurface(0, hasAvailableHistory: true));
        Assert.False(MonthDailyTrend.ShouldShowSurface(0, hasAvailableHistory: false));
        Assert.True(MonthDailyTrend.ShouldShowSurface(5, hasAvailableHistory: false));
    }

    [Fact]
    public void TwentyOneDayColumnsKeepTheFirstDayAcrossDstFallBack()
    {
        var series = Enumerable.Range(0, 21).Select(i => U(new DateTime(2026, 10, 12).AddDays(i).ToString("yyyy-MM-dd"), i == 0 ? 123 : 0)).ToList();
        var cols21 = DailyTrendMetrics.Columns(series, "2026-11-01", DailyTrendWindow.D21);
        Assert.Equal(21, cols21.Count);
        Assert.Equal("2026-10-12", cols21[0].Date);
        Assert.Equal(123, cols21[0].Tokens);
        Assert.Equal(123, cols21.Sum(c => c.Tokens));
    }
}
