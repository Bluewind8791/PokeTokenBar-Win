using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PokeTokenBar.App;
using PokeTokenBar.Core;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// Port of the grid and panel cases of ActivityVisualizationTests.swift (#449). The choice itself (default, cycle,
/// persistence) is Core and lives in Core.Tests.
/// </summary>
public class ActivityVisualizationUiTests
{
    private static Dictionary<string, long> NoDays() => [];

    private static List<ActivityCell> Cells(List<List<ActivityCell>> columns) => columns.SelectMany(c => c).ToList();

    // MARK: Grid geometry

    [Fact]
    public void EveryColumnHasSevenDaysSoWeekdayRowsLineUp()
    {
        var columns = ActivityHeatmapMetrics.Columns(NoDays(), "2026-09-01", "2026-09-30", DayOfWeek.Monday);
        Assert.NotEmpty(columns);
        Assert.All(columns, c => Assert.Equal(7, c.Count));
    }

    /// <summary>Days outside the range at the start of the first column and the end of the last are marked. Without
    /// the flag last month's final day draws as a zero and reads as an unused day.</summary>
    [Fact]
    public void DaysOutsideTheMonthAreMarkedRatherThanDrawnAsZero()
    {
        var cells = Cells(ActivityHeatmapMetrics.Columns(NoDays(), "2026-09-01", "2026-09-30", DayOfWeek.Monday));
        // 2026-09-01 is a Tuesday, so a Monday-first grid starts with August 31.
        Assert.Equal("2026-08-31", cells.First(c => c.IsOutsideRange).Date);
        Assert.All(cells.Where(c => !c.IsOutsideRange), c => Assert.StartsWith("2026-09", c.Date));
        Assert.Equal(30, cells.Count(c => !c.IsOutsideRange));
    }

    /// <summary>
    /// Every week start × both DST directions: dates run on without a repeat or a gap. The Swift version guards
    /// against a fixed 86400-second stride; here days step through <see cref="DateOnly"/>, so this checks contiguity.
    /// </summary>
    [Fact]
    public void GridStaysContiguousAcrossDSTInEveryWeekStartAndDirection()
    {
        foreach (var firstDay in new[] { DayOfWeek.Sunday, DayOfWeek.Monday })
            foreach (var (first, last) in new[] { ("2026-03-01", "2026-03-31"), ("2026-11-01", "2026-11-30") })
            {
                var dates = Cells(ActivityHeatmapMetrics.Columns(NoDays(), first, last, firstDay)).Select(c => c.Date).ToList();
                Assert.Equal(dates.Count, dates.Distinct().Count());
                for (var i = 1; i < dates.Count; i++)
                    Assert.Equal(DateOnly.Parse(dates[i - 1], CultureInfo.InvariantCulture).AddDays(1),
                        DateOnly.Parse(dates[i], CultureInfo.InvariantCulture));
            }
    }

    [Fact]
    public void GridStartDayFollowsTheFirstWeekday()
    {
        var monday = ActivityHeatmapMetrics.Columns(NoDays(), "2026-09-01", "2026-09-30", DayOfWeek.Monday);
        var sunday = ActivityHeatmapMetrics.Columns(NoDays(), "2026-09-01", "2026-09-30", DayOfWeek.Sunday);
        Assert.Equal("2026-08-31", monday[0][0].Date);
        Assert.Equal("2026-08-30", sunday[0][0].Date);
    }

    /// <summary>The grid covers the whole month whatever its length and starting weekday — one month would pass on
    /// the case where February fits exactly four weeks.</summary>
    [Fact]
    public void GridCoversEveryDayOfEveryMonthInAYear()
    {
        for (var month = 1; month <= 12; month++)
        {
            var start = $"2026-{month:00}-01";
            var days = DateTime.DaysInMonth(2026, month);
            var last = $"2026-{month:00}-{days:00}";
            var inRange = Cells(ActivityHeatmapMetrics.Columns(NoDays(), start, last, DayOfWeek.Monday))
                .Where(c => !c.IsOutsideRange).Select(c => c.Date).ToList();
            Assert.Equal(days, inRange.Count);
            Assert.Equal(start, inRange[0]);
            Assert.Equal(last, inRange[^1]);
        }
    }

    [Fact]
    public void UnparseableRangeYieldsAnEmptyGridRatherThanCrashing()
    {
        Assert.Empty(ActivityHeatmapMetrics.Columns(NoDays(), "nope", "2026-09-30", DayOfWeek.Monday));
        Assert.Empty(ActivityHeatmapMetrics.Columns(NoDays(), "2026-09-30", "2026-09-01", DayOfWeek.Monday));   // reversed
        Assert.Empty(ActivityHeatmapMetrics.RecentGrid(NoDays(), null, "nope", DayOfWeek.Monday));
    }

    /// <summary>Row labels start where the grid's columns start, or every row is labelled with the wrong day.</summary>
    [Fact]
    public void WeekdayLabelsFollowTheFirstWeekdayAndTheDisplayLanguage()
    {
        Assert.Equal(new[] { "M", "T", "W", "T", "F", "S", "S" },
            ActivityHeatmapMetrics.WeekdayLabels(DayOfWeek.Monday, AppLanguage.En.DisplayCulture()));
        var sundayFirst = ActivityHeatmapMetrics.WeekdayLabels(DayOfWeek.Sunday, AppLanguage.Ko.DisplayCulture());
        Assert.Equal("일", sundayFirst[0]);
        Assert.Equal("토", sundayFirst[^1]);
        foreach (var lang in Enum.GetValues<AppLanguage>())
            Assert.All(ActivityHeatmapMetrics.WeekdayLabels(DayOfWeek.Monday, lang.DisplayCulture()),
                label => Assert.Single(StringInfo.GetTextElementEnumerator(label).AsEnumerable()));
    }

    // MARK: Shade levels

    /// <summary>0 (unused) splits from 1 (a little). Boundaries are inclusive, so a day at exactly 25% does not leak up.</summary>
    [Fact]
    public void LevelSeparatesUnusedDaysAndSplitsTheRestIntoQuartiles()
    {
        const long peak = 100_000_000;
        Assert.Equal(0, ActivityHeatmapMetrics.Level(0, peak));
        Assert.Equal(1, ActivityHeatmapMetrics.Level(1, peak));
        Assert.Equal(1, ActivityHeatmapMetrics.Level(25_000_000, peak));
        Assert.Equal(2, ActivityHeatmapMetrics.Level(25_000_001, peak));
        Assert.Equal(2, ActivityHeatmapMetrics.Level(50_000_000, peak));
        Assert.Equal(3, ActivityHeatmapMetrics.Level(75_000_000, peak));
        Assert.Equal(4, ActivityHeatmapMetrics.Level(peak, peak));
    }

    /// <summary>Cells outside the range are drawn as nothing — any color would claim something about that day. The
    /// levels use five distinct contribution greens per appearance, never the accent color.</summary>
    [Fact]
    public void OutsideRangeCellsAreDrawnAsNothing()
    {
        Assert.Null(ActivityHeatmapMetrics.FillKey(-1));
        Assert.NotNull(ActivityHeatmapMetrics.FillKey(0));   // an unused day is drawn: the day existed
        foreach (var dark in new[] { false, true })
        {
            var resources = new ResourceDictionary();
            Theme.Apply(resources, dark);
            var colors = Enumerable.Range(0, 5).Select(level => ((SolidColorBrush)resources[ActivityHeatmapMetrics.FillKey(level)!]).Color).ToList();
            Assert.Equal(5, colors.Distinct().Count());
            Assert.DoesNotContain(((SolidColorBrush)resources[Theme.Keys.Accent]).Color, colors);
        }
    }

    // MARK: The popover's window

    private static DailyUsage Usage(string date, long tokens) => new(date, 0, tokens, 0, 0, tokens, 0);

    /// <summary>Every day of the month through <paramref name="today"/>, 1,000 tokens a day.</summary>
    private static List<DailyUsage> Series(string today) =>
        Enumerable.Range(1, int.Parse(today[^2..])).Select(d => Usage($"{today[..7]}-{d:00}", 1_000)).ToList();

    /// <summary>A ledger that has recorded every day of the months given, at 1,000 tokens a day.</summary>
    private static UsageLedger Ledger(params string[] months)
    {
        var ledger = new UsageLedger();
        foreach (var month in months)
            ledger.Merge(Series($"{month}-{DateTime.DaysInMonth(int.Parse(month[..4]), int.Parse(month[5..])):00}"));
        return ledger;
    }

    private static List<List<ActivityCell>> Grid(UsageLedger ledger, IReadOnlyList<DailyUsage> series, string today,
        DayOfWeek firstDay = DayOfWeek.Monday) =>
        ActivityHeatmapMetrics.RecentGrid(ActivityHeatmapMetrics.Days(ledger, series),
            ActivityHeatmapMetrics.RecordedSince(ledger, series), today, firstDay);

    /// <summary>Always <c>Weeks</c> columns ending with today's week — on the 1st, mid-month, and around the
    /// November DST change with Sunday-first weeks.</summary>
    [Fact]
    public void GridIsAlwaysSixteenWeeksEndingWithTodaysWeek()
    {
        var history = Ledger("2026-06", "2026-07", "2026-08", "2026-09", "2026-10");
        foreach (var (today, firstDay) in new[] { ("2026-10-01", DayOfWeek.Monday), ("2026-10-19", DayOfWeek.Monday),
                     ("2026-11-03", DayOfWeek.Sunday), ("2026-10-31", DayOfWeek.Sunday) })
        {
            var grid = Grid(history, [], today, firstDay);
            Assert.Equal(ActivityHeatmapMetrics.Weeks, grid.Count);
            Assert.Contains(grid[^1], c => c.Date == today);
            Assert.Equal(today, Cells(grid).Last(c => !c.IsOutsideRange).Date);
            Assert.Equal(ActivityHeatmapMetrics.Weeks * 7, Cells(grid).Select(c => c.Date).Distinct().Count());
        }
    }

    /// <summary>The point of the window: the 1st does not empty the grid. September comes from the ledger, October
    /// from the live series.</summary>
    [Fact]
    public void TheFirstOfTheMonthKeepsLastMonthsDays()
    {
        var used = Cells(Grid(Ledger("2026-08", "2026-09"), Series("2026-10-01"), "2026-10-01"))
            .Where(c => !c.IsOutsideRange && c.Tokens > 0).Select(c => c.Date).ToList();
        Assert.Contains("2026-09-30", used);
        Assert.Contains("2026-10-01", used);
        Assert.True(used.Count > 7 * 8, "the window should be filled from the ledger");
    }

    /// <summary>Before the app started recording there is no data, which is not the same as no usage: those days are
    /// outside the range, while the grid keeps its width.</summary>
    [Fact]
    public void DaysBeforeRecordingBeganAreOutsideTheRange()
    {
        var grid = Grid(new UsageLedger(), Series("2026-10-19"), "2026-10-19");
        Assert.Equal(ActivityHeatmapMetrics.Weeks, grid.Count);
        Assert.Equal("2026-10-01", Cells(grid).First(c => !c.IsOutsideRange).Date);
        Assert.Contains(Cells(grid), c => c.Date == "2026-09-30" && c.IsOutsideRange);
    }

    /// <summary>With nothing recorded and no series, only today is in range.</summary>
    [Fact]
    public void WithNothingRecordedOnlyTodayIsInRange()
    {
        var grid = Grid(new UsageLedger(), [], "2026-10-19");
        Assert.Equal(new[] { "2026-10-19" }, Cells(grid).Where(c => !c.IsOutsideRange).Select(c => c.Date));
    }

    /// <summary>The ledger saves once per refresh, so its today can trail the live series: the larger wins, like
    /// <see cref="UsageLedger.Merge"/>, so the grid never shows less than the bars for the same day.</summary>
    [Fact]
    public void LiveSeriesWinsWhenTheLedgerTrails()
    {
        var history = new UsageLedger();
        history.Merge([Usage("2026-10-19", 500)]);
        Assert.Equal(2_000, ActivityHeatmapMetrics.Days(history, [Usage("2026-10-19", 2_000)])["2026-10-19"]);
        Assert.Equal(500, ActivityHeatmapMetrics.Days(history, [Usage("2026-10-19", 100)])["2026-10-19"]);   // a day never goes down
    }

    /// <summary>The title states the window, so its number follows <c>Weeks</c> in every language.</summary>
    [Fact]
    public void TitleStatesTheWindowLength()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
            Assert.Contains(ActivityHeatmapMetrics.Weeks.ToString(CultureInfo.InvariantCulture), new L(lang).RecentHeatmap);
    }

    /// <summary>The readout follows the hovered cell: this month's days with cost like the bars, earlier days tokens
    /// only (the ledger keeps no cost), nothing for a day outside the grid.</summary>
    [Fact]
    public void ReadoutReadsTheSeriesThenTheLedger()
    {
        var l = new L(AppLanguage.En);
        var series = new List<DailyUsage> { new("2026-10-19", 0, 2_000_000, 0, 0, 2_000_000, 6.34) };
        var grid = Grid(Ledger("2026-09"), series, "2026-10-19");
        Assert.Equal(DailyTrendMetrics.Readout(series, "2026-10-19", true, l),
            ActivityHeatmapMetrics.Readout(grid, series, "2026-10-19", true, l));
        Assert.Contains("$", ActivityHeatmapMetrics.Readout(grid, series, "2026-10-19", true, l));
        Assert.Equal($"{HomeFormat.DayStamp("2026-09-15", AppLanguage.En)} 1K",
            ActivityHeatmapMetrics.Readout(grid, series, "2026-09-15", true, l));
        Assert.Equal("", ActivityHeatmapMetrics.Readout(grid, series, "2025-01-01", true, l));
    }

    // MARK: Panel

    private const string Today = "2026-08-24";

    private static FrameworkElement? Panel(ActivityVisualization visualization, IReadOnlyList<DailyUsage> series,
        IReadOnlyList<DailyUsage>? heatmapSeries = null, UsageLedger? history = null,
        DailyTrendWindow window = DailyTrendWindow.Month, bool hasAvailableHistory = false, Action? onCycle = null) =>
        ActivityPanel.Build(visualization, series, heatmapSeries, history ?? new UsageLedger(), false, window, _ => { },
            hasAvailableHistory, Today, new L(AppLanguage.En), onCycle ?? (() => { }));

    /// <summary>The panel hands the ledger to the grid. Dropping it would bring back a grid that empties on the 1st.</summary>
    [Fact]
    public void PanelGridDrawsTheLedgersEarlierMonths() => Sta(() =>
    {
        var history = Ledger("2026-07");
        var total = Series(Today);
        var l = new L(AppLanguage.En);
        var panel = Pixels(Panel(ActivityVisualization.RecentHeatmap, total, history: history));
        var withHistory = Pixels(ActivityHeatmap.Build(total, history, false, Today, l, PageDots.Build(ActivityVisualization.RecentHeatmap)));
        var monthOnly = Pixels(ActivityHeatmap.Build(total, new UsageLedger(), false, Today, l, PageDots.Build(ActivityVisualization.RecentHeatmap)));
        Assert.NotEqual(withHistory, monthOnly);   // July must show, or this test proves nothing
        Assert.Equal(withHistory, panel);
    });

    /// <summary>The wrapper keeps the selected bar window and its picker.</summary>
    [Fact]
    public void PanelPreservesTheSelectedBarPeriodAndPicker() => Sta(() =>
    {
        var selected = Series(Today).TakeLast(7).ToList();
        var l = new L(AppLanguage.En);
        var dots = () => PageDots.Build(ActivityVisualization.MonthlyBars);
        var panel = Pixels(Panel(ActivityVisualization.MonthlyBars, selected, window: DailyTrendWindow.D7, hasAvailableHistory: true));
        var expected = Pixels(MonthDailyTrend.Build(selected, false, Today, l, DailyTrendWindow.D7, _ => { }, true, dots()));
        var month = Pixels(MonthDailyTrend.Build(selected, false, Today, l, DailyTrendWindow.Month, _ => { }, false, dots()));
        Assert.NotEqual(expected, month);   // the fixture must tell the selected period apart
        Assert.Equal(expected, panel);
    });

    /// <summary>An empty selected window keeps the period picker and the switch dots on screen for recovery.</summary>
    [Fact]
    public void PanelKeepsThePickerForAnEmptySelectedWindow() => Sta(() =>
    {
        var empty = Series(Today).TakeLast(7).Select(d => Usage(d.Date, 0)).ToList();
        var panel = Panel(ActivityVisualization.MonthlyBars, empty, window: DailyTrendWindow.D7, hasAvailableHistory: true);
        Assert.NotNull(panel);
        Assert.True(Measure(panel).DesiredSize.Height > 0);
    });

    /// <summary>A short bar window must not remove this month's earlier live usage from the grid.</summary>
    [Fact]
    public void PanelGridUsesTheWholeMonthIndependentlyOfTheBarWindow() => Sta(() =>
    {
        var month = Series(Today);
        var selected = month.TakeLast(7).ToList();
        var l = new L(AppLanguage.En);
        var panel = Pixels(Panel(ActivityVisualization.RecentHeatmap, selected, heatmapSeries: month, window: DailyTrendWindow.D7));
        var expected = Pixels(ActivityHeatmap.Build(month, new UsageLedger(), false, Today, l, PageDots.Build(ActivityVisualization.RecentHeatmap)));
        var recentOnly = Pixels(ActivityHeatmap.Build(selected, new UsageLedger(), false, Today, l, PageDots.Build(ActivityVisualization.RecentHeatmap)));
        Assert.NotEqual(expected, recentOnly);   // the fixture must contain usage outside the bar window
        Assert.Equal(expected, panel);
    });

    /// <summary>Every visualization draws something — the panel's switch throws on a member without a view.</summary>
    [Fact]
    public void EveryVisualizationDrawsAPanel() => Sta(() =>
    {
        foreach (var visualization in ActivityVisualizations.AllCases)
            Assert.NotNull(Panel(visualization, Series(Today), history: Ledger("2026-07")));
    });

    /// <summary>A click on the panel moves on to the next visualization; the dots count every member.</summary>
    [Fact]
    public void ClickingThePanelCyclesAndTheDotsCountEveryVisualization() => Sta(() =>
    {
        var cycled = 0;
        var panel = Panel(ActivityVisualization.MonthlyBars, Series(Today), onCycle: () => cycled++)!;
        panel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.Equal(1, cycled);
        Assert.Equal(ActivityVisualizations.AllCases.Count,
            ((Panel)PageDots.Build(ActivityVisualization.MonthlyBars)).Children.Count);
    });

    /// <summary>The dots are the only hint that the row switches, so every visualization carries them — the bars
    /// also in their caption-only state (an empty selected window).</summary>
    [Fact]
    public void PanelShowsTheSwitchDotsInEveryVisualization() => Sta(() =>
    {
        static int Dots(DependencyObject root) =>
            (root is System.Windows.Shapes.Ellipse ? 1 : 0)
            + LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Sum(Dots);
        foreach (var visualization in ActivityVisualizations.AllCases)
            Assert.Equal(ActivityVisualizations.AllCases.Count, Dots(Panel(visualization, Series(Today))!));
        var empty = Series(Today).TakeLast(7).Select(d => Usage(d.Date, 0)).ToList();
        Assert.Equal(ActivityVisualizations.AllCases.Count,
            Dots(Panel(ActivityVisualization.MonthlyBars, empty, window: DailyTrendWindow.D7, hasAvailableHistory: true)!));
    });

    /// <summary>The grid shows only when a day in range has usage (<c>peak &gt; 0</c>), like the bars — and the
    /// ledger alone counts: a quiet month after a busy one still draws.</summary>
    [Fact]
    public void HeatmapShowsOnlyWithUsageInRange() => Sta(() =>
    {
        var l = new L(AppLanguage.En);
        var quiet = Series(Today).Select(d => Usage(d.Date, 0)).ToList();
        Assert.Null(ActivityHeatmap.Build(quiet, new UsageLedger(), false, Today, l));
        Assert.NotNull(ActivityHeatmap.Build(quiet, Ledger("2026-07"), false, Today, l));
    });

    // MARK: Rendering helpers

    /// <summary>Lays the element out at the popover's content width, with the dark theme's resources.</summary>
    private static FrameworkElement Measure(FrameworkElement? element)
    {
        var host = new Border { Width = Theme.ScrollContentWidth, Child = element };
        Theme.Apply(host.Resources, dark: true);
        host.Measure(new Size(Theme.ScrollContentWidth, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        return host;
    }

    private static byte[] Pixels(FrameworkElement? element)
    {
        var host = Measure(element);
        var width = (int)Math.Ceiling(host.ActualWidth * 2);
        var height = Math.Max(1, (int)Math.Ceiling(host.ActualHeight * 2));
        var bitmap = new RenderTargetBitmap(width, height, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>WPF elements need an STA thread.</summary>
    private static void Sta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

internal static class TextElementEnumeratorExtensions
{
    public static IEnumerable<object> AsEnumerable(this TextElementEnumerator e)
    {
        while (e.MoveNext()) yield return e.Current;
    }
}
