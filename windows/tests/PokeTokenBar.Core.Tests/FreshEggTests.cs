using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of FreshEggTests.swift: Fresh Egg (reroll — discards the current Pokémon; Pokédex/odds unaffected).

file sealed class FreshEggNoProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new HttpRequestException("offline"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

public class FreshEggTests
{
    /// <summary>Active (base 10, common 3 forms, 200M growth) + one dex entry + one collected final (1:3) + wallet.
    /// active=false → egg state.</summary>
    private static CompanionStore Store(bool active = true, bool shiny = false, long used = 5_000_000_000, long spent = 0)
    {
        var mon = "{\"baseID\":10,\"pathIDs\":[10],\"stageIndex\":0,\"usedAtStage\":200000000,"
            + "\"rarity\":\"common\",\"totalForms\":3,\"isShiny\":" + (shiny ? "true" : "false") + "}";
        var dex = "{\"baseID\":1,\"finalID\":3,\"chainOrder\":[1,2,3],\"rarity\":\"common\"}";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":" + used + ",\"spentTokens\":" + spent + ","
            + "\"lastDate\":\"d\",\"active\":" + (active ? mon : "null") + ",\"dex\":[" + dex + "],\"collectedFinals\":[\"1:3\"]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("egg"));
        return new CompanionStore(new FreshEggNoProvider(), clock: () => Lines.FixedNow, filePath: path,
            rng: new SeededRng(7), settings: new AppSettings(null));
    }

    [Fact]
    public void PriceIsOneBillion() => Assert.Equal(1_000_000_000, FreshEgg.Price);

    /// <summary>[Core] Reroll = release: the active one leaves, a new egg (eggUsage 0). It stays in the dex as a
    /// release record and the odds weighting (collectedFinals) is unchanged.</summary>
    [Fact]
    public void BuyFreshEggReleasesIntoDexWithoutProbabilityImpact() => MainActor.Run(() =>
    {
        var s = Store(used: 5_000_000_000, spent: 0);
        var persistedDexBefore = s.State.Dex.Count;
        var collectedBefore = s.State.CollectedFinals.ToHashSet();
        Assert.Equal(persistedDexBefore + 1, s.DexEntries.Count); // shown before graduation
        Assert.True(s.HasActive);
        Assert.True(s.BuyFreshEgg());
        Assert.Null(s.State.Active);
        Assert.True(s.IsEgg);
        Assert.Equal(0, s.State.EggUsage);
        Assert.Null(s.State.PendingHatchID);

        Assert.Equal(persistedDexBefore + 1, s.State.Dex.Count); // release record added
        var released = s.State.Dex[^1];
        Assert.Equal(10, released.BaseID);
        Assert.True(released.IsReleased);
        Assert.Equal([10], released.ChainOrder);
        Assert.Equal(persistedDexBefore + 1, s.DexEntries.Count);

        Assert.Equal(collectedBefore, s.State.CollectedFinals);
        Assert.Equal(FreshEgg.Price, s.State.SpentTokens);
        Assert.Equal(5_000_000_000 - FreshEgg.Price, s.AvailableTokens);
    });

    /// <summary>[Regression] A released species never leaves the Pokédex.</summary>
    [Fact]
    public void ReleasedSpeciesStaysInTheDex() => MainActor.Run(() =>
    {
        var s = Store();
        Assert.Contains(s.DexSpeciesList, sp => sp.Id == 10);
        Assert.True(s.BuyFreshEgg());
        Assert.Contains(s.DexSpeciesList, sp => sp.Id == 10);
        Assert.False(s.DexSpeciesList.First(sp => sp.Id == 10).IsRaising);
    });

    [Fact]
    public void DiscardedSpeciesNotCollected() => MainActor.Run(() =>
    {
        var s = Store();
        Assert.True(s.BuyFreshEgg());
        Assert.DoesNotContain(s.State.CollectedFinals, f => f.StartsWith("10:", StringComparison.Ordinal));
    });

    [Fact]
    public void CannotRerollWhenEgg() => MainActor.Run(() =>
    {
        var s = Store(active: false, used: 5_000_000_000);
        Assert.False(s.HasActive);
        Assert.False(s.CanBuyFreshEgg);
        Assert.False(s.BuyFreshEgg());
        Assert.Equal(0, s.State.SpentTokens);
    });

    [Fact]
    public void CannotRerollWithoutFunds() => MainActor.Run(() =>
    {
        var s = Store(used: 500_000_000);
        Assert.False(s.CanBuyFreshEgg);
        Assert.False(s.BuyFreshEgg());
        Assert.NotNull(s.State.Active);
        Assert.Equal(0, s.State.SpentTokens);
    });

    [Fact]
    public void ShinyCanBeRerolled() => MainActor.Run(() =>
    {
        var s = Store(shiny: true);
        Assert.True(s.CurrentIsShiny);
        Assert.True(s.BuyFreshEgg());
        Assert.Null(s.State.Active);
        Assert.False(s.CurrentIsShiny);
    });
}
