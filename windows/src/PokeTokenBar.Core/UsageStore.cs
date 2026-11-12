using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

/// <summary>Port of <c>ProviderSnapshot</c> (Models.swift): one provider's numbers as the store last built them.</summary>
public sealed record ProviderSnapshot
{
    public required string ProviderID { get; init; }
    public required string DisplayName { get; init; }
    public DailyUsage? Today { get; init; }
    public BlockUsage? ActiveBlock { get; init; }
    public PeriodUsage? WeekTotal { get; init; }
    public PeriodUsage? MonthTotal { get; init; }
    /// <summary>This month's day-by-day totals (month start → today, empty days as zeros), or null when
    /// the provider cannot produce a series.</summary>
    public IReadOnlyList<DailyUsage>? MonthDaily { get; init; }
    /// <summary>Rolling recent day-by-day totals (21 days → today), or null.</summary>
    public IReadOnlyList<DailyUsage>? RecentDaily { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    /// <summary>Mirrors <see cref="IUsageProvider.ReportsCost"/>.</summary>
    public bool ReportsCost { get; init; } = true;
    /// <summary>Mirrors <see cref="ProviderEnrichment.LastUsage"/>.</summary>
    public DateTimeOffset? LastUsage { get; init; }

    public string Id => ProviderID;
    public long TodayTotalTokens => Today?.TotalTokens ?? 0;

    /// <summary>Something to show: today, an active block, positive history, or use within the recent window (#336).</summary>
    internal bool HasDisplayableUsage(DateTimeOffset now) =>
        Today != null
        || (ActiveBlock?.TotalTokens ?? 0) > 0
        || (WeekTotal?.TotalTokens ?? 0) > 0
        || (MonthTotal?.TotalTokens ?? 0) > 0
        || MonthDaily?.Any(d => d.TotalTokens > 0) == true
        || RecentDaily?.Any(d => d.TotalTokens > 0) == true
        || (LastUsage is { } last && now - last <= LocalUsageReader.RecentUseWindow);
}

/// <summary>
/// Port of <c>UsageStore.AnimationQuality</c>: smoothness ↔ battery for the always-on animations. The value is
/// the GIF frame-duration floor (seconds); playback speed is unchanged, only the frame rate differs.
/// </summary>
public enum AnimationQuality { PowerSaver, Balanced, Smooth }

public static class AnimationQualities
{
    public static string RawValue(this AnimationQuality quality) => quality switch
    {
        AnimationQuality.Balanced => "balanced",
        AnimationQuality.Smooth => "smooth",
        _ => "powerSaver",
    };

    public static AnimationQuality? FromRawValue(string? raw) => raw switch
    {
        "powerSaver" => AnimationQuality.PowerSaver,
        "balanced" => AnimationQuality.Balanced,
        "smooth" => AnimationQuality.Smooth,
        _ => null,
    };

    /// <summary>Frame floor in seconds = fps cap. Never add a 0 case: native fps is an idle-wakeup regression.</summary>
    public static double FrameFloor(this AnimationQuality quality) => quality switch
    {
        AnimationQuality.Balanced => 0.2,
        AnimationQuality.Smooth => 0.1,
        _ => 0.4,
    };

    /// <summary>Battery saver slows to the power-saver floor; derived, the stored choice is untouched.</summary>
    public static double EffectiveFrameFloor(this AnimationQuality quality, bool lowPower) =>
        lowPower ? Math.Max(quality.FrameFloor(), AnimationQuality.PowerSaver.FrameFloor()) : quality.FrameFloor();
}

/// <summary>Port of <c>UsageStore.LimitsAuthExpiry</c>: where the expired Claude credential came from. The fixes
/// differ: a session key is pasted again in Settings, the OAuth token is renewed by running Claude Code.</summary>
public enum LimitsAuthExpiry { OAuth, SessionKey }

/// <summary>One menu bar limit item: its text plus what its color is judged from.</summary>
/// <param name="Pace">Elapsed fraction of the window; null when its reset time or length is unknown.</param>
public sealed record MenuLimitPart(string Text, double Utilization, double? Pace);

/// <summary>A menu bar limit item and the tier its whole text is colored in.</summary>
public sealed record MenuLimitColorRun(string Text, PaceTier Tier);

/// <summary>Port of <c>UsageStore.FiveHourForecast</c>.</summary>
public sealed record FiveHourForecast(DateTimeOffset DepletionDate, bool BeforeReset);

/// <summary>Port of <c>UsageStore.LimitAlert</c>: one limit alert to fire (pure decision, no side effects).</summary>
/// <param name="Key">Unique per window, for tier tracking and the notification id. Never shown.</param>
/// <param name="Window">Display name; two windows may share one.</param>
/// <param name="Account">Claude account title, next to other accounts.</param>
public sealed record LimitAlert(string Key, string Window, bool IsCritical, double Utilization, string? Account = null)
{
    /// <summary>The account comes last: a bubble cut short loses it, not the percentage.</summary>
    public string Body(L l, bool withAccount = true)
    {
        var text = l.NotifBody(Window, TokenFormatter.Percent(Utilization));
        return withAccount && Account != null ? $"{text} · {Account}" : text;
    }
}

/// <summary>A limit alert for the Windows toast layer (replaces the macOS UNNotificationRequest).</summary>
/// <param name="Identifier">Stable per window and tier, like the macOS request identifier.</param>
/// <param name="PlaySound">Critical alerts play the default sound.</param>
public sealed record LimitNotification(string Identifier, string Title, string Body, bool PlaySound);

/// <summary>
/// Timer seam for <see cref="UsageStore"/>. The default <see cref="SynchronizationContextScheduler"/> runs callbacks
/// on the thread that created the store (the WPF UI thread); tests pass a manual scheduler and fire it themselves.
/// </summary>
public interface IUsageStoreScheduler
{
    /// <summary>Calls <paramref name="tick"/> every <paramref name="interval"/> until disposed.</summary>
    IDisposable Repeat(TimeSpan interval, Action tick);
    /// <summary>Calls <paramref name="action"/> once after <paramref name="delay"/> unless disposed first.</summary>
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary><see cref="System.Threading.Timer"/> callbacks posted to the synchronization context captured at creation.</summary>
public sealed class SynchronizationContextScheduler : IUsageStoreScheduler
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public IDisposable Repeat(TimeSpan interval, Action tick) =>
        new Timer(_ => Dispatch(tick), null, interval, interval);

    public IDisposable After(TimeSpan delay, Action action) =>
        new Timer(_ => Dispatch(action), null, delay, Timeout.InfiniteTimeSpan);

    private void Dispatch(Action action)
    {
        if (_context != null) _context.Post(_ => action(), null);
        else action();
    }
}

/// <summary>
/// Port of <c>UsageStore</c> (UsageStore.swift), Claude Code only. Refreshes the providers on a timer, builds the
/// per-provider snapshots, fetches the official Claude limits (default login and additional config folders),
/// and derives the menu text, warning state, burn tier, alerts, candy windows and the daily ledger.
/// <para>
/// Swift's <c>@MainActor @Observable</c> becomes a class used from one thread (the WPF UI thread): every await
/// resumes on the caller's synchronization context, and <see cref="Changed"/> fires after state changes so the
/// UI can re-read. macOS notifications become <see cref="LimitNotificationPosted"/> and
/// <see cref="CurrentBubbleAlert"/>; the app shows Windows toasts.
/// </para>
/// </summary>
public sealed class UsageStore : IDisposable
{
    // MARK: State

    private List<ProviderSnapshot> _snapshots = [];
    public IReadOnlyList<ProviderSnapshot> Snapshots => _snapshots;
    public LimitStatus? Limits { get; private set; }
    /// <summary>Official limits of the additional Claude config folders, one entry per account, in Settings
    /// order. The default account stays in <see cref="Limits"/>; <see cref="ClaudeAccounts"/> merges both.</summary>
    private List<AdditionalClaudeLimits> _additionalLimits = [];
    public IReadOnlyList<AdditionalClaudeLimits> AdditionalLimits => _additionalLimits;
    /// <summary>A folder has no fresh limits and a manual refresh can fix it: the popover offers it.
    /// Expired folders do not count, a refresh cannot renew their token.</summary>
    public bool AdditionalLimitsPending { get; private set; }
    public DateTimeOffset? LimitsUpdatedAt { get; private set; }
    public bool LimitsAvailable { get; private set; } = true;
    /// <summary>Where the expired Claude credential came from; null = not expired. Cleared on success.</summary>
    public LimitsAuthExpiry? LimitsAuthExpiry { get; private set; }
    /// <summary>The Claude limits call failed on an expired credential: the UI shows a notice and a retry.</summary>
    public bool LimitsAuthExpired => LimitsAuthExpiry != null;
    private readonly Dictionary<string, ProviderStatus> _statuses = [];
    /// <summary>providerID → status-page incident indicator (display only). A failed lookup keeps the previous value.</summary>
    public IReadOnlyDictionary<string, ProviderStatus> Statuses => _statuses;
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool IsRefreshing { get; private set; }
    /// <summary>A refresh requested while one runs: run once more afterwards instead of dropping it.</summary>
    private bool _refreshPending;
    public bool IsRefreshingLimitToken { get; private set; }
    public string? LastErrorDescription { get; private set; }
    private Exception? _limitTokenRefreshFailure;
    public string? LimitTokenRefreshError =>
        _limitTokenRefreshFailure == null ? null : FriendlyLimitError(_limitTokenRefreshFailure, new L(LocalizationLanguage));

    /// <summary>Shown above the account tabs, where it would read as everyone's: next to other accounts it
    /// names the default one, the only account this error is about.</summary>
    public string? LimitTokenRefreshMessage
    {
        get
        {
            if (LimitTokenRefreshError is not { } error) return null;
            var accounts = ClaudeAccounts;
            var account = accounts.FirstOrDefault(a => a.IsDefault);
            return accounts.Count > 1 && account != null ? $"{account.Title} · {error}" : error;
        }
    }

    public string? LastErrorMessage(L l) => LastErrorDescription == null ? null : l.UsageRefreshError + "\n" + LastErrorDescription;

    /// <summary>Transient speech-bubble payload for the floating pet. Cleared after the TTL.</summary>
    public LimitAlert? CurrentBubbleAlert { get; private set; }
    private DateTimeOffset _currentBubbleDate = DateTimeOffset.MinValue;
    private IDisposable? _bubbleDismiss;

    /// <summary>Fires after any state change (refresh phases, settings, actions). The WPF layer re-reads.</summary>
    public event Action? Changed;
    /// <summary>Fires once per new limit alert while <see cref="LimitNotifications"/> is on.</summary>
    public event Action<LimitNotification>? LimitNotificationPosted;

    /// <summary>Called at the end of every refresh (limits loaded): the app updates the companion and grants
    /// candy then (see <see cref="FeedCompanionAfterRefresh"/>).</summary>
    public Action? OnRefresh { get; set; }

    // MARK: Settings (AppSettings, same keys as the macOS UserDefaults)

    private double _refreshInterval;
    /// <summary>Seconds; 0 = manual.</summary>
    public double RefreshInterval
    {
        get => _refreshInterval;
        set
        {
            _refreshInterval = value;
            _settings.Set("refreshInterval", value);
            Reschedule();
            NotifyChanged();
        }
    }

    /// <summary>The Settings sliders overlap (warn 50…95, crit 80…100): moving one past the other pushes the other
    /// along by one step, so the warning tier always sits below the critical one.</summary>
    public const double ThresholdGap = 5;
    private double _warnThreshold;
    private double _critThreshold;

    public double WarnThreshold
    {
        get => _warnThreshold;
        set
        {
            _warnThreshold = value;
            _settings.Set("warnThreshold", value);
            if (CritThreshold <= value) CritThreshold = value + ThresholdGap;
            NotifyChanged();
        }
    }

    public double CritThreshold
    {
        get => _critThreshold;
        set
        {
            _critThreshold = value;
            _settings.Set("critThreshold", value);
            if (WarnThreshold >= value) WarnThreshold = value - ThresholdGap;
            NotifyChanged();
        }
    }

    private bool _showTokensInMenu, _showCostInMenu, _showLimitInMenu;
    public bool ShowTokensInMenu { get => _showTokensInMenu; set => SetBool(ref _showTokensInMenu, value, "showTokensInMenu"); }
    public bool ShowCostInMenu { get => _showCostInMenu; set => SetBool(ref _showCostInMenu, value, "showCostInMenu"); }
    public bool ShowLimitInMenu { get => _showLimitInMenu; set => SetBool(ref _showLimitInMenu, value, "showLimitInMenu"); }
    private bool _showGrowthInMenu;
    /// <summary>The raised companion's hatch / growth percentage in the tray tooltip (<see cref="MenuBarContent"/>).</summary>
    public bool ShowGrowthInMenu { get => _showGrowthInMenu; set => SetBool(ref _showGrowthInMenu, value, "showGrowthInMenu"); }

    private LimitPace.LimitDisplayMode _limitDisplayMode;
    /// <summary>Used (default) or remaining. Numbers only: warning colors, gauges' tiers and alerts keep the raw utilization.</summary>
    public LimitPace.LimitDisplayMode LimitDisplayMode
    {
        get => _limitDisplayMode;
        set
        {
            _limitDisplayMode = value;
            _settings.Set("limitDisplayMode", value == LimitPace.LimitDisplayMode.Remaining ? "remaining" : "used");
            NotifyChanged();
        }
    }

    private DailyTrendWindow _dailyTrendWindow;
    /// <summary>Span of the popover's daily trend (14 days by default).</summary>
    public DailyTrendWindow DailyTrendWindow
    {
        get => _dailyTrendWindow;
        set
        {
            _dailyTrendWindow = value;
            _settings.Set("dailyTrendWindow", value.RawValue());
            NotifyChanged();
        }
    }

    private AnimationQuality _animationQuality;
    public AnimationQuality AnimationQuality
    {
        get => _animationQuality;
        set
        {
            _animationQuality = value;
            _settings.Set("animationQuality", value.RawValue());
            NotifyChanged();
        }
    }

    private ActivityVisualization _activityVisualization;
    /// <summary>
    /// The activity visualization the popover draws. An unknown stored value falls back to the default, so going
    /// back to a build without that visualization, or a hand-edited setting, never leaves the popover empty.
    /// </summary>
    public ActivityVisualization ActivityVisualization
    {
        get => _activityVisualization;
        set
        {
            _activityVisualization = value;
            _settings.Set("activityVisualization", value.RawValue());
            NotifyChanged();
        }
    }

    private bool _limitNotifications, _companionNotifications, _updateNotificationsEnabled, _statusChecksEnabled;
    public bool LimitNotifications { get => _limitNotifications; set => SetBool(ref _limitNotifications, value, "limitNotifications"); }
    public bool CompanionNotifications { get => _companionNotifications; set => SetBool(ref _companionNotifications, value, "companionNotifications"); }
    /// <summary>The popover's update banner. Off hides it; a manual check stays in Settings.</summary>
    public bool UpdateNotificationsEnabled { get => _updateNotificationsEnabled; set => SetBool(ref _updateNotificationsEnabled, value, "updateNotificationsEnabled"); }
    /// <summary>Status-page incident lookup (display only).</summary>
    public bool StatusChecksEnabled { get => _statusChecksEnabled; set => SetBool(ref _statusChecksEnabled, value, "statusChecksEnabled"); }

    private bool _floatingPetEnabled, _floatingPetBubbleAlerts;
    private double _floatingPetSize;
    public bool FloatingPetEnabled { get => _floatingPetEnabled; set => SetBool(ref _floatingPetEnabled, value, "floatingPetEnabled"); }
    private bool _alwaysFacesBack;
    /// <summary>Always show the companion's back sprite, regardless of usage. A display preference only. Default off.</summary>
    public bool AlwaysFacesBack { get => _alwaysFacesBack; set => SetBool(ref _alwaysFacesBack, value, "alwaysFacesBack"); }
    /// <summary>Sprite edge length (px).</summary>
    public double FloatingPetSize
    {
        get => _floatingPetSize;
        set
        {
            _floatingPetSize = value;
            _settings.Set("floatingPetSize", value);
            NotifyChanged();
        }
    }
    /// <summary>Limit alerts as speech bubbles on the floating pet; independent of toasts.</summary>
    public bool FloatingPetBubbleAlerts { get => _floatingPetBubbleAlerts; set => SetBool(ref _floatingPetBubbleAlerts, value, "floatingPetBubbleAlerts"); }
    private bool _floatingPetAgentActivity;
    /// <summary>Let coding agents drive the floating pet through <c>poketokenbar://agent</c> URLs. Default on —
    /// nothing happens until the user wires an agent hook.</summary>
    public bool FloatingPetAgentActivity { get => _floatingPetAgentActivity; set => SetBool(ref _floatingPetAgentActivity, value, "floatingPetAgentActivity"); }

    private MenuLimitColorMode _menuLimitColorMode;
    public MenuLimitColorMode MenuLimitColorMode
    {
        get => _menuLimitColorMode;
        set
        {
            _menuLimitColorMode = value;
            _settings.Set("menuLimitColorMode", value switch
            {
                MenuLimitColorMode.Attention => "attention",
                MenuLimitColorMode.Off => "off",
                _ => "gauge",
            });
            NotifyChanged();
        }
    }

    /// <summary>Same labels and values as the macOS presets (the UI localizes the labels itself).</summary>
    public static readonly IReadOnlyList<(string Label, double Value)> IntervalPresets =
        [("수동", 0), ("1분", 60), ("2분", 120), ("5분", 300), ("15분", 900)];

    /// <summary>App language mirror for alert texts. The single source is <c>CompanionStore.Language</c>: the app
    /// syncs it at launch and on change.</summary>
    public AppLanguage LocalizationLanguage { get; set; } = AppLanguageExtensions.SystemDefault();

    private readonly IReadOnlyList<IUsageProvider> _providers;

    /// <summary>Registered usage sources — Settings lists these so extra scan folders stay provider-tagged (#177).</summary>
    public IReadOnlyList<(string Id, string DisplayName)> RegisteredProviders =>
        _providers.Select(p => (p.Id, p.DisplayName)).ToList();

    public IReadOnlyList<string> RegisteredProviderIDs => _providers.Select(p => p.Id).ToList();

    public string CustomScanRoots(string providerID) =>
        _settings.GetString(Core.CustomScanRoots.DefaultsKey(providerID)) ?? "";

    public void SetCustomScanRoots(string value, string providerID)
    {
        var key = Core.CustomScanRoots.DefaultsKey(providerID);
        if (value == (_settings.GetString(key) ?? "")) return;
        _settings.Set(key, value);
        LocalUsageReader.InvalidateProjectRootsCache();
        Spawn(() => Refresh());
    }

    private readonly IClaudeLimitsProviding _limitsProvider;
    private readonly Func<string, IClaudeLimitsProviding> _additionalClaudeLimitsProvider;
    private readonly Func<IReadOnlyList<string>> _discoverClaudeConfigDirs;
    private readonly Func<AccountIdentity?> _readDefaultIdentity;
    private readonly Func<string, DateTimeOffset?> _readLastPrompt;
    private readonly Func<DateTimeOffset, Task<IReadOnlyList<UsageEntry>>> _claudeUsageEntries;
    private readonly Func<string, Dictionary<string, List<DateTimeOffset>>> _readPromptHistory;
    /// <summary>Rate limits apply per account: one folder's 429 pauses that folder only.</summary>
    private Dictionary<string, (DateTimeOffset Until, TimeSpan Interval)> _additionalBackoff = new();
    /// <summary>One provider per folder, kept across polls: each owns the in-memory token the automatic path relies on.</summary>
    private Dictionary<string, IClaudeLimitsProviding> _additionalProviders = new();
    private readonly ISessionKeyManaging _sessionKeys;
    private readonly Func<string, ISessionKeyManaging> _accountSessionKeys;
    private readonly IProviderStatusProviding _statusProvider;
    private readonly AppSettings _settings;
    private readonly IUsageStoreScheduler _scheduler;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _displaysAsleep;
    private IDisposable? _timer;
    private IDisposable? _dayChangeTimer;
    private NetworkReachabilityMonitor? _networkMonitor;
    private bool _pollingSuspended;
    /// <summary>A slow check that runs only while suspended: a missed display-wake would otherwise freeze the last
    /// snapshot until restart (#350).</summary>
    private IDisposable? _suspendedProbeTimer;
    public static readonly TimeSpan SuspendedProbeInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan EmptyUsageRetryDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan BubbleTTL = TimeSpan.FromSeconds(6);
    private IDisposable? _emptyUsageRetry;
    /// <summary>Edge-triggered alert state: window key → highest tier already alerted (1 warning, 2 critical).
    /// Dropping below the warning line removes the key (re-arms). Volatile fields (resets_at) are never part of it.</summary>
    private readonly Dictionary<string, int> _notifiedTier = new();

    // MARK: Derived values

    private string TodayKey => LocalUsageReader.LocalDay(_now());

    public long TodayTotalTokens
    {
        get
        {
            // A snapshot dated another day (right after midnight) is left out.
            var todayKey = TodayKey;
            return _snapshots.Sum(s => s.Today?.Date == todayKey ? s.TodayTotalTokens : 0);
        }
    }

    /// <summary>Today's usage per provider id. The companion ledger works from this map, not the total: a carrier
    /// snapshot (today == null) or a snapshot dated another day is left out, so a provider that reported nothing
    /// this refresh does not move its ledger line.</summary>
    public IReadOnlyDictionary<string, long> TodayTokensByProvider
    {
        get
        {
            var todayKey = TodayKey;
            var result = new Dictionary<string, long>();
            foreach (var s in _snapshots)
                if (s.Today is { } today && today.Date == todayKey) result[s.ProviderID] = today.TotalTokens;
            return result;
        }
    }

    /// <summary>Any snapshot — the companion's sleep check.</summary>
    public bool HasUsageData => _snapshots.Count > 0;

    /// <summary>
    /// Menu bar lines: two or fewer items enabled → one line each; all three → tokens · cost on one line and the
    /// limit below (two lines). Empty = icon only. The limit line lists providers used today only.
    /// </summary>
    public IReadOnlyList<string> MenuLines => MenuLinesWithGrowth(growthText: null);

    /// <summary>
    /// Port of <c>menuLines(growthText:)</c> (a C# property and method cannot share a name): <see cref="MenuLines"/>
    /// plus the companion's growth. The tray tooltip has no two-line limit and is cut from the end at 127 characters,
    /// so on Windows the growth is its own line after the usage and limit lines instead of joining the usage line:
    /// if the tooltip is ever cut, the growth goes before the lines it was added next to.
    /// </summary>
    public IReadOnlyList<string> MenuLinesWithGrowth(string? growthText)
    {
        // Saved growth is available even before the first usage refresh finishes.
        if (LastUpdated == null) return growthText != null ? [growthText] : ["—"];
        var usage = new List<string>();
        if (ShowTokensInMenu) usage.Add(TokenFormatter.Compact(TodayTotalTokens));
        if (ShowCostInMenu && ShowsCost) usage.Add(TodayUsageCost.Text(new L(LocalizationLanguage), compact: true));
        var limit = MenuLimitLine;
        var lines = usage;
        if (limit != null && usage.Count == 2) lines = [string.Join(" · ", usage), limit];
        else if (limit != null) lines.Add(limit);
        if (growthText != null) lines.Add(growthText);
        return lines;
    }

    /// <summary>One line, used-today providers only (null = none). No "remaining" suffix: the surface is narrow.</summary>
    private string? MenuLimitLine
    {
        get
        {
            var parts = MenuLimitParts();
            return parts.Count == 0 ? null : string.Join(" · ", parts.Select(p => p.Text));
        }
    }

    public IReadOnlyList<MenuLimitPart> MenuLimitParts(DateTimeOffset? now = null)
    {
        if (!ShowLimitInMenu) return [];
        var at = now ?? _now();
        var parts = new List<MenuLimitPart>();
        if (MenuClaudeAccount?.Status.HeadlineWindow is ({ Utilization: { } utilization } window, var span))
        {
            var pace = window.ResetDate is { } reset ? PaceFraction(reset, span, at) : null;
            parts.Add(new MenuLimitPart($"Claude {TokenFormatter.Percent(LimitDisplayPercent(utilization))}", utilization, pace));
        }
        return parts;
    }

    /// <summary>The colored items, left to right. Separators and the token/cost line are never included.</summary>
    public IReadOnlyList<MenuLimitColorRun> MenuLimitColorRuns(DateTimeOffset? now = null)
    {
        if (MenuLimitColorMode == MenuLimitColorMode.Off) return [];
        var runs = new List<MenuLimitColorRun>();
        foreach (var part in MenuLimitParts(now))
        {
            var tier = PaceTiers.Gauge(part.Utilization, part.Pace, WarnThreshold, CritThreshold);
            if (MenuLimitColorMode == MenuLimitColorMode.Attention && tier.IsCalm()) continue;
            runs.Add(new MenuLimitColorRun(part.Text, tier));
        }
        return runs;
    }

    /// <summary>The account whose headline percentage (5h, else weekly) the menu bar shows, null when it shows none.</summary>
    private ClaudeAccountLimits? MenuClaudeAccount
    {
        get
        {
            if (!ShowLimitInMenu || !_snapshots.Any(s => s.ProviderID == "claude_code" && s.TodayTotalTokens > 0)) return null;
            return TrackedClaudeAccount is { Status.HeadlineWindow: not null } account ? account : null;
        }
    }

    /// <summary>With several accounts the menu bar percentage can switch account on its own: the tooltip names it.</summary>
    public string? MenuToolTip =>
        ClaudeAccounts.Count > 1 && MenuClaudeAccount is { } account
            ? new L(LocalizationLanguage).TrackedAccountToolTip(account.Title)
            : null;

    /// <summary>remaining = 100 − utilization, floored at 0. Numbers and gauge fill only.</summary>
    public static double DisplayPercent(double utilization, LimitPace.LimitDisplayMode mode) =>
        LimitPace.DisplayPercent(utilization, mode);

    public double LimitDisplayPercent(double utilization) => DisplayPercent(utilization, LimitDisplayMode);

    /// <summary>How far the window has run (0…1); null outside 0…1 rather than a clamp.</summary>
    public static double? PaceFraction(DateTimeOffset resetsAt, TimeSpan span, DateTimeOffset now) =>
        LimitPace.PaceFraction(resetsAt, span, now);

    /// <summary>Single-line form (accessibility, one-line fallback). The vertical rendering uses <see cref="MenuLines"/>.</summary>
    public string MenuTitle => string.Join(" · ", MenuLines);

    /// <summary>Snapshots that take part in cost aggregates and the cost UI.</summary>
    public IReadOnlyList<ProviderSnapshot> CostingSnapshots => _snapshots.Where(s => s.ReportsCost).ToList();

    /// <summary>A connected provider reports cost (unavailable amounts included).</summary>
    public bool ShowsCost => _snapshots.Any(s => s.ReportsCost);

    public UsageCost TodayUsageCost
    {
        get
        {
            var todayKey = TodayKey;
            var total = new UsageCost();
            foreach (var s in CostingSnapshots)
                if (s.Today is { } day && day.Date == todayKey) total.Add(day.UsageCost);
            return total;
        }
    }

    public double TodayCostTotal => TodayUsageCost.Amount;

    public UsageCost WeekUsageCost => SumCost(s => s.WeekTotal?.UsageCost);
    public UsageCost MonthUsageCost => SumCost(s => s.MonthTotal?.UsageCost);

    private UsageCost SumCost(Func<ProviderSnapshot, UsageCost?> pick)
    {
        var total = new UsageCost();
        foreach (var s in CostingSnapshots)
            if (pick(s) is { } cost) total.Add(cost);
        return total;
    }

    /// <summary>Provider tab selection: the preferred id when connected, else the first.</summary>
    public ProviderSnapshot? Snapshot(string? preferring)
    {
        if (preferring != null && _snapshots.FirstOrDefault(s => s.ProviderID == preferring) is { } s) return s;
        return _snapshots.FirstOrDefault();
    }

    public long WeekTotalTokens => _snapshots.Sum(s => s.WeekTotal?.TotalTokens ?? 0);
    public double WeekCostTotal => WeekUsageCost.Amount;
    public long MonthTotalTokens => _snapshots.Sum(s => s.MonthTotal?.TotalTokens ?? 0);
    public double MonthCostTotal => MonthUsageCost.Amount;

    /// <summary>
    /// This month's day-by-day totals summed across providers, in date order. A provider without a series is
    /// absent from the sum. Cost counts only providers that report it, coverage merged. The date axis is the
    /// union of the providers' own axes, so a scan straddling midnight cannot truncate the others' last day.
    /// </summary>
    public IReadOnlyList<DailyUsage> MonthDailyTotals => MergedDailyTotals(snapshot => snapshot.MonthDaily);

    /// <summary>The trend's day-by-day totals for <see cref="DailyTrendWindow"/>.</summary>
    public IReadOnlyList<DailyUsage> DailyTrendTotals => DailyTrendTotalsFor(DailyTrendWindow);

    /// <summary>
    /// Some provider has a used day in either series. Keeps the window selector on screen when the chosen
    /// window is empty but another one is not (e.g. 7 days idle, used earlier this month).
    /// </summary>
    public bool HasDailyTrendHistory => _snapshots.Any(snapshot =>
        snapshot.MonthDaily?.Any(d => d.TotalTokens > 0) == true
        || snapshot.RecentDaily?.Any(d => d.TotalTokens > 0) == true);

    public IReadOnlyList<DailyUsage> DailyTrendTotalsFor(DailyTrendWindow window) =>
        window.DayCount() is { } days ? RollingDailyTotals(days) : MonthDailyTotals;

    /// <summary>
    /// The last <paramref name="days"/> days summed across providers, merged like <see cref="MonthDailyTotals"/>.
    /// A provider without a rolling series falls back to the tail of its month series.
    /// </summary>
    public IReadOnlyList<DailyUsage> RollingDailyTotals(int days) =>
        MergedDailyTotals(snapshot => (snapshot.RecentDaily ?? snapshot.MonthDaily)?.TakeLast(days));

    private IReadOnlyList<DailyUsage> MergedDailyTotals(Func<ProviderSnapshot, IEnumerable<DailyUsage>?> seriesOf)
    {
        var byDay = new Dictionary<string, DailyUsage>();
        foreach (var snapshot in _snapshots)
        {
            if (seriesOf(snapshot) is not { } series) continue;
            foreach (var day in series)
            {
                var merged = byDay.GetValueOrDefault(day.Date)
                    ?? new DailyUsage(day.Date, 0, 0, 0, 0, 0, 0, CostCoverage: CostCoverage.Empty);
                var coverage = merged.CostCoverage;
                if (snapshot.ReportsCost) coverage.Merge(day.CostCoverage);
                byDay[day.Date] = merged with
                {
                    InputTokens = merged.InputTokens + day.InputTokens,
                    OutputTokens = merged.OutputTokens + day.OutputTokens,
                    CacheCreationTokens = merged.CacheCreationTokens + day.CacheCreationTokens,
                    CacheReadTokens = merged.CacheReadTokens + day.CacheReadTokens,
                    TotalTokens = merged.TotalTokens + day.TotalTokens,
                    TotalCost = merged.TotalCost + (snapshot.ReportsCost ? day.TotalCost : 0),
                    CostCoverage = coverage,
                };
            }
        }
        // "yyyy-MM-dd" sorts lexicographically the same way it sorts chronologically.
        return byDay.Values.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
    }

    /// <summary>Day-by-day history behind the usage recap, copied from the month series at every refresh.</summary>
    public UsageLedger DailyLedger { get; private set; }

    /// <summary>The first refresh of this session has finished its enrichment scan. Until then <c>LastUsage</c> may be a
    /// restored or carried-over value, so nothing is known yet.</summary>
    public bool LastUsageKnown { get; private set; }
    /// <summary>Providers whose latest enrichment scan failed. Their <c>LastUsage</c> is a carried-over value.</summary>
    private HashSet<string> _failedEnrichmentIDs = [];

    /// <summary>
    /// How long ago the newest token usage happened (<see cref="ProviderSnapshot.LastUsage"/>, the same value that keeps
    /// a recently used provider's tab visible). Null only before the first complete refresh, which must never be
    /// treated as idle. After that a provider that cannot be read does not veto: it is ignored, and if no provider
    /// reports any usage time the answer is "at least the scan window".
    /// </summary>
    public TimeSpan? TimeSinceLastUsage(DateTimeOffset? now = null)
    {
        if (!LastUsageKnown) return null;
        var reported = _snapshots.Where(s => !_failedEnrichmentIDs.Contains(s.ProviderID))
            .Select(s => s.LastUsage).OfType<DateTimeOffset>().ToList();
        if (reported.Count == 0) return LocalUsageReader.RecentUseWindow;
        var idle = (now ?? _now()) - reported.Max();
        return idle < TimeSpan.Zero ? TimeSpan.Zero : idle;
    }

    /// <summary>The run of days with usage that is still going, read from the same ledger as the recap.</summary>
    public UsageStreak CurrentStreak => DailyLedger.CurrentStreak(_now(), RecapCalendar.Current());

    private void RecordDailyLedger()
    {
        // UsageLedger is a class: copy it through an in-memory settings store (Swift copied the struct).
        var scratch = new AppSettings(null);
        DailyLedger.Save(scratch);
        var ledger = UsageLedger.Load(scratch);
        ledger.Merge(MonthDailyTotals);
        // This year and the one before it: the year view compares against last year, nothing reaches further.
        var calendar = RecapCalendar.Current();
        var lastYear = new RecapPeriod(RecapScope.Year, _now(), calendar).Shifted(-1, calendar);
        ledger.Prune(LocalUsageReader.LocalDay(lastYear.Start));
        if (ledger.Equals(DailyLedger)) return;
        DailyLedger = ledger;
        ledger.Save(_settings);
    }

    /// <summary>Claude's active 5h block: the forecast pairs with the Claude official limit.</summary>
    private BlockUsage? ClaudeActiveBlock => _snapshots.FirstOrDefault(s => s.ProviderID == "claude_code")?.ActiveBlock;

    /// <summary>Summed burn (tokens/min) of every provider's active block — the companion rhythm.</summary>
    private double CombinedBurnPerMinute => _snapshots.Sum(s => s.ActiveBlock?.TokensPerMinute ?? 0);

    // MARK: Limit depletion forecast

    public FiveHourForecast? FiveHourForecast
    {
        get
        {
            if (TrackedClaudeAccount is not { } account || account.Status.FiveHour is not { } window
                || window.Utilization is not { } utilization || window.ResetDate is not { } reset) return null;
            var now = _now();
            if (utilization >= 100) return new FiveHourForecast(now, true);
            if (ClaudeCurrentBlock(account) is not { TokensPerMinute: { } burn } block
                || ForecastDepletion(block.TotalTokens, burn, utilization, now) is not { } depletion) return null;
            return new FiveHourForecast(depletion, depletion < reset);
        }
    }

    /// <summary>The local 5h block behind an account's tab and forecast. With several accounts the machine-wide
    /// block mixes their usage: use the account's own.</summary>
    public BlockUsage? ClaudeCurrentBlock(ClaudeAccountLimits account) =>
        ClaudeAccounts.Count > 1 ? _claudeAccountBlocks.GetValueOrDefault(account.Id) : ClaudeActiveBlock;

    /// <summary>Estimates the 5h token budget as block tokens ÷ utilization% and extrapolates when 100% is hit.
    /// Null below 5% utilization or 10k tokens/min (unstable), or beyond a day.</summary>
    public static DateTimeOffset? ForecastDepletion(long blockTokens, double tokensPerMinute, double utilization, DateTimeOffset now)
    {
        if (utilization < 5 || utilization >= 100 || blockTokens <= 0 || tokensPerMinute < 10_000) return null;
        var tokensPerPercent = blockTokens / utilization;
        var minutesLeft = (100 - utilization) * tokensPerPercent / tokensPerMinute;
        if (!double.IsFinite(minutesLeft) || minutesLeft >= 60 * 24) return null;
        return now.AddSeconds(minutesLeft * 60);
    }

    /// <summary>Menu bar warning: any window the popover shows at or over critical, or 100% predicted before reset.</summary>
    public bool IsLimitWarning
    {
        get
        {
            if ((TrackedClaudeAccount?.Status.AllUtilizations ?? []).Any(u => u >= CritThreshold)) return true;
            return FiveHourForecast is { BeforeReset: true };
        }
    }

    /// <summary>Highest official-limit utilization of providers used today (compact surfaces only).</summary>
    public double? HighestLimitUtilization
    {
        get
        {
            if (!_snapshots.Any(s => s.ProviderID == "claude_code" && s.TodayTotalTokens > 0)) return null;
            var utils = TrackedClaudeAccount?.Status.AllUtilizations ?? [];
            return utils.Count == 0 ? null : utils.Max();
        }
    }

    /// <summary>
    /// Candy windows: the 5h session (1 candy) and the weekly window (5) of every Claude account. Per-model weekly
    /// and scoped windows are left out (sub-windows of the headline ones; no double grant). Narrower than alerts.
    /// An additional account counts only once armed (seen below 100%).
    /// </summary>
    public IReadOnlyList<CandyWindow> CandyEligibleWindows
    {
        get
        {
            var l = new L(LocalizationLanguage);
            var accounts = ClaudeAccounts;
            var windows = new List<CandyWindow>();
            foreach (var account in accounts)
                windows.AddRange(ClaudeCandyWindows(account, accounts.Count > 1, l)
                    .Where(w => account.IsDefault || _armedCandyWindows.Contains(w.Key)));
            return windows;
        }
    }

    /// <param name="named">Next to other accounts, the notification says whose limit it is.</param>
    private static List<CandyWindow> ClaudeCandyWindows(ClaudeAccountLimits account, bool named, L l)
    {
        var suffix = named ? $" ({account.Title})" : "";
        var windows = new List<CandyWindow>();
        if (account.Status.FiveHour is { Utilization: { } u5 } fiveHour)
            windows.Add(new CandyWindow($"{account.WindowKeyPrefix}.fiveHour", l.ClaudeFiveHour + suffix, WindowClass.Session,
                u5, fiveHour.ResetDate is { } r5 ? Iso8601Parser.Format(r5) : fiveHour.ResetsAt));
        if (account.Status.SevenDay is { Utilization: { } u7 } sevenDay)
            windows.Add(new CandyWindow($"{account.WindowKeyPrefix}.sevenDay", l.ClaudeWeekly + suffix, WindowClass.Weekly,
                u7, sevenDay.ResetDate is { } r7 ? Iso8601Parser.Format(r7) : sevenDay.ResetsAt));
        return windows;
    }

    /// <summary>Candy keys of additional accounts once seen below 100%. The first-run seed only covers windows known
    /// at first launch: an account found later, already at its limit, would otherwise earn candy for a limit
    /// reached before the app knew about it.</summary>
    private readonly HashSet<string> _armedCandyWindows;
    public const string ArmedCandyWindowsKey = "claudeAccountCandyArmed";

    private void ArmCandyWindows()
    {
        var l = new L(LocalizationLanguage);
        var added = false;
        foreach (var account in ClaudeAccounts.Where(a => !a.IsDefault))
            foreach (var window in ClaudeCandyWindows(account, false, l).Where(w => w.Utilization < 100))
                added |= _armedCandyWindows.Add(window.Key);
        if (!added) return;
        var array = new JsonArray();
        foreach (var key in _armedCandyWindows.Order(StringComparer.Ordinal)) array.Add(key);
        _settings.SetNode(ArmedCandyWindowsKey, array);
    }

    /// <summary>At least one account's limits loaded — the candy first-run seed gate (no seeding while loading).</summary>
    public bool LimitsReady => Limits != null || _additionalLimits.Count > 0;

    /// <summary>Burn tier from the summed burn of every provider.</summary>
    public BurnTier BurnTier
    {
        get
        {
            var burn = CombinedBurnPerMinute;
            if (burn <= 1_000) return BurnTier.Idle;
            if (burn < 100_000) return BurnTier.Normal;
            if (burn < 400_000) return BurnTier.Fast;
            return BurnTier.Blazing;
        }
    }

    public bool IsStale
    {
        get
        {
            if (LastUpdated is not { } last) return true;
            var allowance = RefreshInterval > 0 ? RefreshInterval * 2 : 1800;
            return (_now() - last).TotalSeconds > allowance;
        }
    }

    // MARK: Companion feed (the macOS AppDelegate's updateCompanion / onStoreRefreshed)

    /// <summary>Usage → companion (EXP ledger and display state). Call on every <see cref="Changed"/>.</summary>
    public void UpdateCompanion(CompanionStore companion)
    {
        companion.AlwaysFacesBack = AlwaysFacesBack;
        companion.Update(TodayTokensByProvider, TodayKey, MonthTotalTokens, BurnTier, IsLimitWarning, HasUsageData,
            TimeSinceLastUsage());
    }

    /// <summary>Companion update plus candy grant, at the point the limits are fresh. Call from <see cref="OnRefresh"/>.</summary>
    public void FeedCompanionAfterRefresh(CompanionStore companion)
    {
        UpdateCompanion(companion);
        companion.GrantCandies(CandyEligibleWindows, LimitsReady);
    }

    // MARK: Lifecycle

    /// <param name="providers">Usage sources. Default: Claude Code only (the Windows port).</param>
    /// <param name="claudeLimitsProvider">Session key first, then the OAuth credentials file.</param>
    /// <param name="settings">Settings store; tests pass an in-memory one. Default <see cref="AppSettings.Current"/>.</param>
    /// <param name="displaysAsleep">Whether the display is off. Default false: the host reports display sleep through
    /// <see cref="SuspendPolling"/> / <see cref="ResumePolling"/>.</param>
    /// <param name="scheduler">Timers. Default: thread timers posted to the creating thread's context.</param>
    /// <param name="now">Clock seam.</param>
    public UsageStore(
        IReadOnlyList<IUsageProvider>? providers = null,
        IClaudeLimitsProviding? claudeLimitsProvider = null,
        Func<string, IClaudeLimitsProviding>? additionalClaudeLimitsProvider = null,
        Func<IReadOnlyList<string>>? discoverClaudeConfigDirs = null,
        Func<AccountIdentity?>? readDefaultIdentity = null,
        Func<string, DateTimeOffset?>? readLastPrompt = null,
        Func<DateTimeOffset, Task<IReadOnlyList<UsageEntry>>>? claudeUsageEntries = null,
        Func<string, Dictionary<string, List<DateTimeOffset>>>? readPromptHistory = null,
        IProviderStatusProviding? statusProvider = null,
        ISessionKeyManaging? sessionKeys = null,
        Func<string, ISessionKeyManaging>? accountSessionKeys = null,
        bool autoRefresh = true,
        AppSettings? settings = null,
        Func<bool>? displaysAsleep = null,
        IUsageStoreScheduler? scheduler = null,
        Func<DateTimeOffset>? now = null)
    {
        _providers = providers ?? [new LocalClaudeProvider()];
        // The session key and the chain must be the same instance: the organization Settings picks is the one the
        // limits call uses.
        _sessionKeys = sessionKeys ?? SessionKeyLimitsProvider.Shared;
        _limitsProvider = claudeLimitsProvider
            ?? new ChainedLimitsProvider(SessionKeyLimitsProvider.Shared, new OAuthLimitsProvider());
        _additionalClaudeLimitsProvider = additionalClaudeLimitsProvider ?? (root => ChainedLimitsProvider.ForConfigRoot(root));
        _discoverClaudeConfigDirs = discoverClaudeConfigDirs ?? InstalledDiscovery;
        _readDefaultIdentity = readDefaultIdentity ?? (() => ClaudeAccountRoots.InstalledDefaultIdentity());
        _readLastPrompt = readLastPrompt ?? (dir => ClaudeAccountRoots.InstalledLastPromptDate(dir));
        _claudeUsageEntries = claudeUsageEntries ?? (since => InstalledClaudeUsageEntries(since));
        _readPromptHistory = readPromptHistory ?? (dir => ClaudePromptHistory.InstalledPrompts(dir));
        _accountSessionKeys = accountSessionKeys ?? (root => new SessionKeyLimitsProvider(SessionKeyStore.ForConfigRoot(root)));
        _statusProvider = statusProvider ?? new StatuspageStatusProvider();
        _settings = settings ?? AppSettings.Current;
        _displaysAsleep = displaysAsleep ?? (() => false);
        _scheduler = scheduler ?? new SynchronizationContextScheduler();
        _now = now ?? (() => DateTimeOffset.Now);

        var s = _settings;
        _refreshInterval = s.GetDouble("refreshInterval") ?? 120;
        _critThreshold = s.GetDouble("critThreshold") ?? 95;
        _warnThreshold = Math.Min(s.GetDouble("warnThreshold") ?? 80, _critThreshold - ThresholdGap);
        _showTokensInMenu = s.GetBool("showTokensInMenu") ?? true;
        _showCostInMenu = s.GetBool("showCostInMenu") ?? false;
        _showLimitInMenu = s.GetBool("showLimitInMenu") ?? false;
        _showGrowthInMenu = s.GetBool("showGrowthInMenu") ?? false;
        _limitDisplayMode = s.GetString("limitDisplayMode") == "remaining"
            ? LimitPace.LimitDisplayMode.Remaining : LimitPace.LimitDisplayMode.Used;
        _dailyTrendWindow = DailyTrendWindows.FromRawValue(s.GetString("dailyTrendWindow")) ?? DailyTrendWindow.D14;
        _menuLimitColorMode = s.GetString("menuLimitColorMode") switch
        {
            "attention" => MenuLimitColorMode.Attention,
            "off" => MenuLimitColorMode.Off,
            _ => MenuLimitColorMode.Gauge,
        };
        _activityVisualization = ActivityVisualizations.FromRawValue(s.GetString("activityVisualization"))
            ?? ActivityVisualizations.Default;
        _limitNotifications =s.GetBool("limitNotifications") ?? true;
        _companionNotifications = s.GetBool("companionNotifications") ?? true;
        _updateNotificationsEnabled = s.GetBool("updateNotificationsEnabled") ?? true;
        _statusChecksEnabled = s.GetBool("statusChecksEnabled") ?? true;
        _floatingPetEnabled = s.GetBool("floatingPetEnabled") ?? false;
        _alwaysFacesBack = s.GetBool("alwaysFacesBack") ?? false;
        _floatingPetSize = s.GetDouble("floatingPetSize") ?? 96;
        _floatingPetBubbleAlerts = s.GetBool("floatingPetBubbleAlerts") ?? true;
        _floatingPetAgentActivity = s.GetBool("floatingPetAgentActivity") ?? true;
        // Power saver by default: the frame rate of the fixed cap before this setting existed.
        _animationQuality = AnimationQualities.FromRawValue(s.GetString("animationQuality")) ?? AnimationQuality.PowerSaver;
        _additionalClaudeConfigDirs = s.GetString(ClaudeAccountRoots.DefaultsKey) ?? "";
        _claudeTrackedAccountMode = ClaudeTrackedAccountMode.FromStoredValue(s.GetString(ClaudeTrackedAccountMode.DefaultsKey));
        _armedCandyWindows = s.GetNode(ArmedCandyWindowsKey) is JsonArray armed
            ? armed.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var key) ? key : null)
                .Where(k => k != null).Select(k => k!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        DailyLedger = UsageLedger.Load(s);

        if (_sessionKeys.Credential() is { } credential)
        {
            SessionKeyConfigured = true;
            SessionKeySelectedOrgID = credential.OrganizationID;
        }

        Reschedule();
        // Midnight: refresh the "today" bucket at once (NSCalendarDayChanged on macOS).
        ScheduleDayChange();

        // Network back: refresh without waiting for the timer. Installed app only, like the macOS bundle check.
        if (AppEnv.IsInstalledApp)
        {
            var net = new NetworkReachabilityMonitor();
            var context = SynchronizationContext.Current;
            net.OnReconnected = () =>
            {
                if (context != null) context.Post(_ => Spawn(() => Refresh()), null);
                else Spawn(() => Refresh());
            };
            net.Start();
            _networkMonitor = net;
        }

        if (autoRefresh) Spawn(() => Refresh());
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _dayChangeTimer?.Dispose();
        _suspendedProbeTimer?.Dispose();
        _suspendedProbeTimer = null;
        _emptyUsageRetry?.Dispose();
        _bubbleDismiss?.Dispose();
        _networkMonitor?.Dispose();
    }

    private static IReadOnlyList<string> InstalledDiscovery() =>
        AppEnv.IsInstalledApp
            ? ClaudeAccountRoots.Discovered(AppEnv.HomeDirectory, UsageEnvironment.Value("CLAUDE_CONFIG_DIR"))
            : [];

    private void Reschedule()
    {
        _timer?.Dispose();
        _timer = null;
        if (_pollingSuspended || RefreshInterval <= 0) return;
        _timer = _scheduler.Repeat(TimeSpan.FromSeconds(RefreshInterval), () => Spawn(() => Refresh()));
    }

    private void ScheduleDayChange()
    {
        var now = _now();
        var next = LocalUsageReader.StartOfDay(now).AddDays(1);
        _dayChangeTimer?.Dispose();
        _dayChangeTimer = _scheduler.After(next - now + TimeSpan.FromSeconds(1), () =>
        {
            ScheduleDayChange();
            Spawn(() => Refresh());
        });
    }

    public bool IsPollingSuspended => _pollingSuspended;
    public bool IsPollingTimerScheduled => _timer != null;
    public bool IsSuspendedProbeScheduled => _suspendedProbeTimer != null;

    /// <summary>Display off → stop the polling timer (battery). A slow probe runs meanwhile (#350).</summary>
    public void SuspendPolling()
    {
        _pollingSuspended = true;
        _timer?.Dispose();
        _timer = null;
        _suspendedProbeTimer?.Dispose();
        _suspendedProbeTimer = _scheduler.Repeat(SuspendedProbeInterval, () => Spawn(RunSuspendedPollingProbe));
    }

    /// <summary>Display on → resume polling and refresh once. Called by the host (screensDidWake on macOS).</summary>
    public void ResumePolling()
    {
        if (!_pollingSuspended) return;
        ClearPollingSuspension();
        Spawn(() => Refresh());
    }

    private void ClearPollingSuspension()
    {
        _pollingSuspended = false;
        _suspendedProbeTimer?.Dispose();
        _suspendedProbeTimer = null;
        Reschedule();
    }

    /// <summary>The display is already on without a wake event: lift the suspension. True when lifted.</summary>
    private bool ClearStaleSuspension(string reason)
    {
        if (!_pollingSuspended || _displaysAsleep()) return false;
        AppLog.Write($"polling resumed without display wake ({reason})");
        ClearPollingSuspension();
        return true;
    }

    public async Task RunSuspendedPollingProbe()
    {
        if (!ClearStaleSuspension("probe")) return;
        await Refresh();
    }

    /// <summary>System resume from sleep (the host forwards the power event).</summary>
    public async Task HandleSystemWake()
    {
        ClearStaleSuspension("didWake");
        await Refresh();
    }

    // MARK: Refresh

    public async Task Refresh(bool scheduleEmptyRetry = true)
    {
        // A request during a refresh is not dropped: it runs once afterwards (coalesced).
        if (IsRefreshing)
        {
            _refreshPending = true;
            return;
        }
        IsRefreshing = true;
        NotifyChanged();
        ClearStaleSuspension("refresh");
        try
        {
            await RefreshBody(scheduleEmptyRetry);
        }
        finally
        {
            IsRefreshing = false;
            // The arrival rate of requests is finite, so this never loops forever.
            if (_refreshPending)
            {
                _refreshPending = false;
                Spawn(() => Refresh(scheduleEmptyRetry));
            }
            NotifyChanged();
        }
    }

    private async Task RefreshBody(bool scheduleEmptyRetry)
    {
        var todayKey = TodayKey;

        // ── Phase 1: daily (critical) — the menu number and the stale check are settled here.
        var outcomes = await Task.WhenAll(_providers.Select(async provider =>
        {
            try
            {
                return (provider.Id, Today: await provider.FetchDaily(), Error: (string?)null);
            }
            catch (Exception error)
            {
                return (provider.Id, Today: (DailyUsage?)null, Error: (string?)error.Message);
            }
        }));
        var dailyByID = new Dictionary<string, DailyUsage>();
        var failedIDs = new HashSet<string>();
        var errors = new List<string>();
        foreach (var (id, today, error) in outcomes)
        {
            if (today != null) dailyByID[id] = today;
            if (error == null) continue;
            failedIDs.Add(id);
            errors.Add($"{id}: {error}");
        }

        var newSnapshots = new List<ProviderSnapshot>();
        var fetchedAt = _now();
        foreach (var provider in _providers)
        {
            // Yesterday's numbers are not carried (no midnight freeze); block/week/month are, so the popover's
            // "this week / this month" rows do not blink until phase 2 fills them again.
            var previous = _snapshots.FirstOrDefault(s => s.ProviderID == provider.Id);
            var prevToday = previous?.Today?.Date == todayKey ? previous.Today : null;

            DailyUsage? today;
            if (dailyByID.TryGetValue(provider.Id, out var fetched))
            {
                if (prevToday != null && fetched.TotalTokens < prevToday.TotalTokens)
                    AppLog.Write($"usage regression provider={provider.Id} date={fetched.Date} previous={prevToday.TotalTokens} current={fetched.TotalTokens}");
                today = fetched;
            }
            else if (failedIDs.Contains(provider.Id))
                today = prevToday;   // failure → keep today's previous value
            else
                today = null;        // success without usage today

            var snapshot = new ProviderSnapshot
            {
                ProviderID = provider.Id,
                DisplayName = provider.DisplayName,
                Today = today,
                ActiveBlock = previous?.ActiveBlock,
                WeekTotal = previous?.WeekTotal,
                MonthTotal = previous?.MonthTotal,
                MonthDaily = previous?.MonthDaily,
                RecentDaily = previous?.RecentDaily,
                FetchedAt = fetchedAt,
                ReportsCost = provider.ReportsCost,
                LastUsage = previous?.LastUsage,
            };
            // Keep history visible while phase 2 refreshes it, including on idle days.
            if (snapshot.HasDisplayableUsage(fetchedAt)) newSnapshots.Add(snapshot);
        }
        _snapshots = newSnapshots;

        if (errors.Count == 0)
        {
            LastUpdated = _now();
            LastErrorDescription = null;
        }
        else
        {
            LastErrorDescription = string.Join(" / ", errors);
            if (LastUpdated == null && _snapshots.Count > 0) LastUpdated = _now();
        }
        AppLog.Write($"phase1 done total={TodayTotalTokens} errors={(errors.Count == 0 ? "none" : string.Join(" | ", errors))}");
        HandleEmptyUsageRetry(scheduleEmptyRetry, errors.Count > 0);
        NotifyChanged();

        // ── Phase 2: block / week / month (best effort) — a failure keeps the previous values.
        var enrichments = await Task.WhenAll(_providers.Select(async provider =>
        {
            try
            {
                return (provider, Enrichment: await provider.FetchEnrichment());
            }
            catch (Exception error)
            {
                AppLog.Write($"enrichment failed provider={provider.Id}: {error.Message}");
                return (provider, Enrichment: new ProviderEnrichment());
            }
        }));
        var enrichedAt = _now();
        var failedEnrichment = new HashSet<string>();
        foreach (var (provider, enrichment) in enrichments)
        {
            if (!enrichment.PeriodsOK) failedEnrichment.Add(provider.Id);
            var index = _snapshots.FindIndex(s => s.ProviderID == provider.Id);
            if (index < 0)
            {
                // An idle day still has useful history; positive usage only (local providers also return
                // all-zero period summaries).
                var carrier = new ProviderSnapshot
                {
                    ProviderID = provider.Id,
                    DisplayName = provider.DisplayName,
                    ActiveBlock = enrichment.BlocksOK ? enrichment.ActiveBlock : null,
                    WeekTotal = enrichment.PeriodsOK ? enrichment.WeekTotal : null,
                    MonthTotal = enrichment.PeriodsOK ? enrichment.MonthTotal : null,
                    MonthDaily = enrichment.PeriodsOK ? enrichment.MonthDaily : null,
                    RecentDaily = enrichment.PeriodsOK ? enrichment.RecentDaily : null,
                    FetchedAt = enrichedAt,
                    ReportsCost = provider.ReportsCost,
                    LastUsage = enrichment.PeriodsOK ? enrichment.LastUsage : null,
                };
                if (carrier.HasDisplayableUsage(enrichedAt)) _snapshots.Add(carrier);
                continue;
            }
            var updated = _snapshots[index];
            if (enrichment.BlocksOK) updated = updated with { ActiveBlock = enrichment.ActiveBlock };
            if (enrichment.PeriodsOK)
                updated = updated with
                {
                    WeekTotal = enrichment.WeekTotal,
                    MonthTotal = enrichment.MonthTotal,
                    MonthDaily = enrichment.MonthDaily,
                    RecentDaily = enrichment.RecentDaily,
                    LastUsage = enrichment.LastUsage,
                };
            _snapshots[index] = updated;
        }
        _failedEnrichmentIDs = failedEnrichment;
        LastUsageKnown = true;
        // A successful empty enrichment removes obsolete carriers; failed reads retained their history above.
        _snapshots.RemoveAll(s => !s.HasDisplayableUsage(enrichedAt));
        // Keep tabs in provider registration order.
        var order = new Dictionary<string, int>();
        for (var i = 0; i < _providers.Count; i++) order.TryAdd(_providers[i].Id, i);
        _snapshots = _snapshots.OrderBy(s => order.GetValueOrDefault(s.ProviderID, int.MaxValue)).ToList();
        // The ledger is recorded here: the month series only exists after phase 2.
        RecordDailyLedger();
        NotifyChanged();

        // ── Official limits (network, last).
        if (_claudeLimitsBackoffUntil is { } until && _now() < until)
        {
            // 429 backoff: polling pauses so the rate limit does not get worse.
            AppLog.Write($"claude limits backoff: skipping ({(int)(until - _now()).TotalSeconds}s left)");
        }
        else
        {
            try
            {
                Limits = await _limitsProvider.Fetch(allowKeychainPrompt: false);
                LimitsAvailable = true;
                LimitsUpdatedAt = _now();
                LimitsAuthExpiry = null;
                _limitTokenRefreshFailure = null;   // the values loaded: a manual failure is no longer news
                ResetLimitsBackoff();
                AppLog.Write($"limits refreshed fiveHour={Limits.FiveHour?.Utilization} sevenDay={Limits.SevenDay?.Utilization}");
            }
            catch (Exception error)
            {
                // The endpoint is unofficial: a failure hides the section, the token numbers are unaffected.
                if (Limits == null) LimitsAvailable = false;
                UpdateAuthExpired(error);
                ApplyLimitsBackoffIfRateLimited(error);
                AppLog.WriteIfChanged("claude-limits", $"limits unavailable: {error.Message}");
            }
        }
        await RefreshAdditionalClaudeLimits(allowKeychainPrompt: false);
        await RefreshProviderStatuses();

        CheckLimitAlerts();
        AppLog.Write($"refresh done [{string.Join(", ", _snapshots.Select(s => $"{s.ProviderID}:{s.Today?.Date ?? "nil"}={s.TodayTotalTokens}"))}]");
        NotifyChanged();
        OnRefresh?.Invoke();   // the companion and candy follow fresh limits
    }

    private void HandleEmptyUsageRetry(bool schedule, bool hasErrors)
    {
        _emptyUsageRetry?.Dispose();
        _emptyUsageRetry = null;
        if (!schedule || hasErrors || _snapshots.Count > 0) return;
        AppLog.Write("empty usage retry scheduled");
        _emptyUsageRetry = _scheduler.After(EmptyUsageRetryDelay, () =>
        {
            _emptyUsageRetry = null;
            Spawn(() => Refresh(scheduleEmptyRetry: false));
        });
    }

    /// <summary>
    /// The popover/Settings "refresh limits" button. macOS reads the Keychain here (the only path allowed to
    /// prompt). Windows has no Keychain: this re-reads the credentials file now (<c>allowKeychainPrompt: true</c>
    /// makes the OAuth path skip its in-memory token, #300) and bypasses the 429 backoff once. The macOS
    /// "declined prompt stops the other prompts" rule (#280) has nothing to decline here and is dropped.
    /// </summary>
    public async Task RefreshLimitTokenFromKeychain()
    {
        if (IsRefreshingLimitToken) return;
        IsRefreshingLimitToken = true;
        NotifyChanged();
        try
        {
            try
            {
                Limits = await _limitsProvider.Fetch(allowKeychainPrompt: true);
                LimitsAvailable = true;
                LimitsUpdatedAt = _now();
                LimitsAuthExpiry = null;
                _limitTokenRefreshFailure = null;
                ResetLimitsBackoff();
                AppLog.Write($"limits refreshed by user action fiveHour={Limits.FiveHour?.Utilization} sevenDay={Limits.SevenDay?.Utilization}");
            }
            catch (Exception error)
            {
                _limitTokenRefreshFailure = error;
                if (Limits == null) LimitsAvailable = false;
                UpdateAuthExpired(error);
                ApplyLimitsBackoffIfRateLimited(error);
                AppLog.Write($"limits user refresh failed: {error.Message}");
            }
            await RefreshAdditionalClaudeLimits(allowKeychainPrompt: true);
        }
        finally
        {
            IsRefreshingLimitToken = false;
            NotifyChanged();
        }
    }

    // MARK: Additional Claude accounts

    /// <summary>Every Claude account with official limits: the default login, then the additional folders.
    /// Tabs, alerts and candy iterate this list; single-account surfaces follow <see cref="TrackedClaudeAccount"/>.</summary>
    public IReadOnlyList<ClaudeAccountLimits> ClaudeAccounts
    {
        get
        {
            var additional = _additionalLimits.Select(ClaudeAccountLimits.Additional).ToList();
            if (Limits is { } limits)
                return [ClaudeAccountLimits.DefaultAccount(limits, LimitsAuthExpired, LimitsUpdatedAt), .. additional];
            // Next to other accounts the default login keeps its tab while its limits load, so tabs do not come
            // and go. Only when that login exists: CLAUDE_CONFIG_DIR-only users would get a tab that never fills.
            if (additional.Count == 0 || DefaultSavedIdentity is not { } identity) return additional;
            var placeholder = new LimitStatus { AccountEmail = identity.Email, AccountOrganizationName = identity.OrganizationName };
            return [ClaudeAccountLimits.DefaultAccount(placeholder, LimitsAuthExpired), .. additional];
        }
    }

    /// <summary>Login saved for the default folder, read at each refresh.</summary>
    public AccountIdentity? DefaultSavedIdentity { get; private set; }

    private ClaudeTrackedAccountMode _claudeTrackedAccountMode;
    /// <summary>Which account the single-account surfaces follow.</summary>
    public ClaudeTrackedAccountMode ClaudeTrackedAccountMode
    {
        get => _claudeTrackedAccountMode;
        set
        {
            _claudeTrackedAccountMode = value;
            _settings.Set(ClaudeTrackedAccountMode.DefaultsKey, value.StoredValue);
            NotifyChanged();
            OnRefresh?.Invoke();   // the companion mood follows the tracked account's warning state
        }
    }

    private Dictionary<string, DateTimeOffset> _claudeAccountActivity = new();
    /// <summary>Last prompt per account id, read at each refresh while several accounts are shown.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> ClaudeAccountActivity => _claudeAccountActivity;

    private Dictionary<string, ClaudeAccountUsage> _claudeAccountUsage = new();
    /// <summary>Local tokens and cost per account id while several accounts are shown. Header totals stay machine-wide.</summary>
    public IReadOnlyDictionary<string, ClaudeAccountUsage> ClaudeAccountUsage => _claudeAccountUsage;
    /// <summary>Usage no login's history explains (print mode, SDK runs, sessions older than the history).</summary>
    public ClaudeAccountUsage UnattributedClaudeUsage { get; private set; }
    /// <summary>Each account's own active 5h block.</summary>
    private Dictionary<string, BlockUsage> _claudeAccountBlocks = new();

    /// <summary>The account behind the menu bar percentage, warning state, companion mood, pet hover and forecast.</summary>
    public ClaudeAccountLimits? TrackedClaudeAccount =>
        TrackedAccount(ClaudeAccounts, ClaudeTrackedAccountMode, _claudeAccountActivity);

    public static ClaudeAccountLimits? TrackedAccount(IReadOnlyList<ClaudeAccountLimits> accounts,
        ClaudeTrackedAccountMode mode, IReadOnlyDictionary<string, DateTimeOffset> activity) =>
        ClaudeAccountLimits.TrackedAccount(accounts, mode, activity);

    /// <summary>The default account has no limits although it should (not with CLAUDE_CONFIG_DIR logins only).</summary>
    public bool ClaudeLimitsMissing => Limits == null && (_additionalLimits.Count == 0 || DefaultSavedIdentity != null);

    /// <summary>An additional account's values are older than 15 minutes.</summary>
    public bool AdditionalLimitsStale => ClaudeAccounts.Any(a => !a.IsDefault && a.IsStale(_now()));

    /// <summary>Folders found automatically at the last refresh (Settings lists them).</summary>
    public IReadOnlyList<string> DetectedClaudeConfigDirs { get; private set; } = [];

    private string _additionalClaudeConfigDirs;
    /// <summary>Settings → Advanced: extra Claude config folders, comma or newline separated.</summary>
    public string AdditionalClaudeConfigDirs
    {
        get => _additionalClaudeConfigDirs;
        set
        {
            if (value == _additionalClaudeConfigDirs) return;
            _additionalClaudeConfigDirs = value;
            _settings.Set(ClaudeAccountRoots.DefaultsKey, value);
            LocalUsageReader.InvalidateProjectRootsCache();   // their projects/ folders are scanned too
            NotifyChanged();
            Spawn(() => Refresh());
        }
    }

    /// <summary>Additional refreshes run one at a time: a later one would otherwise overwrite fresher values.</summary>
    private Task? _additionalRefresh;

    private async Task RefreshAdditionalClaudeLimits(bool allowKeychainPrompt)
    {
        var task = RunAfter(_additionalRefresh, allowKeychainPrompt);
        _additionalRefresh = task;
        await task;
    }

    private async Task RunAfter(Task? previous, bool allowKeychainPrompt)
    {
        if (previous != null) await previous;
        await RunAdditionalClaudeLimitsRefresh(allowKeychainPrompt);
    }

    /// <summary>
    /// A manual refresh bypasses each folder's 429 backoff. With a session key on the default account, only the
    /// folders listed in Settings count as fixable by a manual refresh. An account already shown (same email and
    /// organization) is listed once. A rejected token marks the folder expired until a fetch succeeds again.
    /// </summary>
    private async Task RunAdditionalClaudeLimitsRefresh(bool allowKeychainPrompt)
    {
        var discover = _discoverClaudeConfigDirs;
        var readIdentity = _readDefaultIdentity;
        var setting = _additionalClaudeConfigDirs;
        var sessionKeys = _accountSessionKeys;
        var found = await Task.Run(() =>
        {
            var detected = discover();
            var roots = ClaudeAccountRoots.Merged(detected, setting);
            var saved = roots.ToDictionary(r => r, ClaudeAccountRoots.SavedIdentity, StringComparer.OrdinalIgnoreCase);
            var listed = ClaudeAccountRoots.Roots(setting).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keyed = roots.Where(r => sessionKeys(r).Credential() != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (Detected: detected, Roots: roots, Listed: listed, Keyed: keyed, Saved: saved, DefaultIdentity: readIdentity());
        });
        DetectedClaudeConfigDirs = found.Detected.ToList();
        DefaultSavedIdentity = found.DefaultIdentity;
        AdditionalClaudeConfigRoots = found.Roots;
        _accountSessionKeyPaths = found.Keyed;
        var roots = found.Roots;
        var paths = roots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _additionalProviders = _additionalProviders.Where(p => paths.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        _additionalBackoff = _additionalBackoff.Where(p => paths.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (roots.Count == 0)
        {
            ClearAdditionalClaudeAccounts();
            return;
        }

        // Team and personal plans can share an email, so the organization is part of the account.
        static string? AccountKey(LimitStatus status) =>
            status.AccountEmail is { } email ? $"{email}\n{status.AccountOrganizationName ?? ""}" : null;
        var defaultStatus = (Limits ?? new LimitStatus()) with { };
        defaultStatus.FillIdentity(found.DefaultIdentity);
        var owners = new Dictionary<string, string>();
        if (AccountKey(defaultStatus) is { } defaultKey) owners[defaultKey] = ClaudeAccountLimits.DefaultID;
        // Folder id → id of the tab already showing that account.
        var aliases = new Dictionary<string, string>();

        var refreshed = new List<AdditionalClaudeLimits>();
        var pending = false;
        foreach (var root in roots)
        {
            var id = ClaudeAccountRoots.PathKey(root);
            var previous = _additionalLimits.FirstOrDefault(a => string.Equals(a.RootPath, root, StringComparison.OrdinalIgnoreCase));
            var mayPrompt = !SessionKeyConfigured || found.Listed.Contains(root);

            void Keep(AdditionalClaudeLimits account)
            {
                if (AccountKey(account.Status) is { } key)
                {
                    if (owners.TryGetValue(key, out var owner))
                    {
                        aliases[id] = owner;
                        return;
                    }
                    owners[key] = id;
                }
                refreshed.Add(account);
            }

            if (!allowKeychainPrompt && _additionalBackoff.TryGetValue(root, out var backoff) && _now() < backoff.Until)
            {
                if (previous != null) Keep(previous);
                continue;
            }
            try
            {
                var status = await AdditionalProvider(root).Fetch(allowKeychainPrompt && mayPrompt);
                _additionalBackoff.Remove(root);
                AppLog.Write($"additional claude limits refreshed ({Path.GetFileName(root)}) fiveHour={status.FiveHour?.Utilization} sevenDay={status.SevenDay?.Utilization}");
                status = status with { };
                status.FillIdentity(found.Saved.GetValueOrDefault(root));
                Keep(new AdditionalClaudeLimits(root, status, updatedAt: _now()));
            }
            catch (Exception error)
            {
                if (error is LimitsError { Kind: LimitsErrorKind.RateLimited } rateLimited)
                {
                    var interval = NextLimitsBackoff(_additionalBackoff.TryGetValue(root, out var b) ? b.Interval : TimeSpan.Zero);
                    _additionalBackoff[root] = (_now() + (rateLimited.RetryAfter ?? interval), interval);
                }
                AppLog.Write($"additional claude limits unavailable ({Path.GetFileName(root)}): {error.Message}");
                // The chain rethrows the folder's session key error when it has one.
                var keyRejected = error is LimitsError { Kind: LimitsErrorKind.SessionKeyInvalid };
                if (IsAuthRejection(error) || keyRejected || previous?.IsExpired == true)
                {
                    // Keep the tab, with the last values when there are some, so the account stays visible.
                    var status = (previous?.Status ?? new LimitStatus()) with { };
                    status.FillIdentity(found.Saved.GetValueOrDefault(root));
                    // Another failure keeps the known cause; a token rejection replaces it.
                    var sessionKeyExpired = keyRejected || (!IsAuthRejection(error) && previous?.SessionKeyExpired == true);
                    Keep(new AdditionalClaudeLimits(root, status, isExpired: true, sessionKeyExpired: sessionKeyExpired,
                        updatedAt: previous?.UpdatedAt));
                }
                else
                {
                    // Only a refresh allowed for this folder can fix it.
                    if (mayPrompt) pending = true;
                    // Keep the last known value, as the primary path does, instead of dropping the account.
                    if (previous != null) Keep(previous);
                }
            }
        }
        _additionalLimits = refreshed;
        AdditionalLimitsPending = pending;
        ArmCandyWindows();

        // A folder hidden as a duplicate adds its prompts to the tab showing its account.
        var folders = new List<(string Id, string Folder)> { (ClaudeAccountLimits.DefaultID, ClaudeAccountRoots.DefaultConfigDir()) };
        foreach (var root in roots)
        {
            var id = ClaudeAccountRoots.PathKey(root);
            folders.Add((aliases.GetValueOrDefault(id, id), root));
        }
        var lastPrompt = _readLastPrompt;
        _claudeAccountActivity = await Task.Run(() =>
        {
            var dates = new Dictionary<string, DateTimeOffset>();
            foreach (var (id, folder) in folders)
            {
                if (lastPrompt(folder) is not { } date) continue;
                dates[id] = dates.TryGetValue(id, out var existing) && existing > date ? existing : date;
            }
            return dates;
        });
        await RefreshClaudeAccountUsage(folders);
        NotifyChanged();
    }

    private void ClearAdditionalClaudeAccounts()
    {
        _additionalLimits = [];
        AdditionalLimitsPending = false;
        _claudeAccountActivity = new();
        _claudeAccountUsage = new();
        _claudeAccountBlocks = new();
        UnattributedClaudeUsage = new ClaudeAccountUsage();
        NotifyChanged();
    }

    /// <summary>Reuses the usage cache (no re-parse) and each login's prompt history, off the UI thread.
    /// Folders sharing an id count as one account.</summary>
    private async Task RefreshClaudeAccountUsage(List<(string Id, string Folder)> folders)
    {
        var now = _now();
        var todayKey = LocalUsageReader.LocalDay(now);
        var monthStartKey = LocalUsageReader.LocalDay(LocalUsageReader.StartOfMonth(now));
        var entries = await _claudeUsageEntries(ClaudeUsageScanStart(now));
        var readHistory = _readPromptHistory;
        // Each tab's 5h block follows its account's official window. Expired values are too old for that.
        var windows = new Dictionary<string, ClaudeAccountUsageAttribution.FiveHourWindow>();
        foreach (var account in ClaudeAccounts.Where(a => !a.IsExpired))
        {
            if (account.Status.FiveHour is not { } window) continue;
            if (window.ResetDate is { } reset) windows[account.Id] = new ClaudeAccountUsageAttribution.FiveHourWindow.Running(reset);
            else if (window.HasNotStarted) windows[account.Id] = new ClaudeAccountUsageAttribution.FiveHourWindow.NotStarted();
        }
        var result = await Task.Run(() =>
        {
            var ids = new List<string>();
            var prompts = new Dictionary<string, Dictionary<string, List<DateTimeOffset>>>();
            foreach (var (id, folder) in folders)
            {
                if (!prompts.TryGetValue(id, out var merged))
                {
                    ids.Add(id);
                    prompts[id] = merged = new Dictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);
                }
                foreach (var (session, dates) in readHistory(folder))
                    merged[session] = merged.TryGetValue(session, out var existing) ? existing.Concat(dates).Order().ToList() : dates;
            }
            var accounts = ids.Select(id => new ClaudeAccountUsageAttribution.Account(id, prompts[id])).ToList();
            return ClaudeAccountUsageAttribution.Usage(entries, accounts, now, todayKey, monthStartKey, windows);
        });
        // An account without a tab (no login, limits not loaded yet) is not shown: its usage is unattributed.
        var shown = ClaudeAccounts.Select(a => a.Id).ToHashSet();
        var unattributed = result.Unattributed;
        foreach (var (id, usage) in result.ByAccount)
            if (!shown.Contains(id)) unattributed.Add(usage);
        _claudeAccountUsage = result.ByAccount.Where(p => shown.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        _claudeAccountBlocks = result.ActiveBlocks.Where(p => shown.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        UnattributedClaudeUsage = unattributed;
    }

    /// <summary>The month, and the whole 5h block when it started last month.</summary>
    public static DateTimeOffset ClaudeUsageScanStart(DateTimeOffset now)
    {
        var monthStart = LocalUsageReader.StartOfMonth(now);
        var blockStart = now - LocalUsageReader.BlockWindow;
        return monthStart < blockStart ? monthStart : blockStart;
    }

    /// <summary>Local usage for the installed app only (tests never read the developer's logs).</summary>
    public static Task<IReadOnlyList<UsageEntry>> InstalledClaudeUsageEntries(DateTimeOffset since, bool? isInstalledApp = null)
    {
        if (!(isInstalledApp ?? AppEnv.IsInstalledApp)) return Task.FromResult<IReadOnlyList<UsageEntry>>([]);
        return Task.Run<IReadOnlyList<UsageEntry>>(() => LocalUsageCache.Shared.ClaudeEntries(since));
    }

    private IClaudeLimitsProviding AdditionalProvider(string root)
    {
        if (_additionalProviders.TryGetValue(root, out var provider)) return provider;
        provider = _additionalClaudeLimitsProvider(root);
        _additionalProviders[root] = provider;
        return provider;
    }

    /// <summary>The token was rejected (401/403) or is already known to be expired (Windows credentials file).</summary>
    public static bool IsAuthRejection(Exception error) => LimitsError.IsAuthRejection(error);

    // MARK: Additional account session keys

    /// <summary>Additional folders at the last refresh (detected and listed), each with its own session key row.</summary>
    public IReadOnlyList<string> AdditionalClaudeConfigRoots { get; private set; } = [];
    private HashSet<string> _accountSessionKeyPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders with a saved session key of their own, read from disk at each refresh.</summary>
    public IReadOnlySet<string> AccountSessionKeyPaths => _accountSessionKeyPaths;
    private readonly Dictionary<string, Exception> _accountSessionKeyFailures = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The folder whose pasted key is being checked.</summary>
    public string? ValidatingAccountSessionKeyPath { get; private set; }

    public string? AccountSessionKeyError(string rootPath) =>
        _accountSessionKeyFailures.TryGetValue(rootPath, out var error) ? FriendlyLimitError(error, new L(LocalizationLanguage)) : null;

    /// <summary>Same check as the default key (listing the organizations proves the key works), then a refresh.
    /// Picks the organization the folder is logged in to when the key sees several.</summary>
    public async Task SaveAccountSessionKey(string raw, string rootPath)
    {
        if (ValidatingAccountSessionKeyPath != null) return;
        ValidatingAccountSessionKeyPath = rootPath;
        _accountSessionKeyFailures.Remove(rootPath);
        NotifyChanged();
        try
        {
            var keys = _accountSessionKeys(rootPath);
            var key = SessionKeyStore.Normalize(raw);
            var organizations = await keys.Organizations(key);
            var picked = AccountOrganization(organizations, ClaudeAccountRoots.SavedOrganizationID(rootPath))
                ?? throw LimitsError.Of(LimitsErrorKind.SessionKeyNoOrganization);
            keys.Save(key, picked.Id);
            _accountSessionKeyPaths.Add(rootPath);
            AppLog.Write($"account session key saved ({Path.GetFileName(rootPath)}, orgs={organizations.Count})");
            await Refresh();
        }
        catch (Exception error)
        {
            _accountSessionKeyFailures[rootPath] = error;
            AppLog.Write($"account session key save failed ({Path.GetFileName(rootPath)}): {error.Message}");
        }
        finally
        {
            ValidatingAccountSessionKeyPath = null;
            NotifyChanged();
        }
    }

    public void ClearAccountSessionKey(string rootPath)
    {
        _accountSessionKeys(rootPath).Clear();
        _accountSessionKeyPaths.Remove(rootPath);
        _accountSessionKeyFailures.Remove(rootPath);
        // An expiry that was the key's goes with it: the folder's token decides again at the next refresh.
        if (_additionalLimits.FirstOrDefault(a => string.Equals(a.RootPath, rootPath, StringComparison.OrdinalIgnoreCase))
            is { SessionKeyExpired: true } account)
        {
            account.IsExpired = false;
            account.SessionKeyExpired = false;
        }
        AppLog.Write($"account session key cleared ({Path.GetFileName(rootPath)})");
        NotifyChanged();
        Spawn(() => Refresh());
    }

    /// <summary>The folder's own organization first, then the default key's rule (one with usage data).</summary>
    public static SessionKeyOrganization? AccountOrganization(IReadOnlyList<SessionKeyOrganization> organizations, string? loggedIn) =>
        SessionKeyOrganization.AccountOrganization(organizations, loggedIn);

    // MARK: claude.ai session key

    /// <summary>A key is stored. Only the fact is exposed, never the value.</summary>
    public bool SessionKeyConfigured { get; private set; }
    /// <summary>The stored key was rejected as expired (the badge must not keep saying "Saved").</summary>
    public bool SessionKeyExpired => SessionKeyConfigured && LimitsAuthExpiry == Core.LimitsAuthExpiry.SessionKey;
    /// <summary>Candidates from the last check — Settings shows a picker only with two or more.</summary>
    public IReadOnlyList<SessionKeyOrganization> SessionKeyOrganizations { get; private set; } = [];
    public string? SessionKeySelectedOrgID { get; private set; }
    private Exception? _sessionKeyFailure;
    public string? SessionKeyError =>
        _sessionKeyFailure == null ? null : FriendlyLimitError(_sessionKeyFailure, new L(LocalizationLanguage));
    public bool IsValidatingSessionKey { get; private set; }

    /// <summary>Validates (organization list) and saves a pasted key, then refreshes so the limits show at once.</summary>
    public async Task SaveSessionKey(string raw)
    {
        if (IsValidatingSessionKey) return;
        IsValidatingSessionKey = true;
        _sessionKeyFailure = null;
        NotifyChanged();
        try
        {
            var key = SessionKeyStore.Normalize(raw);
            var organizations = await _sessionKeys.Organizations(key);
            var picked = organizations.FirstOrDefault(o => o.HasUsageData) ?? organizations.FirstOrDefault()
                ?? throw LimitsError.Of(LimitsErrorKind.SessionKeyNoOrganization);
            _sessionKeys.Save(key, picked.Id);
            SessionKeyOrganizations = organizations;
            SessionKeySelectedOrgID = picked.Id;
            SessionKeyConfigured = true;
            AppLog.Write($"session key saved (orgs={organizations.Count} picked={picked.Id})");
            await Refresh();
        }
        catch (Exception error)
        {
            _sessionKeyFailure = error;
            AppLog.Write($"session key save failed: {error.Message}");
        }
        finally
        {
            IsValidatingSessionKey = false;
            NotifyChanged();
        }
    }

    /// <summary>Refills the organization candidates from the stored key (they live in memory only), when Settings opens.</summary>
    public async Task RefreshSessionOrganizations()
    {
        if (!SessionKeyConfigured || SessionKeyOrganizations.Count > 0 || IsValidatingSessionKey
            || _sessionKeys.Credential() is not { } credential) return;
        IsValidatingSessionKey = true;
        try
        {
            SessionKeyOrganizations = await _sessionKeys.Organizations(credential.Key);
            SessionKeySelectedOrgID = credential.OrganizationID;
        }
        catch (Exception error)
        {
            // Not reported: the user did not ask for it, and the limits refresh on their own.
            AppLog.Write($"session key org list refresh failed: {error.Message}");
        }
        finally
        {
            IsValidatingSessionKey = false;
            NotifyChanged();
        }
    }

    public void ClearSessionKey()
    {
        _sessionKeys.Clear();
        SessionKeyConfigured = false;
        SessionKeyOrganizations = [];
        SessionKeySelectedOrgID = null;
        _sessionKeyFailure = null;
        AppLog.Write("session key cleared");
        NotifyChanged();
        Spawn(() => Refresh());   // back to the OAuth path (or the limits section hides)
    }

    /// <summary>Manual organization switch, when the automatic pick chose the wrong one.</summary>
    public async Task SelectSessionOrganization(string id)
    {
        if (_sessionKeys.Credential() is not { } credential || credential.OrganizationID == id) return;
        try
        {
            _sessionKeys.Save(credential.Key, id);
            SessionKeySelectedOrgID = id;
            AppLog.Write($"session key org switched to {id}");
            NotifyChanged();
            await Refresh();
        }
        catch (Exception error)
        {
            _sessionKeyFailure = error;
            NotifyChanged();
        }
    }

    /// <summary>Sets the expiry source on a rejection; other errors (network, file locks) are not expiry.
    /// The chain rethrows a configured session key's error, so the source survives to here.</summary>
    private void UpdateAuthExpired(Exception error)
    {
        if (error is LimitsError { Kind: LimitsErrorKind.SessionKeyInvalid })
        {
            LimitsAuthExpiry = Core.LimitsAuthExpiry.SessionKey;
            return;
        }
        if (IsAuthRejection(error)) LimitsAuthExpiry = Core.LimitsAuthExpiry.OAuth;
    }

    // MARK: Claude limits 429 backoff

    private DateTimeOffset? _claudeLimitsBackoffUntil;
    private TimeSpan _claudeLimitsBackoffInterval = TimeSpan.Zero;

    /// <summary>429 → exponential backoff 5 → 10 → … → 60 minutes; Retry-After wins when given.</summary>
    private void ApplyLimitsBackoffIfRateLimited(Exception error)
    {
        if (error is not LimitsError { Kind: LimitsErrorKind.RateLimited } limitsError) return;
        _claudeLimitsBackoffInterval = NextLimitsBackoff(_claudeLimitsBackoffInterval);
        var delay = limitsError.RetryAfter ?? _claudeLimitsBackoffInterval;
        _claudeLimitsBackoffUntil = _now() + delay;
        AppLog.Write($"claude limits rate-limited: backing off {(int)delay.TotalSeconds}s");
    }

    private void ResetLimitsBackoff()
    {
        _claudeLimitsBackoffUntil = null;
        _claudeLimitsBackoffInterval = TimeSpan.Zero;
    }

    public static TimeSpan NextLimitsBackoff(TimeSpan current) =>
        current == TimeSpan.Zero ? TimeSpan.FromSeconds(300) : TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * 2, 3600));

    /// <summary>A limits failure as user guidance. Windows credential errors map where macOS maps the Keychain ones.</summary>
    public static string FriendlyLimitError(Exception error, L l)
    {
        if (error is not LimitsError limitsError) return l.LimitRefreshGeneric;
        return limitsError.Kind switch
        {
            LimitsErrorKind.RateLimited => l.LimitRefreshRateLimited,
            LimitsErrorKind.HttpStatus => l.LimitRefreshHTTPError(limitsError.Status ?? 0),
            // macOS: keychainUnavailable / credentialFormat.
            LimitsErrorKind.NoCredential or LimitsErrorKind.CredentialFormat => l.LimitRefreshNoCredential,
            // Windows only: the file's token expired. Only Claude Code renews it, on its next run.
            LimitsErrorKind.CredentialExpired => l.ClaudeAuthExpiredHint,
            LimitsErrorKind.CredentialMissingAccountOAuth => l.LimitRefreshReauthNeeded,
            // liveFetchNotPermitted never reaches the installed app, so it gets the generic text.
            LimitsErrorKind.LiveFetchNotPermitted => l.LimitRefreshGeneric,
            LimitsErrorKind.SessionKeyMissing => l.LimitRefreshNoCredential,
            LimitsErrorKind.SessionKeyMalformed => l.SessionKeyMalformedError,
            LimitsErrorKind.SessionKeyInvalid => l.SessionKeyExpiredError,
            LimitsErrorKind.SessionKeyNoOrganization => l.SessionKeyNoOrgError,
            _ => l.LimitRefreshGeneric,
        };
    }

    /// <summary>Status-page lookup — display only, rides on the refresh loop. A provider whose lookup failed keeps
    /// its previous value; turned off, the stored statuses are cleared.</summary>
    private async Task RefreshProviderStatuses()
    {
        if (!StatusChecksEnabled)
        {
            _statuses.Clear();
            return;
        }
        var fresh = await _statusProvider.Fetch();
        foreach (var (id, status) in fresh) _statuses[id] = status;
        if (fresh.Count > 0)
            AppLog.WriteIfChanged("provider-status", "provider status: "
                + string.Join(" ", fresh.Select(p => $"{p.Key}={p.Value.Indicator}").Order(StringComparer.Ordinal)));
    }

    /// <summary>Null while the lookup is off (takes effect before the next refresh clears the stored values).
    /// Operational is returned too: callers gate on <c>HasIssue</c>.</summary>
    public ProviderStatus? ProviderStatus(string providerID) =>
        StatusChecksEnabled ? _statuses.GetValueOrDefault(providerID) : null;

    /// <summary>Claude limits older than 15 minutes (polling pauses during a 429 backoff of up to 60 minutes).</summary>
    public bool ClaudeLimitsStale =>
        Limits != null && LimitsUpdatedAt is { } updated && _now() - updated > TimeSpan.FromMinutes(15);

    // MARK: Limit alerts

    /// <summary>
    /// Edge-triggered alert decision: fires only when a window newly crosses a threshold. One alert at the warning
    /// line and one at the critical line per episode; dropping below the warning line re-arms. Windows are told
    /// apart by <c>Key</c>, never by display name (#61).
    /// </summary>
    public static List<LimitAlert> EvaluateLimitAlerts(
        IEnumerable<(string Key, string Name, double Utilization)> windows,
        IReadOnlyDictionary<string, string>? accounts,
        double warn, double crit, Dictionary<string, int> tiers)
    {
        var alerts = new List<LimitAlert>();
        foreach (var (key, name, utilization) in windows)
        {
            var tier = utilization >= crit ? 2 : utilization >= warn ? 1 : 0;
            // Removed rather than stored as 0, so the map only holds rising windows and stays finite. No cap: a
            // cap would clear a window still over its threshold and make it alert again.
            if (tier == 0)
            {
                tiers.Remove(key);
                continue;
            }
            if (tier <= tiers.GetValueOrDefault(key)) continue;
            tiers[key] = tier;
            alerts.Add(new LimitAlert(key, name, tier == 2, utilization, accounts?.GetValueOrDefault(key)));
        }
        return alerts;
    }

    /// <summary>The single bubble for a refresh: critical over warning, then the highest utilization.</summary>
    public static LimitAlert? BubbleAlert(IReadOnlyList<LimitAlert> alerts)
    {
        LimitAlert? best = null;
        foreach (var alert in alerts)
        {
            if (best == null
                || (alert.IsCritical && !best.IsCritical)
                || (alert.IsCritical == best.IsCritical && alert.Utilization > best.Utilization)) best = alert;
        }
        return best;
    }

    /// <summary>Whether a bubble shown at <paramref name="shownAt"/> should clear by <paramref name="now"/>.</summary>
    public static bool ShouldDismissBubble(DateTimeOffset shownAt, DateTimeOffset now, TimeSpan? ttl = null) =>
        now - shownAt >= (ttl ?? BubbleTTL);

    /// <summary>Evaluate once, advance tiers once, then fan out to toasts and the pet bubble under separate gates.</summary>
    private void CheckLimitAlerts()
    {
        var (windows, accounts) = BuildLimitWindows();
        var alerts = EvaluateLimitAlerts(windows, accounts, WarnThreshold, CritThreshold, _notifiedTier);
        if (alerts.Count == 0) return;
        if (LimitNotifications) PostLimitNotifications(alerts);
        if (FloatingPetEnabled && FloatingPetBubbleAlerts) ShowBubble(BubbleAlert(alerts));
    }

    /// <summary>(unique key, display name, utilization) for every window the popover shows as a limit row, for
    /// every Claude account (the default account keeps its historical keys). <c>Accounts</c>: next to other
    /// accounts, the title of the account behind each window key.</summary>
    public (List<(string Key, string Name, double Utilization)> Windows, Dictionary<string, string> Accounts) BuildLimitWindows()
    {
        var l = new L(LocalizationLanguage);
        var windows = new List<(string Key, string Name, double Utilization)>();
        var titles = new Dictionary<string, string>();
        var accounts = ClaudeAccounts;
        foreach (var account in accounts)
        {
            var limits = account.Status;
            var prefix = account.WindowKeyPrefix;
            var first = windows.Count;
            if (limits.FiveHour?.Utilization is { } u5) windows.Add(($"{prefix}.fiveHour", l.ClaudeFiveHour, u5));
            if (limits.SevenDay?.Utilization is { } u7) windows.Add(($"{prefix}.sevenDay", l.ClaudeWeekly, u7));
            if (limits.SevenDayOpus?.Utilization is { } uo) windows.Add(($"{prefix}.sevenDayOpus", $"Claude {l.WeeklyOpus}", uo));
            if (limits.SevenDaySonnet?.Utilization is { } us) windows.Add(($"{prefix}.sevenDaySonnet", $"Claude {l.WeeklySonnet}", us));
            // Per-model weekly (weekly_scoped) and others. The index keeps identical kind/model pairs apart.
            var scoped = limits.ScopedLimitEntries;
            for (var i = 0; i < scoped.Count; i++)
            {
                var entry = scoped[i];
                if (entry.Percent is not { } u) continue;
                var model = entry.Scope?.Model?.DisplayName;
                windows.Add(($"{prefix}.scoped.{entry.Kind ?? "?"}.{model ?? "?"}.{i}", $"Claude {l.ClaudeLimitEntry(entry.Kind, model)}", u));
            }
            if (accounts.Count > 1)
                for (var i = first; i < windows.Count; i++) titles[windows[i].Key] = account.Title;
        }
        return (windows, titles);
    }

    private void PostLimitNotifications(List<LimitAlert> alerts)
    {
        var l = new L(LocalizationLanguage);
        foreach (var alert in alerts)
            LimitNotificationPosted?.Invoke(new LimitNotification(
                $"{alert.Key}-{(alert.IsCritical ? "critical" : "warning")}",
                alert.IsCritical ? l.NotifCritical : l.NotifWarning,
                alert.Body(l),
                alert.IsCritical));
    }

    private void ShowBubble(LimitAlert? alert)
    {
        if (alert == null) return;
        var shownAt = _now();
        CurrentBubbleAlert = alert;
        _currentBubbleDate = shownAt;
        _bubbleDismiss?.Dispose();
        _bubbleDismiss = _scheduler.After(BubbleTTL, () =>
        {
            // The timer is the TTL; a newer bubble owns its own timer.
            if (_currentBubbleDate != shownAt) return;
            CurrentBubbleAlert = null;
            NotifyChanged();
        });
        NotifyChanged();
    }

    // MARK: Helpers

    private void SetBool(ref bool field, bool value, string key)
    {
        field = value;
        _settings.Set(key, value);
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    /// <summary>Swift <c>Task { … }</c>: runs after the current turn, on the current synchronization context.</summary>
    private static void Spawn(Func<Task> work) => _ = Deferred(work);

    private static async Task Deferred(Func<Task> work)
    {
        await Task.Yield();
        await work();
    }
}
