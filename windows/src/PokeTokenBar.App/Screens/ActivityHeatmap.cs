using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// One cell of the heatmap. <c>IsOutsideRange</c>: inside the grid but not part of the range. The grid is cut into
/// whole weeks so the weekday rows line up, which puts days outside the range into the first and last columns.
/// Dropping them shortens a column and shifts every weekday below; drawing them as zero reads as "unused day".
/// </summary>
public sealed record ActivityCell(string Date, long Tokens, bool IsOutsideRange);

/// <summary>
/// Port of <c>ActivityHeatmapMetrics</c> (ActivityHeatmap.swift): grid geometry and shading as pure functions, so
/// the week-boundary and month-start math stays checkable without a window.
/// Day keys step through <see cref="DateOnly"/>, which is calendar arithmetic: there is no fixed 86400 seconds to
/// slip on a DST day, so the Swift <c>calendar</c>/<c>timeZone</c> parameters reduce to the first weekday.
/// </summary>
public static class ActivityHeatmapMetrics
{
    /// <summary>Row height. Cells take the row's width, so the grid fills the popover like the bars do.</summary>
    public const double Cell = 10;
    public const double Spacing = 2;

    /// <summary>
    /// How far back the popover's grid reaches. Long enough that a weekday habit shows over more than one month,
    /// short enough that the cells keep the bars' row height in the popover's width (a year, 53 columns, would not fit).
    /// </summary>
    public const int Weeks = 16;

    /// <summary>
    /// One-letter weekday names for the rows, starting at <paramref name="firstDayOfWeek"/> like <see cref="Columns"/>.
    /// Without them nothing says which row is the weekend — the pattern the grid exists to show. The names come
    /// from the display language; the first weekday from the region.
    /// </summary>
    public static IReadOnlyList<string> WeekdayLabels(DayOfWeek firstDayOfWeek, CultureInfo culture)
    {
        var symbols = culture.DateTimeFormat.ShortestDayNames;    // Sunday first
        var start = (int)firstDayOfWeek % symbols.Length;
        return [.. symbols[start..], .. symbols[..start]];
    }

    /// <summary>
    /// Weeks (columns) × weekdays (rows): the fewest whole weeks covering <paramref name="first"/>…<paramref name="last"/>.
    /// A column starts on <paramref name="firstDayOfWeek"/> — Monday in some regions, Sunday in others: the locale's
    /// call, not this code's. Not day keys, or a reversed range: an empty grid rather than a crash.
    /// </summary>
    public static List<List<ActivityCell>> Columns(IReadOnlyDictionary<string, long> days, string first, string last,
        DayOfWeek firstDayOfWeek)
    {
        if (string.CompareOrdinal(first, last) > 0 || ParseDay(first) is not { } firstDate || ParseDay(last) is not { } lastDate)
            return [];
        var columns = new List<List<ActivityCell>>();
        for (var cursor = StartOfWeek(firstDate, firstDayOfWeek); cursor <= lastDate; cursor = cursor.AddDays(7))
        {
            var column = new List<ActivityCell>(7);
            for (var offset = 0; offset < 7; offset++)
            {
                var key = Key(cursor.AddDays(offset));
                // "yyyy-MM-dd" sorts lexicographically the same way it sorts chronologically.
                column.Add(new ActivityCell(key, days.GetValueOrDefault(key),
                    string.CompareOrdinal(key, first) < 0 || string.CompareOrdinal(key, last) > 0));
            }
            columns.Add(column);
        }
        return columns;
    }

    /// <summary>
    /// The popover's grid: the last <see cref="Weeks"/> weeks, ending with the week <paramref name="today"/> falls in.
    /// The window rolls with today rather than resetting on the 1st, so the grid never opens nearly empty.
    /// Days before <paramref name="recordedSince"/> the app was not recording, and days after today have not happened:
    /// both are outside the range, so neither reads as an unused day.
    /// </summary>
    public static List<List<ActivityCell>> RecentGrid(IReadOnlyDictionary<string, long> days, string? recordedSince,
        string today, DayOfWeek firstDayOfWeek)
    {
        if (ParseDay(today) is not { } todayDate) return [];
        var windowStart = Key(StartOfWeek(todayDate, firstDayOfWeek).AddDays(-7 * (Weeks - 1)));
        // With nothing recorded yet, only today can be vouched for (the series is about to fill it).
        var since = recordedSince ?? today;
        var first = string.CompareOrdinal(windowStart, since) >= 0 ? windowStart : since;
        // Lay the grid out from the window's first week even when recording began later, so the width stays
        // `Weeks` columns, then mark the unrecorded days.
        return Columns(days, windowStart, today, firstDayOfWeek)
            .Select(column => column
                .Select(cell => string.CompareOrdinal(cell.Date, first) < 0 ? cell with { IsOutsideRange = true } : cell)
                .ToList())
            .ToList();
    }

    /// <summary>
    /// The ledger's days with this month's live series laid over them. The ledger is written once per refresh, so
    /// today in the ledger can trail the series the bars draw; taking the larger keeps the two views agreeing on
    /// today (the same rule as <see cref="UsageLedger.Merge"/>).
    /// </summary>
    public static Dictionary<string, long> Days(UsageLedger ledger, IEnumerable<DailyUsage> series)
    {
        var days = new Dictionary<string, long>(ledger.TokensByDay, StringComparer.Ordinal);
        foreach (var day in series)
            if (day.TotalTokens > 0) days[day.Date] = Math.Max(days.GetValueOrDefault(day.Date), day.TotalTokens);
        return days;
    }

    /// <summary>The first day the grid can vouch for: the ledger's coverage, or the series' first day when it reaches
    /// back further (a fresh ledger that has not been saved yet).</summary>
    public static string? RecordedSince(UsageLedger ledger, IEnumerable<DailyUsage> series) =>
        new[] { ledger.CoveredSince, series.Select(d => d.Date).Min(StringComparer.Ordinal) }
            .Where(d => d != null).Min(StringComparer.Ordinal);

    /// <summary>
    /// Shade level 0…4: 0 is an unused day, 1…4 are quartiles of the peak (boundaries inclusive). No
    /// <c>peak == 0</c> guard: <paramref name="peak"/> is the window's maximum, so <c>tokens &gt; 0</c> implies
    /// <c>peak &gt;= tokens &gt; 0</c> (and the view draws only when <c>peak &gt; 0</c>).
    /// </summary>
    public static int Level(long tokens, long peak)
    {
        if (tokens <= 0) return 0;
        var ratio = tokens / (double)peak;
        if (ratio <= 0.25) return 1;
        if (ratio <= 0.50) return 2;
        if (ratio <= 0.75) return 3;
        return 4;
    }

    /// <summary>Level → theme key of its contribution green; null (drawn as nothing) for a cell outside the range
    /// (<c>-1</c>): any color would claim something about that day.</summary>
    public static string? FillKey(int level) =>
        level >= 0 && level < Theme.Keys.ContributionGreens.Length ? Theme.Keys.ContributionGreens[level] : null;

    /// <summary>
    /// The caption readout for <paramref name="day"/>: this month's days read like the bars' (cost included); earlier
    /// days have tokens only — the ledger does not keep cost. Empty for a day outside the grid.
    /// </summary>
    public static string Readout(IReadOnlyList<List<ActivityCell>> grid, IReadOnlyList<DailyUsage> series, string day,
        bool showsCost, L l)
    {
        if (series.Any(d => d.Date == day)) return DailyTrendMetrics.Readout(series, day, showsCost, l);
        if (grid.SelectMany(c => c).FirstOrDefault(c => c.Date == day) is not { } cell) return "";
        return $"{HomeFormat.DayStamp(day, l.Lang)} {TokenFormatter.Compact(cell.Tokens)}";
    }

    private static DateOnly? ParseDay(string key) =>
        DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Key(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDayOfWeek) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
}

/// <summary>
/// Port of <c>ActivityHeatmap</c>: the last 16 weeks of usage as a calendar grid —
/// <see cref="ActivityVisualization.RecentHeatmap"/>. Bars compare amounts by height; the grid's rows are fixed
/// weekdays, so a habit like "barely anything on weekends" shows at a glance, across months.
/// Shown only when some day in range has usage (<c>peak &gt; 0</c>), like the bars.
/// </summary>
public static class ActivityHeatmap
{
    /// <param name="series">This month's totals, for the readout with cost and for days the ledger has not saved yet.</param>
    /// <param name="history">The days before this month (<see cref="UsageStore.DailyLedger"/>).</param>
    /// <param name="trailing">The switch dots, as in <see cref="MonthDailyTrend.Build"/>.</param>
    /// <param name="firstDayOfWeek">The region's first weekday; the current culture's when null.</param>
    public static FrameworkElement? Build(IReadOnlyList<DailyUsage> series, UsageLedger history, bool showsCost,
        string today, L l, FrameworkElement? trailing = null, DayOfWeek? firstDayOfWeek = null)
    {
        var firstDay = firstDayOfWeek ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var grid = ActivityHeatmapMetrics.RecentGrid(ActivityHeatmapMetrics.Days(history, series),
            ActivityHeatmapMetrics.RecordedSince(history, series), today, firstDay);
        var used = grid.SelectMany(c => c).Where(c => !c.IsOutsideRange && c.Tokens > 0).ToList();
        var peak = used.Count == 0 ? 0 : used.Max(c => c.Tokens);
        if (peak <= 0) return null;

        // Cells are too small for their own tooltip, so the caption's readout follows the pointer and falls back to today.
        var readout = Ui.Text(ActivityHeatmapMetrics.Readout(grid, series, today, showsCost, l), Theme.FontSize.Caption,
            Theme.Keys.Secondary, FontWeights.Medium, monospacedDigits: true);
        readout.TextTrimming = TextTrimming.None;
        void Hover(string? day) => readout.Text = ActivityHeatmapMetrics.Readout(grid, series, day ?? today, showsCost, l);

        // The peak hides on a single used day, for the bars' reason (DailyTrendMetrics.ShowsPeak).
        var trend = Ui.VStack(3,
            CaptionRow(l, readout, peak, used.Count > 1, trailing),
            GridView(grid, peak, today, l, firstDay, Hover));
        return trend.Padding(0, 4, 0, 0);
    }

    /// <summary>Same layout as the bars' caption — title, readout, peak, switch dots. Shades are relative, so the
    /// peak states the absolute scale.</summary>
    private static FrameworkElement CaptionRow(L l, TextBlock readout, long peak, bool showsPeak, FrameworkElement? trailing)
    {
        var row = Ui.HStack(5,
            Ui.Text(l.RecentHeatmap, Theme.FontSize.Caption, Theme.Keys.Tertiary),
            readout,
            Ui.Spacer());
        if (showsPeak)
        {
            row.Children.Add(Ui.Text(l.PeakDay, Theme.FontSize.Caption2, Theme.Keys.Tertiary));
            row.Children.Add(Ui.Text(TokenFormatter.Compact(peak), Theme.FontSize.Caption2, Theme.Keys.Secondary,
                FontWeights.SemiBold, monospacedDigits: true));
        }
        if (trailing != null) row.Children.Add(trailing);
        return row.BaselineAligned();
    }

    private static FrameworkElement GridView(List<List<ActivityCell>> grid, long peak, string today, L l,
        DayOfWeek firstDay, Action<string?> hover)
    {
        var labels = Ui.VStack(ActivityHeatmapMetrics.Spacing);
        foreach (var label in ActivityHeatmapMetrics.WeekdayLabels(firstDay, l.Lang.DisplayCulture()))
        {
            var text = Ui.Text(label, 8, Theme.Keys.Tertiary);
            text.Height = ActivityHeatmapMetrics.Cell;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            text.LineHeight = ActivityHeatmapMetrics.Cell;
            text.TextTrimming = TextTrimming.None;
            labels.Children.Add(text);
        }
        var columns = new EqualColumns { Spacing = ActivityHeatmapMetrics.Spacing };
        foreach (var column in grid)
        {
            var cells = Ui.VStack(ActivityHeatmapMetrics.Spacing);
            foreach (var cell in column) cells.Children.Add(CellView(cell, peak, today, hover));
            columns.Children.Add(cells);
        }
        var row = Ui.HStack(ActivityHeatmapMetrics.Spacing + 2, labels, columns);
        labels.VerticalAlignment = VerticalAlignment.Top;
        columns.VerticalAlignment = VerticalAlignment.Top;
        return row;
    }

    private static FrameworkElement CellView(ActivityCell cell, long peak, string today, Action<string?> hover)
    {
        var level = cell.IsOutsideRange ? -1 : ActivityHeatmapMetrics.Level(cell.Tokens, peak);
        var fill = new Border { Height = ActivityHeatmapMetrics.Cell, CornerRadius = new CornerRadius(2) };
        // Outside the range or still to come: holds a place, claims nothing (and, without a background, takes no hover).
        if (ActivityHeatmapMetrics.FillKey(level) is not { } key) return fill;
        fill.SetResourceReference(Border.BackgroundProperty, key);
        fill.MouseEnter += (_, _) => hover(cell.Date);
        fill.MouseLeave += (_, _) => hover(null);
        if (cell.Date != today) return fill;
        var host = new Grid { Height = ActivityHeatmapMetrics.Cell };
        var stroke = new Border { CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), Opacity = 0.55, IsHitTestVisible = false };
        stroke.SetResourceReference(Border.BorderBrushProperty, Theme.Keys.Primary);
        host.Children.Add(fill);
        host.Children.Add(stroke);
        return host;
    }
}
