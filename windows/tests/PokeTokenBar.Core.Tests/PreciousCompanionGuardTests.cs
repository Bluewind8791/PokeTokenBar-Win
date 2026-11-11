using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of PreciousCompanionGuardTests.swift.

file sealed class GuardNoOpProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new HttpRequestException("offline"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

public class PreciousCompanionGuardTests
{
    private static CompanionStore Store(bool active = true, Rarity rarity = Rarity.Common, bool shiny = false,
        int? dittoDisguise = null, bool dittoRevealed = false)
    {
        var disguiseField = dittoDisguise != null
            ? ",\"dittoDisguise\":" + dittoDisguise + ",\"dittoRevealed\":" + (dittoRevealed ? "true" : "false") : "";
        var mon = "{\"baseID\":10,\"pathIDs\":[10],\"stageIndex\":0,\"usedAtStage\":200000000,"
            + "\"rarity\":\"" + rarity.RawValue() + "\",\"totalForms\":3,\"isShiny\":" + (shiny ? "true" : "false")
            + disguiseField + "}";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":5000000000,\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"active\":" + (active ? mon : "null") + ",\"dex\":[],\"collectedFinals\":[]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("guard"));
        return new CompanionStore(new GuardNoOpProvider(), clock: () => Lines.FixedNow, filePath: path,
            rng: new SeededRng(1), settings: new AppSettings(null));
    }

    [Fact]
    public void HighValueCompanionIdentifiesLegendaryNonShiny() => MainActor.Run(() =>
    {
        var s = Store(rarity: Rarity.Legendary, shiny: false);
        Assert.Equal(Rarity.Legendary, s.Rarity);
        Assert.False(s.CurrentIsShiny);
        Assert.True(s.IsHighValueCompanion); // legendary needs the two-step confirmation even when not shiny
    });

    [Fact]
    public void HighValueCompanionIdentifiesShinyNonLegendary() => MainActor.Run(() =>
    {
        var s = Store(rarity: Rarity.Common, shiny: true);
        Assert.Equal(Rarity.Common, s.Rarity);
        Assert.True(s.CurrentIsShiny);
        Assert.True(s.IsHighValueCompanion);
    });

    [Fact]
    public void HighValueCompanionIdentifiesShinyLegendary() => MainActor.Run(() =>
    {
        var s = Store(rarity: Rarity.Legendary, shiny: true);
        Assert.Equal(Rarity.Legendary, s.Rarity);
        Assert.True(s.CurrentIsShiny);
        Assert.True(s.IsHighValueCompanion);
    });

    [Fact]
    public void HighValueCompanionExcludesCommonNonShiny() => MainActor.Run(() =>
        Assert.False(Store(rarity: Rarity.Common, shiny: false).IsHighValueCompanion));

    [Fact]
    public void HighValueCompanionExcludesUncommonNonShiny() => MainActor.Run(() =>
        Assert.False(Store(rarity: Rarity.Uncommon, shiny: false).IsHighValueCompanion));

    /// <summary>Rare is excluded from the two-step warning to avoid fatigue on repeated rare-egg rerolls.</summary>
    [Fact]
    public void HighValueCompanionExcludesRareNonShinyToAvoidRerollFatigue() => MainActor.Run(() =>
    {
        var s = Store(rarity: Rarity.Rare, shiny: false);
        Assert.Equal(Rarity.Rare, s.Rarity);
        Assert.False(s.CurrentIsShiny);
        Assert.False(s.IsHighValueCompanion);
    });

    [Fact]
    public void HighValueCompanionIsFalseWhenNoActive() => MainActor.Run(() =>
    {
        var s = Store(active: false);
        Assert.False(s.HasActive);
        Assert.False(s.IsHighValueCompanion);
    });

    /// <summary>A disguised shiny Ditto must not leak its identity through the confirmation.</summary>
    [Fact]
    public void DisguisedDittoDoesNotLeakHighValueStatusBeforeReveal() => MainActor.Run(() =>
    {
        var s = Store(rarity: Rarity.Common, shiny: true, dittoDisguise: 10, dittoRevealed: false);
        Assert.False(s.CurrentIsShiny);
        Assert.Equal(Rarity.Common, s.Rarity);
        Assert.False(s.IsHighValueCompanion);

        var revealed = Store(rarity: Rarity.Rare, shiny: true, dittoDisguise: 10, dittoRevealed: true);
        Assert.True(revealed.CurrentIsShiny);
        Assert.True(revealed.IsHighValueCompanion);
    });

    // testLocalizationStringsCoverAllSevenLanguages: not ported — asserts L strings, owned by the localization port.
}
