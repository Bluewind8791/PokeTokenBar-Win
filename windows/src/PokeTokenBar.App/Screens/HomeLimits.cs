using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>PopoverView.limitsSection</c> and its Claude rows: official 5h/weekly percentages with reset
/// countdowns, pace markers, account tabs and the expired/stale/refresh notices. Windows has Claude as its
/// only provider, so the Codex/Antigravity/Cursor branches are not ported.
/// </summary>
public static class HomeLimits
{
    /// <summary>
    /// Swift <c>selectedProviderHasLimits</c> for Claude. macOS hides the section only when Keychain access
    /// is turned off; Windows reads a credentials file (no Keychain switch), so the section always shows —
    /// which keeps the one-tap "load limits" row reachable on first run.
    /// </summary>
    public static bool HasLimits(ProviderSnapshot? selected) => selected?.ProviderID == "claude_code";

    public static FrameworkElement Build(AppServices s)
    {
        var store = s.Store;
        var l = s.L;
        var section = Ui.VStack(8);

        var heading = Ui.HStack(4, Ui.Text(l.LimitsOfficial, Theme.FontSize.Caption, Theme.Keys.Secondary));
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        section.Children.Add(heading);

        if (store.LimitsAuthExpiry == LimitsAuthExpiry.SessionKey)
            section.Children.Add(SessionKeyExpiredNotice(s));
        else if (store.LimitsAuthExpired)
            section.Children.Add(AuthExpiredNotice(s, l.ClaudeAuthExpiredHint));
        else if (store.ClaudeLimitsMissing || store.ClaudeLimitsStale || store.AdditionalLimitsPending || store.AdditionalLimitsStale)
            // Polling never prompts, so first-run and post-expiry limits load from this explicit tap.
            section.Children.Add(RefreshRow(s));

        foreach (var child in AccountsContent(s)) section.Children.Add(child);
        return section;
    }

    // MARK: Accounts

    /// <summary>Claude official limits, one tab per account when there are several (default login first).</summary>
    private static IEnumerable<FrameworkElement> AccountsContent(AppServices s)
    {
        var store = s.Store;
        var l = s.L;
        var accounts = store.ClaudeAccounts;
        var selected = accounts.FirstOrDefault(a => a.Id == s.Nav.ClaudeAccountID) ?? accounts.FirstOrDefault();
        if (accounts.Count > 1)
        {
            var trackedID = store.TrackedClaudeAccount?.Id;
            yield return CapsuleTabBar.Make(
                accounts.Select(a => new CapsuleTabBar.Item(a.Id, a.Title, a.Status.AccountDisplay, a.Id == trackedID)).ToList(),
                selected?.Id, id => s.Nav.ClaudeAccountID = id);
        }
        if (selected != null)
            foreach (var child in AccountLimits(s, selected, showsUsage: accounts.Count > 1)) yield return child;
        // Tab totals do not add up to the header when some sessions belong to no login's history.
        if (accounts.Count > 1 && store.UnattributedClaudeUsage.MonthTokens > 0)
            yield return Ui.Text(l.UnattributedClaudeUsage(TokenFormatter.Compact(store.UnattributedClaudeUsage.MonthTokens)),
                Theme.FontSize.Caption2, Theme.Keys.Tertiary).Wrapping();
    }

    private static IEnumerable<FrameworkElement> AccountLimits(AppServices s, ClaudeAccountLimits account, bool showsUsage)
    {
        var store = s.Store;
        var l = s.L;
        var limits = account.Status;
        if (account.IsExpired && !account.IsDefault)
        {
            // A dead session key is fixed in Settings; the retry banner would only re-read the token.
            yield return account.SessionKeyExpired ? SessionKeyExpiredNotice(s)
                : AuthExpiredNotice(s, l.AdditionalAccountExpiredHint(account.FallbackTitle));
        }
        else if (!account.IsDefault && account.IsStale(DateTimeOffset.Now) && StaleBadge(l, account.UpdatedAt) is { } stale)
            // The default account's stale label lives in the refresh row above the tabs.
            yield return stale;
        // Plan (an account property) — only with subscription info.
        if (limits.PlanDisplay is { } plan)
            yield return Ui.Text(l.Plan(plan), Theme.FontSize.Caption, Theme.Keys.Tertiary);
        // Which account these limits belong to, where two accounts take turns on one credential.
        if (limits.AccountDisplay is { } accountLabel)
            yield return Ui.Text(l.LimitsAccount(accountLabel), Theme.FontSize.Caption, Theme.Keys.Tertiary);
        // This account's share of the local usage (the header keeps the machine-wide totals).
        if (showsUsage && store.ClaudeAccountUsage.TryGetValue(account.Id, out var usage) && !usage.IsEmpty)
            yield return Ui.HStack(14,
                HomeView.PeriodLabel(l, l.Today, usage.TodayTokens, store.ShowsCost ? usage.TodayCost : null),
                HomeView.PeriodLabel(l, l.ThisMonth, usage.MonthTokens, store.ShowsCost ? usage.MonthCost : null),
                Ui.Spacer());

        var rows = Ui.VStack(8);
        Add(rows, LimitRow(s, l.FiveHourSession, limits.FiveHour, LimitWindowSpan.FiveHour, l.FiveHourNotStarted));
        // The forecast follows the tracked account (it combines that account's utilization with local usage).
        if (account.Id == store.TrackedClaudeAccount?.Id) Add(rows, ForecastRow(s));
        Add(rows, LimitRow(s, l.Weekly, limits.SevenDay, LimitWindowSpan.SevenDay));
        Add(rows, LimitRow(s, l.WeeklyOpus, limits.SevenDayOpus, LimitWindowSpan.SevenDay));
        Add(rows, LimitRow(s, l.WeeklySonnet, limits.SevenDaySonnet, LimitWindowSpan.SevenDay));
        // New-style limits[] — per-model weekly (weekly_scoped) and other windows outside the legacy fields.
        foreach (var entry in limits.ScopedLimitEntries)
            Add(rows, LimitRow(s, l.ClaudeLimitEntry(entry.Kind, entry.Scope?.Model?.DisplayName),
                new LimitWindow { Utilization = entry.Percent, ResetsAt = entry.ResetsAt }, entry.WindowSpan));
        if (store.ClaudeCurrentBlock(account) is { EndDate: { } end } block)
        {
            var lang = l.Lang;
            Add(rows, Ui.HStack(8,
                Ui.Text(l.ClaudeCurrentBlock, Theme.FontSize.Caption, Theme.Keys.Secondary),
                Ui.Text(TokenFormatter.Compact(block.TotalTokens), Theme.FontSize.Caption, monospacedDigits: true),
                Ui.Spacer(),
                Ui.Text("", Theme.FontSize.Caption, Theme.Keys.Tertiary)
                    .Ticking(() => $"{l.Reset} {HomeFormat.Relative(end, lang)}{HomeFormat.ResetClockSuffix(end, lang)}")));
        }
        // With an expired session the values are from before it expired — dim them: "not current".
        rows.Opacity = account.IsExpired ? 0.5 : 1;
        yield return rows;
    }

    private static void Add(Panel panel, FrameworkElement? child)
    {
        if (child != null) panel.Children.Add(child);
    }

    // MARK: Rows

    private static FrameworkElement? LimitRow(AppServices s, string name, LimitWindow? window, TimeSpan? span,
        string? notStartedHint = null)
    {
        if (window?.Utilization is not { } utilization) return null;
        return QuotaRow(s, name, utilization, window.ResetDate, span, idleHint: window.HasNotStarted ? notStartedHint : null);
    }

    /// <summary>
    /// One limit: name, reset countdown, percent and the gauge. Every row shares the trailing percent
    /// alignment. The pace marker needs both the reset time and the window length.
    /// </summary>
    private static FrameworkElement QuotaRow(AppServices s, string name, double utilization, DateTimeOffset? reset,
        TimeSpan? span = null, string? idleHint = null)
    {
        var store = s.Store;
        var l = s.L;
        // Read at build time, so the marker moves with each refresh and each popover opening.
        double? pace = reset is { } r && span is { } sp ? UsageStore.PaceFraction(r, sp, DateTimeOffset.Now) : null;
        // Tier against pace when there is one; the absolute thresholds otherwise (same rule as the menu bar).
        var tier = PaceTiers.Tier(utilization, pace, store.CritThreshold);
        var gauge = PaceTiers.Gauge(utilization, pace, store.WarnThreshold, store.CritThreshold);

        var head = Ui.HStack(8, Ui.Text(name, Theme.FontSize.Callout), Ui.Spacer());
        if (reset is { } resetDate)
        {
            var lang = l.Lang;
            head.Children.Add(Ui.Text("", Theme.FontSize.Caption, Theme.Keys.Tertiary).Ticking(() => HomeFormat.ResetLabel(resetDate, lang)));
        }
        else if (idleHint != null)
            head.Children.Add(Ui.Text(idleHint, Theme.FontSize.Caption, Theme.Keys.Tertiary));
        head.Children.Add(Ui.Text(PercentText(store, l, utilization), Theme.FontSize.Callout, Theme.PacePercentKey(gauge),
            monospacedDigits: true));
        head.BaselineAligned();

        var bar = new LimitProgressBar(store.LimitDisplayPercent(utilization), Theme.PaceKey(gauge),
            LimitPace.MarkerFraction(pace, store.LimitDisplayMode));
        var row = Ui.VStack(2, head, bar);
        row.Background = System.Windows.Media.Brushes.Transparent;   // the tooltip covers the whole row
        return row.Help(PaceHelp(store, l, pace, tier, utilization));
    }

    /// <summary>"62%", or in remaining mode the remaining percent with "left". Warning colors follow actual use.</summary>
    private static string PercentText(UsageStore store, L l, double utilization)
    {
        var text = TokenFormatter.Percent(store.LimitDisplayPercent(utilization));
        return store.LimitDisplayMode == LimitPace.LimitDisplayMode.Remaining ? l.PercentRemaining(text) : text;
    }

    /// <summary>
    /// Tooltip: the tier and its delta first (the row only shows a color), then where the pace is — through
    /// the same display transform as the marker.
    /// </summary>
    private static string? PaceHelp(UsageStore store, L l, double? pace, PaceTier? tier, double utilization)
    {
        if (pace is not { } p) return null;
        var hint = l.PaceHint(TokenFormatter.Percent(store.LimitDisplayPercent(p * 100)));
        if (tier is not { } t) return hint;
        var head = l.PaceTier(t);
        if (l.PaceDelta(PaceTiers.RoundedDelta(utilization, p)) is { } delta) head += " · " + delta;
        return head + "\n" + hint;
    }

    /// <summary>When the current burn rate reaches 100% of the 5h limit.</summary>
    private static FrameworkElement? ForecastRow(AppServices s)
    {
        if (s.Store.FiveHourForecast is not { } forecast) return null;
        var l = s.L;
        var key = forecast.BeforeReset ? Theme.Keys.Orange : Theme.Keys.Tertiary;
        var row = Ui.HStack(4,
            Ui.Icon(forecast.BeforeReset ? Ui.Glyphs.Warning : "", Theme.FontSize.Caption2, key),   // checkmark.circle
            Ui.Text(forecast.BeforeReset ? l.ForecastReach(forecast.DepletionDate.ToLocalTime().ToString("HH:mm"))
                : l.ForecastNoReach, Theme.FontSize.Caption, key));
        row.HorizontalAlignment = HorizontalAlignment.Left;
        return row.Padding(2, 0, 0, 0);
    }

    // MARK: Notices

    /// <summary>
    /// Expired session key — a retry cannot revive a dead cookie, so the button goes to where the key is
    /// entered again instead of re-reading credentials.
    /// </summary>
    private static FrameworkElement SessionKeyExpiredNotice(AppServices s)
    {
        var l = s.L;
        return Notice("",   // key.slash.fill
            l.SessionKeyExpiredTitle, Buttons.Small(l.Settings, s.Nav.OpenSessionKeySettings), l.SessionKeyExpiredNoticeHint);
    }

    /// <summary>
    /// Expired session with a retry: why the values stopped, a one-tap retry, and that running Claude Code
    /// renews it. The default account above the tabs, an additional account inside its tab.
    /// </summary>
    private static FrameworkElement AuthExpiredNotice(AppServices s, string hint)
    {
        var store = s.Store;
        var l = s.L;
        var retry = Buttons.Small(store.IsRefreshingLimitToken ? "…" : l.Retry, () => _ = store.RefreshLimitTokenFromKeychain());
        retry.IsEnabled = !store.IsRefreshingLimitToken;
        return Notice(Ui.Glyphs.Warning, l.ClaudeAuthExpiredTitle, retry, hint);
    }

    private static FrameworkElement Notice(string glyph, string title, FrameworkElement button, string hint)
    {
        var head = Ui.HStack(6,
            Ui.Icon(glyph, Theme.FontSize.Caption + 1, Theme.Keys.Orange),
            Ui.Text(title, Theme.FontSize.Caption, weight: FontWeights.SemiBold),
            Ui.Spacer(),
            button);
        var body = Ui.VStack(4, head, Ui.Text(hint, Theme.FontSize.Caption2, Theme.Keys.Secondary).Wrapping());
        return HomeLayout.Box(body, HomeLayout.Tint(Theme.Keys.Orange, 0.12), 8, 8);
    }

    /// <summary>The one-tap refresh: polling never prompts, so the token is re-read only on this explicit action.</summary>
    private static FrameworkElement RefreshRow(AppServices s)
    {
        var store = s.Store;
        var l = s.L;
        TextBlock label;
        // The row also shows for another account's pending refresh: only call the values stale when the
        // default account's own values are.
        if (store.Limits != null && store.ClaudeLimitsStale)
        {
            var since = store.LimitsUpdatedAt ?? DateTimeOffset.Now;
            var lang = l.Lang;
            label = Ui.Text("", Theme.FontSize.Caption, Theme.Keys.Orange)
                .Ticking(() => $"{l.StaleLimits} · {HomeFormat.Relative(since, lang)}");
        }
        else label = Ui.Text(l.LimitsTapToLoad, Theme.FontSize.Caption, Theme.Keys.Secondary);
        var refresh = Buttons.Small(store.IsRefreshingLimitToken ? "…" : l.Refresh, () => _ = store.RefreshLimitTokenFromKeychain(),
            fontSize: Theme.FontSize.Callout);
        refresh.IsEnabled = !store.IsRefreshingLimitToken;
        var column = Ui.VStack(2, Ui.HStack(6, label, Ui.Spacer(), refresh));
        // Without this the button looks inert when the refresh fails (e.g. rate limited).
        if (store.LimitTokenRefreshMessage is { } error && !store.IsRefreshingLimitToken)
            column.Children.Add(Ui.Text(error, Theme.FontSize.Caption2, Theme.Keys.Orange).Wrapping());
        return column;
    }

    /// <summary>Limit snapshot is late — relative time since the last success.</summary>
    private static FrameworkElement? StaleBadge(L l, DateTimeOffset? updatedAt)
    {
        if (updatedAt is not { } updated) return null;
        var lang = l.Lang;
        return Ui.Text("", Theme.FontSize.Caption, Theme.Keys.Orange)
            .Ticking(() => $"{l.StaleLimits} · {HomeFormat.Relative(updated, lang)}");
    }
}
