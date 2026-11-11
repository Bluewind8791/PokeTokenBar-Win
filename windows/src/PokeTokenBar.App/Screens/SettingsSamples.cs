using System.IO;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Sample stores for the Settings and recap renders: like <see cref="Services"/>, plus a stored session
/// key (fake — the user's key file is never read), a year of usage history in the ledger, Rotom
/// graduations this week and a pinned representative, mirroring assets/settings.png and
/// assets/screenshot-usage-recap.png. Settings stay in memory and the save lives in a temp file, so a
/// render never writes the registry, the user's settings or the user's save.
/// </summary>
public static partial class SampleData
{
    /// <summary>The popover on sample Settings/recap stores, after a navigation route and an optional preset.</summary>
    public static System.Windows.FrameworkElement SettingsPopover(RenderHarness.Context c, Action<PopoverNavigation> route,
        bool sessionKey = false, Action<AppServices>? preset = null)
    {
        var s = SettingsServices(c, sessionKey);
        route(s.Nav);
        preset?.Invoke(s);
        return PopoverRoot.Build(s);
    }

    public static AppServices SettingsServices(RenderHarness.Context c, bool sessionKey = false)
    {
        // Renders never walk the disk for the scan-folder match count.
        SettingsState.CountScanMatches = (_, raw) => raw.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        var settings = new AppSettings(null);
        settings.Set("showLimitInMenu", true);
        settings.Set(CustomScanRoots.DefaultsKey("claude_code"), "~/work/claude-archive,\n~/Dropbox/*/claude/projects");
        SeedLedger(settings);
        var store = new UsageStore(
            providers: [new FakeProvider()],
            claudeLimitsProvider: new FakeLimits(),
            discoverClaudeConfigDirs: () => [],
            readDefaultIdentity: () => null,
            readLastPrompt: _ => null,
            claudeUsageEntries: _ => Task.FromResult<IReadOnlyList<UsageEntry>>([]),
            readPromptHistory: _ => new(),
            statusProvider: new NoStatus(),
            sessionKeys: new FakeSessionKeys(sessionKey),
            accountSessionKeys: _ => new FakeSessionKeys(false),
            autoRefresh: false,
            settings: settings);
        var companion = new CompanionStore(filePath: WriteSettingsSave(c.Language), settings: settings);
        companion.SetLanguage(c.Language);
        var updater = new UpdateChecker(currentVersion: "1.0.0", settings: settings,
            fetchLatest: () => Task.FromResult<UpdateChecker.LatestRelease?>(null));
        store.LocalizationLanguage = c.Language;
        Pump(store.Refresh());
        // Two restore points in the temp save's own snapshot folder, for the expanded history.
        companion.CreateManualSnapshot(Now.AddDays(-3));
        companion.CreateManualSnapshot(Now.AddHours(-20));
        return new AppServices { Store = store, Companion = companion, Updater = updater, Nav = new PopoverNavigation() };
    }

    /// <summary>~14 months of daily totals (a weekday rhythm), so every scope and its comparison have data.</summary>
    private static void SeedLedger(AppSettings settings)
    {
        var ledger = new System.Text.Json.Nodes.JsonObject();
        var today = Now.Date;
        var start = today.AddDays(-430);
        long[] rhythm = [1_400_000, 1_250_000, 1_100_000, 950_000, 700_000, 0, 500_000];
        var rng = new Random(7);
        for (var d = start; d < today; d = d.AddDays(1))
        {
            var tokens = rhythm[(int)d.DayOfWeek] * (70 + rng.Next(60)) / 100;
            // Last week ran hot, so this week's chip reads as a drop like the reference.
            if ((today - d).TotalDays is >= 4 and <= 10) tokens = tokens * 3;
            if (tokens > 0) ledger[d.ToString("yyyy-MM-dd")] = tokens;
        }
        settings.SetNode(UsageLedger.DefaultsKey, ledger);
        settings.Set(UsageLedger.CoverageKey, start.ToString("yyyy-MM-dd"));
    }

    private static string WriteSettingsSave(AppLanguage lang)
    {
        var rotomNames = new Dictionary<int, Dictionary<string, string>>
        {
            [479] = new() { ["en"] = "Rotom", ["ko"] = "로토무", ["ja-hrkt"] = "ロトム", ["ja"] = "ロトム", ["de"] = "Rotom", ["fr"] = "Motisma", ["es"] = "Rotom", ["pt-br"] = "Rotom" },
        };
        var heat = new PokemonForm("heat");
        var wash = new PokemonForm("wash");
        var state = new CompanionState
        {
            InstallBaselineSet = true,
            UsedSinceInstall = 9_000_000_000,
            SpentTokens = 1_000_000_000,
            LastDate = LocalUsageReader.LocalDay(Now),
            Language = lang,
            Active = new MonState(479, [479], stageIndex: 0, usedAtStage: 250_000_000, rarity: Rarity.Rare,
                totalForms: 1, nature: PokemonNature.Hardy, form: heat),
            RepresentativeSpeciesID = 479,
            RepresentativeForm = heat,
        };
        // This week's graduations (newest first in the strip), plus older ones outside it.
        var weekStart = new RecapPeriod(RecapScope.Week, Now, RecapCalendar.Current()).Start;
        DateTimeOffset InWeek(double hours) => Min(weekStart.AddHours(hours), Now.AddMinutes(-5));
        state.Dex.Add(new DexEntry(479, 479, [479], Rarity.Rare, InWeek(10), names: rotomNames, form: heat, nature: PokemonNature.Bold));
        state.Dex.Add(new DexEntry(479, 479, [479], Rarity.Rare, InWeek(30), names: rotomNames, form: heat, nature: PokemonNature.Calm));
        state.Dex.Add(new DexEntry(479, 479, [479], Rarity.Rare, InWeek(40), isShiny: true, names: rotomNames, form: wash, nature: PokemonNature.Adamant));
        state.Dex.Add(new DexEntry(25, 26, [25, 26], Rarity.Uncommon, Now.AddDays(-12), nature: PokemonNature.Bold));
        state.Dex.Add(new DexEntry(147, 149, [147, 148, 149], Rarity.Rare, Now.AddDays(-40), nature: PokemonNature.Calm));
        var path = Path.Combine(Path.GetTempPath(), $"ptb-sample-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, CompanionJson.Serialize(state));
        return path;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>A stored key (or none) without the user's DPAPI file; saving and clearing stay in memory.</summary>
    private sealed class FakeSessionKeys(bool configured) : ISessionKeyManaging
    {
        private SessionKeyCredential? _credential = configured ? new SessionKeyCredential("sk-ant-sid-sample", "org-sample") : null;
        public SessionKeyCredential? Credential() => _credential;
        public Task<List<SessionKeyOrganization>> Organizations(string sessionKey) =>
            Task.FromResult(new List<SessionKeyOrganization> { new("org-sample", "Sample", true, new LimitStatus()) });
        public void Save(string key, string? organizationID) => _credential = new SessionKeyCredential(key, organizationID);
        public void Clear() => _credential = null;
    }
}
