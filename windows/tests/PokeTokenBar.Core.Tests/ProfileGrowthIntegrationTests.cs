using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of ProfileGrowthIntegrationTests.swift.

file sealed class GrowthProfileProvider(int forms = 3) : IPokeProviding, IPokemonDetailProviding
{
    public Task<EvoLine> Line(int baseSpeciesID)
    {
        var node = new EvoNode(forms);
        for (var id = forms - 1; id >= 1; id--) node = new EvoNode(id, [node]);
        var names = Enumerable.Range(1, forms).ToDictionary(id => id, id => new Dictionary<string, string> { ["en"] = $"P{id}" });
        return Task.FromResult(new EvoLine(1, node, Rarity.Common, names));
    }

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);

    public Task<PokemonDetails> PokemonDetails(int speciesID) => Task.FromResult(new PokemonDetails(
        speciesID, "p", 1, 1, null, -1, [], new Dictionary<string, int>(), [],
        [new PokemonMoveOption($"final-{speciesID}", [new PokemonMoveLearnMethod("level-up", 100)])]));
}

public sealed class ProfileGrowthIntegrationTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private (CompanionStore Store, string File, AppSettings Settings) Fixture(CompanionState? state = null,
        double difficulty = 1, int forms = 3)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"profile-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var file = Path.Combine(dir, "state.json");
        StateFiles.Write(state ?? new CompanionState(), file);
        var settings = new AppSettings(null);
        settings.Set("growthDifficulty", difficulty);
        return (new CompanionStore(new GrowthProfileProvider(forms), filePath: file, dittoDisguiseRollingEnabled: false,
            settings: settings), file, settings);
    }

    [Fact]
    public void EveryDifficultyAndRepeatHatchGraduatesAtLevel100() => MainActor.Run(async () =>
    {
        foreach (var difficulty in new[] { 0.1, 1, 2 })
        {
            for (var forms = 1; forms <= 3; forms++)
            {
                var (s, _, _) = Fixture(difficulty: difficulty, forms: forms);
                foreach (var repeated in new[] { false, true })
                {
                    await s.Hatch(1);
                    Assert.Equal(repeated, s.State.Active?.HasGrowthBoost);
                    Assert.Equal(5, s.State.Active?.Profile?.Level);
                    await s.LoadPokemonDetails(forms);
                    for (var i = 0; i < forms; i++) s.ApplyUsage(s.TokensToNext);
                    Assert.Null(s.State.Active);
                    Assert.Equal(100, s.State.Dex[^1].Profile?.Level);
                    Assert.Equal(750_000_000, s.State.Dex[^1].Profile?.GrowthTokens);
                    Assert.Equal([$"final-{forms}"], s.State.Dex[^1].Profile!.Moves.Select(m => m.Name));
                }
            }
        }
    });

    [Fact]
    public void DifficultyChangesAndRestartNeverUndoEarnedLevels() => MainActor.Run(async () =>
    {
        var seed = new CompanionState { CollectedFinals = ["1:1"] };
        var (s, file, settings) = Fixture(seed, difficulty: 0.5, forms: 1);
        await s.Hatch(1);
        s.ApplyUsage(93_750_000);
        var profile = s.State.Active?.Profile?.Clone();
        Assert.NotNull(profile);
        Assert.Equal(52, profile!.Level);
        s.SetGrowthDifficulty(2);
        Assert.Equal(52, s.State.Active?.Profile?.Level);
        var reloaded = new CompanionStore(new GrowthProfileProvider(1), filePath: file, settings: settings);
        Assert.Equal(profile.InstanceID, reloaded.State.Active?.Profile?.InstanceID);
        Assert.Equal(profile.Ivs, reloaded.State.Active?.Profile?.Ivs);
        Assert.Equal(52, reloaded.State.Active?.Profile?.Level);
        s.SetGrowthDifficulty(0.1);
        Assert.Equal(0.5, s.Progress, 6);
        Assert.Equal(52, s.State.Active?.Profile?.Level);
        s.ApplyUsage(s.TokensToNext);
        Assert.Null(s.State.Active);
        Assert.Equal(100, s.State.Dex[^1].Profile?.Level);
    });

    [Fact]
    public void SmallUsageChunksMatchSingleDeltaAtHardDifficulty() => MainActor.Run(async () =>
    {
        var (small, _, _) = Fixture(difficulty: 2);
        var (large, _, _) = Fixture(difficulty: 2);
        await small.Hatch(1);
        await large.Hatch(1);
        for (var i = 0; i < 20; i++) small.ApplyUsage(1);
        large.ApplyUsage(20);
        Assert.Equal(10, small.State.Active?.Profile?.GrowthTokens);
        Assert.Equal(large.State.Active?.Profile?.GrowthTokens, small.State.Active?.Profile?.GrowthTokens);
        Assert.Equal(large.State.Active?.Profile?.Level, small.State.Active?.Profile?.Level);
    });

    [Fact]
    public void LegacyStageMigrationUsesBoostAndDifficulty() => MainActor.Run(() =>
    {
        var seed = new CompanionState
        {
            Active = new MonState(1, [1, 2], [1, 2, 3], stageIndex: 1, usedAtStage: 6_250_000, rarity: Rarity.Common,
                totalForms: 3, hasGrowthBoost: true),
        };
        var (s, _, _) = Fixture(seed, difficulty: 0.1);
        Assert.Equal(250_000_000, s.State.Active?.Profile?.GrowthTokens);
        Assert.Equal(36, s.State.Active?.Profile?.Level);
    });

    [Fact]
    public void OfflineOverflowCatchesUpWhenLineLoads() => MainActor.Run(async () =>
    {
        var seed = new CompanionState
        {
            Active = new MonState(1, [1], [1, 2, 3], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3),
        };
        var (s, _, _) = Fixture(seed, difficulty: 0.1);
        s.ApplyUsage(75_000_000);
        Assert.Equal(75_000_000, s.State.Active?.UsedAtStage);
        s.Update(new Dictionary<string, long>(), "day", 0, BurnTier.Idle, false, false);
        await MainActor.WaitUntil(() => s.State.Active == null);
        Assert.Null(s.State.Active);
        Assert.Equal(100, s.State.Dex[^1].Profile?.Level);
    });

    [Fact]
    public void ImportOntoHarderDeviceKeepsEarnedIdentityAndLevel() => MainActor.Run(async () =>
    {
        var (source, _, _) = Fixture(difficulty: 0.1, forms: 1);
        await source.Hatch(1);
        source.ApplyUsage(37_500_000);
        var original = source.State.Active?.Profile?.Clone();
        Assert.NotNull(original);
        Assert.Equal(52, original!.Level);
        var data = SaveTransfer.Encode(source.State, "test", "source", DateTimeOffset.Now);
        var (destination, _, _) = Fixture(difficulty: 2, forms: 1);
        destination.ApplySave(SaveTransfer.Decode(data), new Dictionary<string, long>(), "day", false);
        Assert.Equal(original.InstanceID, destination.State.Active?.Profile?.InstanceID);
        Assert.Equal(original.Ivs, destination.State.Active?.Profile?.Ivs);
        Assert.Equal(52, destination.State.Active?.Profile?.Level);
        await MainActor.WaitUntil(() => destination.CurrentLine != null);
        destination.ApplyUsage(destination.TokensToNext);
        Assert.Equal(100, destination.State.Dex[^1].Profile?.Level);
    });

    [Fact]
    public void RareCandyFinalizesGraduationProfile() => MainActor.Run(async () =>
    {
        var seed = new CompanionState();
        seed.Inventory[ItemKind.RareCandy.RawValue()] = 1;
        var (s, _, _) = Fixture(seed, difficulty: 0.1);
        await s.Hatch(1);
        await s.LoadPokemonDetails(3);
        s.UseRareCandy();
        Assert.Null(s.State.Active);
        Assert.Equal(100, s.State.Dex[^1].Profile?.Level);
        Assert.Equal(["final-3"], s.State.Dex[^1].Profile!.Moves.Select(m => m.Name));
    });
}
