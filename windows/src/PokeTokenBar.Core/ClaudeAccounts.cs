using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>
/// The rest of <c>ClaudeAccountRoots.swift</c>: saved login identity, organization, the folder id
/// (<see cref="PathKey"/>) and per-account file locations. The macOS Keychain service names are not
/// ported: on Windows each folder's credential is its own <c>.credentials.json</c>.
/// </summary>
public static partial class ClaudeAccountRoots
{
    /// <summary>Email and organization Claude Code saved for this folder's login. Used when the profile
    /// endpoint gives nothing, typically because the token expired. Local read, no network.</summary>
    public static AccountIdentity? SavedIdentity(string root) =>
        SavedIdentityInFile(Path.Combine(root, ".claude.json"));

    /// <summary>Login saved for the default folder (<c>~/.claude.json</c>), to name its tab before its
    /// limits load. Installed app only: tests must not read the developer's own login.</summary>
    public static AccountIdentity? InstalledDefaultIdentity(bool? isInstalledApp = null, string? home = null)
    {
        if (!(isInstalledApp ?? AppEnv.IsInstalledApp)) return null;
        return SavedIdentityInFile(Path.Combine(home ?? AppEnv.HomeDirectory, ".claude.json"));
    }

    public static AccountIdentity? SavedIdentityInFile(string file) => SavedLogins.Login(file)?.Identity;

    /// <summary>Organization of this folder's login, to pick the same one among a session key's organizations.</summary>
    public static string? SavedOrganizationID(string root) =>
        SavedLogins.Login(Path.Combine(root, ".claude.json"))?.OrganizationID;

    private sealed record SavedLogin(AccountIdentity? Identity, string? OrganizationID = null);

    private static readonly SavedLoginCache SavedLogins = new();

    /// <summary><c>.claude.json</c> also keeps per-project state and grows with use, while only its
    /// <c>oauthAccount</c> matters here: the file is parsed again only when it changed.</summary>
    private sealed class SavedLoginCache
    {
        private sealed record Hit(DateTime MTime, long Size, SavedLogin? Login);

        private readonly object _gate = new();
        private readonly Dictionary<string, Hit> _hits = new(StringComparer.OrdinalIgnoreCase);

        public SavedLogin? Login(string file)
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            var mtime = info.LastWriteTimeUtc;
            var size = info.Length;
            lock (_gate)
            {
                if (_hits.TryGetValue(file, out var hit) && hit.MTime == mtime && hit.Size == size) return hit.Login;
            }
            var login = Read(file);
            lock (_gate) _hits[file] = new Hit(mtime, size, login);
            return login;
        }

        private static SavedLogin? Read(string file)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("oauthAccount", out var account)
                    || account.ValueKind != JsonValueKind.Object) return null;
                var email = Text(account, "emailAddress");
                if (email == null) return new SavedLogin(null);
                return new SavedLogin(new AccountIdentity(email, Text(account, "organizationName")),
                    Text(account, "organizationUuid"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        private static string? Text(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    }

    /// <summary>
    /// First 8 hex characters of the SHA-256 of the folder's full path (no trailing separator). The
    /// folder's stable id for tab selection, alert/candy keys and its session key file, so no path ends
    /// up in the save file.
    /// </summary>
    public static string PathKey(string root)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
    }

    public static string CredentialsFilePath(string root) => Path.Combine(root, ".credentials.json");

    /// <summary>The default login's config folder (Claude Code without <c>CLAUDE_CONFIG_DIR</c>).</summary>
    public static string DefaultConfigDir(string? home = null) => Path.Combine(home ?? AppEnv.HomeDirectory, ".claude");

    /// <summary>When this login last received a prompt: Claude Code appends one line per prompt to the
    /// folder's own <c>history.jsonl</c>, never shared between logins. Metadata only.</summary>
    public static DateTimeOffset? LastPromptDate(string configDir)
    {
        var file = new FileInfo(Path.Combine(configDir, "history.jsonl"));
        return file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) : null;
    }

    /// <summary>Installed app only: tests must not read the developer's own history.</summary>
    public static DateTimeOffset? InstalledLastPromptDate(string configDir, bool? isInstalledApp = null) =>
        (isInstalledApp ?? AppEnv.IsInstalledApp) ? LastPromptDate(configDir) : null;

    /// <summary>Port of <c>abbreviatingWithTildeInPath</c>: the home prefix becomes <c>~</c>.</summary>
    public static string AbbreviatingWithTilde(string path, string? home = null)
    {
        home = Path.TrimEndingDirectorySeparator(home ?? AppEnv.HomeDirectory);
        if (path.Equals(home, StringComparison.OrdinalIgnoreCase)) return "~";
        return path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..]
            : path;
    }
}

/// <summary>
/// Port of <c>ClaudeTrackedAccountMode</c>: which Claude account drives the single-account surfaces when
/// several are shown (menu bar percentage, warning state, floating pet hover, 5h forecast).
/// </summary>
public readonly record struct ClaudeTrackedAccountMode
{
    public enum ModeKind { Automatic, DefaultAccount, Highest, Account }

    public const string DefaultsKey = "claudeTrackedAccount";

    public ModeKind Kind { get; }
    /// <summary>The pinned account id for <see cref="ModeKind.Account"/>.</summary>
    public string? AccountID { get; }

    private ClaudeTrackedAccountMode(ModeKind kind, string? accountID = null)
    {
        Kind = kind;
        AccountID = accountID;
    }

    /// <summary>The account that received the latest prompt.</summary>
    public static readonly ClaudeTrackedAccountMode Automatic = new(ModeKind.Automatic);
    public static readonly ClaudeTrackedAccountMode DefaultAccount = new(ModeKind.DefaultAccount);
    /// <summary>The account with the highest official window.</summary>
    public static readonly ClaudeTrackedAccountMode Highest = new(ModeKind.Highest);
    public static ClaudeTrackedAccountMode Account(string id) => new(ModeKind.Account, id);

    public static ClaudeTrackedAccountMode FromStoredValue(string? storedValue) => storedValue switch
    {
        "default" => DefaultAccount,
        "highest" => Highest,
        { } v when v.StartsWith("account:", StringComparison.Ordinal) => Account(v["account:".Length..]),
        _ => Automatic,
    };

    public string StoredValue => Kind switch
    {
        ModeKind.DefaultAccount => "default",
        ModeKind.Highest => "highest",
        ModeKind.Account => $"account:{AccountID}",
        _ => "automatic",
    };
}

/// <summary>Port of <c>AdditionalClaudeLimits</c>: official limits of one additional Claude config folder.</summary>
public sealed record AdditionalClaudeLimits
{
    public string RootPath { get; }
    public string Key { get; }
    public LimitStatus Status { get; set; }
    /// <summary>Last successful fetch; null for an expired folder shown without values.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
    /// <summary>The folder's token was rejected. Only Claude Code running on that folder renews it, so a
    /// refresh alone cannot help: the tab shows the last values dimmed, with what to do.</summary>
    public bool IsExpired { get; set; }
    /// <summary>The rejected credential is the folder's own session key: the fix is a fresh key in
    /// Settings, not Claude Code.</summary>
    public bool SessionKeyExpired { get; set; }

    public AdditionalClaudeLimits(string rootPath, LimitStatus status, bool isExpired = false,
        bool sessionKeyExpired = false, DateTimeOffset? updatedAt = null)
    {
        RootPath = rootPath;
        Key = ClaudeAccountRoots.PathKey(rootPath);
        Status = status;
        IsExpired = isExpired;
        SessionKeyExpired = sessionKeyExpired;
        UpdatedAt = updatedAt;
    }
}

/// <summary>
/// Port of <c>ClaudeAccountLimits</c>: one Claude account with official limits, as the tabs, alerts and
/// candy see it. The default login first, then the additional folders.
/// </summary>
public sealed record ClaudeAccountLimits(
    string Id,
    string WindowKeyPrefix,
    string FallbackTitle,
    LimitStatus Status,
    bool IsDefault,
    bool IsExpired,
    DateTimeOffset? UpdatedAt = null,
    bool SessionKeyExpired = false)
{
    public const string DefaultID = "default";

    /// <summary>The default login keeps its historical <c>claude.*</c> alert and candy keys.</summary>
    public static ClaudeAccountLimits DefaultAccount(LimitStatus status, bool isExpired = false, DateTimeOffset? updatedAt = null) =>
        new(DefaultID, "claude", @"~\.claude", status, true, isExpired, updatedAt);

    public static ClaudeAccountLimits Additional(AdditionalClaudeLimits account) =>
        new(account.Key, $"claude.{account.Key}", ClaudeAccountRoots.AbbreviatingWithTilde(account.RootPath),
            account.Status, false, account.IsExpired, account.UpdatedAt, account.SessionKeyExpired);

    /// <summary>At least one official window to show (a placeholder tab has none).</summary>
    public bool HasLimits => Status.AllUtilizations.Count > 0;

    /// <summary>Same 15-minute rule as the default account's stale label.</summary>
    public bool IsStale(DateTimeOffset now) =>
        HasLimits && !IsExpired && UpdatedAt is { } updated && now - updated > TimeSpan.FromMinutes(15);

    /// <summary>The organization for a team plan, the email for a personal plan (its generated
    /// organization name repeats the email), the folder when both are unknown.</summary>
    public string Title
    {
        get
        {
            var email = Status.AccountEmail;
            if (string.IsNullOrEmpty(email)) return FallbackTitle;
            var org = Status.AccountOrganizationName;
            return !string.IsNullOrEmpty(org) && !org.Contains(email, StringComparison.Ordinal) ? org : email;
        }
    }

    /// <summary>Port of <c>UsageStore.trackedAccount</c>: the account the single-account surfaces follow.
    /// Only accounts with values qualify.</summary>
    public static ClaudeAccountLimits? TrackedAccount(IReadOnlyList<ClaudeAccountLimits> accounts,
        ClaudeTrackedAccountMode mode, IReadOnlyDictionary<string, DateTimeOffset> activity)
    {
        var loaded = accounts.Where(a => a.HasLimits).ToList();
        return mode.Kind switch
        {
            ClaudeTrackedAccountMode.ModeKind.DefaultAccount =>
                loaded.FirstOrDefault(a => a.IsDefault) ?? LastUsed(loaded, activity),
            ClaudeTrackedAccountMode.ModeKind.Highest =>
                FirstBest(loaded, a => a.Status.AllUtilizations.DefaultIfEmpty(-1).Max()),
            ClaudeTrackedAccountMode.ModeKind.Account =>
                loaded.FirstOrDefault(a => a.Id == mode.AccountID) ?? LastUsed(loaded, activity),
            _ => LastUsed(loaded, activity),
        };
    }

    /// <summary>Latest prompt wins; an undated account never beats a dated one, and ties keep the list
    /// order (default account first).</summary>
    private static ClaudeAccountLimits? LastUsed(List<ClaudeAccountLimits> accounts,
        IReadOnlyDictionary<string, DateTimeOffset> activity) =>
        FirstBest(accounts, a => activity.TryGetValue(a.Id, out var d) ? d.ToUnixTimeMilliseconds() : double.NegativeInfinity);

    private static ClaudeAccountLimits? FirstBest(List<ClaudeAccountLimits> accounts, Func<ClaudeAccountLimits, double> score)
    {
        ClaudeAccountLimits? best = null;
        var bestScore = 0.0;
        foreach (var account in accounts)
        {
            var value = score(account);
            if (best == null || value > bestScore) (best, bestScore) = (account, value);
        }
        return best;
    }
}

/// <summary>Port of <c>ClaudeAccountUsage</c>: tokens and cost of one Claude account, today and this month.</summary>
public record struct ClaudeAccountUsage(long TodayTokens = 0, UsageCost TodayCost = default,
    long MonthTokens = 0, UsageCost MonthCost = default)
{
    public readonly bool IsEmpty => MonthTokens == 0 && TodayTokens == 0;

    public void Add(ClaudeAccountUsage other)
    {
        TodayTokens += other.TodayTokens;
        var today = TodayCost;
        today.Add(other.TodayCost);
        TodayCost = today;
        MonthTokens += other.MonthTokens;
        var month = MonthCost;
        month.Add(other.MonthCost);
        MonthCost = month;
    }
}

/// <summary>
/// Port of <c>ClaudeAccountUsageAttribution</c>: splits local Claude usage between accounts.
/// Transcripts carry no account and <c>projects/</c> may be shared between logins, but each login's own
/// <c>history.jsonl</c> lists its prompts with session and time. A turn belongs to the login that sent
/// the latest prompt of its session before it, so a session resumed from another login changes owner at
/// that point. Turns of sessions no history mentions stay unattributed rather than guessed.
/// </summary>
public static class ClaudeAccountUsageAttribution
{
    /// <summary>One login: prompt times per session, ascending.</summary>
    public sealed record Account(string Id, IReadOnlyDictionary<string, List<DateTimeOffset>> Prompts);

    /// <summary>An account's official 5h window, as its limits report it.</summary>
    public abstract record FiveHourWindow
    {
        public sealed record Running(DateTimeOffset Reset) : FiveHourWindow;
        public sealed record NotStarted : FiveHourWindow;
    }

    public sealed record Result(
        Dictionary<string, ClaudeAccountUsage> ByAccount,
        ClaudeAccountUsage Unattributed,
        Dictionary<string, BlockUsage> ActiveBlocks);

    /// <summary>Accounts are checked in order; on equal times the first one wins.</summary>
    public static string? Owner(string session, DateTimeOffset at, IReadOnlyList<Account> accounts) =>
        Owner([session], at, accounts);

    /// <summary>
    /// A copied turn can appear in both its original and fork sessions. The latest prompt at or before
    /// the turn across all candidates wins; a fork prompt created later cannot claim it.
    /// </summary>
    public static string? Owner(IEnumerable<string> sessions, DateTimeOffset at, IReadOnlyList<Account> accounts)
    {
        (string Id, DateTimeOffset Date)? latest = null;
        (string Id, DateTimeOffset Date)? earliest = null;
        var ordered = sessions.Distinct().Order(StringComparer.Ordinal).ToList();
        foreach (var account in accounts)
        {
            foreach (var session in ordered)
            {
                if (!account.Prompts.TryGetValue(session, out var times) || times.Count == 0) continue;
                var before = times.LastOrDefault(t => t <= at, DateTimeOffset.MinValue);
                if (before != DateTimeOffset.MinValue && (latest == null || before > latest.Value.Date))
                    latest = (account.Id, before);
                if (earliest == null || times[0] < earliest.Value.Date) earliest = (account.Id, times[0]);
            }
        }
        // A turn logged before any prompt of its session (clock skew) goes to the login that started it.
        return latest?.Id ?? earliest?.Id;
    }

    /// <summary>
    /// <see cref="Result.ActiveBlocks"/> holds each account's own 5h block, for its tab and forecast: the
    /// machine-wide block would mix another account's burn into it. With an official window the block
    /// counts that window's turns and ends at its reset; without one it is the rolling local block.
    /// </summary>
    public static Result Usage(IEnumerable<UsageEntry> entries, IReadOnlyList<Account> accounts, DateTimeOffset now,
        string todayKey, string monthStartKey, IReadOnlyDictionary<string, FiveHourWindow>? fiveHourWindows = null)
    {
        // Account ids are never empty, so "" collects the unattributed turns.
        const string unattributed = "";
        var blockStart = now - LocalUsageReader.BlockWindow;
        var today = new Dictionary<string, UsageBucket>();
        var month = new Dictionary<string, UsageBucket>();
        var recent = new Dictionary<string, List<UsageEntry>>();
        foreach (var entry in entries)
        {
            var inMonth = string.CompareOrdinal(entry.LocalDay, monthStartKey) >= 0 && string.CompareOrdinal(entry.LocalDay, todayKey) <= 0;
            // A block started before the month began still counts.
            var inBlock = entry.Date >= blockStart;
            if (!inMonth && !inBlock) continue;
            var key = Owner(entry.ClaudeSessionIDs, entry.Date, accounts) ?? unattributed;
            if (inBlock && key != unattributed)
            {
                if (!recent.TryGetValue(key, out var list)) recent[key] = list = [];
                list.Add(entry);
            }
            if (!inMonth) continue;
            Add(month, key, entry);
            if (entry.LocalDay == todayKey) Add(today, key, entry);
        }

        ClaudeAccountUsage UsageOf(string key)
        {
            var day = today.GetValueOrDefault(key);
            var period = month.GetValueOrDefault(key);
            return new ClaudeAccountUsage(day.Total, new UsageCost(day.Cost, day.CostCoverage),
                period.Total, new UsageCost(period.Cost, period.CostCoverage));
        }

        var byAccount = month.Keys.Where(id => id != unattributed).ToDictionary(id => id, UsageOf);
        var activeBlocks = new Dictionary<string, BlockUsage>();
        foreach (var (id, turns) in recent)
        {
            switch (fiveHourWindows?.GetValueOrDefault(id))
            {
                case FiveHourWindow.Running running when running.Reset > now:
                    var start = running.Reset - LocalUsageReader.BlockWindow;
                    if (LocalUsageReader.ActiveBlock(turns.Where(t => t.Date >= start), now) is { } block)
                        activeBlocks[id] = block with { EndTime = Iso8601Parser.Format(running.Reset) };
                    break;
                case FiveHourWindow:
                    // No session runs: these turns belong to a window that is over.
                    break;
                default:
                    if (LocalUsageReader.ActiveBlock(turns, now) is { } rolling) activeBlocks[id] = rolling;
                    break;
            }
        }
        return new Result(byAccount, UsageOf(unattributed), activeBlocks);
    }

    private static void Add(Dictionary<string, UsageBucket> buckets, string key, UsageEntry entry)
    {
        var bucket = buckets.GetValueOrDefault(key);
        bucket.Add(entry);
        buckets[key] = bucket;
    }
}

/// <summary>
/// Port of <c>ClaudePromptHistory</c>: reads <c>&lt;config folder&gt;/history.jsonl</c> (one prompt per
/// line: <c>sessionId</c>, <c>timestamp</c> in ms). The prompt text itself is never kept.
/// </summary>
public static class ClaudePromptHistory
{
    public static Dictionary<string, List<DateTimeOffset>> Prompts(string configDir) =>
        Cache.Prompts(Path.Combine(configDir, "history.jsonl"));

    /// <summary>Installed app only, like <see cref="ClaudeAccountRoots.InstalledAccountRoots"/>.</summary>
    public static Dictionary<string, List<DateTimeOffset>> InstalledPrompts(string configDir, bool? isInstalledApp = null) =>
        (isInstalledApp ?? AppEnv.IsInstalledApp) ? Prompts(configDir) : [];

    public static Dictionary<string, List<DateTimeOffset>> Parse(string text)
    {
        var output = new Dictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("\"sessionId\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var obj = doc.RootElement;
                if (obj.ValueKind != JsonValueKind.Object
                    || !obj.TryGetProperty("sessionId", out var s) || s.ValueKind != JsonValueKind.String
                    || s.GetString() is not { Length: > 0 } session
                    || !obj.TryGetProperty("timestamp", out var ts) || PromptDate(ts) is not { } date) continue;
                if (!output.TryGetValue(session, out var dates)) output[session] = dates = [];
                dates.Add(date);
            }
            catch (JsonException) { }
        }
        foreach (var dates in output.Values) dates.Sort();
        return output;
    }

    private static DateTimeOffset? PromptDate(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Number && raw.GetDouble() is var ms && ms > 0 && double.IsFinite(ms))
            return DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
        return raw.ValueKind == JsonValueKind.String ? Iso8601Parser.Date(raw.GetString()!) : null;
    }

    private static readonly FileCache Cache = new();

    /// <summary>Claude Code only appends to the history: after the first read only new lines are parsed.</summary>
    private sealed class FileCache
    {
        /// <param name="Offset">Bytes parsed so far, always at a line end: a line still being written is read next time.</param>
        private sealed record Parsed(DateTime MTime, long Size, long Offset, Dictionary<string, List<DateTimeOffset>> Prompts);

        private readonly object _gate = new();
        private readonly Dictionary<string, Parsed> _parsed = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<DateTimeOffset>> Prompts(string file)
        {
            var info = new FileInfo(file);
            if (!info.Exists) return [];
            var mtime = info.LastWriteTimeUtc;
            var size = info.Length;
            Parsed? hit;
            lock (_gate) _parsed.TryGetValue(file, out hit);
            if (hit != null && hit.MTime == mtime && hit.Size == size) return Copy(hit.Prompts);
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Resume only where the previous read ended on a line break; anything else is a rewrite.
                var resumeFrom = hit;
                if (resumeFrom != null)
                {
                    var resumes = resumeFrom.Offset > 0 && resumeFrom.Offset <= stream.Length;
                    if (resumes)
                    {
                        stream.Seek(resumeFrom.Offset - 1, SeekOrigin.Begin);
                        resumes = stream.ReadByte() == '\n';
                    }
                    if (!resumes) resumeFrom = null;
                }
                var start = resumeFrom?.Offset ?? 0;
                stream.Seek(start, SeekOrigin.Begin);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
                var complete = data[..(data.LastIndexOf((byte)'\n') + 1)];
                var prompts = resumeFrom != null ? Copy(resumeFrom.Prompts) : new Dictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);
                foreach (var (session, dates) in Parse(Encoding.UTF8.GetString(complete)))
                {
                    if (!prompts.TryGetValue(session, out var merged)) prompts[session] = merged = [];
                    merged.AddRange(dates);
                    merged.Sort();
                }
                lock (_gate) _parsed[file] = new Parsed(mtime, size, start + complete.Length, prompts);
                return Copy(prompts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return hit != null ? Copy(hit.Prompts) : [];
            }
        }

        /// <summary>Callers get their own lists: the cached ones are appended to on the next read.</summary>
        private static Dictionary<string, List<DateTimeOffset>> Copy(Dictionary<string, List<DateTimeOffset>> prompts) =>
            prompts.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal);
    }
}
