using System.IO;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>Home render variants (<c>--render home-*</c>).</summary>
public enum HomeSample
{
    /// <summary>The README screenshot numbers, with a loaded evolution line and types.</summary>
    Full,
    /// <summary>Nothing used today, history this month (Swift <c>testQuietDayKeepsHistoricalGraphVisible</c>).</summary>
    QuietDay,
    /// <summary>No usage at all this month and a fresh egg (<c>testEmptyHistoryStillShowsZeroPeriodSummaries</c>).</summary>
    Empty,
    /// <summary>Update banner, status-page incident, expired login notice, growth boost and a branching line.</summary>
    Alerts,
}

/// <summary>
/// Sample stores for the Home variants. Same isolation as <see cref="Services"/>: fake usage, fake limits,
/// an in-memory settings store and a temp save; evolution lines, types and type names come from in-process
/// fakes, so the header renders without PokéAPI (sprites still come from the sprite cache/PokéAPI).
/// </summary>
public static partial class SampleData
{
    public static AppServices HomeServices(RenderHarness.Context c, HomeSample sample)
    {
        var settings = new AppSettings(null);
        settings.Set("showLimitInMenu", true);
        IUsageProvider provider = sample switch
        {
            HomeSample.QuietDay => new QuietProvider(history: true),
            HomeSample.Empty => new QuietProvider(history: false),
            _ => new FakeProvider(),
        };
        IClaudeLimitsProviding limits = sample == HomeSample.Alerts ? new ExpiredLimits() : new FakeLimits();
        var store = new UsageStore(
            providers: [provider],
            claudeLimitsProvider: limits,
            discoverClaudeConfigDirs: () => [],
            readDefaultIdentity: () => null,
            readLastPrompt: _ => null,
            claudeUsageEntries: _ => Task.FromResult<IReadOnlyList<UsageEntry>>([]),
            readPromptHistory: _ => new(),
            statusProvider: new IncidentStatus(sample == HomeSample.Alerts),
            autoRefresh: false,
            settings: settings);
        var poke = new FakePoke();
        var companion = new CompanionStore(provider: poke, detailProvider: poke, filePath: WriteHomeSave(c.Language, sample),
            settings: settings, formNameLoader: FakePoke.TypeNames);
        companion.SetLanguage(c.Language);
        var updater = new UpdateChecker(currentVersion: "1.0.0", settings: settings,
            fetchLatest: () => Task.FromResult<UpdateChecker.LatestRelease?>(null));
        if (sample == HomeSample.Alerts)
            updater.Consider("v2.6.0", "https://github.com/chattymin/PokeTokenBar/releases/tag/v2.6.0");
        store.LocalizationLanguage = c.Language;
        Pump(store.Refresh());
        // Without a baseline yet this only loads the raised Pokémon's line (no usage is applied).
        companion.Update(new Dictionary<string, long>(), LocalUsageReader.TodayKey(), 0, BurnTier.Idle, false, false);
        return new AppServices { Store = store, Companion = companion, Updater = updater, Nav = new PopoverNavigation() };
    }

    private static string WriteHomeSave(AppLanguage lang, HomeSample sample)
    {
        var state = new CompanionState
        {
            UsedSinceInstall = 9_000_000_000,
            SpentTokens = 1_000_000_000,
            LastDate = LocalUsageReader.LocalDay(Now),
            Language = lang,
            Active = sample switch
            {
                HomeSample.Empty => null,
                HomeSample.QuietDay => new MonState(129, [129, 130], stageIndex: 1, usedAtStage: 170_100_000,
                    rarity: Rarity.Common, totalForms: 2, nature: PokemonNature.Hardy),
                HomeSample.Alerts => new MonState(133, [133], stageIndex: 0, usedAtStage: 40_000_000,
                    rarity: Rarity.Uncommon, totalForms: 2, nature: PokemonNature.Calm, hasGrowthBoost: true),
                _ => new MonState(479, [479], stageIndex: 0, usedAtStage: 250_000_000, rarity: Rarity.Rare,
                    totalForms: 1, nature: PokemonNature.Hardy),
            },
        };
        state.Dex.Add(new DexEntry(129, 130, [129, 130], Rarity.Common, Now.AddDays(-12), nature: PokemonNature.Bold));
        state.Dex.Add(new DexEntry(133, 134, [133, 134], Rarity.Uncommon, Now.AddDays(-5), nature: PokemonNature.Calm));
        var path = Path.Combine(Path.GetTempPath(), $"ptb-home-sample-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, CompanionJson.Serialize(state));
        return path;
    }

    /// <summary>No usage today; with history, each earlier day of the month has some (Swift <c>QuietHomeProvider</c>).</summary>
    private sealed class QuietProvider(bool history) : IUsageProvider
    {
        public string Id => "claude_code";
        public string DisplayName => "Claude Code";
        public bool ReportsCost => false;

        public Task<DailyUsage?> FetchDaily() => Task.FromResult<DailyUsage?>(null);

        public Task<ProviderEnrichment> FetchEnrichment()
        {
            var monthStart = LocalUsageReader.StartOfMonth(Now);
            var day = Now.Day;
            var series = Enumerable.Range(0, day).Select(offset =>
            {
                var tokens = history && offset < day - 1 ? (offset % 6 + 1) * 230_000L : 0;
                return new DailyUsage(LocalUsageReader.LocalDay(monthStart.AddDays(offset)), tokens, 0, 0, 0, tokens, 0);
            }).ToList();
            var weekKey = LocalUsageReader.LocalDay(LocalUsageReader.StartOfWeek(Now));
            return Task.FromResult(new ProviderEnrichment
            {
                BlocksOK = true,
                WeekTotal = new PeriodUsage(weekKey, series.Where(d => string.CompareOrdinal(d.Date, weekKey) >= 0).Sum(d => d.TotalTokens), 0),
                MonthTotal = new PeriodUsage(LocalUsageReader.MonthKey(Now), series.Sum(d => d.TotalTokens), 0),
                MonthDaily = series,
                PeriodsOK = true,
            });
        }
    }

    private sealed class ExpiredLimits : IClaudeLimitsProviding
    {
        public Task<LimitStatus> Fetch(bool allowKeychainPrompt) => Task.FromException<LimitStatus>(LimitsError.Http(401));
    }

    private sealed class IncidentStatus(bool incident) : IProviderStatusProviding
    {
        public Task<Dictionary<string, ProviderStatus>> Fetch() => Task.FromResult(incident
            ? new Dictionary<string, ProviderStatus> { ["claude_code"] = new(ProviderStatusIndicator.Minor, "Elevated errors on Claude Opus") }
            : new Dictionary<string, ProviderStatus>());
    }

    /// <summary>In-process PokéAPI stand-in: the few lines, details and type names the Home samples raise.</summary>
    private sealed class FakePoke : IPokeProviding, IPokemonDetailProviding
    {
        private static Dictionary<string, string> Names(string en, string ko, string ja) => new() { ["en"] = en, ["ko"] = ko, ["ja-hrkt"] = ja };

        public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(baseSpeciesID switch
        {
            129 => new EvoLine(129, new EvoNode(129, [new EvoNode(130)]), Rarity.Common, new Dictionary<int, Dictionary<string, string>>
                { [129] = Names("Magikarp", "잉어킹", "コイキング"), [130] = Names("Gyarados", "갸라도스", "ギャラドス") }),
            133 => new EvoLine(133, new EvoNode(133, [new EvoNode(134), new EvoNode(135), new EvoNode(136), new EvoNode(196), new EvoNode(197)]),
                Rarity.Uncommon, new Dictionary<int, Dictionary<string, string>>
                { [133] = Names("Eevee", "이브이", "イーブイ"), [134] = Names("Vaporeon", "샤미드", "シャワーズ") }),
            _ => new EvoLine(479, new EvoNode(479), Rarity.Rare, new Dictionary<int, Dictionary<string, string>>
                { [479] = Names("Rotom", "로토무", "ロトム") }),
        });

        public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);

        public Task<PokemonDetails> PokemonDetails(int speciesID) => Task.FromResult(new PokemonDetails(speciesID, "",
            10, 100, null, -1, speciesID switch { 479 => ["electric", "ghost"], 130 => ["water", "flying"], 129 or 134 => ["water"], _ => ["normal"] },
            new Dictionary<string, int>(), [], []));

        public static Task<Dictionary<string, string>?> TypeNames(PokemonNameResource resource) =>
            Task.FromResult<Dictionary<string, string>?>(resource.Name switch
            {
                "electric" => Names("Electric", "전기", "でんき"),
                "ghost" => Names("Ghost", "고스트", "ゴースト"),
                "water" => Names("Water", "물", "みず"),
                "flying" => Names("Flying", "비행", "ひこう"),
                "normal" => Names("Normal", "노말", "ノーマル"),
                _ => null,
            });
    }
}
