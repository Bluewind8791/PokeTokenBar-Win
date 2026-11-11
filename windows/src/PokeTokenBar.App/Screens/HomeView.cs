using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of the Home tab of <c>PopoverView.mainContent</c>: CompanionHeader, today's total with the period
/// totals and the daily trend, the provider's breakdown, the status-page banner and the official limits.
/// <see cref="PopoverRoot"/> puts it in the fixed-height scroll area (same height as Shop/Bag/Collection).
/// </summary>
public static class HomeView
{
    public static FrameworkElement Build(AppServices s)
    {
        var selected = s.Store.Snapshot(s.Nav.ProviderID);
        var column = Ui.VStack(12,
            CompanionHeader.Build(s.Companion, id => s.Nav.OpenDexEntry(id)),
            Ui.Divider(),
            Header(s, selected),
            Ui.Divider(),
            // Only during an incident — independent of whether limits loaded (an API outage leaves them nil).
            ProviderStatusBanner(s, selected));
        if (HomeLimits.HasLimits(selected))
        {
            column.Children.Add(HomeLimits.Build(s));
            column.Children.Add(Ui.Divider());
        }
        return column;
    }

    // MARK: Header — today's total + provider / token-type breakdown

    private static FrameworkElement Header(AppServices s, ProviderSnapshot? selected)
    {
        var store = s.Store;
        var l = s.L;
        var header = Ui.VStack(6, Ui.Text(l.TodayTokens, Theme.FontSize.Caption, Theme.Keys.Secondary));

        var big = Ui.Text(TokenFormatter.Compact(store.TodayTotalTokens), 28, weight: FontWeights.Bold, monospacedDigits: true);
        big.FontFamily = Theme.DisplayFont;
        var today = Ui.HStack(8, big);
        if (store.TodayTotalTokens >= 1_000)
            today.Children.Add(Ui.Text(TokenFormatter.Grouped(store.TodayTotalTokens), Theme.FontSize.Caption, Theme.Keys.Secondary,
                monospacedDigits: true));
        today.Children.Add(Ui.Spacer());
        if (store.ShowsCost)
            today.Children.Add(Ui.Text(store.TodayUsageCost.Text(l), Theme.FontSize.Callout, Theme.Keys.Secondary));
        header.Children.Add(today.BaselineAligned());

        // Period totals and the saved-history button stay visible on quiet days too.
        var totals = new ViewThatFits(
            Ui.HStack(14, PeriodTotals(s), Ui.Spacer(), RecapButton(s)),
            WithTrailing(PeriodTotals(s), RecapButton(s)));
        header.Children.Add(totals.Padding(0, 2, 0, 0));

        if (MonthDailyTrend.Build(store.MonthDailyTotals, store.ShowsCost, LocalUsageReader.TodayKey(), l) is { } trend)
            header.Children.Add(trend);

        // With two or more services connected, small tabs switch the details (totals above stay combined).
        if (store.Snapshots.Count > 1)
            header.Children.Add(CapsuleTabBar.Make(
                store.Snapshots.Select(snap => new CapsuleTabBar.Item(snap.ProviderID, snap.DisplayName)).ToList(),
                selected?.ProviderID, id => s.Nav.ProviderID = id).Padding(0, 6, 0, 0));
        if (selected is { Today: { } todayUsage })
            header.Children.Add(ProviderRow(l, selected, todayUsage));
        return header;
    }

    /// <summary>The VStack(alignment: .trailing) fallback: totals full-width leading, the recap button trailing.</summary>
    private static FrameworkElement WithTrailing(FrameworkElement totals, FrameworkElement recap)
    {
        recap.HorizontalAlignment = HorizontalAlignment.Right;
        return Ui.VStack(6, totals, recap);
    }

    /// <summary>Week and month on one line, or stacked when they do not fit (long languages, cost text).</summary>
    private static FrameworkElement PeriodTotals(AppServices s)
    {
        FrameworkElement[] Labels()
        {
            var store = s.Store;
            var l = s.L;
            return
            [
                PeriodLabel(l, l.ThisWeek, store.WeekTotalTokens, store.ShowsCost ? store.WeekUsageCost : null),
                PeriodLabel(l, l.ThisMonth, store.MonthTotalTokens, store.ShowsCost ? store.MonthUsageCost : null),
            ];
        }
        var stacked = Ui.VStack(4, Labels());
        foreach (FrameworkElement child in stacked.Children) child.HorizontalAlignment = HorizontalAlignment.Left;
        return new ViewThatFits(Ui.HStack(14, Labels()), stacked);
    }

    public static FrameworkElement PeriodLabel(L l, string name, long tokens, UsageCost? cost)
    {
        var row = Ui.HStack(4,
            Ui.Text(name, Theme.FontSize.Caption, Theme.Keys.Tertiary),
            Ui.Text(TokenFormatter.Compact(tokens), Theme.FontSize.Caption, weight: FontWeights.SemiBold, monospacedDigits: true));
        if (cost is { } c) row.Children.Add(Ui.Text(c.Text(l), Theme.FontSize.Caption, Theme.Keys.Secondary));
        // fixedSize: the labels never truncate; ViewThatFits picks the stacked layout instead.
        foreach (TextBlock t in row.Children) t.TextTrimming = TextTrimming.None;
        return row.BaselineAligned();
    }

    private static FrameworkElement RecapButton(AppServices s)
    {
        var l = s.L;
        var content = Ui.HStack(3,
            Ui.Icon(Ui.Glyphs.Chart, Theme.FontSize.Body, Theme.Keys.Primary),
            Ui.Text(l.RecapDetails, Theme.FontSize.Caption, Theme.Keys.Primary));
        var pill = Capsule.Round(new Border { Child = content, Padding = new Thickness(8, 4, 8, 4) });
        pill.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Fill);
        pill.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(pill, l.RecapOpen);
        return pill.OnClick(() => s.Nav.ShowingRecap = true).Help(l.RecapOpen);
    }

    private static FrameworkElement ProviderRow(L l, ProviderSnapshot snapshot, DailyUsage today)
    {
        var head = Ui.HStack(8,
            Ui.Text(snapshot.DisplayName, Theme.FontSize.Callout, weight: FontWeights.SemiBold),
            Ui.Spacer(),
            Ui.Text(TokenFormatter.Compact(today.TotalTokens), Theme.FontSize.Callout, monospacedDigits: true));
        if (snapshot.ReportsCost)
            head.Children.Add(Ui.Text(today.UsageCost.Text(l), Theme.FontSize.Caption, Theme.Keys.Secondary));
        head.BaselineAligned();

        Stack TypeRow(params FrameworkElement[] labels)
        {
            var r = Ui.HStack(10, labels);
            r.HorizontalAlignment = HorizontalAlignment.Left;
            return r;
        }
        var row = Ui.VStack(3, head,
            Ui.VStack(3,
                TypeRow(TokenTypeLabel(l.TokenInput, today.InputTokens), TokenTypeLabel(l.TokenOutput, today.OutputTokens)),
                TypeRow(TokenTypeLabel(l.TokenCacheWrite, today.CacheCreationTokens), TokenTypeLabel(l.TokenCacheRead, today.CacheReadTokens))));
        if (today.Models is { Count: > 1 } models)
        {
            foreach (var (model, tokens) in models.OrderByDescending(m => m.Value))
            {
                var name = Ui.Text(model.Split('/').Last(), Theme.FontSize.Caption2, Theme.Keys.Secondary);
                name.TextTrimming = TextTrimming.CharacterEllipsis;   // macOS truncates in the middle
                row.Children.Add(Ui.HStack(6, name, Ui.Spacer(),
                    Ui.Text(TokenFormatter.Compact(tokens), Theme.FontSize.Caption2, Theme.Keys.Tertiary, monospacedDigits: true)));
            }
        }
        return row.Padding(0, 2, 0, 0);
    }

    private static FrameworkElement TokenTypeLabel(string name, long value) =>
        Ui.HStack(3,
            Ui.Text(name, Theme.FontSize.Caption2, Theme.Keys.Tertiary),
            Ui.Text(TokenFormatter.Compact(value), Theme.FontSize.Caption2, Theme.Keys.Secondary, monospacedDigits: true));

    // MARK: Status banner

    /// <summary>
    /// The status page's incident for the selected provider, so an API outage is not mistaken for the app
    /// breaking. Display only; nothing when there is no incident or the lookup is off.
    /// </summary>
    private static FrameworkElement? ProviderStatusBanner(AppServices s, ProviderSnapshot? selected)
    {
        if (selected?.ProviderID is not { } id || s.Store.ProviderStatus(id) is not { } status || !status.Indicator.HasIssue())
            return null;
        var dot = new Ellipse { Width = 7, Height = 7 };
        dot.SetResourceReference(Shape.FillProperty, StatusKey(status.Indicator));
        var row = Ui.HStack(6, dot, Ui.Text(s.L.ProviderStatusLabel(status.Indicator), Theme.FontSize.Caption, weight: FontWeights.Medium));
        if (!string.IsNullOrEmpty(status.Description))
            row.Children.Add(Ui.Text(status.Description, Theme.FontSize.Caption, Theme.Keys.Secondary));
        row.Children.Add(Ui.Spacer());
        return row;
    }

    private static string StatusKey(ProviderStatusIndicator indicator) => indicator switch
    {
        ProviderStatusIndicator.Operational => Theme.Keys.Green,
        ProviderStatusIndicator.Minor or ProviderStatusIndicator.Maintenance => Theme.Keys.Yellow,
        ProviderStatusIndicator.Major => Theme.Keys.Orange,
        ProviderStatusIndicator.Critical => Theme.Keys.Red,
        _ => Theme.Keys.Gray,
    };
}
