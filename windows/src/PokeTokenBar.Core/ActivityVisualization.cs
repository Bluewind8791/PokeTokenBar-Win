namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>ActivityVisualization</c> (UI/ActivityVisualization.swift): one way of drawing recent activity in the
/// popover. Lives in Core because <see cref="UsageStore"/> persists the choice.
///
/// <para><b>Adding a visualization:</b> add a member here, then an arm in <see cref="ActivityVisualizations.Title"/>
/// and in <c>ActivityPanel.Chart</c> (App), and one name string in <see cref="L"/>. C# cannot make a missing switch
/// arm a build error without a warning on every complete switch, so both switches throw on an unknown value and
/// <c>ActivityVisualizationTests</c> walks every member through both — a member without a view fails there.</para>
/// </summary>
public enum ActivityVisualization
{
    /// <summary>Day-by-day bars. <b>The default and first in the cycle</b> — an existing install's popover must
    /// not change on update.</summary>
    MonthlyBars,
    /// <summary>The last 16 weeks as a calendar grid: weekday patterns ("barely anything on weekends") at a glance,
    /// across months.</summary>
    RecentHeatmap,
}

public static class ActivityVisualizations
{
    public const ActivityVisualization Default = ActivityVisualization.MonthlyBars;

    /// <summary>Swift <c>allCases</c>, in declaration order (the click cycle's order).</summary>
    public static IReadOnlyList<ActivityVisualization> AllCases { get; } = Enum.GetValues<ActivityVisualization>();

    public static string RawValue(this ActivityVisualization visualization) => visualization switch
    {
        ActivityVisualization.MonthlyBars => "monthlyBars",
        ActivityVisualization.RecentHeatmap => "recentHeatmap",
        _ => throw new ArgumentOutOfRangeException(nameof(visualization), visualization, null),
    };

    /// <summary>Null for an unknown value — the caller falls back to <see cref="Default"/>.</summary>
    public static ActivityVisualization? FromRawValue(string? raw) =>
        AllCases.Where(v => v.RawValue() == raw).Select(v => (ActivityVisualization?)v).FirstOrDefault();

    /// <summary>The next one in the click cycle. It follows <see cref="AllCases"/>, so a new member joins on its own.</summary>
    public static ActivityVisualization Next(this ActivityVisualization visualization)
    {
        var index = ((IList<ActivityVisualization>)AllCases).IndexOf(visualization);
        return index < 0 ? Default : AllCases[(index + 1) % AllCases.Count];
    }

    public static string Title(this ActivityVisualization visualization, L l) => visualization switch
    {
        ActivityVisualization.MonthlyBars => l.DailyTrend,
        ActivityVisualization.RecentHeatmap => l.RecentHeatmap,
        _ => throw new ArgumentOutOfRangeException(nameof(visualization), visualization, null),
    };
}
