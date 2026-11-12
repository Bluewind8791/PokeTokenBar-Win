using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PokeTokenBar.App;
using PokeTokenBar.Core;

namespace PokeTokenBar.App.Tests;

/// <summary>Port of the badge cases of UsageStreakTests.swift (#451). The streak itself is Core (UsageStreakTests).</summary>
public class StreakBadgeTests
{
    [Fact]
    public void TheBadgeStartsAtTwoDays()
    {
        Assert.False(StreakBadge.IsShown(UsageStreak.None));
        Assert.False(StreakBadge.IsShown(new UsageStreak(1, IncludesToday: true)));   // one day is just "used today"
        Assert.True(StreakBadge.IsShown(new UsageStreak(2, IncludesToday: false)));
    }

    /// <summary>Hidden takes no space in the period row; shown, today's run and a pending one draw differently
    /// (orange flame vs gray), so the badge says whether today already counts.</summary>
    [Fact]
    public void TheBadgeTakesNoSpaceWhenHiddenAndMarksAPendingDay() => Sta(() =>
    {
        var l = new L(AppLanguage.En);
        Assert.Null(StreakBadge.Build(new UsageStreak(1, IncludesToday: true), l));

        var today = Pixels(StreakBadge.Build(new UsageStreak(5, IncludesToday: true), l)!);
        var pending = Pixels(StreakBadge.Build(new UsageStreak(5, IncludesToday: false), l)!);
        Assert.NotEqual(today, pending);
    });

    /// <summary>The tooltip (and the accessible name) says whether today already counts.</summary>
    [Fact]
    public void TheTooltipNamesTheRunAndWhetherTodayCounts() => Sta(() =>
    {
        var l = new L(AppLanguage.En);
        var today = StreakBadge.Build(new UsageStreak(5, IncludesToday: true), l)!;
        var pending = StreakBadge.Build(new UsageStreak(5, IncludesToday: false), l)!;
        Assert.Equal(l.StreakDays(5), today.ToolTip);
        Assert.Equal(l.StreakDaysPending(5), pending.ToolTip);
        Assert.Equal(l.StreakDaysPending(5), System.Windows.Automation.AutomationProperties.GetName(pending));
    });

    private static byte[] Pixels(FrameworkElement element)
    {
        var host = new Border { Child = element };
        Theme.Apply(host.Resources, dark: true);
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(host.ActualWidth * 2));
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
