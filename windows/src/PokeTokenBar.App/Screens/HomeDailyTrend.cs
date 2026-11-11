using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>One column of the trend axis. <c>IsFuture</c> is a day of this month that has not come yet.</summary>
public sealed record DailyTrendColumn(string Date, long Tokens, bool IsFuture);

/// <summary>
/// Port of <c>DailyTrendMetrics</c> (PopoverView.swift): the trend geometry as pure functions, so the
/// 0 and peak edges (where a view-local version goes quietly wrong) stay checkable without a window.
/// </summary>
public static class DailyTrendMetrics
{
    /// <summary>Bar track height — sparkline-low, the header is already dense.</summary>
    public const double Track = 26;
    public const double Spacing = 1.5;
    /// <summary>Floor tick for a day without usage: 0 pt would make the day look missing from the axis.</summary>
    public const double Baseline = 1.5;
    public const double TickHeight = 1.5;

    /// <summary>Lays <paramref name="tokens"/> into the track relative to <paramref name="peak"/>; at least the floor tick.</summary>
    public static double BarHeight(long tokens, long peak)
    {
        if (peak <= 0 || tokens <= 0) return Baseline;
        var ratio = Math.Min(1, tokens / (double)peak);
        return Math.Max(Baseline, ratio * Track);
    }

    /// <summary>
    /// The calendar month (1st…last), not the series length: the series ends today, so sizing columns by it
    /// would stretch one bar across the row on the 1st. Days up to today missing from the series are 0;
    /// later days are <c>IsFuture</c>. Other months' dates (a provider still on last month's series just
    /// after midnight) stay off the axis. The fill happens only here — the data keeps ending today.
    /// </summary>
    public static List<DailyTrendColumn> MonthColumns(IReadOnlyList<DailyUsage> series, string today)
    {
        var month = today.Length >= 7 ? today[..7] : today;
        if (!int.TryParse(today.AsSpan(0, Math.Min(4, today.Length)), out var year)
            || !int.TryParse(month.AsSpan(Math.Max(0, month.Length - 2)), out var monthNumber)
            || !int.TryParse(today.AsSpan(Math.Max(0, today.Length - 2)), out var todayOfMonth)
            || monthNumber is < 1 or > 12)
            return series.Select(d => new DailyTrendColumn(d.Date, d.TotalTokens, false)).ToList();
        var tokensByDay = new Dictionary<string, long>();
        foreach (var d in series) tokensByDay[d.Date] = tokensByDay.GetValueOrDefault(d.Date) + d.TotalTokens;
        return Enumerable.Range(1, DateTime.DaysInMonth(year, monthNumber)).Select(day =>
        {
            var date = $"{month}-{day:00}";
            return new DailyTrendColumn(date, tokensByDay.GetValueOrDefault(date), day > todayOfMonth);
        }).ToList();
    }

    /// <summary>Weekend underline only under past days and today — under empty future cells it reads as "__ __".</summary>
    public static bool ShowsWeekendTick(DailyTrendColumn column) => !column.IsFuture && IsWeekend(column.Date);

    /// <summary>Peak only when two or more days have usage; with one it repeats the readout.</summary>
    public static bool ShowsPeak(IReadOnlyList<DailyTrendColumn> columns) => columns.Count(c => c.Tokens > 0) > 1;

    /// <summary>
    /// Label the 1st, every 7th day, and today. Today always gets one (a low bar is a one-line tick, so the
    /// number is the only clue where today is); a regular label within 2 days of today is dropped, since at
    /// ~9 pt per column "21 22" would read as one number.
    /// </summary>
    public static string? AxisLabel(string date, string today, int labelInterval = 7, int minimumSeparation = 3)
    {
        if (!int.TryParse(date.AsSpan(Math.Max(0, date.Length - 2)), out var dayOfMonth)) return null;
        if (date == today) return dayOfMonth.ToString();
        var isRegular = dayOfMonth == 1 || (labelInterval > 0 && dayOfMonth % labelInterval == 0);
        if (!isRegular) return null;
        if (int.TryParse(today.AsSpan(Math.Max(0, today.Length - 2)), out var todayOfMonth)
            && Math.Abs(dayOfMonth - todayOfMonth) < minimumSeparation) return null;
        return dayOfMonth.ToString();
    }

    /// <summary>Saturday/Sunday. (macOS asks the user's calendar; .NET has no locale weekend API.)</summary>
    public static bool IsWeekend(string date) =>
        DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
        && d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Swift <c>MonthDailyTrend.readout</c>: today's "Fri, 10/16 2M $6.34"; empty outside the series.</summary>
    public static string Readout(IReadOnlyList<DailyUsage> series, string today, bool showsCost, L l)
    {
        if (series.FirstOrDefault(d => d.Date == today) is not { } day) return "";
        var stamp = HomeFormat.DayStamp(day.Date, l.Lang);
        var tokens = TokenFormatter.Compact(day.TotalTokens);
        return showsCost ? $"{stamp} {tokens} {day.UsageCost.Text(l)}" : $"{stamp} {tokens}";
    }
}

/// <summary>
/// Port of <c>MonthDailyTrend</c>: this month's daily bars under the period totals. Shown only when some day
/// has usage (<c>peak &gt; 0</c>) — the producer fills unused days with 0, so gating on the series being
/// non-empty would show an empty chart to someone who never used anything this month (#56 class).
/// Windows has a single provider, so the per-provider stacking and legend of the macOS view are not ported.
/// </summary>
public static class MonthDailyTrend
{
    public static FrameworkElement? Build(IReadOnlyList<DailyUsage> series, bool showsCost, string today, L l)
    {
        var columns = DailyTrendMetrics.MonthColumns(series, today);
        var peak = columns.Count == 0 ? 0 : columns.Max(c => c.Tokens);
        if (peak <= 0) return null;
        var trend = Ui.VStack(3,
            CaptionRow(series, today, showsCost, l, peak, DailyTrendMetrics.ShowsPeak(columns)),
            BarRow(columns, peak, series, showsCost, today, l),
            WeekendTickRow(columns),
            AxisRow(columns, today));
        return trend.Padding(0, 4, 0, 0);
    }

    /// <summary>
    /// Caption + today's readout + peak. The readout does not follow the hover (the tooltip shows other days):
    /// opening the popover shows today's total at once. Bars are relative, so the peak gives the scale.
    /// </summary>
    private static FrameworkElement CaptionRow(IReadOnlyList<DailyUsage> series, string today, bool showsCost, L l,
        long peak, bool showsPeak)
    {
        var row = Ui.HStack(5,
            Ui.Text(l.DailyTrend, Theme.FontSize.Caption, Theme.Keys.Tertiary),
            Ui.Text(DailyTrendMetrics.Readout(series, today, showsCost, l), Theme.FontSize.Caption, Theme.Keys.Secondary,
                FontWeights.Medium, monospacedDigits: true),
            Ui.Spacer());
        if (showsPeak)
        {
            row.Children.Add(Ui.Text(l.PeakDay, Theme.FontSize.Caption2, Theme.Keys.Tertiary));
            row.Children.Add(Ui.Text(TokenFormatter.Compact(peak), Theme.FontSize.Caption2, Theme.Keys.Secondary,
                FontWeights.SemiBold, monospacedDigits: true));
        }
        return row.BaselineAligned();
    }

    private static FrameworkElement BarRow(List<DailyTrendColumn> columns, long peak, IReadOnlyList<DailyUsage> series,
        bool showsCost, string today, L l)
    {
        var bars = new EqualColumns { Spacing = DailyTrendMetrics.Spacing, Height = DailyTrendMetrics.Track };
        // The hovered day's numbers float just above the bars, centred on the column and clamped to the row —
        // shown at once, without the tooltip delay.
        var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        Border? tooltip = null;

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var index = i;
            if (column.IsFuture)
            {
                // A day not yet here: no bar, floor tick or weekend line, and not hoverable.
                bars.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
                continue;
            }
            var isToday = column.Date == today;
            // A 0-token day is only the floor tick, which a tiny day also is — one step dimmer tells them apart.
            var isEmptyDay = column.Tokens == 0;
            var restOpacity = isEmptyDay ? 0.18 : 0.45;
            var bar = new Border
            {
                Height = DailyTrendMetrics.BarHeight(column.Tokens, peak),
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = isToday ? 1 : restOpacity,
            };
            bar.SetResourceReference(Border.BackgroundProperty, isToday ? Theme.Keys.Accent : Theme.Keys.Secondary);
            // The whole column is the hover target, so low bars are easy to hit.
            var cell = new Grid
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            cell.Children.Add(bar);
            cell.MouseEnter += (_, _) =>
            {
                if (!isToday) bar.Opacity = 0.8;
                if (tooltip != null) overlay.Children.Remove(tooltip);
                tooltip = TooltipCard(column.Date, series, showsCost, l);
                if (tooltip == null) return;
                overlay.Children.Add(tooltip);
                tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var width = bars.ActualWidth;
                var columnWidth = (width + DailyTrendMetrics.Spacing) / columns.Count;
                var center = columnWidth * (index + 0.5);
                var w = tooltip.DesiredSize.Width;
                Canvas.SetLeft(tooltip, Math.Min(Math.Max(center - w / 2, 0), Math.Max(0, width - w)));
                Canvas.SetTop(tooltip, -tooltip.DesiredSize.Height - 4);
            };
            cell.MouseLeave += (_, _) =>
            {
                if (!isToday) bar.Opacity = restOpacity;
                if (tooltip != null) overlay.Children.Remove(tooltip);
                tooltip = null;
            };
            bars.Children.Add(cell);
        }

        var host = new Grid();
        host.Children.Add(bars);
        host.Children.Add(overlay);
        Panel.SetZIndex(host, 1);   // the tooltip rises over the caption and the rows above
        return host;
    }

    /// <summary>Port of <c>DailyTrendHover.info</c> + <c>tooltipCard</c> (single provider: no per-provider rows).</summary>
    private static Border? TooltipCard(string day, IReadOnlyList<DailyUsage> series, bool showsCost, L l)
    {
        if (series.FirstOrDefault(d => d.Date == day) is not { } total) return null;
        var row = Ui.HStack(5,
            Ui.Text(HomeFormat.DayStamp(day, l.Lang), Theme.FontSize.Caption, weight: FontWeights.SemiBold, monospacedDigits: true),
            Ui.Text(TokenFormatter.Compact(total.TotalTokens), Theme.FontSize.Caption, monospacedDigits: true),
            showsCost ? Ui.Text(total.UsageCost.Text(l), Theme.FontSize.Caption, Theme.Keys.Secondary, monospacedDigits: true) : null);
        var card = new Border
        {
            Child = row,
            Padding = new Thickness(7, 5, 7, 5),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0.5),
            BorderBrush = HomeLayout.Tint(Theme.Keys.Gray, 0.35),
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.25, BlurRadius = 8, ShadowDepth = 1, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Background);
        return card;
    }

    /// <summary>
    /// A short underline under weekend columns. Not full-height shading — on the dark background that shading
    /// read as bars, making weekends look like heavy usage.
    /// </summary>
    private static FrameworkElement WeekendTickRow(List<DailyTrendColumn> columns)
    {
        var row = new EqualColumns { Spacing = DailyTrendMetrics.Spacing };
        foreach (var column in columns)
        {
            var tick = new Border { Height = DailyTrendMetrics.TickHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (DailyTrendMetrics.ShowsWeekendTick(column))
            {
                tick.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Secondary);
                tick.Opacity = 0.5;
            }
            row.Children.Add(tick);
        }
        return row;
    }

    /// <summary>
    /// Date axis. Columns are ~9 pt and two digits do not fit, so only the 1st, every 7th day and today are
    /// labelled; the other columns keep empty slots so labels stay under their bars.
    /// </summary>
    private static FrameworkElement AxisRow(List<DailyTrendColumn> columns, string today)
    {
        var row = new EqualColumns { Spacing = DailyTrendMetrics.Spacing };
        foreach (var column in columns)
        {
            if (DailyTrendMetrics.AxisLabel(column.Date, today) is { } label)
            {
                var key = column.Date == today ? Theme.Keys.Accent : column.IsFuture ? Theme.Keys.Tertiary : Theme.Keys.Secondary;
                var text = Ui.Text(label, Theme.FontSize.Caption2, key, monospacedDigits: true);
                text.TextTrimming = TextTrimming.None;
                row.Children.Add(text);
            }
            else row.Children.Add(new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        return row;
    }
}
