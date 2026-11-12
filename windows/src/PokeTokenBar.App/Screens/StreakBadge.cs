using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>StreakBadge</c>: the current run of days with usage, next to the recap button — a flame and a number.
/// <para>
/// It sits in the period row rather than on a line of its own, so the popover does not grow. The flame is orange
/// once today counts and gray while today has no usage yet — the run is not broken, but today's first use is what
/// extends it; the tooltip says so.
/// </para>
/// </summary>
public static class StreakBadge
{
    /// <summary>Shown from two days. A one-day run only says "used today", which the totals beside it already say,
    /// and it would flicker in and out for anyone who uses the app now and then.</summary>
    public const int MinimumDays = 2;

    public static bool IsShown(UsageStreak streak) => streak.Days >= MinimumDays;

    /// <summary>The badge, or null while hidden so it takes no space (and no spacing) in the row.</summary>
    public static FrameworkElement? Build(UsageStreak streak, L l)
    {
        if (!IsShown(streak)) return null;
        var label = streak.IncludesToday ? l.StreakDays(streak.Days) : l.StreakDaysPending(streak.Days);
        var badge = Ui.HStack(2,
            Flame(Theme.FontSize.Caption, streak.IncludesToday ? Theme.Keys.Orange : Theme.Keys.Secondary),
            Ui.Text(streak.Days.ToString(System.Globalization.CultureInfo.InvariantCulture), Theme.FontSize.Caption,
                Theme.Keys.Secondary, FontWeights.Medium, monospacedDigits: true));
        badge.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(badge, label);
        return badge.Help(label);
    }

    /// <summary>SF <c>flame.fill</c>, drawn: Segoe Fluent Icons has no flame.</summary>
    private static Path Flame(double size, string brushKey)
    {
        var path = new Path
        {
            Data = Geometry.Parse("M6,0 C6.6,3.2 11,5.2 11.6,9.4 C12.2,13.4 9.6,16 6,16 C2.4,16 -0.2,13.4 0.4,9.8 " +
                                  "C0.7,8 1.6,6.6 2.8,5.6 C2.8,7.4 3.4,8.6 4.4,9.2 C4.2,5.8 4.8,2.8 6,0 Z"),
            Stretch = Stretch.Uniform, Height = size, VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetResourceReference(Shape.FillProperty, brushKey);
        return path;
    }
}
