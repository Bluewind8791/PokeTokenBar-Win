using System.Security.Cryptography;
using System.Text;
using PokeTokenBar.Core;
using static PokeTokenBar.Core.Tests.LimitFixtures;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of ClaudeAccountTitleTests.</summary>
public sealed class ClaudeAccountTitleTests
{
    private static readonly string Work = Path.Combine(AppEnv.HomeDirectory, ".claude-work");

    private static ClaudeAccountLimits Account(string? email, string? org) =>
        ClaudeAccountLimits.Additional(new AdditionalClaudeLimits(Work, Full(1, 1, email: email, org: org)));

    [Fact]
    public void TeamPlanShowsTheOrganization() => Assert.Equal("Corp", Account("me@corp.example", "Corp").Title);

    [Fact]
    public void PersonalPlanShowsTheEmailNotItsGeneratedOrganization()
    {
        Assert.Equal("me@example.com", Account("me@example.com", "me@example.com's Organization").Title);
        Assert.Equal("me@example.com", Account("me@example.com", "").Title);
        Assert.Equal("me@example.com", Account("me@example.com", null).Title);
    }

    [Fact]
    public void UnknownAccountShowsTheFolder()
    {
        Assert.Equal(@"~\.claude-work", Account(null, "Corp").Title);
        Assert.Equal(@"~\.claude", ClaudeAccountLimits.DefaultAccount(Full(1, 1, email: "")).Title);
    }

    /// <summary>First 8 hex of the SHA-256 of the full path; a trailing separator does not change it.</summary>
    [Fact]
    public void FolderKeyIsThePathHash()
    {
        const string path = @"C:\Users\example\.claude-work";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
        Assert.Equal(expected, new AdditionalClaudeLimits(path, Full(1, 1)).Key);
        Assert.Equal(expected, ClaudeAccountRoots.PathKey(path + @"\"));
        Assert.Equal(expected, ClaudeAccountRoots.PathKey(@"C:/Users/example/.claude-work/"));
        Assert.NotEqual(expected, ClaudeAccountRoots.PathKey(@"C:\Users\example\.claude-personal"));
    }
}

/// <summary>Port of ClaudeAccountRootsTests (folder list) and ClaudeConfigDiscoveryTests.</summary>
public sealed class ClaudeAccountRootsTests : IDisposable
{
    private const string LoggedIn = """{"oauthAccount":{"emailAddress":"me@example.com"},"projects":{}}""";
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private string Folder(string name, string? claudeJSON = null)
    {
        var path = Path.Combine(_home.Path, name);
        Directory.CreateDirectory(path);
        if (claudeJSON != null) File.WriteAllText(Path.Combine(path, ".claude.json"), claudeJSON);
        return path;
    }

    [Fact]
    public void RootsKeepExistingFoldersInOrderAndDropDefaultsDuplicatesAndInvalidEntries()
    {
        foreach (var f in new[] { ".claude", Path.Combine(".config", "claude"), ".claude-work", ".claude-personal" }) Folder(f);
        var raw = $"~/.claude-work, ~/.claude-work/\n~/.claude\n~/.config/claude,~/missing, relative/path, ,\n{_home.Path}\\.claude-personal";
        Assert.Equal([".claude-work", ".claude-personal"], ClaudeAccountRoots.Roots(raw, _home.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void RootsAreEmptyWithoutASetting()
    {
        Assert.Empty(ClaudeAccountRoots.Roots(null, _home.Path));
        Assert.Empty(ClaudeAccountRoots.Roots("  \n , ", _home.Path));
    }

    [Fact]
    public void InstalledAccountRootsReadTheSettingOutsideTheApp()
    {
        var settings = new AppSettings(null);
        Assert.Empty(ClaudeAccountRoots.InstalledAccountRoots(settings));
        var personal = Folder(".claude-personal");
        settings.Set(ClaudeAccountRoots.DefaultsKey, personal);
        Assert.Equal([personal], ClaudeAccountRoots.InstalledAccountRoots(settings));
    }

    [Fact]
    public void DetectsExportedFoldersThenLoggedInClaudeFolders()
    {
        Folder(".claude-work", LoggedIn);
        Folder(".claude_team", LoggedIn);
        Folder(Path.Combine("configs", "claude"));
        Folder(".claude", LoggedIn);
        Folder(".claude-old", """{"oauthAccount":null}""");
        Folder(".claude-broken", "{not json");
        Folder(".claude-empty");
        Folder(".claudex", LoggedIn);

        var found = ClaudeAccountRoots.Discovered(_home.Path, $"{_home.Path}\\configs\\claude, ~/.claude, ~/.claude-work");
        Assert.Equal([Path.Combine("configs", "claude"), ".claude-work", ".claude_team"],
            found.Select(p => Path.GetRelativePath(_home.Path, p)));
    }

    [Fact]
    public void MergedListPutsDetectedFoldersFirstWithoutDuplicates()
    {
        var work = Folder(".claude-work", LoggedIn);
        Folder("elsewhere");
        var merged = ClaudeAccountRoots.Merged([work], "~/elsewhere, ~/.claude-work", _home.Path);
        Assert.Equal([".claude-work", "elsewhere"], merged.Select(Path.GetFileName));
    }

    [Fact]
    public void TheDefaultLoginIsReadFromHomeOnlyInsideTheApp()
    {
        var file = Path.Combine(_home.Path, ".claude.json");
        File.WriteAllText(file, """{"oauthAccount":{"emailAddress":"me@corp.example","organizationName":"Corp"}}""");
        Assert.Null(ClaudeAccountRoots.InstalledDefaultIdentity(isInstalledApp: false, home: _home.Path));
        Assert.Equal(new AccountIdentity("me@corp.example", "Corp"),
            ClaudeAccountRoots.InstalledDefaultIdentity(isInstalledApp: true, home: _home.Path));

        WriteStamped(file, """{"oauthAccount":{"emailAddress":"me@example.com","organizationName":""}}""", DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(new AccountIdentity("me@example.com", null),
            ClaudeAccountRoots.InstalledDefaultIdentity(isInstalledApp: true, home: _home.Path));

        WriteStamped(file, """{"oauthAccount":{"emailAddress":""}}""", DateTime.UtcNow.AddMinutes(2));
        Assert.Null(ClaudeAccountRoots.InstalledDefaultIdentity(isInstalledApp: true, home: _home.Path));
    }

    [Fact]
    public void TheSavedLoginIsReadAgainOnlyWhenTheFileChanges()
    {
        var file = Path.Combine(_home.Path, ".claude.json");
        var stamp = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        void Save(string email) => WriteStamped(file, $"{{\"oauthAccount\":{{\"emailAddress\":\"{email}\"}}}}", stamp);

        Save("aa@example.com");
        Assert.Equal("aa@example.com", ClaudeAccountRoots.SavedIdentity(_home.Path)?.Email);
        Save("bb@example.com");
        Assert.Equal("aa@example.com", ClaudeAccountRoots.SavedIdentity(_home.Path)?.Email);   // same size and date: cached
        Save("ccc@example.com");
        Assert.Equal("ccc@example.com", ClaudeAccountRoots.SavedIdentity(_home.Path)?.Email);
        Assert.True(ClaudeAccountRoots.HasLogin(_home.Path));
        File.Delete(file);
        Assert.False(ClaudeAccountRoots.HasLogin(_home.Path));
        Assert.Null(ClaudeAccountRoots.SavedIdentity(_home.Path));
    }

    [Fact]
    public void TheSavedOrganizationPicksTheSessionKeyOrganization()
    {
        var root = Folder("personal", """{"oauthAccount":{"emailAddress":"me@example.com","organizationUuid":"org-team"}}""");
        Assert.Equal("org-team", ClaudeAccountRoots.SavedOrganizationID(root));
        Assert.Null(ClaudeAccountRoots.SavedOrganizationID(Folder("other", LoggedIn)));
    }

    [Fact]
    public void FillIdentityOnlyWhenTheProfileGaveNothing()
    {
        var status = Status(1);
        status.FillIdentity(new AccountIdentity("saved@example.com", "Org"));
        Assert.Equal("saved@example.com", status.AccountEmail);
        status.FillIdentity(new AccountIdentity("other@example.com", null));
        Assert.Equal("saved@example.com", status.AccountEmail);
    }

    internal static void WriteStamped(string file, string text, DateTime stampUtc)
    {
        File.WriteAllText(file, text);
        File.SetLastWriteTimeUtc(file, stampUtc);
    }
}

/// <summary>Port of ClaudeTrackedAccountModeTests.</summary>
public sealed class ClaudeTrackedAccountModeTests
{
    private static ClaudeAccountLimits Account(string id, double? fiveHour, double? weekly = null, bool isDefault = false)
    {
        var parts = new List<string>();
        if (fiveHour is { } f) parts.Add($"\"five_hour\":{{\"utilization\":{f}}}");
        if (weekly is { } w) parts.Add($"\"seven_day\":{{\"utilization\":{w}}}");
        var status = LimitStatus.Decode(Encoding.UTF8.GetBytes("{" + string.Join(",", parts) + "}"));
        return new ClaudeAccountLimits(id, $"claude.{id}", id, status, isDefault, false);
    }

    private static string? Tracked(IReadOnlyList<ClaudeAccountLimits> accounts, ClaudeTrackedAccountMode mode,
        Dictionary<string, DateTimeOffset>? activity = null) =>
        ClaudeAccountLimits.TrackedAccount(accounts, mode, activity ?? [])?.Id;

    [Fact]
    public void StoredValueRoundTripsAndUnknownValuesMeanAutomatic()
    {
        foreach (var mode in new[] { ClaudeTrackedAccountMode.Automatic, ClaudeTrackedAccountMode.DefaultAccount,
                     ClaudeTrackedAccountMode.Highest, ClaudeTrackedAccountMode.Account("dd1118a7") })
            Assert.Equal(mode, ClaudeTrackedAccountMode.FromStoredValue(mode.StoredValue));
        Assert.Equal(ClaudeTrackedAccountMode.Automatic, ClaudeTrackedAccountMode.FromStoredValue(null));
        Assert.Equal(ClaudeTrackedAccountMode.Automatic, ClaudeTrackedAccountMode.FromStoredValue("something-else"));
    }

    [Fact]
    public void AutomaticFollowsTheLatestPrompt()
    {
        var accounts = new[] { Account("default", 90, isDefault: true), Account("personal", 10) };
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("personal", Tracked(accounts, ClaudeTrackedAccountMode.Automatic, new() { ["default"] = now.AddMinutes(-1), ["personal"] = now }));
        Assert.Equal("default", Tracked(accounts, ClaudeTrackedAccountMode.Automatic, new() { ["default"] = now, ["personal"] = now.AddMinutes(-1) }));
        Assert.Equal("personal", Tracked(accounts, ClaudeTrackedAccountMode.Automatic, new() { ["personal"] = now }));
        Assert.Equal("default", Tracked(accounts, ClaudeTrackedAccountMode.Automatic));
    }

    [Fact]
    public void OnlyAccountsWithValuesCanBeTracked()
    {
        var accounts = new[] { Account("default", null, isDefault: true), Account("personal", 10) };
        Assert.Equal("personal", Tracked(accounts, ClaudeTrackedAccountMode.Automatic, new() { ["default"] = DateTimeOffset.UtcNow }));
        Assert.Equal("personal", Tracked(accounts, ClaudeTrackedAccountMode.DefaultAccount, new() { ["personal"] = DateTimeOffset.UtcNow }));
        Assert.Null(Tracked([], ClaudeTrackedAccountMode.Automatic));
    }

    [Fact]
    public void DefaultHighestAndPinnedModes()
    {
        var accounts = new[] { Account("default", 20, isDefault: true), Account("work", 10, 95), Account("personal", 60) };
        var recent = new Dictionary<string, DateTimeOffset> { ["personal"] = DateTimeOffset.UtcNow };
        Assert.Equal("default", Tracked(accounts, ClaudeTrackedAccountMode.DefaultAccount, recent));
        Assert.Equal("work", Tracked(accounts, ClaudeTrackedAccountMode.Highest, recent));
        Assert.Equal("work", Tracked(accounts, ClaudeTrackedAccountMode.Account("work"), recent));
        Assert.Equal("personal", Tracked(accounts, ClaudeTrackedAccountMode.Account("gone"), recent));
    }

    [Fact]
    public void HighestModeCountsPerModelWeeklyWindows()
    {
        var scoped = Full(10, 20, fable: 97, email: "work@example.com");
        var work = new ClaudeAccountLimits("work", "claude.work", "work", scoped, false, false);
        Assert.Equal([10.0, 20, 97], scoped.AllUtilizations);
        Assert.Equal("work", Tracked([Account("default", 90, isDefault: true), work], ClaudeTrackedAccountMode.Highest));
    }

    [Fact]
    public void LastPromptDateIsTheHistoryFileDate()
    {
        using var folder = new TempDir();
        Assert.Null(ClaudeAccountRoots.LastPromptDate(folder.Path));
        var file = Path.Combine(folder.Path, "history.jsonl");
        var stamp = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        ClaudeAccountRootsTests.WriteStamped(file, "{}\n", stamp);
        Assert.Equal(new DateTimeOffset(stamp), ClaudeAccountRoots.LastPromptDate(folder.Path));
        Assert.Null(ClaudeAccountRoots.InstalledLastPromptDate(folder.Path, isInstalledApp: false));
        Assert.NotNull(ClaudeAccountRoots.InstalledLastPromptDate(folder.Path, isInstalledApp: true));
    }

    [Fact]
    public void StaleMeansLoadedFreshOrNotOlderThanFifteenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var loaded = Account("personal", 10) with { UpdatedAt = now.AddMinutes(-16) };
        Assert.True(loaded.IsStale(now));
        Assert.False((loaded with { UpdatedAt = now.AddMinutes(-14) }).IsStale(now));
        Assert.False((Account("empty", null) with { UpdatedAt = now.AddHours(-1) }).IsStale(now));
        Assert.False((loaded with { IsExpired = true, UpdatedAt = now.AddHours(-1) }).IsStale(now));
    }
}

/// <summary>Port of ClaudePromptHistoryTests.</summary>
public sealed class ClaudePromptHistoryTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    [Fact]
    public void PromptsAreGroupedBySessionAndSorted()
    {
        const string text = """
            {"display":"b","timestamp":1790000060000,"sessionId":"s1","project":"/p"}
            {"display":"a","timestamp":1790000000000,"sessionId":"s1","project":"/p"}
            {"display":"c","timestamp":"2026-09-16T10:00:00Z","sessionId":"s2"}
            {"display":"old line without a session","timestamp":1790000000000}
            {"display":"bad timestamp","timestamp":0,"sessionId":"s3"}
            not json
            """;
        var prompts = ClaudePromptHistory.Parse(text);
        Assert.Equal(["s1", "s2"], prompts.Keys.Order());
        Assert.Equal([Stamp, Stamp.AddSeconds(60)], prompts["s1"]);
        Assert.Single(prompts["s2"]);
    }

    [Fact]
    public void TheHistoryIsReadAgainOnlyForItsNewLines()
    {
        using var folder = new TempDir();
        var file = Path.Combine(folder.Path, "history.jsonl");
        var stamp = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        static string Line(string session, long seconds = 1_790_000_000) =>
            "{\"timestamp\":" + seconds + "000,\"sessionId\":\"" + session + "\"}\n";
        void Write(string text) => ClaudeAccountRootsTests.WriteStamped(file, text, stamp);
        List<string> Sessions() => ClaudePromptHistory.Prompts(folder.Path).Keys.Order(StringComparer.Ordinal).ToList();

        Assert.Empty(Sessions());
        Write(Line("s1"));
        Assert.Equal(["s1"], Sessions());
        // Same size and date: the cached result is served even though the content differs.
        Write(Line("s9"));
        Assert.Equal(["s1"], Sessions());
        // Appended lines only: the first line is not parsed again (s9 stays unseen).
        var complete = Line("s9") + Line("s22") + Line("s1", 1_789_999_000);
        Write(complete);
        Assert.Equal(["s1", "s22"], Sessions());
        Assert.Equal([DateTimeOffset.FromUnixTimeSeconds(1_789_999_000), Stamp], ClaudePromptHistory.Prompts(folder.Path)["s1"]);
        // A line still being written waits for its line break.
        var partial = Line("s3");
        Write(complete + partial[..^5]);
        Assert.Equal(["s1", "s22"], Sessions());
        Write(complete + partial);
        Assert.Equal(["s1", "s22", "s3"], Sessions());
        // A shorter file was rewritten: read it all again.
        Write(Line("s4"));
        Assert.Equal(["s4"], Sessions());
        // So was a longer one that no longer breaks a line where the last read ended.
        Write("{\"display\":\"a longer first prompt\",\"timestamp\":1790000000000,\"sessionId\":\"s5\"}\n");
        Assert.Equal(["s5"], Sessions());
    }

    [Fact]
    public void NoHistoryIsReadOutsideTheApp()
    {
        using var folder = new TempDir();
        File.WriteAllText(Path.Combine(folder.Path, "history.jsonl"), "{\"timestamp\":1790000000000,\"sessionId\":\"s1\"}\n");
        Assert.Empty(ClaudePromptHistory.InstalledPrompts(folder.Path, isInstalledApp: false));
        Assert.Equal(["s1"], ClaudePromptHistory.InstalledPrompts(folder.Path, isInstalledApp: true).Keys);
    }
}

/// <summary>Port of ClaudeAccountUsageAttributionTests.</summary>
public sealed class ClaudeAccountUsageAttributionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
    private static DateTimeOffset At(double minutes) => T0.AddMinutes(minutes);

    private static List<DateTimeOffset> Times(params double[] minutes) => minutes.Select(At).ToList();

    private static readonly List<ClaudeAccountUsageAttribution.Account> Accounts =
    [
        new("default", new Dictionary<string, List<DateTimeOffset>>
        {
            ["work"] = Times(0), ["resumed"] = Times(0, 10), ["tie"] = Times(5), ["pingpong"] = Times(0, 40),
        }),
        new("personal", new Dictionary<string, List<DateTimeOffset>>
        {
            ["mine"] = Times(0), ["resumed"] = Times(20, 30), ["tie"] = Times(5), ["pingpong"] = Times(20),
        }),
    ];

    private static string? Owner(string session, double minutes) =>
        ClaudeAccountUsageAttribution.Owner(session, At(minutes), Accounts);

    private static UsageEntry Entry(string id, string? session, DateTimeOffset date, long tokens, string day,
        string model = "claude-sonnet-4-6") =>
        new() { Id = id, Date = date, LocalDay = day, Model = model, Input = tokens, SessionID = session };

    [Fact]
    public void ASessionBelongsToItsOnlyLogin()
    {
        Assert.Equal("default", Owner("work", 3));
        Assert.Equal("personal", Owner("mine", 3));
        Assert.Null(Owner("unknown", 3));
    }

    [Fact]
    public void AResumedSessionChangesOwnerAtTheFirstPromptOfTheOtherLogin()
    {
        Assert.Equal("default", Owner("resumed", 15));
        Assert.Equal("personal", Owner("resumed", 20));
        Assert.Equal("personal", Owner("resumed", 45));
        Assert.Equal("personal", Owner("pingpong", 30));
        Assert.Equal("default", Owner("pingpong", 45));
    }

    [Fact]
    public void ForkedTurnStaysWithOriginalAccountRegardlessOfScanOrder()
    {
        var forkAccounts = new List<ClaudeAccountUsageAttribution.Account>
        {
            new("original", new Dictionary<string, List<DateTimeOffset>> { ["source-session"] = Times(0) }),
            new("fork", new Dictionary<string, List<DateTimeOffset>> { ["fork-session"] = Times(10) }),
        };
        foreach (var order in new[] { false, true })
        {
            var source = Entry("same-turn", "source-session", At(5), 100, "2026-09-17");
            var replay = Entry("same-turn", "fork-session", At(15), 100, "2026-09-17");
            var deduped = LocalUsageReader.DedupKeepMax(order ? [replay, source] : [source, replay]);
            var result = ClaudeAccountUsageAttribution.Usage(deduped, forkAccounts, At(20), "2026-09-17", "2026-09-01");

            Assert.Single(deduped);
            Assert.Equal(At(5), deduped[0].Date);
            Assert.Equal(["fork-session", "source-session"], deduped[0].ClaudeSessionIDs);
            Assert.Equal(100, result.ByAccount["original"].TodayTokens);
            Assert.False(result.ByAccount.ContainsKey("fork"));
            Assert.Equal(0, result.Unattributed.TodayTokens);
        }
    }

    [Fact]
    public void EdgeCasesStayDeterministic()
    {
        Assert.Equal("default", Owner("resumed", -1));
        Assert.Equal("default", Owner("tie", 6));
    }

    [Fact]
    public void TotalsPerAccountTodayAndThisMonth()
    {
        var entries = new[]
        {
            Entry("1", "work", At(1), 100, "2026-09-17"),
            Entry("2", "work", At(1), 40, "2026-09-02"),
            Entry("3", "resumed", At(25), 7, "2026-09-17"),
            Entry("4", "unknown", At(1), 5, "2026-09-17"),
            Entry("5", null, At(1), 3, "2026-09-10"),
            Entry("6", "work", At(1), 1_000, "2026-08-31"),
        };
        var result = ClaudeAccountUsageAttribution.Usage(entries, Accounts, At(30), "2026-09-17", "2026-09-01");

        Assert.Equal(100, result.ByAccount["default"].TodayTokens);
        Assert.Equal(140, result.ByAccount["default"].MonthTokens);
        Assert.Equal(7, result.ByAccount["personal"].TodayTokens);
        Assert.Equal(5, result.Unattributed.TodayTokens);
        Assert.Equal(8, result.Unattributed.MonthTokens);
        Assert.Equal(["default", "personal"], result.ByAccount.Keys.Order());
        Assert.True(result.ByAccount["default"].MonthCost.Amount > 0);
        Assert.True(result.ByAccount["default"].MonthCost.Coverage.Estimated);

        var allKnown = ClaudeAccountUsageAttribution.Usage([entries[0]], Accounts, At(30), "2026-09-17", "2026-09-01");
        Assert.Equal(new ClaudeAccountUsage(), allKnown.Unattributed);
    }

    [Fact]
    public void EachAccountGetsItsOwnActiveBlock()
    {
        var entries = new[]
        {
            Entry("1", "work", At(1), 100, "2026-09-17"),
            Entry("2", "work", At(-240), 40, "2026-08-31"),
            Entry("3", "resumed", At(25), 7, "2026-09-17"),
            Entry("4", "unknown", At(1), 5, "2026-09-17"),
            Entry("5", "work", At(-400), 1_000, "2026-09-17"),
        };
        var result = ClaudeAccountUsageAttribution.Usage(entries, Accounts, At(30), "2026-09-17", "2026-09-01");
        Assert.Equal(140, result.ActiveBlocks["default"].TotalTokens);
        Assert.Equal(7, result.ActiveBlocks["personal"].TotalTokens);
        Assert.Equal(["default", "personal"], result.ActiveBlocks.Keys.Order());

        var later = ClaudeAccountUsageAttribution.Usage(entries, Accounts, At(600), "2026-09-17", "2026-09-01");
        Assert.Empty(later.ActiveBlocks);
    }

    [Fact]
    public void ABlockFollowsTheAccountsOfficialWindow()
    {
        var entries = new[]
        {
            Entry("1", "work", At(1), 100, "2026-09-17"),
            Entry("2", "work", At(-240), 40, "2026-08-31"),
            Entry("3", "resumed", At(25), 7, "2026-09-17"),
        };
        var reset = At(120);
        var result = ClaudeAccountUsageAttribution.Usage(entries, Accounts, At(30), "2026-09-17", "2026-09-01",
            new Dictionary<string, ClaudeAccountUsageAttribution.FiveHourWindow>
            {
                ["default"] = new ClaudeAccountUsageAttribution.FiveHourWindow.Running(reset),
                ["personal"] = new ClaudeAccountUsageAttribution.FiveHourWindow.NotStarted(),
            });
        var block = result.ActiveBlocks["default"];
        Assert.Equal(100, block.TotalTokens);
        Assert.Equal(reset, block.EndDate);
        Assert.False(result.ActiveBlocks.ContainsKey("personal"));
        Assert.Equal(7, result.ByAccount["personal"].TodayTokens);

        var ended = ClaudeAccountUsageAttribution.Usage(entries, Accounts, At(30), "2026-09-17", "2026-09-01",
            new Dictionary<string, ClaudeAccountUsageAttribution.FiveHourWindow>
            {
                ["default"] = new ClaudeAccountUsageAttribution.FiveHourWindow.Running(At(30)),
            });
        Assert.False(ended.ActiveBlocks.ContainsKey("default"));
        Assert.Equal(7, ended.ActiveBlocks["personal"].TotalTokens);

        var outside = ClaudeAccountUsageAttribution.Usage([entries[1]], Accounts, At(30), "2026-09-17", "2026-09-01",
            new Dictionary<string, ClaudeAccountUsageAttribution.FiveHourWindow>
            {
                ["default"] = new ClaudeAccountUsageAttribution.FiveHourWindow.Running(reset),
            });
        Assert.False(outside.ActiveBlocks.ContainsKey("default"));
    }

    [Fact]
    public void TotalsAddUp()
    {
        var total = new ClaudeAccountUsage(1, new UsageCost(0.5, CostCoverage.Source), 10, new UsageCost(2, CostCoverage.Source));
        total.Add(new ClaudeAccountUsage(2, new UsageCost(0.25, CostCoverage.Source), 20, new UsageCost(1, CostCoverage.Source)));
        Assert.Equal(3, total.TodayTokens);
        Assert.Equal(0.75, total.TodayCost.Amount);
        Assert.Equal(30, total.MonthTokens);
        Assert.Equal(3, total.MonthCost.Amount);
    }
}

/// <summary>Port of FiveHourNotStartedTests and LimitPaceTests (Claude parts), plus the plan label.</summary>
public sealed class LimitModelTests
{
    private static LimitWindow Window(string json) => System.Text.Json.JsonSerializer.Deserialize<LimitWindow>(json)!;

    [Fact]
    public void OnlyAnIdleWindowHasNotStarted()
    {
        Assert.True(Window("""{"utilization":0.0,"resets_at":null}""").HasNotStarted);
        Assert.True(Window("""{"utilization":0}""").HasNotStarted);
        Assert.False(Window("""{"utilization":0,"resets_at":"2026-09-17T16:00:00Z"}""").HasNotStarted);
        Assert.False(Window("""{"utilization":3,"resets_at":null}""").HasNotStarted);
        Assert.False(Window("""{"resets_at":null}""").HasNotStarted);
    }

    [Fact]
    public void WindowSpanFromOAuthLimitEntryKind()
    {
        Assert.Equal(LimitWindowSpan.FiveHour, new OAuthLimitEntry { Kind = "session" }.WindowSpan);
        Assert.Equal(LimitWindowSpan.SevenDay, new OAuthLimitEntry { Kind = "weekly_all" }.WindowSpan);
        Assert.Equal(LimitWindowSpan.SevenDay, new OAuthLimitEntry { Kind = "weekly_scoped" }.WindowSpan);
        Assert.Null(new OAuthLimitEntry().WindowSpan);
        Assert.Null(new OAuthLimitEntry { Kind = "monthly" }.WindowSpan);
        Assert.Equal(TimeSpan.FromMinutes(90), LimitWindowSpan.FromMinutes(90));
        Assert.Null(LimitWindowSpan.FromMinutes(0));
    }

    [Fact]
    public void PaceFractionTracksElapsedPortionOfWindow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var five = LimitWindowSpan.FiveHour;
        double? Pace(TimeSpan reset) => LimitPace.PaceFraction(now + reset, five, now);
        Assert.Equal(0, Pace(five)!.Value, 4);
        Assert.Equal(0.5, Pace(five / 2)!.Value, 4);
        Assert.Equal(0.75, Pace(five / 4)!.Value, 4);
        Assert.Equal(1, Pace(TimeSpan.Zero)!.Value, 4);
    }

    [Fact]
    public void PaceFractionRejectsWindowsOutsideItsOwnSpan()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var five = LimitWindowSpan.FiveHour;
        Assert.Null(LimitPace.PaceFraction(now.AddSeconds(-1), five, now));
        Assert.Null(LimitPace.PaceFraction(now + five + TimeSpan.FromSeconds(1), five, now));
        Assert.Null(LimitPace.PaceFraction(now + five / 2, TimeSpan.Zero, now));
        Assert.Null(LimitPace.PaceFraction(now + five / 2, -five, now));
    }

    [Fact]
    public void MarkerFractionMirrorsRemainingDisplayMode()
    {
        Assert.Equal(0.25, LimitPace.MarkerFraction(0.25, LimitPace.LimitDisplayMode.Used)!.Value, 4);
        Assert.Equal(0.75, LimitPace.MarkerFraction(0.25, LimitPace.LimitDisplayMode.Remaining)!.Value, 4);
        Assert.Equal(0, LimitPace.MarkerFraction(1, LimitPace.LimitDisplayMode.Remaining)!.Value, 4);
        Assert.Null(LimitPace.MarkerFraction(null, LimitPace.LimitDisplayMode.Used));
        Assert.Null(LimitPace.MarkerFraction(null, LimitPace.LimitDisplayMode.Remaining));
    }

    /// <summary>Port of ProgressDetailTests (the CompanionHeader helper).</summary>
    [Theory]
    [InlineData(37_200, 50_000, "37.2K / 50K · 74%")]
    [InlineData(60_000, 50_000, "50K / 50K · 100%")]
    [InlineData(0, 0, "0 / 0 · 0%")]
    [InlineData(999, 1_000, "999 / 1K · 99%")]
    public void ProgressDetail(long used, long total, string expected) =>
        Assert.Equal(expected, LimitPace.ProgressDetail(used, total));

    [Fact]
    public void PlanAndAccountLabels()
    {
        var status = new LimitStatus { SubscriptionType = "max", RateLimitTier = "default_claude_max_20x" };
        Assert.Equal("Max 20x", status.PlanDisplay);
        Assert.Equal("Pro", (status with { SubscriptionType = "pro", RateLimitTier = "default_claude_pro" }).PlanDisplay);
        Assert.Null(new LimitStatus().PlanDisplay);
        Assert.Equal("a@b.c · Team", new LimitStatus { AccountEmail = "a@b.c", AccountOrganizationName = "Team" }.AccountDisplay);
        Assert.Null(new LimitStatus { AccountOrganizationName = "Team" }.AccountDisplay);
    }

    [Fact]
    public void ScopedEntriesSkipTheLegacyWindowsUnlessTheyAreMissing()
    {
        var both = LimitStatus.Decode("""
            {"five_hour":{"utilization":1},"limits":[{"kind":"session","percent":1},{"kind":"weekly_scoped","percent":9}]}
            """u8);
        Assert.Equal(["weekly_scoped"], both.ScopedLimitEntries.Select(e => e.Kind));
        var newOnly = LimitStatus.Decode("""{"limits":[{"kind":"session","percent":1}]}"""u8);
        Assert.Equal(["session"], newOnly.ScopedLimitEntries.Select(e => e.Kind));
    }
}
