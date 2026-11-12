using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>ActivityPanel</c> (ActivityVisualization.swift): draws the visualization the user picked; a click moves
/// on to the next one. Showing one instead of stacking both keeps the popover's height — both answer "how has usage
/// been going" — and lets a third be added later without competing for space.
/// </summary>
public static class ActivityPanel
{
    /// <param name="series">The selected bar window; it can cover fewer days than this month's live series.</param>
    /// <param name="heatmapSeries">This month's live series for the grid, whatever the bar window (null: <paramref name="series"/>).</param>
    /// <param name="history">Days before this month, for the grid (<see cref="UsageStore.DailyLedger"/>).</param>
    public static FrameworkElement? Build(ActivityVisualization visualization, IReadOnlyList<DailyUsage> series,
        IReadOnlyList<DailyUsage>? heatmapSeries, UsageLedger history, bool showsCost, DailyTrendWindow window,
        Action<DailyTrendWindow> selectWindow, bool hasAvailableHistory, string today, L l, Action onCycle)
    {
        if (Chart(visualization, series, heatmapSeries, history, showsCost, window, selectWindow, hasAvailableHistory,
                today, l) is not { } chart)
            return null;
        // A transparent host makes the gaps clickable too. Controls inside (the bar window menu) handle their own click.
        var host = new System.Windows.Controls.Border { Child = chart, Background = Brushes.Transparent };
        host.MouseLeftButtonUp += (_, e) => { e.Handled = true; onCycle(); };
        return host.Help(l.ActivitySwitchHint);
    }

    /// <summary>One arm per visualization; an unknown member throws (see <see cref="ActivityVisualization"/>).</summary>
    private static FrameworkElement? Chart(ActivityVisualization visualization, IReadOnlyList<DailyUsage> series,
        IReadOnlyList<DailyUsage>? heatmapSeries, UsageLedger history, bool showsCost, DailyTrendWindow window,
        Action<DailyTrendWindow> selectWindow, bool hasAvailableHistory, string today, L l) => visualization switch
    {
        ActivityVisualization.MonthlyBars => MonthDailyTrend.Build(series, showsCost, today, l, window, selectWindow,
            hasAvailableHistory, PageDots.Build(visualization)),
        ActivityVisualization.RecentHeatmap => ActivityHeatmap.Build(heatmapSeries ?? series, history, showsCost, today, l,
            PageDots.Build(visualization)),
        _ => throw new ArgumentOutOfRangeException(nameof(visualization), visualization, null),
    };
}

/// <summary>
/// Port of <c>PageDots</c>: the only visual hint that there is another view — a click cycle is hard to discover.
/// The count comes from <see cref="ActivityVisualizations.AllCases"/>, so a new member gets a dot for free.
/// </summary>
public static class PageDots
{
    public static FrameworkElement Build(ActivityVisualization current)
    {
        var row = Ui.HStack(3);
        foreach (var item in ActivityVisualizations.AllCases)
        {
            var dot = new Ellipse { Width = 4, Height = 4, Opacity = item == current ? 0.75 : 0.25 };
            dot.SetResourceReference(Shape.FillProperty, Theme.Keys.Secondary);
            row.Children.Add(dot);
        }
        return row;
    }
}
