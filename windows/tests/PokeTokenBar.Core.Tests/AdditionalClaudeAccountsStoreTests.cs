using PokeTokenBar.Core;
using static PokeTokenBar.Core.Tests.StoreFixtures;

namespace PokeTokenBar.Core.Tests;

// Port of the store halves of AdditionalClaudeAccountsTests.swift (AdditionalClaudeAccountsStoreTests,
// ClaudeTrackedAccountStoreTests, ClaudeAccountUsageStoreTests) and AccountSessionKeyTests.swift
// (AccountSessionKeyStoreTests). The pure helpers (titles, roots, tracked-account modes, attribution, the
// session-key chain) are already ported in ClaudeAccountsTests.cs / SessionKeyLimitsTests.cs.
// Not ported (no Windows counterpart): Keychain-off tests (disableKeychainAccess), declined-prompt tests
// (#280, isKeychainPromptDeclined), and the floating-pet bubble layout test (UI). Swift's
// keychainInteractionNotAllowed ("can't read without a prompt") maps to LimitsErrorKind.NoCredential.

/// <summary>Scripted results (the last one repeats), recording the prompt flag of every call.</summary>
internal sealed class AccountsScriptedLimits(params Func<LimitStatus>[] results) : IClaudeLimitsProviding
{
    private readonly List<Func<LimitStatus>> _results = results.ToList();
    public List<bool> PromptFlags { get; } = [];

    public Task<LimitStatus> Fetch(bool allowKeychainPrompt)
    {
        PromptFlags.Add(allowKeychainPrompt);
        var result = _results.Count > 1 ? _results[0] : _results[^1];
        if (_results.Count > 1) _results.RemoveAt(0);
        try
        {
            return Task.FromResult(result());
        }
        catch (Exception error)
        {
            return Task.FromException<LimitStatus>(error);
        }
    }

    public static Func<LimitStatus> Ok(LimitStatus status) => () => status with { };
    public static Func<LimitStatus> Fail(Exception error) => () => throw error;
    public static Func<LimitStatus> NoCredential => Fail(LimitsError.Of(LimitsErrorKind.NoCredential));
}

/// <summary>The first call waits until the test releases it.</summary>
internal sealed class AccountsGatedLimits : IClaudeLimitsProviding
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }

    public async Task<LimitStatus> Fetch(bool allowKeychainPrompt)
    {
        Calls++;
        if (Calls != 1) return AccountsFx.LimitStatus(25, "work@example.com");
        await _gate.Task;
        return AccountsFx.LimitStatus(20, "work@example.com");
    }

    public void Release() => _gate.TrySetResult();
}

internal static class AccountsFx
{
    public const string ValidKey = "sk-ant-sid01-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static LimitStatus LimitStatus(double fiveHour, string? email) =>
        new() { FiveHour = new LimitWindow { Utilization = fiveHour }, AccountEmail = email };

    public static LimitStatus FullStatus(double fiveHour, double sevenDay, double? fable = null, string? email = null,
        string? org = null) => new()
    {
        FiveHour = new LimitWindow { Utilization = fiveHour },
        SevenDay = new LimitWindow { Utilization = sevenDay },
        Limits = fable is { } f
            ? [new OAuthLimitEntry
            {
                Kind = "weekly_scoped", Percent = f,
                Scope = new OAuthLimitEntry.LimitScope { Model = new OAuthLimitEntry.LimitScopeModel { DisplayName = "Fable" } },
            }]
            : null,
        AccountEmail = email,
        AccountOrganizationName = org,
    };

    /// <summary>Five-hour window resetting at <paramref name="reset"/> (null resets_at when null).</summary>
    public static LimitStatus WithReset(double utilization, DateTimeOffset? reset, string? email) => new()
    {
        FiveHour = new LimitWindow { Utilization = utilization, ResetsAt = reset is { } r ? Iso8601Parser.Format(r) : null },
        AccountEmail = email,
    };

    public static FakeUsageProvider NilDaily() => new("claude_code", "Claude Code");

    public static FakeUsageProvider TodayUsage(BlockUsage? block = null, long tokens = 1_000) =>
        new("claude_code", "Claude Code", TodayDaily(tokens))
        {
            Enrichment = new ProviderEnrichment { ActiveBlock = block, BlocksOK = true },
        };

    public static UsageEntry Entry(string id, string? session, DateTimeOffset date, long tokens, string day) =>
        new() { Id = id, Date = date, LocalDay = day, Model = "claude-sonnet-4-6", Input = tokens, SessionID = session };

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static SessionKeyOrganization Organization(string id, bool hasUsageData) =>
        new(id, id, hasUsageData, new LimitStatus());
}

public sealed class AdditionalClaudeAccountsStoreTests : IDisposable
{
    private readonly TempDir _base = new();
    private readonly AppSettings _settings = new(null);

    public AdditionalClaudeAccountsStoreTests()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Personal);
    }

    public void Dispose() => _base.Dispose();

    private string Work => Path.Combine(_base.Path, "work");
    private string Personal => Path.Combine(_base.Path, "personal");
    private string WorkKey => ClaudeAccountRoots.PathKey(Work);

    private UsageStore MakeStore(AccountsScriptedLimits primary, Dictionary<string, AccountsScriptedLimits> folders,
        AccountIdentity? defaultIdentity = null)
    {
        _settings.Set(ClaudeAccountRoots.DefaultsKey,
            string.Join(",", folders.Keys.OrderByDescending(k => k, StringComparer.Ordinal)));
        var byPath = new Dictionary<string, AccountsScriptedLimits>(folders, StringComparer.OrdinalIgnoreCase);
        return Stores.Make(_settings, [AccountsFx.NilDaily()], claude: primary,
            additional: root => byPath[root], readDefaultIdentity: () => defaultIdentity);
    }

    private static AccountsScriptedLimits Limits(params Func<LimitStatus>[] results) => new(results);

    [Fact]
    public void AdditionalAccountsFollowTheSettingsOrder() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), new()
        {
            [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com"))),
            [Personal] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(30, "personal@example.com"))),
        });
        await store.Refresh(scheduleEmptyRetry: false);

        Assert.Equal([Work, Personal], store.AdditionalLimits.Select(a => a.RootPath));
        Assert.Equal([20.0, 30.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));
        Assert.False(store.AdditionalLimitsPending);
        Assert.Equal(10, store.Limits?.FiveHour?.Utilization);
    });

    [Fact]
    public void AnAccountAlreadyShownIsListedOnce() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), new()
        {
            [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            [Personal] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(30, null))),
        });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([Personal], store.AdditionalLimits.Select(a => a.RootPath));
    });

    /// <summary>A team seat and a personal plan can share an email: they are two accounts.</summary>
    [Fact]
    public void TheSameEmailInAnotherOrganizationIsAnotherAccount() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(10, 1, email: "me@example.com", org: "Corp"))), new()
        {
            [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(20, 1, email: "me@example.com", org: "me@example.com's Organization"))),
            [Personal] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(30, 1, email: "me@example.com", org: "Corp"))),
        });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([Work], store.AdditionalLimits.Select(a => a.RootPath));
    });

    /// <summary>A manual refresh started during a poll waits for it: the poll's older values must not overwrite
    /// the manual ones.</summary>
    [Fact]
    public void OverlappingRefreshesRunOneAfterTheOther() => MainActor.Run(async () =>
    {
        _settings.Set(ClaudeAccountRoots.DefaultsKey, Work);
        var folder = new AccountsGatedLimits();
        var store = Stores.Make(_settings, [AccountsFx.NilDaily()],
            claude: Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            additional: _ => folder);

        var poll = store.Refresh(scheduleEmptyRetry: false);
        Assert.True(await MainActor.WaitUntil(() => folder.Calls >= 1, 5));
        var manual = store.RefreshLimitTokenFromKeychain();
        await Task.Delay(100);
        Assert.Equal(1, folder.Calls);

        folder.Release();
        await poll;
        await manual;
        Assert.Equal(2, folder.Calls);
        Assert.Equal([25.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));
    });

    /// <summary>With a session key on the default account, only folders listed in Settings take the manual path;
    /// a detected folder that needs it does not pin the refresh row.</summary>
    [Fact]
    public void WithASessionKeyOnlyListedFoldersPrompt() => MainActor.Run(async () =>
    {
        _settings.Set(ClaudeAccountRoots.DefaultsKey, Personal);
        var detected = Limits(AccountsScriptedLimits.NoCredential);
        var listed = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(30, "personal@example.com")));
        var store = Stores.Make(_settings, [AccountsFx.NilDaily()],
            claude: Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            additional: root => AccountsFx.Same(root, Work) ? detected : listed,
            discover: () => [Work],
            sessionKeys: new StubSessionKeys(new SessionKeyCredential("sk-ant-sid01-test", "org")));

        await store.RefreshLimitTokenFromKeychain();
        Assert.Equal([false], detected.PromptFlags);
        Assert.Equal([true], listed.PromptFlags);
        Assert.False(store.AdditionalLimitsPending);
    });

    [Fact]
    public void AnAutomaticSuccessClearsTheManualRefreshError() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Fail(LimitsError.Http(500)),
            AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), []);
        await store.RefreshLimitTokenFromKeychain();
        Assert.NotNull(store.LimitTokenRefreshError);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Null(store.LimitTokenRefreshError);
    });

    /// <summary>The row sits above the tabs: next to another account, a 429 must say whose check failed.</summary>
    [Fact]
    public void TheManualRefreshErrorNamesTheDefaultAccountNextToOthers() => MainActor.Run(async () =>
    {
        var alone = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com")),
            AccountsScriptedLimits.Fail(LimitsError.RateLimited(null))), []);
        await alone.Refresh(scheduleEmptyRetry: false);
        await alone.RefreshLimitTokenFromKeychain();
        Assert.Single(alone.ClaudeAccounts);
        var plain = alone.LimitTokenRefreshError;
        Assert.NotNull(plain);
        Assert.Equal(plain, alone.LimitTokenRefreshMessage);

        var both = MakeStore(Limits(AccountsScriptedLimits.Fail(LimitsError.RateLimited(null))),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com"))) },
            new AccountIdentity("main@example.com", "Main Corp"));
        await both.RefreshLimitTokenFromKeychain();
        Assert.Equal(["Main Corp", "work@example.com"], both.ClaudeAccounts.Select(a => a.Title));
        Assert.Equal($"Main Corp · {plain}", both.LimitTokenRefreshMessage);
    });

    /// <summary>The default account's refresh row: pointless for someone using CLAUDE_CONFIG_DIR logins only.</summary>
    [Fact]
    public void MissingDefaultLimitsNeedADefaultLoginNextToOtherAccounts() => MainActor.Run(async () =>
    {
        Dictionary<string, AccountsScriptedLimits> Folders() => new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com"))) };
        AccountsScriptedLimits Failing() => Limits(AccountsScriptedLimits.NoCredential);

        var alone = MakeStore(Failing(), []);
        await alone.Refresh(scheduleEmptyRetry: false);
        Assert.True(alone.ClaudeLimitsMissing);

        var configDirOnly = MakeStore(Failing(), Folders());
        await configDirOnly.Refresh(scheduleEmptyRetry: false);
        Assert.False(configDirOnly.ClaudeLimitsMissing);

        var both = MakeStore(Failing(), Folders(), new AccountIdentity("main@example.com", null));
        await both.Refresh(scheduleEmptyRetry: false);
        Assert.True(both.ClaudeLimitsMissing);

        var loaded = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), Folders());
        await loaded.Refresh(scheduleEmptyRetry: false);
        Assert.False(loaded.ClaudeLimitsMissing);
    });

    /// <summary>Windows: "prompt" means "re-read the credentials file now" — only the manual path asks for it.</summary>
    [Fact]
    public void AutomaticRefreshNeverPromptsManualRefreshDoes() => MainActor.Run(async () =>
    {
        var folder = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com")));
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), new() { [Work] = folder });

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([false], folder.PromptFlags);

        await store.RefreshLimitTokenFromKeychain();
        Assert.Equal([false, true], folder.PromptFlags);
    });

    [Fact]
    public void AFailingAccountKeepsItsLastValueAndAsksForARefresh() => MainActor.Run(async () =>
    {
        var folder = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com")), AccountsScriptedLimits.NoCredential,
            AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(25, "work@example.com")));
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), new() { [Work] = folder });

        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([20.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));
        Assert.True(store.AdditionalLimitsPending);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([25.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));
        Assert.False(store.AdditionalLimitsPending);
    });

    [Fact]
    public void AFailingAccountWithoutHistoryIsNotShown() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            new() { [Work] = Limits(AccountsScriptedLimits.NoCredential) });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.AdditionalLimits);
        Assert.True(store.AdditionalLimitsPending);
    });

    /// <summary>Rate limits apply per account: the default account's 429 must not pause the other folders.</summary>
    [Fact]
    public void ADefaultAccountRateLimitDoesNotPauseTheOtherAccounts() => MainActor.Run(async () =>
    {
        var primary = Limits(AccountsScriptedLimits.Fail(LimitsError.RateLimited(TimeSpan.FromSeconds(600))));
        var folder = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com")));
        var store = MakeStore(primary, new() { [Work] = folder });

        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([false], primary.PromptFlags);
        Assert.Equal([false, false], folder.PromptFlags);
    });

    [Fact]
    public void AFolderRateLimitPausesOnlyThatFolderAndKeepsItsValues() => MainActor.Run(async () =>
    {
        var primary = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com")));
        var workLimits = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com")),
            AccountsScriptedLimits.Fail(LimitsError.RateLimited(TimeSpan.FromSeconds(600))),
            AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(21, "work@example.com")));
        var personalLimits = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(30, "personal@example.com")));
        var store = MakeStore(primary, new() { [Work] = workLimits, [Personal] = personalLimits });

        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);   // work: 429
        await store.Refresh(scheduleEmptyRetry: false);   // work: paused
        Assert.Equal([false, false], workLimits.PromptFlags);
        Assert.Equal([false, false, false], personalLimits.PromptFlags);
        Assert.Equal([false, false, false], primary.PromptFlags);
        Assert.Equal([20.0, 30.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));

        await store.RefreshLimitTokenFromKeychain();
        Assert.Equal([false, false, true], workLimits.PromptFlags);
        Assert.Equal([21.0, 30.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([false, false, true, false], workLimits.PromptFlags);
    });

    [Fact]
    public void RemovingTheFoldersClearsTheAccounts() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            new() { [Work] = Limits(AccountsScriptedLimits.NoCredential) });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.AdditionalLimitsPending);

        // The setter refreshes on its own; let that refresh finish inside the test.
        store.AdditionalClaudeConfigDirs = "";
        Assert.True(await MainActor.WaitUntil(() => !store.AdditionalLimitsPending && !store.IsRefreshing, 5));
        await MainActor.Drain();
        Assert.Empty(store.AdditionalLimits);
        Assert.False(store.AdditionalLimitsPending);
        Assert.Equal("", _settings.GetString(ClaudeAccountRoots.DefaultsKey));
    });

    [Fact]
    public void AccountsListTheDefaultLoginFirstWithItsHistoricalKeys() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(2, 45, email: "me@corp.example", org: "Corp"))),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(32, 4, email: "me@example.com"))) });
        await store.Refresh(scheduleEmptyRetry: false);

        Assert.Equal([ClaudeAccountLimits.DefaultID, WorkKey], store.ClaudeAccounts.Select(a => a.Id));
        Assert.Equal(["Corp", "me@example.com"], store.ClaudeAccounts.Select(a => a.Title));
        Assert.Equal(["claude", $"claude.{WorkKey}"], store.ClaudeAccounts.Select(a => a.WindowKeyPrefix));
        Assert.Equal([true, false], store.ClaudeAccounts.Select(a => a.IsDefault));
    });

    [Fact]
    public void CandyWindowsCoverEveryAccountAndKeepTheDefaultKeys() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(100, 45, fable: 100, email: "me@corp.example", org: "Corp"))),
            new()
            {
                [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(50, 50, email: "me@example.com")),
                    AccountsScriptedLimits.Ok(AccountsFx.FullStatus(100, 100, email: "me@example.com"))),
            });
        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);
        var l = new L(store.LocalizationLanguage);
        var windows = store.CandyEligibleWindows;
        var byKey = windows.GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.First());

        Assert.Equal(byKey.Count, windows.Count);
        Assert.Equal(
            new HashSet<string> { "claude.fiveHour", "claude.sevenDay", $"claude.{WorkKey}.fiveHour", $"claude.{WorkKey}.sevenDay" },
            byKey.Keys.ToHashSet());
        Assert.Equal($"{l.ClaudeFiveHour} (Corp)", byKey["claude.fiveHour"].Name);
        Assert.Equal($"{l.ClaudeFiveHour} (me@example.com)", byKey[$"claude.{WorkKey}.fiveHour"].Name);
        Assert.Equal(WindowClass.Weekly, byKey[$"claude.{WorkKey}.sevenDay"].Kind);
    });

    /// <summary>Each account's full gauge pays once, and a folder removed then added back at 100% does not pay
    /// again: its key is the folder's path hash.</summary>
    [Fact]
    public void CandyIsGrantedPerAccountAndNotAgainAfterReAddingAFolder() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(100, 10, email: "me@corp.example"))),
            new()
            {
                [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(99, 99, email: "me@example.com")),
                    AccountsScriptedLimits.Ok(AccountsFx.FullStatus(100, 100, email: "me@example.com"))),
            });
        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);
        var tiers = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();

        var grants = CompanionStore.EvaluateCandyGrants(store.CandyEligibleWindows, tiers, epochs);
        Assert.Equal(1 + 1 + RareCandy.WeeklyGrant, grants.Sum(g => g.Count));

        var defaultOnly = store.CandyEligibleWindows.Where(w => !w.Key.Contains(WorkKey)).ToList();
        Assert.Empty(CompanionStore.EvaluateCandyGrants(defaultOnly, tiers, epochs));
        Assert.Empty(CompanionStore.EvaluateCandyGrants(store.CandyEligibleWindows, tiers, epochs));
    });

    /// <summary>An account found later at its limit pays nothing until seen below it, and that is remembered.</summary>
    [Fact]
    public void AnAccountFoundAtItsLimitEarnsNoCandyUntilSeenBelowIt() => MainActor.Run(async () =>
    {
        var primary = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(10, 10, email: "me@corp.example")));
        var store = MakeStore(primary, new()
        {
            [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(100, 40, email: "me@example.com")),
                AccountsScriptedLimits.Ok(AccountsFx.FullStatus(20, 100, email: "me@example.com")),
                AccountsScriptedLimits.Ok(AccountsFx.FullStatus(100, 100, email: "me@example.com"))),
        });
        HashSet<string> Keys(UsageStore s) => s.CandyEligibleWindows.Select(w => w.Key).ToHashSet();
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(new HashSet<string> { "claude.fiveHour", "claude.sevenDay", $"claude.{WorkKey}.sevenDay" }, Keys(store));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(new HashSet<string> { "claude.fiveHour", "claude.sevenDay", $"claude.{WorkKey}.fiveHour", $"claude.{WorkKey}.sevenDay" },
            Keys(store));

        var restarted = MakeStore(primary, new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(100, 100, email: "me@example.com"))) });
        await restarted.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(Keys(store), Keys(restarted));
    });

    [Fact]
    public void AlertWindowsCoverEveryAccountAndKeepTheDefaultKeys() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(2, 45, fable: 77, email: "me@corp.example", org: "Corp"))),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(32, 4, fable: 12, email: "me@example.com"))) });
        await store.Refresh(scheduleEmptyRetry: false);
        var l = new L(store.LocalizationLanguage);
        var (windows, accounts) = store.BuildLimitWindows();
        Assert.Equal(windows.Count, windows.Select(w => w.Key).Distinct().Count());
        var byKey = windows.GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.First());

        Assert.Equal(2, byKey["claude.fiveHour"].Utilization);
        Assert.Equal(77, byKey["claude.scoped.weekly_scoped.Fable.0"].Utilization);
        Assert.Equal(32, byKey[$"claude.{WorkKey}.fiveHour"].Utilization);
        Assert.Equal(l.ClaudeWeekly, byKey[$"claude.{WorkKey}.sevenDay"].Name);
        Assert.Equal(12, byKey[$"claude.{WorkKey}.scoped.weekly_scoped.Fable.0"].Utilization);
        Assert.Equal(6, windows.Count);
        Assert.Equal(new Dictionary<string, string>
        {
            ["claude.fiveHour"] = "Corp", ["claude.sevenDay"] = "Corp", ["claude.scoped.weekly_scoped.Fable.0"] = "Corp",
            [$"claude.{WorkKey}.fiveHour"] = "me@example.com", [$"claude.{WorkKey}.sevenDay"] = "me@example.com",
            [$"claude.{WorkKey}.scoped.weekly_scoped.Fable.0"] = "me@example.com",
        }, accounts);

        var alone = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(2, 45, email: "me@corp.example", org: "Corp"))), []);
        await alone.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(alone.BuildLimitWindows().Accounts);
        Assert.Equal(l.ClaudeFiveHour, alone.CandyEligibleWindows.First(w => w.Key == "claude.fiveHour").Name);
    });

    /// <summary>Next to other accounts, an alert says whose limit it is, after the percentage.</summary>
    [Fact]
    public void AlertsNameTheirAccountAfterThePercentage()
    {
        var tiers = new Dictionary<string, int>();
        var alerts = UsageStore.EvaluateLimitAlerts(
            [("claude.fiveHour", "Claude 5-hour session", 96), ("other.x.primary", "Other 5h", 85)],
            new Dictionary<string, string> { ["claude.fiveHour"] = "Corp" }, 80, 95, tiers);
        Assert.Equal(["Corp", null], alerts.Select(a => a.Account));
        Assert.Equal(["Claude 5-hour session at 96% · Corp", "Other 5h at 85%"], alerts.Select(a => a.Body(new L(AppLanguage.En))));
        Assert.Equal("Claude 5-hour session at 96%", alerts[0].Body(new L(AppLanguage.En), withAccount: false));
    }

    [Fact]
    public void LimitsAreReadyWithOnlyAnAdditionalAccount() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.NoCredential),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(32, 4, email: "me@example.com"))) });
        Assert.False(store.LimitsReady);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Null(store.Limits);
        Assert.True(store.LimitsReady);
        Assert.Equal([WorkKey], store.ClaudeAccounts.Select(a => a.Id));
    });

    [Fact]
    public void DetectedFoldersAreShownWithoutAnySetting() => MainActor.Run(async () =>
    {
        var folder = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com")));
        var store = Stores.Make(_settings, [AccountsFx.NilDaily()],
            claude: Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            additional: root => AccountsFx.Same(root, Work) ? folder : Limits(AccountsScriptedLimits.Fail(LimitsError.Http(500))),
            discover: () => [Work]);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([Work], store.AdditionalLimits.Select(a => a.RootPath));
        Assert.Equal([Work], store.DetectedClaudeConfigDirs);
        Assert.Null(_settings.GetString(ClaudeAccountRoots.DefaultsKey));
    });

    [Fact]
    public void SettingFoldersComeAfterTheDetectedOnes() => MainActor.Run(async () =>
    {
        _settings.Set(ClaudeAccountRoots.DefaultsKey, $"{Work},{Personal}");
        var store = Stores.Make(_settings, [AccountsFx.NilDaily()],
            claude: Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            additional: root => Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, Path.GetFileName(root)))),
            discover: () => [Personal]);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([Personal, Work], store.AdditionalLimits.Select(a => a.RootPath));
    });

    private static void SaveLogin(string folder, string email, string? org = null)
    {
        var orgField = org == null ? "" : $",\"organizationName\":\"{org}\"";
        File.WriteAllText(Path.Combine(folder, ".claude.json"), $"{{\"oauthAccount\":{{\"emailAddress\":\"{email}\"{orgField}}}}}");
    }

    /// <summary>A rejected token cannot be fixed by a refresh: the account stays visible, dimmed, and does not pin
    /// the refresh row. It stays expired through automatic failures, until a fetch succeeds.</summary>
    [Fact]
    public void ARejectedTokenMarksTheAccountExpiredUntilAFetchSucceeds() => MainActor.Run(async () =>
    {
        var folder = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com")),
            AccountsScriptedLimits.Fail(LimitsError.Http(401)), AccountsScriptedLimits.NoCredential,
            AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(25, "work@example.com")));
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), new() { [Work] = folder });

        await store.Refresh(scheduleEmptyRetry: false);
        await store.RefreshLimitTokenFromKeychain();
        Assert.Equal([true], store.AdditionalLimits.Select(a => a.IsExpired));
        Assert.Equal([20.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));
        Assert.False(store.AdditionalLimitsPending);
        Assert.Equal([false, true], store.ClaudeAccounts.Select(a => a.IsExpired));

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([true], store.AdditionalLimits.Select(a => a.IsExpired));
        Assert.False(store.AdditionalLimitsPending);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([false], store.AdditionalLimits.Select(a => a.IsExpired));
        Assert.Equal([25.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));
    });

    /// <summary>Windows: an expired token in the folder's credentials file is a rejection too (only Claude Code
    /// renews it), not a failure a refresh can fix.</summary>
    [Fact]
    public void AnExpiredCredentialsFileMarksTheAccountExpired() => MainActor.Run(async () =>
    {
        var folder = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com")),
            AccountsScriptedLimits.Fail(LimitsError.Of(LimitsErrorKind.CredentialExpired)));
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))), new() { [Work] = folder });

        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);
        var account = Assert.Single(store.AdditionalLimits);
        Assert.True(account.IsExpired);
        Assert.False(account.SessionKeyExpired);
        Assert.Equal(20, account.Status.FiveHour?.Utilization);
        Assert.False(store.AdditionalLimitsPending, "a refresh cannot renew this token");
    });

    [Fact]
    public void AnExpiredAccountWithoutHistoryKeepsATabNamedFromItsSavedLogin() => MainActor.Run(async () =>
    {
        SaveLogin(Work, "work@example.com", "work@example.com's Organization");
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            new() { [Work] = Limits(AccountsScriptedLimits.Fail(LimitsError.Http(403))) });

        await store.Refresh(scheduleEmptyRetry: false);
        var account = store.ClaudeAccounts[^1];
        Assert.True(account.IsExpired);
        Assert.Equal("work@example.com", account.Title);
        Assert.Null(account.Status.FiveHour);
        Assert.False(store.AdditionalLimitsPending);
    });

    [Fact]
    public void AnExpiredFolderOfTheDefaultLoginIsNotShown() => MainActor.Run(async () =>
    {
        SaveLogin(Work, "main@example.com");
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            new() { [Work] = Limits(AccountsScriptedLimits.Fail(LimitsError.Http(401))) });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.AdditionalLimits);
    });

    [Fact]
    public void TheSavedLoginNamesAnAccountWhenTheProfileIsUnknown() => MainActor.Run(async () =>
    {
        SaveLogin(Work, "work@example.com", "Work Corp");
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, null))) });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal("work@example.com", store.AdditionalLimits[0].Status.AccountEmail);
        Assert.Equal("Work Corp", store.ClaudeAccounts[^1].Title);
    });

    [Fact]
    public void TheProfileNameWinsOverTheSavedLogin() => MainActor.Run(async () =>
    {
        SaveLogin(Work, "old@example.com", "Old Corp");
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.FullStatus(20, 1, email: "work@example.com", org: "Work Corp"))) });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal("work@example.com", store.AdditionalLimits[0].Status.AccountEmail);
        Assert.Equal("Work Corp", store.ClaudeAccounts[^1].Title);
    });

    [Fact]
    public void TheDefaultAccountTabFollowsTheDefaultExpiry() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com")), AccountsScriptedLimits.Fail(LimitsError.Http(401))),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com"))) });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(store.ClaudeAccounts[0].IsExpired);
        await store.RefreshLimitTokenFromKeychain();
        Assert.True(store.ClaudeAccounts[0].IsExpired);
    });

    /// <summary>Next to another account the default login keeps a tab (named from its saved login) while its
    /// limits are not loaded; alone, nothing changes.</summary>
    [Fact]
    public void TheDefaultTabStaysWhileItsLimitsAreNotLoaded() => MainActor.Run(async () =>
    {
        var identity = new AccountIdentity("main@example.com", "Corp");
        var store = MakeStore(Limits(AccountsScriptedLimits.Fail(LimitsError.RateLimited(TimeSpan.FromSeconds(60)))),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "work@example.com"))) }, identity);

        await store.Refresh(scheduleEmptyRetry: false);
        var first = store.ClaudeAccounts[0];
        Assert.True(first.IsDefault);
        Assert.Equal("Corp", first.Title);
        Assert.Null(first.Status.FiveHour);
        Assert.All(store.CandyEligibleWindows, w => Assert.False(w.Key.StartsWith("claude.fiveHour", StringComparison.Ordinal)));
        Assert.Equal(identity, store.DefaultSavedIdentity);

        var alone = MakeStore(Limits(AccountsScriptedLimits.Fail(LimitsError.RateLimited(TimeSpan.FromSeconds(60)))), [], identity);
        await alone.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(alone.ClaudeAccounts);
    });

    [Fact]
    public void AFolderOfTheDefaultLoginIsHiddenEvenBeforeTheDefaultLimitsLoad() => MainActor.Run(async () =>
    {
        var store = MakeStore(Limits(AccountsScriptedLimits.NoCredential),
            new() { [Work] = Limits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "main@example.com"))) },
            new AccountIdentity("main@example.com", null));
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Empty(store.AdditionalLimits);
    });

    [Fact]
    public void OnlyARejectedOrExpiredTokenCountsAsARejectedToken()
    {
        Assert.True(UsageStore.IsAuthRejection(LimitsError.Http(401)));
        Assert.True(UsageStore.IsAuthRejection(LimitsError.Http(403)));
        Assert.True(UsageStore.IsAuthRejection(LimitsError.Of(LimitsErrorKind.CredentialExpired)));
        Assert.False(UsageStore.IsAuthRejection(LimitsError.Http(500)));
        Assert.False(UsageStore.IsAuthRejection(LimitsError.Of(LimitsErrorKind.NoCredential)));
    }
}

public sealed class TrackedAccountAdditionalClaudeAccountsStoreTests : IDisposable
{
    private readonly TempDir _base = new();
    private readonly AppSettings _settings = new(null);

    public TrackedAccountAdditionalClaudeAccountsStoreTests() => Directory.CreateDirectory(Personal);

    public void Dispose() => _base.Dispose();

    private string Personal => Path.Combine(_base.Path, "personal");
    private string PersonalKey => ClaudeAccountRoots.PathKey(Personal);

    private UsageStore MakeStore(LimitStatus primary, LimitStatus? personal,
        Dictionary<string, DateTimeOffset> lastPrompt, AccountsScriptedLimits? personalLimits = null,
        IReadOnlyList<UsageEntry>? usage = null,
        Dictionary<string, Dictionary<string, List<DateTimeOffset>>>? prompts = null,
        BlockUsage? machineBlock = null, long todayTokens = 1_000)
    {
        if (personal != null) _settings.Set(ClaudeAccountRoots.DefaultsKey, Personal);
        var personalPath = Personal;
        string Who(string folder) => AccountsFx.Same(folder, personalPath) ? "personal" : "default";
        return Stores.Make(_settings, [AccountsFx.TodayUsage(machineBlock, todayTokens)],
            claude: new AccountsScriptedLimits(AccountsScriptedLimits.Ok(primary)),
            additional: _ => personalLimits
                ?? new AccountsScriptedLimits(personal != null ? AccountsScriptedLimits.Ok(personal) : AccountsScriptedLimits.NoCredential),
            readLastPrompt: folder => lastPrompt.TryGetValue(Who(folder), out var d) ? d : null,
            claudeUsageEntries: _ => Task.FromResult(usage ?? []),
            readPromptHistory: folder => prompts?.GetValueOrDefault(Who(folder))
                ?.ToDictionary(p => p.Key, p => p.Value.ToList()) ?? []);
    }

    private static readonly BlockUsage Machine600k = new("m", "", "", true, 600_000, 0, 60_000);

    [Fact]
    public void AutomaticModeFollowsTheLastUsedAccountEverywhere() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var store = MakeStore(AccountsFx.FullStatus(100, 100, email: "me@corp.example", org: "Corp"),
            AccountsFx.FullStatus(61, 21, email: "me@example.com"),
            new() { ["default"] = now.AddHours(-1), ["personal"] = now });
        store.ShowLimitInMenu = true;
        store.ShowTokensInMenu = false;
        store.LocalizationLanguage = AppLanguage.En;
        await store.Refresh(scheduleEmptyRetry: false);

        Assert.Equal(now.AddHours(-1), store.ClaudeAccountActivity[ClaudeAccountLimits.DefaultID]);
        Assert.Equal(PersonalKey, store.TrackedClaudeAccount?.Id);
        Assert.Equal(["Claude 61%"], store.MenuLines);
        Assert.Equal("Tracked Claude account: me@example.com", store.MenuToolTip);
        Assert.False(store.IsLimitWarning);
        Assert.Equal(61, store.HighestLimitUtilization!.Value, 2);

        store.ClaudeTrackedAccountMode = ClaudeTrackedAccountMode.DefaultAccount;
        Assert.Equal(["Claude 100%"], store.MenuLines);
        Assert.Equal("Tracked Claude account: Corp", store.MenuToolTip);
        Assert.True(store.IsLimitWarning);
        Assert.Null(store.FiveHourForecast);
        Assert.Equal("default", _settings.GetString(ClaudeTrackedAccountMode.DefaultsKey));
    });

    [Fact]
    public void TheMenuToolTipOnlyNamesAnAccountBehindAShownPercentage() => MainActor.Run(async () =>
    {
        var alone = MakeStore(AccountsFx.FullStatus(40, 10, email: "me@corp.example", org: "Corp"), null, []);
        alone.ShowLimitInMenu = true;
        await alone.Refresh(scheduleEmptyRetry: false);
        Assert.Equal("Claude 40%", alone.MenuLines[^1]);
        Assert.Null(alone.MenuToolTip);

        var both = MakeStore(AccountsFx.FullStatus(40, 10, email: "me@corp.example", org: "Corp"),
            AccountsFx.FullStatus(61, 21, email: "me@example.com"), new() { ["personal"] = DateTimeOffset.Now });
        both.LocalizationLanguage = AppLanguage.Fr;
        both.ShowLimitInMenu = false;
        await both.Refresh(scheduleEmptyRetry: false);
        Assert.Null(both.MenuToolTip);
        both.ShowLimitInMenu = true;
        Assert.Equal(new L(AppLanguage.Fr).TrackedAccountToolTip("me@example.com"), both.MenuToolTip);

        var idle = MakeStore(AccountsFx.FullStatus(40, 10, email: "me@corp.example", org: "Corp"),
            AccountsFx.FullStatus(61, 21, email: "me@example.com"), new() { ["personal"] = DateTimeOffset.Now }, todayTokens: 0);
        idle.ShowLimitInMenu = true;
        await idle.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(PersonalKey, idle.TrackedClaudeAccount?.Id);
        Assert.DoesNotContain(idle.MenuLines, l => l.StartsWith("Claude", StringComparison.Ordinal));
        Assert.Null(idle.MenuToolTip);

        var weeklyOnly = new LimitStatus { SevenDay = new LimitWindow { Utilization = 30 }, AccountEmail = "me@example.com" };
        var noFiveHour = MakeStore(AccountsFx.FullStatus(40, 10, email: "me@corp.example", org: "Corp"), weeklyOnly,
            new() { ["personal"] = DateTimeOffset.Now });
        noFiveHour.ShowLimitInMenu = true;
        await noFiveHour.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(PersonalKey, noFiveHour.TrackedClaudeAccount?.Id);
        Assert.DoesNotContain(noFiveHour.MenuLines, l => l.StartsWith("Claude", StringComparison.Ordinal));
        Assert.Null(noFiveHour.MenuToolTip);
    });

    [Fact]
    public void TheForecastUsesTheTrackedAccount() => MainActor.Run(async () =>
    {
        var reset = DateTimeOffset.Now.AddHours(1);
        var store = MakeStore(AccountsFx.WithReset(40, reset, "me@corp.example"), AccountsFx.WithReset(100, reset, "me@example.com"),
            new() { ["personal"] = DateTimeOffset.Now });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.FiveHourForecast?.BeforeReset);

        store.ClaudeTrackedAccountMode = ClaudeTrackedAccountMode.DefaultAccount;
        Assert.Null(store.FiveHourForecast);
    });

    /// <summary>With several accounts the machine-wide block mixes their burn: each forecast uses its own.</summary>
    [Fact]
    public void TheForecastUsesTheTrackedAccountsOwnBurn() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var reset = now.AddHours(1);
        var burst = AccountsFx.Entry("b", "home", now.AddSeconds(-600), 600_000, LocalUsageReader.TodayKey());
        var store = MakeStore(AccountsFx.WithReset(40, reset, "me@corp.example"), AccountsFx.WithReset(40, reset, "me@example.com"),
            new() { ["personal"] = now }, usage: [burst],
            prompts: new() { ["personal"] = new() { ["home"] = [now.AddSeconds(-700)] } },
            machineBlock: Machine600k);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(PersonalKey, store.TrackedClaudeAccount?.Id);
        Assert.True(store.FiveHourForecast?.BeforeReset);

        store.ClaudeTrackedAccountMode = ClaudeTrackedAccountMode.DefaultAccount;
        Assert.Null(store.FiveHourForecast);
    });

    /// <summary>The machine-wide block ends at neither account's reset: each tab shows its own block, within that
    /// account's official window.</summary>
    [Fact]
    public void EachTabShowsItsOwnFiveHourBlock() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var day = LocalUsageReader.TodayKey();
        var reset = now.AddHours(1);
        var usage = new[]
        {
            AccountsFx.Entry("b", "home", now.AddSeconds(-600), 600_000, day),
            // Before the personal window started, still inside the rolling 5 hours.
            AccountsFx.Entry("old", "home", now.AddHours(-4.5), 9_000, day),
            AccountsFx.Entry("w", "work", now.AddSeconds(-1200), 50_000, day),
        };
        var machine = new BlockUsage("m", "", "", true, 900_000, 0, 60_000);
        var store = MakeStore(new LimitStatus { FiveHour = new LimitWindow { Utilization = 0 } },
            AccountsFx.WithReset(52, reset, "me@example.com"),
            new() { ["personal"] = now }, usage: usage,
            prompts: new()
            {
                ["personal"] = new() { ["home"] = [now.AddHours(-5)] },
                ["default"] = new() { ["work"] = [now.AddSeconds(-1300)] },
            },
            machineBlock: machine);
        await store.Refresh(scheduleEmptyRetry: false);
        var accounts = store.ClaudeAccounts;
        Assert.Equal([ClaudeAccountLimits.DefaultID, PersonalKey], accounts.Select(a => a.Id));
        Assert.Equal(50_000, store.ClaudeAccountUsage[ClaudeAccountLimits.DefaultID].TodayTokens);
        Assert.Null(store.ClaudeCurrentBlock(accounts[0]));
        var own = store.ClaudeCurrentBlock(accounts[1]);
        Assert.NotNull(own);
        Assert.Equal(600_000, own.TotalTokens);
        Assert.True(Math.Abs((own.EndDate!.Value - reset).TotalSeconds) <= 1, "ends at the official reset");
        Assert.NotEqual(machine.Id, own.Id);
    });

    /// <summary>An expired account's values are too old to place its window: its tab keeps the rolling block.</summary>
    [Fact]
    public void AnExpiredAccountKeepsTheRollingBlock() => MainActor.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        var status = AccountsFx.WithReset(52, now.AddSeconds(-300), "me@example.com");
        var burst = AccountsFx.Entry("b", "home", now.AddSeconds(-600), 600_000, LocalUsageReader.TodayKey());
        var store = MakeStore(AccountsFx.FullStatus(10, 1, email: "me@corp.example"), status,
            new() { ["personal"] = now },
            personalLimits: new AccountsScriptedLimits(AccountsScriptedLimits.Ok(status), AccountsScriptedLimits.Fail(LimitsError.Http(401))),
            usage: [burst], prompts: new() { ["personal"] = new() { ["home"] = [now.AddSeconds(-700)] } });
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Null(store.ClaudeCurrentBlock(store.ClaudeAccounts[1]));

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.ClaudeAccounts[1].IsExpired);
        Assert.Equal(600_000, store.ClaudeCurrentBlock(store.ClaudeAccounts[1])?.TotalTokens);
    });

    [Fact]
    public void ASingleAccountForecastUsesTheMachineBlock() => MainActor.Run(async () =>
    {
        var store = MakeStore(AccountsFx.WithReset(40, DateTimeOffset.Now.AddHours(1), "me@corp.example"), null, [],
            machineBlock: Machine600k);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.FiveHourForecast?.BeforeReset);
        Assert.Equal("m", store.ClaudeCurrentBlock(store.ClaudeAccounts[0])?.Id);
    });

    [Fact]
    public void ChangingTheTrackedAccountUpdatesTheCompanionAtOnce()
    {
        var store = MakeStore(AccountsFx.FullStatus(10, 1, email: "me@corp.example"), null, []);
        var updates = 0;
        store.OnRefresh = () => updates++;
        store.ClaudeTrackedAccountMode = ClaudeTrackedAccountMode.Highest;
        Assert.Equal(1, updates);
    }

    [Fact]
    public void TheSavedModeIsReadBackAndSingleAccountsIgnoreIt() => MainActor.Run(async () =>
    {
        _settings.Set(ClaudeTrackedAccountMode.DefaultsKey, $"account:{PersonalKey}");
        var store = MakeStore(AccountsFx.FullStatus(10, 1, email: "me@corp.example"), null, []);
        Assert.Equal(ClaudeTrackedAccountMode.Account(PersonalKey), store.ClaudeTrackedAccountMode);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(ClaudeAccountLimits.DefaultID, store.TrackedClaudeAccount?.Id);
        Assert.Empty(store.ClaudeAccountActivity);
    });

    [Fact]
    public void AdditionalValuesAreDatedAndFlaggedStale() => MainActor.Run(async () =>
    {
        var store = MakeStore(AccountsFx.FullStatus(10, 1, email: "me@corp.example"),
            AccountsFx.FullStatus(61, 21, email: "me@example.com"), []);
        var before = DateTimeOffset.Now;
        await store.Refresh(scheduleEmptyRetry: false);
        var updatedAt = store.AdditionalLimits[0].UpdatedAt!.Value;
        Assert.True(updatedAt >= before);
        Assert.False(store.AdditionalLimitsStale);
        Assert.True(store.ClaudeAccounts[^1].IsStale(updatedAt.AddMinutes(16)));
        Assert.Equal(store.LimitsUpdatedAt, store.ClaudeAccounts[0].UpdatedAt);
    });
}

public sealed class UsageAdditionalClaudeAccountsStoreTests : IDisposable
{
    private readonly TempDir _base = new();
    private readonly AppSettings _settings = new(null);

    public UsageAdditionalClaudeAccountsStoreTests() => Directory.CreateDirectory(Path.Combine(_base.Path, "personal"));

    public void Dispose() => _base.Dispose();

    [Fact]
    public void UsageIsSplitBetweenTheListedAccounts() => MainActor.Run(async () =>
    {
        var personal = Path.Combine(_base.Path, "personal");
        var personalKey = ClaudeAccountRoots.PathKey(personal);
        _settings.Set(ClaudeAccountRoots.DefaultsKey, personal);
        var now = DateTimeOffset.Now;
        var today = LocalUsageReader.TodayKey();
        var asked = new List<DateTimeOffset>();
        var store = Stores.Make(_settings, [AccountsFx.NilDaily()],
            claude: new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "me@corp.example"))),
            additional: _ => new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "me@example.com"))),
            claudeUsageEntries: since =>
            {
                lock (asked) asked.Add(since);
                return Task.FromResult<IReadOnlyList<UsageEntry>>(
                [
                    AccountsFx.Entry("1", "corp", now, 100, today), AccountsFx.Entry("2", "home", now, 30, today),
                    AccountsFx.Entry("3", "cli-print", now, 4, today),
                ]);
            },
            readPromptHistory: folder => AccountsFx.Same(folder, personal)
                ? new() { ["home"] = [now.AddSeconds(-60)] }
                : new() { ["corp"] = [now.AddSeconds(-60)] });

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal(100, store.ClaudeAccountUsage[ClaudeAccountLimits.DefaultID].TodayTokens);
        Assert.Equal(30, store.ClaudeAccountUsage[personalKey].TodayTokens);
        Assert.Equal(4, store.UnattributedClaudeUsage.TodayTokens);
        var scannedFrom = asked[0];
        Assert.True(Math.Abs((scannedFrom - UsageStore.ClaudeUsageScanStart(now)).TotalSeconds) <= 5);

        store.AdditionalClaudeConfigDirs = "";
        Assert.True(await MainActor.WaitUntil(() => store.ClaudeAccountUsage.Count == 0 && !store.IsRefreshing, 5));
        Assert.Empty(store.ClaudeAccountUsage);
        Assert.Equal(new ClaudeAccountUsage(), store.UnattributedClaudeUsage);
    });

    /// <summary>A folder hidden as a duplicate still counts for its account, and an account without a tab is not
    /// shown under another name.</summary>
    [Fact]
    public void DuplicateFoldersAddUpAndTablessAccountsAreUnattributed() => MainActor.Run(async () =>
    {
        var work = Path.Combine(_base.Path, "work");
        var team = Path.Combine(_base.Path, "team");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(team);
        var personal = Path.Combine(_base.Path, "personal");
        var personalKey = ClaudeAccountRoots.PathKey(personal);
        _settings.Set(ClaudeAccountRoots.DefaultsKey, string.Join(",", work, personal, team));
        var now = DateTimeOffset.Now;
        var today = LocalUsageReader.TodayKey();
        var byFolder = new Dictionary<string, (string Session, DateTimeOffset Date)>(StringComparer.OrdinalIgnoreCase)
        {
            [work] = ("w", now.AddSeconds(-30)), [personal] = ("p", now.AddSeconds(-50)), [team] = ("t", now.AddSeconds(-10)),
        };
        var store = Stores.Make(_settings, [AccountsFx.NilDaily()],
            claude: new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "me@corp.example"))),
            additional: root =>
                AccountsFx.Same(root, work) ? new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "me@corp.example")))
                : AccountsFx.Same(root, personal) ? new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "me@example.com")))
                : new AccountsScriptedLimits(AccountsScriptedLimits.NoCredential),
            readLastPrompt: folder => byFolder.TryGetValue(folder, out var f) ? f.Date : now.AddSeconds(-20),
            claudeUsageEntries: _ => Task.FromResult<IReadOnlyList<UsageEntry>>(
                new[] { "c", "w", "p", "t" }.Select(s => AccountsFx.Entry(s, s, now, 10, today)).ToList()),
            readPromptHistory: folder =>
            {
                var (session, date) = byFolder.TryGetValue(folder, out var f) ? f : ("c", now.AddSeconds(-20));
                return new() { [session] = [date] };
            });

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.Equal([ClaudeAccountLimits.DefaultID, personalKey], store.ClaudeAccounts.Select(a => a.Id));
        Assert.Equal(20, store.ClaudeAccountUsage[ClaudeAccountLimits.DefaultID].TodayTokens);
        Assert.Equal(10, store.ClaudeAccountUsage[personalKey].TodayTokens);
        Assert.Equal(new HashSet<string> { ClaudeAccountLimits.DefaultID, personalKey }, store.ClaudeAccountUsage.Keys.ToHashSet());
        Assert.Equal(10, store.UnattributedClaudeUsage.TodayTokens);
        Assert.Equal(now.AddSeconds(-20), store.ClaudeAccountActivity[ClaudeAccountLimits.DefaultID]);
    });

    [Fact]
    public void TheScanCoversTheMonthAndABlockStartedLastMonth()
    {
        var monthStart = LocalUsageReader.StartOfMonth(DateTimeOffset.Now);
        var early = monthStart.AddHours(2);
        Assert.Equal(early - LocalUsageReader.BlockWindow, UsageStore.ClaudeUsageScanStart(early));
        var later = monthStart.AddDays(10);
        Assert.Equal(monthStart, UsageStore.ClaudeUsageScanStart(later));
    }
}

/// <summary>Port of AccountSessionKeyStoreTests: a claude.ai session key per additional Claude folder.</summary>
public sealed class SessionKeyAdditionalClaudeAccountsStoreTests : IDisposable
{
    private readonly TempDir _base = new();
    private readonly AppSettings _settings = new(null);

    public SessionKeyAdditionalClaudeAccountsStoreTests()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Personal);
    }

    public void Dispose() => _base.Dispose();

    private string Work => Path.Combine(_base.Path, "work");
    private string Personal => Path.Combine(_base.Path, "personal");

    private UsageStore MakeStore(Dictionary<string, AccountsScriptedLimits> folders, Dictionary<string, StubSessionKeys> keys)
    {
        _settings.Set(ClaudeAccountRoots.DefaultsKey,
            string.Join(",", folders.Keys.OrderByDescending(k => k, StringComparer.Ordinal)));
        var byPath = new Dictionary<string, AccountsScriptedLimits>(folders, StringComparer.OrdinalIgnoreCase);
        var keysByPath = new Dictionary<string, StubSessionKeys>(keys, StringComparer.OrdinalIgnoreCase);
        return Stores.Make(_settings, [AccountsFx.NilDaily()],
            claude: new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(10, "main@example.com"))),
            additional: root => byPath[root],
            accountSessionKeys: root => keysByPath.TryGetValue(root, out var k) ? k : new StubSessionKeys());
    }

    /// <summary>The key sees the personal and the team organization: the folder's own login decides, even
    /// against the default rule (the one with usage data).</summary>
    [Fact]
    public void SavingAKeyPicksTheFolderOrganizationAndShowsTheAccount() => MainActor.Run(async () =>
    {
        File.WriteAllText(Path.Combine(Personal, ".claude.json"),
            """{"oauthAccount":{"emailAddress":"me@example.com","organizationUuid":"org-team"}}""");
        var keys = new StubSessionKeys(organizations: [AccountsFx.Organization("org-personal", true), AccountsFx.Organization("org-team", false)]);
        var store = MakeStore(new() { [Personal] = new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(42, null))) },
            new() { [Personal] = keys });

        await store.SaveAccountSessionKey($" {AccountsFx.ValidKey}\n", Personal);

        Assert.Null(store.AccountSessionKeyError(Personal));
        Assert.Equal(new SessionKeyCredential(AccountsFx.ValidKey, "org-team"), keys.Stored);
        Assert.Equal([Personal], store.AccountSessionKeyPaths);
        Assert.Equal([42.0], store.AdditionalLimits.Select(a => a.Status.FiveHour!.Utilization!.Value));
    });

    [Fact]
    public void AMalformedKeyIsRejectedAndNothingIsSaved() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(organizations: [AccountsFx.Organization("org-personal", true)]);
        var store = MakeStore(new() { [Personal] = new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(42, null))) },
            new() { [Personal] = keys });

        await store.SaveAccountSessionKey("not-a-key", Personal);

        Assert.NotNull(store.AccountSessionKeyError(Personal));
        Assert.Null(keys.Stored);
        Assert.Empty(store.AccountSessionKeyPaths);
    });

    /// <summary>A rejected key keeps the tab with its last values and points to Settings; a rejected token (the
    /// folder's Claude Code login) keeps the Claude Code advice.</summary>
    [Fact]
    public void ARejectedKeyExpiresTheTabUntilAnotherCauseReplacesIt() => MainActor.Run(async () =>
    {
        var folder = new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, "me@example.com")),
            AccountsScriptedLimits.Fail(LimitsError.Of(LimitsErrorKind.SessionKeyInvalid)),
            AccountsScriptedLimits.Fail(LimitsError.RateLimited(null)),
            AccountsScriptedLimits.Fail(LimitsError.Http(401)));
        var store = MakeStore(new() { [Personal] = folder },
            new() { [Personal] = new StubSessionKeys(new SessionKeyCredential(AccountsFx.ValidKey)) });
        ClaudeAccountLimits? Account() => store.ClaudeAccounts.FirstOrDefault(a => !a.IsDefault);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.False(Account()?.IsExpired);

        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(Account()?.IsExpired);
        Assert.True(Account()?.SessionKeyExpired);
        Assert.Equal(20, Account()?.Status.FiveHour?.Utilization);

        await store.RefreshLimitTokenFromKeychain();   // bypasses the 429 backoff
        Assert.True(Account()?.SessionKeyExpired);

        await store.RefreshLimitTokenFromKeychain();
        Assert.True(Account()?.IsExpired);
        Assert.False(Account()?.SessionKeyExpired);
    });

    [Fact]
    public void ClearingAKeyDropsItsExpiry() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(new SessionKeyCredential(AccountsFx.ValidKey));
        var folder = new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, null)),
            AccountsScriptedLimits.Fail(LimitsError.Of(LimitsErrorKind.SessionKeyInvalid)));
        var store = MakeStore(new() { [Personal] = folder }, new() { [Personal] = keys });
        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.AdditionalLimits[0].SessionKeyExpired);

        store.ClearAccountSessionKey(Personal);

        Assert.Null(keys.Stored);
        Assert.Empty(store.AccountSessionKeyPaths);
        Assert.False(store.AdditionalLimits[0].IsExpired);
        Assert.False(store.AdditionalLimits[0].SessionKeyExpired);
    });

    /// <summary>Only the key's own expiry goes with it: a rejected folder token still needs Claude Code.</summary>
    [Fact]
    public void ClearingAKeyKeepsATokenExpiry() => MainActor.Run(async () =>
    {
        var keys = new StubSessionKeys(new SessionKeyCredential(AccountsFx.ValidKey));
        var folder = new AccountsScriptedLimits(AccountsScriptedLimits.Ok(AccountsFx.LimitStatus(20, null)), AccountsScriptedLimits.Fail(LimitsError.Http(401)));
        var store = MakeStore(new() { [Personal] = folder }, new() { [Personal] = keys });
        await store.Refresh(scheduleEmptyRetry: false);
        await store.Refresh(scheduleEmptyRetry: false);
        Assert.True(store.AdditionalLimits[0].IsExpired);

        store.ClearAccountSessionKey(Personal);

        Assert.True(store.AdditionalLimits[0].IsExpired);
    });
}
