using System.IO;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Sample stores for <see cref="RenderHarness"/>: fake usage, fake limits, an in-memory settings store
/// and a companion save in a temp file. Nothing touches the user's logs, credentials or save.
/// Pokémon names/types and sprites come from PokéAPI (cached), like the real app.
/// The numbers mirror the macOS README screenshot (assets/screenshot-home.png).
/// </summary>
public static partial class SampleData
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Now;

    public static AppServices Services(RenderHarness.Context c)
    {
        var settings = new AppSettings(null);
        settings.Set("showLimitInMenu", true);
        var store = new UsageStore(
            providers: [new FakeProvider()],
            claudeLimitsProvider: new FakeLimits(),
            discoverClaudeConfigDirs: () => [],
            readDefaultIdentity: () => null,
            readLastPrompt: _ => null,
            claudeUsageEntries: _ => Task.FromResult<IReadOnlyList<UsageEntry>>([]),
            readPromptHistory: _ => new(),
            statusProvider: new NoStatus(),
            autoRefresh: false,
            settings: settings);
        var companion = new CompanionStore(filePath: WriteSave(c.Language), settings: settings);
        companion.SetLanguage(c.Language);
        var updater = new UpdateChecker(currentVersion: "1.0.0", settings: settings,
            fetchLatest: () => Task.FromResult<UpdateChecker.LatestRelease?>(null));
        store.LocalizationLanguage = c.Language;
        Pump(store.Refresh());
        return new AppServices { Store = store, Companion = companion, Updater = updater, Nav = new PopoverNavigation() };
    }

    /// <summary>Waits for a task on the UI thread without deadlocking its continuations (they post back here).</summary>
    public static void Pump(Task task, double timeoutSeconds = 15)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSeconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static string WriteSave(AppLanguage lang)
    {
        var state = new CompanionState
        {
            InstallBaselineSet = true,
            UsedSinceInstall = 9_000_000_000,
            SpentTokens = 1_000_000_000,
            LastDate = LocalUsageReader.LocalDay(Now),
            Language = lang,
            Active = new MonState(479, [479], stageIndex: 0, usedAtStage: 250_000_000, rarity: Rarity.Rare,
                totalForms: 1, nature: PokemonNature.Hardy, form: null),
        };
        state.Dex.Add(new DexEntry(25, 26, [25, 26], Rarity.Uncommon, Now.AddDays(-12), nature: PokemonNature.Bold));
        state.Dex.Add(new DexEntry(4, 6, [4, 5, 6], Rarity.Common, Now.AddDays(-30), isShiny: true, nature: PokemonNature.Adamant));
        state.Dex.Add(new DexEntry(147, 149, [147, 148, 149], Rarity.Rare, Now.AddDays(-5), nature: PokemonNature.Calm));
        var path = Path.Combine(Path.GetTempPath(), $"ptb-sample-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, CompanionJson.Serialize(state));
        return path;
    }

    private sealed class FakeProvider : IUsageProvider
    {
        public string Id => "claude_code";
        public string DisplayName => "Claude Code";

        private static DailyUsage Day(string date, long tokens) =>
            new(date, tokens / 4, tokens / 4, tokens / 4, tokens / 4, tokens, tokens * 3.2e-6, null, CostCoverage.Estimate);

        public Task<DailyUsage?> FetchDaily() =>
            Task.FromResult<DailyUsage?>(Day(LocalUsageReader.LocalDay(Now), 1_980_000));

        public Task<ProviderEnrichment> FetchEnrichment()
        {
            var monthStart = LocalUsageReader.StartOfMonth(Now);
            var days = new List<DailyUsage>();
            var shape = new long[] { 0, 1_200_000, 1_900_000, 2_600_000, 2_300_000, 900_000, 0, 1_800_000, 1_700_000, 2_400_000, 1_100_000, 1_600_000, 0, 2_100_000, 2_800_000, 1_980_000 };
            var i = 0;
            for (var d = monthStart; d <= Now; d = d.AddDays(1), i++)
                days.Add(Day(LocalUsageReader.LocalDay(d), shape[i % shape.Length]));
            days[^1] = Day(days[^1].Date, 1_980_000);
            var month = days.Sum(x => x.TotalTokens);
            return Task.FromResult(new ProviderEnrichment
            {
                ActiveBlock = new BlockUsage("block-sample", Iso8601Parser.Format(Now.AddHours(-2)), Iso8601Parser.Format(Now.AddHours(3)),
                    true, 860_000, 2.75, 7_100, CostCoverage.Estimate),
                BlocksOK = true,
                WeekTotal = new PeriodUsage("w", 12_200_000, 39.10, CostCoverage.Estimate),
                MonthTotal = new PeriodUsage(LocalUsageReader.MonthKey(Now), month, month * 3.2e-6, CostCoverage.Estimate),
                MonthDaily = days,
                PeriodsOK = true,
                LastUsage = Now,
            });
        }
    }

    private sealed class FakeLimits : IClaudeLimitsProviding
    {
        public Task<LimitStatus> Fetch(bool allowKeychainPrompt) => Task.FromResult(new LimitStatus
        {
            FiveHour = new LimitWindow { Utilization = 42, ResetsAt = Iso8601Parser.Format(Now.AddHours(2.4)) },
            SevenDay = new LimitWindow { Utilization = 61, ResetsAt = Iso8601Parser.Format(Now.AddDays(2.5)) },
            SubscriptionType = "max",
        });
    }

    private sealed class NoStatus : IProviderStatusProviding
    {
        public Task<Dictionary<string, ProviderStatus>> Fetch() => Task.FromResult(new Dictionary<string, ProviderStatus>());
    }
}
