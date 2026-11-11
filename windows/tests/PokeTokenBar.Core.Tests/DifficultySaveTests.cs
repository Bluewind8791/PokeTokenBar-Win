using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of DifficultySaveTests.swift (store logic). Not ported: testDraftDoesNotApplyUntilSaveAndSavesBothControls
// (DifficultyDraft is SwiftUI settings glue) and testSaveButtonAppearsBelowEditedSlidersInEveryLanguage (view rendering).

file sealed class DifficultySaveProvider : IPokeProviding
{
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);

    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(new EvoLine(1,
        new EvoNode(1, [new EvoNode(2, [new EvoNode(3)])]), Rarity.Common, new Dictionary<int, Dictionary<string, string>>()));
}

public class DifficultySaveTests
{
    private static (CompanionStore Store, string Path, AppSettings Settings) Fixture(bool egg = false, double difficulty = 1,
        bool boost = false, double fraction = 0.5)
    {
        var state = new CompanionState
        {
            InstallBaselineSet = true,
            LastDate = "2026-09-11",
            ClaimedTodayTokensByProvider = new Dictionary<string, long> { ["test"] = 1234 },
            UsedSinceInstall = 900_000_000,
            SpentTokens = 123_000_000,
        };
        if (egg)
        {
            state.EggUsage = (long)(PokemonBalance.EggHatchThreshold * difficulty * fraction);
        }
        else
        {
            var mon = new MonState(1, [1], [1, 2, 3], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common,
                totalForms: 3, hasGrowthBoost: boost);
            mon.UsedAtStage = (long)(mon.PhaseThreshold * difficulty * fraction);
            state.Active = mon;
        }
        var path = StateFiles.Write(state, StateFiles.TempPath("difficulty-save"));
        var settings = new AppSettings(null);
        settings.Set("growthDifficulty", difficulty);
        settings.Set("shopDifficulty", 20.0);
        var store = new CompanionStore(new DifficultySaveProvider(), filePath: path, dittoDisguiseRollingEnabled: false,
            settings: settings);
        return (store, path, settings);
    }

    [Fact]
    public void SavedDifficultyPreservesEggAndActiveProgressAndActualUsage() => MainActor.Run(() =>
    {
        foreach (var egg in new[] { false, true })
        {
            foreach (var boost in new[] { false, true })
            {
                var (s, _, _) = Fixture(egg: egg, boost: boost);
                var ledger = new Dictionary<string, long>(s.State.ClaimedTodayTokensByProvider!);
                var wallet = s.AvailableTokens;
                var profile = s.State.Active?.Profile?.Clone();
                foreach (var difficulty in new[] { 0.1, 2.0, 0.5, 1.0 })
                {
                    s.SetGrowthDifficulty(difficulty);
                    Assert.Equal(0.5, egg ? s.EggProgress : s.Progress, 6);
                    Assert.Equal(egg ? null : 0, s.State.Active?.StageIndex);
                    Assert.Equal(profile, s.State.Active?.Profile);
                    Assert.Equal(900_000_000, s.State.UsedSinceInstall);
                    Assert.Equal(123_000_000, s.State.SpentTokens);
                    Assert.Equal(wallet, s.AvailableTokens);
                    Assert.Equal(ledger, s.State.ClaimedTodayTokensByProvider);
                    Assert.Equal("2026-09-11", s.State.LastDate);
                    Assert.Empty(s.State.Dex);
                }
            }
        }
    });

    [Fact]
    public void LegacyRangeMigrationPreservesFractionAndDoesNotRepeatOnRestart() => MainActor.Run(() =>
    {
        foreach (var old in new[] { 0.0001, 0.01, 20.0 })
        {
            foreach (var egg in new[] { false, true })
            {
                var (s, path, settings) = Fixture(egg: egg, difficulty: old, boost: true);
                var expected = PokemonBalance.ClampDifficulty(old);
                Assert.Equal(expected, s.GrowthDifficulty);
                Assert.Equal(expected, settings.GetDouble("growthDifficulty"));
                Assert.Equal(2, s.ShopDifficulty);
                Assert.Equal(2, settings.GetDouble("shopDifficulty"));
                Assert.Equal(0.5, egg ? s.EggProgress : s.Progress, 6);
                var activeCredits = s.State.Active?.UsedAtStage;
                var eggCredits = s.State.EggUsage;
                var profile = s.State.Active?.Profile?.Clone();
                var again = new CompanionStore(new DifficultySaveProvider(), filePath: path, settings: settings);
                Assert.Equal(activeCredits, again.State.Active?.UsedAtStage);
                Assert.Equal(eggCredits, again.State.EggUsage);
                Assert.Equal(profile, again.State.Active?.Profile);
                Assert.Equal(900_000_000, again.State.UsedSinceInstall);
                Assert.Empty(again.State.Dex);
            }
        }
    });

    [Fact]
    public void NewRangeClampsBothControls() => MainActor.Run(() =>
    {
        var (s, _, _) = Fixture();
        s.SetGrowthDifficulty(0.001);
        s.SetShopDifficulty(10);
        Assert.Equal(0.1, s.GrowthDifficulty);
        Assert.Equal(2, s.ShopDifficulty);
        s.SetGrowthDifficulty(20);
        s.SetShopDifficulty(0.01);
        Assert.Equal(2, s.GrowthDifficulty);
        Assert.Equal(0.1, s.ShopDifficulty);
    });
}
