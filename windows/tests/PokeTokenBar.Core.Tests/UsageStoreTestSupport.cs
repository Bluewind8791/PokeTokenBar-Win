using System.Globalization;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Fakes shared by the UsageStore test ports. Nothing here reads a real credential, log or network endpoint:
// every store a test builds goes through Stores.Make, which injects a fake for each live dependency.

internal sealed class StubException : Exception
{
    public StubException() : base("boom") { }
}

/// <summary>A usage provider whose behavior can change between refreshes (single-threaded tests only).</summary>
internal sealed class FakeUsageProvider(string id, string displayName, DailyUsage? daily = null, bool reportsCost = true)
    : IUsageProvider
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public bool ReportsCost { get; } = reportsCost;
    public DailyUsage? Daily { get; set; } = daily;
    public ProviderEnrichment Enrichment { get; set; } = new();
    public bool FailDaily { get; set; }

    public Task<DailyUsage?> FetchDaily() =>
        FailDaily ? Task.FromException<DailyUsage?>(new StubException()) : Task.FromResult(Daily);

    public Task<ProviderEnrichment> FetchEnrichment() => Task.FromResult(Enrichment);
}

/// <summary>Holds the first FetchDaily until <see cref="Release"/>, so a second refresh overlaps the first.</summary>
internal sealed class GatedUsageProvider(DailyUsage daily) : IUsageProvider
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id => "claude_code";
    public string DisplayName => "Claude Code";
    public int DailyCalls { get; private set; }

    public async Task<DailyUsage?> FetchDaily()
    {
        DailyCalls++;
        if (DailyCalls == 1) await _gate.Task;
        return daily;
    }

    public Task<ProviderEnrichment> FetchEnrichment() => Task.FromResult(new ProviderEnrichment());
    public void Release() => _gate.TrySetResult();
}

/// <summary>Returns the status, or fails like a missing credential when there is none.</summary>
internal sealed class FakeClaudeLimits(LimitStatus? status = null) : IClaudeLimitsProviding
{
    public LimitStatus? Status { get; set; } = status;

    public Task<LimitStatus> Fetch(bool allowKeychainPrompt) => Status is { } s
        ? Task.FromResult(s with { })
        : Task.FromException<LimitStatus>(LimitsError.Of(LimitsErrorKind.NoCredential));
}

/// <summary>The first calls throw the given errors, later ones succeed (or fail when there is no success).</summary>
internal sealed class SequenceClaudeLimits(IReadOnlyList<Exception> errors, LimitStatus? success = null) : IClaudeLimitsProviding
{
    public int Call { get; private set; }
    public LimitStatus? Success { get; set; } = success;

    public Task<LimitStatus> Fetch(bool allowKeychainPrompt)
    {
        var call = Call++;
        if (call < errors.Count) return Task.FromException<LimitStatus>(errors[call]);
        return Success is { } s
            ? Task.FromResult(s with { })
            : Task.FromException<LimitStatus>(LimitsError.Of(LimitsErrorKind.NoCredential));
    }
}

/// <summary>Records the allowKeychainPrompt flag of every call (manual vs automatic path).</summary>
internal sealed class RecordingClaudeLimits(LimitStatus? status = null) : IClaudeLimitsProviding
{
    public List<bool> PromptFlags { get; } = [];
    public LimitStatus? Status { get; set; } = status;

    public Task<LimitStatus> Fetch(bool allowKeychainPrompt)
    {
        PromptFlags.Add(allowKeychainPrompt);
        return Status is { } s
            ? Task.FromResult(s with { })
            : Task.FromException<LimitStatus>(LimitsError.Of(LimitsErrorKind.NoCredential));
    }
}

/// <summary>Session key storage stub — never touches the real key file.</summary>
internal sealed class StubSessionKeys(SessionKeyCredential? credential = null, List<SessionKeyOrganization>? organizations = null)
    : ISessionKeyManaging
{
    public SessionKeyCredential? Stored { get; set; } = credential;
    public List<SessionKeyOrganization> Discovered { get; set; } = organizations ?? [];
    public bool Cleared { get; private set; }
    public List<SessionKeyCredential> Saves { get; } = [];

    public SessionKeyCredential? Credential() => Stored;
    public Task<List<SessionKeyOrganization>> Organizations(string sessionKey) => Task.FromResult(Discovered.ToList());

    public void Save(string key, string? organizationID)
    {
        var saved = new SessionKeyCredential(key, organizationID);
        Stored = saved;
        Saves.Add(saved);
    }

    public void Clear()
    {
        Stored = null;
        Cleared = true;
    }
}

internal sealed class FakeStatusProvider(Dictionary<string, ProviderStatus>? result = null) : IProviderStatusProviding
{
    public Dictionary<string, ProviderStatus> Result { get; set; } = result ?? [];
    public Task<Dictionary<string, ProviderStatus>> Fetch() => Task.FromResult(new Dictionary<string, ProviderStatus>(Result));
}

/// <summary>Timers that only fire when a test says so.</summary>
internal sealed class ManualScheduler : IUsageStoreScheduler
{
    internal sealed class Entry(TimeSpan interval, Action action, bool repeats) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;
        public Action Action { get; } = action;
        public bool Repeats { get; } = repeats;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public List<Entry> Entries { get; } = [];
    public IEnumerable<Entry> Active => Entries.Where(e => !e.Disposed);

    public IDisposable Repeat(TimeSpan interval, Action tick) => Add(new Entry(interval, tick, true));
    public IDisposable After(TimeSpan delay, Action action) => Add(new Entry(delay, action, false));

    private Entry Add(Entry entry)
    {
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Fires every live one-shot with this delay (and disposes it).</summary>
    public void FireAfter(TimeSpan delay)
    {
        foreach (var entry in Active.Where(e => !e.Repeats && e.Interval == delay).ToList())
        {
            entry.Dispose();
            entry.Action();
        }
    }
}

internal static class Stores
{
    /// <summary>A store with a fake for every live dependency and an in-memory settings store.</summary>
    public static UsageStore Make(
        AppSettings settings,
        IReadOnlyList<IUsageProvider>? providers = null,
        IClaudeLimitsProviding? claude = null,
        Func<string, IClaudeLimitsProviding>? additional = null,
        Func<IReadOnlyList<string>>? discover = null,
        Func<AccountIdentity?>? readDefaultIdentity = null,
        Func<string, DateTimeOffset?>? readLastPrompt = null,
        Func<DateTimeOffset, Task<IReadOnlyList<UsageEntry>>>? claudeUsageEntries = null,
        Func<string, Dictionary<string, List<DateTimeOffset>>>? readPromptHistory = null,
        IProviderStatusProviding? status = null,
        ISessionKeyManaging? sessionKeys = null,
        Func<string, ISessionKeyManaging>? accountSessionKeys = null,
        Func<bool>? displaysAsleep = null,
        IUsageStoreScheduler? scheduler = null,
        Func<DateTimeOffset>? now = null) =>
        new(providers: providers ?? [],
            claudeLimitsProvider: claude ?? new FakeClaudeLimits(),
            additionalClaudeLimitsProvider: additional ?? (_ => new FakeClaudeLimits()),
            discoverClaudeConfigDirs: discover ?? (() => []),
            readDefaultIdentity: readDefaultIdentity ?? (() => null),
            readLastPrompt: readLastPrompt ?? (_ => null),
            claudeUsageEntries: claudeUsageEntries ?? (_ => Task.FromResult<IReadOnlyList<UsageEntry>>([])),
            readPromptHistory: readPromptHistory ?? (_ => []),
            statusProvider: status ?? new FakeStatusProvider(),
            sessionKeys: sessionKeys ?? new StubSessionKeys(),
            accountSessionKeys: accountSessionKeys ?? (_ => new StubSessionKeys()),
            autoRefresh: false,
            settings: settings,
            displaysAsleep: displaysAsleep,
            scheduler: scheduler ?? new ManualScheduler(),
            now: now);
}

internal static class StoreFixtures
{
    public static DailyUsage TodayDaily(long tokens, double cost = 0) =>
        new(LocalUsageReader.TodayKey(), 0, 0, 0, 0, tokens, cost);

    public static string DayKey(int daysFromToday) =>
        LocalUsageReader.LocalDay(DateTimeOffset.Now.AddDays(daysFromToday));

    public static BlockUsage Block(double tokensPerMinute) =>
        new("b", "", "", true, 1000, 1, tokensPerMinute);

    public static LimitStatus ClaudeLimits(double fiveHourUtil, string? resetsAt = null) =>
        new() { FiveHour = new LimitWindow { Utilization = fiveHourUtil, ResetsAt = resetsAt } };

    /// <summary>A five-hour window whose reset lies <paramref name="resetsIn"/> from <paramref name="now"/>.</summary>
    public static LimitStatus ClaudeFiveHour(double utilization, TimeSpan? resetsIn, DateTimeOffset now) =>
        ClaudeLimits(utilization, resetsIn is { } r ? Iso8601Parser.Format(now + r) : null);

    public static SessionKeyOrganization SessionOrg(string id, bool hasUsage) =>
        new(id, id.ToUpper(CultureInfo.InvariantCulture), hasUsage, ClaudeLimits(hasUsage ? 7 : 0));
}
