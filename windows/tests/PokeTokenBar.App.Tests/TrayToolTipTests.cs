using System.IO;
using PokeTokenBar.App;
using PokeTokenBar.Core;
using Xunit.Abstractions;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// The tray tooltip stands in for the macOS menu-bar text (#388): the menu-bar lines, then the companion's growth as
/// its own last line, so the 127-character cut never reaches the usage and limit lines before the growth.
/// </summary>
public class TrayToolTipTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GrowthIsTheLastLineAfterTheUsageAndLimitLines()
    {
        var (store, companion, path) = await Stores();
        try
        {
            Feed(companion, 0);
            Feed(companion, companion.EggHatchThreshold * 2 / 5);
            var usage = $"{TokenFormatter.Compact(1_200_000)} · {store.TodayUsageCost.Text(new L(AppLanguage.En), compact: true)}";
            Assert.Equal($"PokeTokenBar\n{usage}\nClaude 40%\nHatch 40%", AppController.ToolTip(store, companion));
            output.WriteLine("egg:\n" + AppController.ToolTip(store, companion) + "\n");

            await companion.Hatch(1);
            companion.ApplyUsage(companion.Threshold / 2);
            Assert.Equal($"PokeTokenBar\n{usage}\nClaude 40%\nGrowth 50%", AppController.ToolTip(store, companion));
            output.WriteLine("raising:\n" + AppController.ToolTip(store, companion) + "\n");

            companion.ApplyUsage(companion.TokensToNext);
            companion.ApplyUsage(companion.TokensToNext);
            companion.ApplyUsage(companion.Threshold * 97 / 100);
            Assert.True(companion.IsFinalStage);
            Assert.EndsWith("\nGrowth 97%", AppController.ToolTip(store, companion));
            output.WriteLine("near graduation:\n" + AppController.ToolTip(store, companion) + "\n");

            // Every language fits the 127-character tooltip with every item on: nothing is cut.
            foreach (var language in Enum.GetValues<AppLanguage>())
            {
                companion.SetLanguage(language);
                store.LocalizationLanguage = language;
                var text = AppController.ToolTip(store, companion);
                Assert.True(text.Length <= 127, $"{language}: {text.Length}");
                output.WriteLine($"{language} ({text.Length} chars): {text.Replace("\n", " | ")}");
            }

            store.ShowGrowthInMenu = false;
            Assert.Equal($"PokeTokenBar\n{usage}\nClaude 40%", AppController.ToolTip(store, companion));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Saved growth shows before the first usage refresh, instead of the dash.</summary>
    [Fact]
    public async Task GrowthReplacesTheDashBeforeTheFirstRefresh()
    {
        var (store, companion, path) = await Stores(refresh: false);
        try
        {
            Assert.Equal("PokeTokenBar\nHatch 0%", AppController.ToolTip(store, companion));
            store.ShowGrowthInMenu = false;
            Assert.Equal("PokeTokenBar\n—", AppController.ToolTip(store, companion));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Feed(CompanionStore companion, long credits) =>
        companion.Update(new Dictionary<string, long> { ["claude_code"] = credits }, "d", 0, BurnTier.Idle, false, true);

    private static async Task<(UsageStore, CompanionStore, string)> Stores(bool refresh = true)
    {
        var settings = new AppSettings(null);
        var path = Path.Combine(Path.GetTempPath(), $"ptb-tooltip-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, CompanionJson.Serialize(new CompanionState { Language = AppLanguage.En }));
        var companion = new CompanionStore(provider: new LinePoke(), filePath: path, dittoDisguiseRollingEnabled: false,
            settings: settings);
        var store = new UsageStore(
            providers: [new TodayProvider()],
            claudeLimitsProvider: new Limits(),
            discoverClaudeConfigDirs: () => [],
            readDefaultIdentity: () => null,
            readLastPrompt: _ => null,
            claudeUsageEntries: _ => Task.FromResult<IReadOnlyList<UsageEntry>>([]),
            readPromptHistory: _ => new(),
            statusProvider: new NoStatus(),
            sessionKeys: new NoSessionKey(),
            accountSessionKeys: _ => new NoSessionKey(),
            autoRefresh: false,
            settings: settings);
        store.LocalizationLanguage = AppLanguage.En;
        store.ShowTokensInMenu = true;
        store.ShowCostInMenu = true;
        store.ShowLimitInMenu = true;
        store.ShowGrowthInMenu = true;
        if (refresh) await store.Refresh(scheduleEmptyRetry: false);
        return (store, companion, path);
    }

    private sealed class TodayProvider : IUsageProvider
    {
        public string Id => "claude_code";
        public string DisplayName => "Claude Code";
        public Task<DailyUsage?> FetchDaily() =>
            Task.FromResult<DailyUsage?>(new DailyUsage(LocalUsageReader.TodayKey(), 0, 0, 0, 0, 1_200_000, 3.45));
        public Task<ProviderEnrichment> FetchEnrichment() => Task.FromResult(new ProviderEnrichment());
    }

    private sealed class Limits : IClaudeLimitsProviding
    {
        public Task<LimitStatus> Fetch(bool allowKeychainPrompt) => Task.FromResult(new LimitStatus
        {
            FiveHour = new LimitWindow { Utilization = 40, ResetsAt = "2099-01-01T00:00:00Z" },
        });
    }

    private sealed class NoStatus : IProviderStatusProviding
    {
        public Task<Dictionary<string, ProviderStatus>> Fetch() => Task.FromResult(new Dictionary<string, ProviderStatus>());
    }

    private sealed class NoSessionKey : ISessionKeyManaging
    {
        public SessionKeyCredential? Credential() => null;
        public Task<List<SessionKeyOrganization>> Organizations(string sessionKey) => Task.FromResult(new List<SessionKeyOrganization>());
        public void Save(string key, string? organizationID) { }
        public void Clear() { }
    }

    /// <summary>One three-stage line, 1 → 2 → 3.</summary>
    private sealed class LinePoke : IPokeProviding
    {
        public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(new EvoLine(1,
            new EvoNode(1, [new EvoNode(2, [new EvoNode(3)])]), Rarity.Common,
            new Dictionary<int, Dictionary<string, string>>()));
        public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(1, 255)]);
    }
}
