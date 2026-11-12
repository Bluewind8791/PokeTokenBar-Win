using System.Text.Json.Serialization;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>LimitWindowSpan</c> (Models.swift). Drawing a pace line needs the window length, not only
/// its reset time; Claude tells it through the <c>limits[]</c> kind or the legacy field name.
/// </summary>
public static class LimitWindowSpan
{
    public static readonly TimeSpan FiveHour = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDay = TimeSpan.FromDays(7);

    /// <summary>Window length in minutes. Zero or less cannot be a length.</summary>
    public static TimeSpan? FromMinutes(int? minutes) =>
        minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null;

    /// <summary>oauth <c>limits[]</c> kind. weekly_scoped (per-model weekly) has the weekly length too.</summary>
    public static TimeSpan? FromKind(string? kind) => kind switch
    {
        "session" => FiveHour,
        "weekly_all" or "weekly_scoped" => SevenDay,
        _ => null,
    };
}

/// <summary>Port of <c>LimitWindow</c> (api.anthropic.com/api/oauth/usage).</summary>
public sealed record LimitWindow
{
    [JsonPropertyName("utilization")] public double? Utilization { get; set; }
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }

    [JsonIgnore] public DateTimeOffset? ResetDate => ResetsAt == null ? null : Iso8601Parser.Date(ResetsAt);

    /// <summary>No running window: the API sends 0% without a reset date until the account's next message.</summary>
    [JsonIgnore] public bool HasNotStarted => Utilization == 0 && ResetDate == null;
}

/// <summary>
/// Port of <c>LimitStatus</c>. A record so a copy can be changed with <c>with</c>; the plan and account
/// fields are not part of the usage response and are filled by the provider after decoding.
/// </summary>
public sealed record LimitStatus
{
    [JsonPropertyName("five_hour")] public LimitWindow? FiveHour { get; set; }
    [JsonPropertyName("seven_day")] public LimitWindow? SevenDay { get; set; }
    [JsonPropertyName("seven_day_opus")] public LimitWindow? SevenDayOpus { get; set; }
    [JsonPropertyName("seven_day_sonnet")] public LimitWindow? SevenDaySonnet { get; set; }
    [JsonPropertyName("limits")] public List<OAuthLimitEntry>? Limits { get; set; }

    /// <summary>Plan from the OAuth credential (not the usage response), for the plan label.</summary>
    [JsonIgnore] public string? SubscriptionType { get; set; }
    [JsonIgnore] public string? RateLimitTier { get; set; }
    /// <summary>The token's real owner, from the profile endpoint. Tells two logins sharing one
    /// credential slot apart.</summary>
    [JsonIgnore] public string? AccountEmail { get; set; }
    [JsonIgnore] public string? AccountOrganizationName { get; set; }
    /// <summary>The current plan name the profile endpoint reports ("Ultra", …), injected by the provider.
    /// The server names the plan; the app shows it as is, so a rename needs no code change (#463).</summary>
    [JsonIgnore] public string? PlanDisplayName { get; set; }

    public static LimitStatus Decode(ReadOnlySpan<byte> json) =>
        System.Text.Json.JsonSerializer.Deserialize<LimitStatus>(json)
        ?? throw new System.Text.Json.JsonException("null limit status");

    /// <summary>
    /// The plan label. The profile endpoint's name (<c>plan_display_name</c>) wins; without it the label is built
    /// from subscriptionType plus the tier multiplier. Claude renamed the Max tiers (Max 5x → Max, Max 20x →
    /// Ultra) while credentials still carry <c>default_claude_max_5x/20x</c>, so the rename is applied here.
    /// e.g. ("max", "default_claude_max_20x") → "Ultra". Other plans keep "Name Nx" whenever the tier carries a
    /// multiplier, and an unknown Max multiplier stays "Max 10x" so a new tier never hides its number.
    /// </summary>
    [JsonIgnore]
    public string? PlanDisplay
    {
        get
        {
            if (PlanDisplayName?.Trim() is { Length: > 0 } named) return named;
            if (string.IsNullOrEmpty(SubscriptionType)) return null;
            var name = char.ToUpperInvariant(SubscriptionType[0]) + SubscriptionType[1..];
            if (RateLimitTier == null || TierMultiplier(RateLimitTier) is not { } multiplier) return name;
            if (!SubscriptionType.Equals("max", StringComparison.OrdinalIgnoreCase)) return $"{name} {multiplier}";
            return multiplier switch
            {
                "5x" => "Max",
                "20x" => "Ultra",
                _ => $"Max {multiplier}",
            };
        }
    }

    /// <summary>"email · organization". A personal plan's generated organization name repeats the email
    /// and is dropped. No email, no label: an organization alone does not identify the login.</summary>
    [JsonIgnore]
    public string? AccountDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(AccountEmail)) return null;
            var org = AccountOrganizationName;
            return string.IsNullOrEmpty(org) || org.Contains(AccountEmail, StringComparison.Ordinal)
                ? AccountEmail
                : $"{AccountEmail} · {org}";
        }
    }

    private static string? TierMultiplier(string tier)
    {
        foreach (var part in tier.Split('_'))
        {
            if (!part.EndsWith('x')) continue;
            var digits = part[..^1];
            if (digits.Length > 0 && digits.All(char.IsAsciiDigit)) return part;
        }
        return null;
    }

    /// <summary>
    /// The window a compact surface (menu bar line, tray tooltip) shows: the 5-hour session when present,
    /// else the weekly window, with the span for the pace marker. Plans without a session limit (Ultra)
    /// send <c>five_hour: null</c>; reading the 5-hour window alone dropped Claude from the line (#464).
    /// </summary>
    [JsonIgnore]
    public (LimitWindow Window, TimeSpan Span)? HeadlineWindow =>
        FiveHour is { Utilization: not null } fiveHour ? (fiveHour, LimitWindowSpan.FiveHour)
        : SevenDay is { Utilization: not null } sevenDay ? (sevenDay, LimitWindowSpan.SevenDay)
        : null;

    /// <summary>
    /// Windows the legacy fields cannot carry: session (=five_hour) and weekly_all (=seven_day) already
    /// have rows. When the legacy fields are both missing (new-shape response only), every entry shows.
    /// </summary>
    [JsonIgnore]
    public List<OAuthLimitEntry> ScopedLimitEntries
    {
        get
        {
            var entries = Limits ?? [];
            if (FiveHour == null && SevenDay == null) return entries;
            return entries.Where(e => e.Kind != "session" && e.Kind != "weekly_all").ToList();
        }
    }

    /// <summary>Every official window this status carries: legacy fields, then the scoped entries.</summary>
    [JsonIgnore]
    public List<double> AllUtilizations =>
        new[] { FiveHour?.Utilization, SevenDay?.Utilization, SevenDayOpus?.Utilization, SevenDaySonnet?.Utilization }
            .Concat(ScopedLimitEntries.Select(e => e.Percent))
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();

    /// <summary>Names the account from its saved login when the profile endpoint gave nothing.</summary>
    public void FillIdentity(AccountIdentity? saved)
    {
        if (AccountEmail != null || saved == null) return;
        AccountEmail = saved.Email;
        AccountOrganizationName = saved.OrganizationName;
    }
}

/// <summary>
/// Port of <c>OAuthLimitEntry</c>: the newer <c>limits[]</c> list generalizing five_hour/seven_day.
/// Per-model weekly limits only come here (kind=weekly_scoped + scope.model.display_name).
/// </summary>
public sealed record OAuthLimitEntry
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("group")] public string? Group { get; set; }
    [JsonPropertyName("percent")] public double? Percent { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
    [JsonPropertyName("scope")] public LimitScope? Scope { get; set; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; set; }

    public sealed record LimitScope
    {
        [JsonPropertyName("model")] public LimitScopeModel? Model { get; set; }
    }

    public sealed record LimitScopeModel
    {
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }

    [JsonIgnore] public DateTimeOffset? ResetDate => ResetsAt == null ? null : Iso8601Parser.Date(ResetsAt);
    [JsonIgnore] public TimeSpan? WindowSpan => LimitWindowSpan.FromKind(Kind);
}

public enum LimitsErrorKind
{
    /// <summary>A live limits call outside the installed app (tests, a raw build). See <see cref="LiveFetchGate"/>.</summary>
    LiveFetchNotPermitted,
    /// <summary>Windows: no Claude Code credential was found in the credentials file (missing file,
    /// or no usable token and nothing cached). Takes the place of the macOS Keychain errors.</summary>
    NoCredential,
    /// <summary>Windows: the credentials file holds an expired token. Only Claude Code renews it (it
    /// refreshes on its next run), so this reads like a rejected token (<see cref="LimitsError.IsAuthRejection"/>).</summary>
    CredentialExpired,
    CredentialFormat,
    /// <summary>The credential was read but has no Claude account OAuth (<c>claudeAiOauth</c>), only MCP
    /// server state. Seen with Claude Code 2.1.x: a re-login is needed, not a format fix.</summary>
    CredentialMissingAccountOAuth,
    HttpStatus,
    /// <summary>429, with the server's Retry-After (seconds) when given. Drives the poll backoff.</summary>
    RateLimited,
    /// <summary>No session key stored: the normal state for users without one; the chain quietly falls back.</summary>
    SessionKeyMissing,
    /// <summary>The pasted value is not a session key (Settings validation).</summary>
    SessionKeyMalformed,
    /// <summary>The session key expired or is invalid: copy it again from the browser. claude.ai rejects
    /// with 403 as well as 401 (a 403 on the non-org-scoped <c>/api/organizations</c> is that signal).</summary>
    SessionKeyInvalid,
    /// <summary>The key is valid but no organization can show limits (all 403).</summary>
    SessionKeyNoOrganization,
}

/// <summary>
/// Port of the Swift <c>LimitsError</c> enum as an exception. Equality is by kind, HTTP status and
/// Retry-After, like the Swift <c>Equatable</c> enum, so tests can compare errors directly.
/// </summary>
public sealed class LimitsError : Exception, IEquatable<LimitsError>
{
    public LimitsErrorKind Kind { get; }
    public int? Status { get; }
    public TimeSpan? RetryAfter { get; }

    private LimitsError(LimitsErrorKind kind, int? status = null, TimeSpan? retryAfter = null)
        : base(status is { } s ? $"{kind}({s})" : retryAfter is { } r ? $"{kind}({r.TotalSeconds}s)" : kind.ToString())
    {
        Kind = kind;
        Status = status;
        RetryAfter = retryAfter;
    }

    public static LimitsError Of(LimitsErrorKind kind) => new(kind);
    public static LimitsError Http(int status) => new(LimitsErrorKind.HttpStatus, status: status);
    public static LimitsError RateLimited(TimeSpan? retryAfter) => new(LimitsErrorKind.RateLimited, retryAfter: retryAfter);

    /// <summary>
    /// The token was rejected (401/403), or on Windows it is already known to be expired. Only Claude
    /// Code running on that login renews it, so a retry alone cannot help.
    /// </summary>
    public static bool IsAuthRejection(Exception error) => error is LimitsError e
        && ((e.Kind == LimitsErrorKind.HttpStatus && e.Status is 401 or 403) || e.Kind == LimitsErrorKind.CredentialExpired);

    public bool Equals(LimitsError? other) =>
        other != null && Kind == other.Kind && Status == other.Status && RetryAfter == other.RetryAfter;

    public override bool Equals(object? obj) => Equals(obj as LimitsError);
    public override int GetHashCode() => HashCode.Combine(Kind, Status, RetryAfter);
}

/// <summary>Port of <c>ClaudeLimitsProviding</c>: the real provider or a test stub.</summary>
public interface IClaudeLimitsProviding
{
    /// <param name="allowKeychainPrompt">
    /// True for a user-initiated refresh. macOS reads the Keychain only then; Windows has no Keychain,
    /// so the flag only makes the OAuth path skip its in-memory token (#300). Kept for the call shape.
    /// </param>
    Task<LimitStatus> Fetch(bool allowKeychainPrompt);
}

/// <summary>Port of the pace helpers of <c>UsageStore</c> / <c>LimitProgressBar</c>.</summary>
public static class LimitPace
{
    public enum LimitDisplayMode { Used, Remaining }

    /// <summary>
    /// How far the window has run (0…1), from its reset time and length. Outside 0…1 gives null rather
    /// than a clamp: a 5h window starts at the first request, so an idle account keeps a stale
    /// resets_at, and pinning it to an end would claim the window just started or ended.
    /// </summary>
    public static double? PaceFraction(DateTimeOffset resetsAt, TimeSpan span, DateTimeOffset now)
    {
        if (span <= TimeSpan.Zero) return null;
        var fraction = (span - (resetsAt - now)).TotalSeconds / span.TotalSeconds;
        return double.IsFinite(fraction) && fraction is >= 0 and <= 1 ? fraction : null;
    }

    public static double DisplayPercent(double utilization, LimitDisplayMode mode) =>
        mode == LimitDisplayMode.Remaining ? Math.Max(0, 100 - utilization) : utilization;

    /// <summary>The pace marker goes through the same transform as the bar fill.</summary>
    public static double? MarkerFraction(double? pace, LimitDisplayMode mode) =>
        pace is { } p ? DisplayPercent(p * 100, mode) / 100 : null;

    /// <summary>
    /// Port of <c>CompanionHeader.progressDetail</c>: "X / Y · Z%" for the growth bars. Used is clamped
    /// to total (egg usage can overshoot while a hatch retry is pending) and the percent floors, so 100%
    /// means done.
    /// </summary>
    public static string ProgressDetail(long used, long total)
    {
        used = Math.Min(used, total);
        var percent = total > 0 ? used * 100 / total : 0;
        return $"{TokenFormatter.Compact(used)} / {TokenFormatter.Compact(total)} · {percent}%";
    }
}
