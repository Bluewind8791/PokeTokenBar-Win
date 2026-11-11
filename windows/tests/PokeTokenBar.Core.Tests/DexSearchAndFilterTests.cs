using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of DexSearchAndFilterTests.swift (logic; the L label test is skipped — L is a temporary stub here).

file sealed class MockPokeException : Exception;

file sealed class MockPokeProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new MockPokeException());
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

public class DexSearchAndFilterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static Dictionary<string, string> N(string ko, string en, string fr) => new() { ["ko"] = ko, ["en"] = en, ["fr"] = fr };

    private static CompanionStore CreateTestStore()
    {
        var bulbasaur = new DexEntry(1, 3, [1, 2, 3], Rarity.Common, DateTimeOffset.FromUnixTimeSeconds(1_700_000_100),
            isShiny: false, names: new()
            {
                [1] = N("이상해씨", "Bulbasaur", "Bulbizarre"),
                [2] = N("이상해풀", "Ivysaur", "Herbizarre"),
                [3] = N("이상해꽃", "Venusaur", "Florizarre"),
            }, id: "entry-1");
        var pikachu = new DexEntry(25, 26, [25, 26], Rarity.Rare, DateTimeOffset.FromUnixTimeSeconds(1_700_000_200),
            isShiny: true, names: new()
            {
                [25] = N("피카츄", "Pikachu", "Pikachu"),
                [26] = N("라이츄", "Raichu", "Raichu"),
            }, id: "entry-2");
        var mewtwo = new DexEntry(150, 150, [150], Rarity.Legendary, DateTimeOffset.FromUnixTimeSeconds(1_700_000_300),
            isShiny: false, names: new() { [150] = N("뮤츠", "Mewtwo", "Mewtwo") }, id: "entry-3");
        return MakeStore([bulbasaur, pikachu, mewtwo]);
    }

    private static CompanionStore MakeStore(List<DexEntry> dex, MonState? active = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dex-search-{Guid.NewGuid():N}.json");
        var state = new CompanionState { Dex = dex, Active = active, Language = AppLanguage.En };
        File.WriteAllBytes(path, CompanionJson.Serialize(state));
        return new CompanionStore(new MockPokeProvider(), clock: () => Now, filePath: path, rng: new SeededRng(42),
            settings: new AppSettings(null));
    }

    /// <summary>Sprite → Pokédex page links cover only species in the dex; Unown links to its species cell.</summary>
    [Fact]
    public void TestDexLinkTargetsCoverOnlySpeciesInTheDex() => MainActor.Run(() =>
    {
        var unown = new DexEntry(201, 201, [201], Rarity.Rare, Now, isShiny: false, form: new PokemonForm("q"), id: "entry-unown");
        var raising = new MonState(4, [4, 5], [4, 5, 6], stageIndex: 1, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3);
        var store = MakeStore([unown, .. CreateTestStore().State.Dex], raising);

        var links = store.DexLinkTargets;
        Assert.Equal("1", links[1]);
        Assert.Equal("26", links[26]);
        Assert.Equal("201", links[201]); // the letter is chosen on the page
        Assert.Equal("5", links[5]);     // the raising Pokémon's current form
        Assert.Equal("4", links[4]);     // a reached earlier stage
        Assert.False(links.ContainsKey(6));   // an unreached evolution
        Assert.False(links.ContainsKey(999)); // an uncaught species
        Assert.Equal(store.DexSpeciesList.Select(s => s.Id).Order(), links.Keys.Order());
        foreach (var (id, collectionID) in links)
            Assert.Equal(id, store.DexSpeciesList.First(s => s.CollectionID == collectionID).Id);
    });

    /// <summary>An egg has no species, so the Home header sprite gets no link target.</summary>
    [Fact]
    public void TestEggHasNoDexLinkTarget() => MainActor.Run(() =>
    {
        var store = MakeStore(CreateTestStore().State.Dex);
        Assert.True(store.IsEgg);
        Assert.Null(store.CurrentSpeciesID);
    });

    [Fact]
    public void TestDexSpeciesSearchByIDAndName() => MainActor.Run(() =>
    {
        var store = CreateTestStore();

        var byID = store.FilteredDexSpecies(query: "25");
        Assert.Contains(byID, s => s.Id == 25);
        Assert.DoesNotContain(byID, s => s.Id == 1);

        var byHashID = store.FilteredDexSpecies(query: "#150");
        Assert.Single(byHashID);
        Assert.Equal(150, byHashID[0].Id);

        // Partial number, with or without the # prefix
        Assert.Equal([150], store.FilteredDexSpecies(query: "15").Select(s => s.Id));
        Assert.Equal(["entry-3"], store.FilteredDexEntries(query: "#15").Select(e => e.Id));

        var byName = store.FilteredDexSpecies(query: "Venusaur");
        Assert.Single(byName);
        Assert.Equal(3, byName[0].Id);

        Assert.Contains(store.FilteredDexSpecies(query: "pika"), s => s.Id == 25);         // case-insensitive
        Assert.Contains(store.FilteredDexSpecies(query: "Bulbizarre"), s => s.Id == 1);    // French name

        var byKoreanName = store.FilteredDexSpecies(query: "뮤츠");
        Assert.Single(byKoreanName);
        Assert.Equal(150, byKoreanName[0].Id);

        Assert.Empty(store.FilteredDexSpecies(query: "MissingNo"));
        Assert.Equal(store.DexSpeciesList.Count, store.FilteredDexSpecies(query: "").Count);
    });

    /// <summary>Regression: accents must not block name search ("flabebe" finds Flabébé) on both screens.</summary>
    [Fact]
    public void TestNameSearchIgnoresCaseAndDiacriticsInPokedexAndCatchLog() => MainActor.Run(() =>
    {
        var flabebe = new DexEntry(669, 669, [669], Rarity.Common, DateTimeOffset.FromUnixTimeSeconds(1_700_000_400),
            names: new() { [669] = N("플라베베", "Flabébé", "Flabébé") }, id: "entry-flabebe");
        var pikachu = new DexEntry(25, 25, [25], Rarity.Rare, DateTimeOffset.FromUnixTimeSeconds(1_700_000_500),
            names: new() { [25] = N("피카츄", "Pikachu", "Pikachu") }, id: "entry-pikachu");
        var store = MakeStore([flabebe, pikachu]);

        foreach (var query in new[] { "flabebe", "FLABEBE", "Flabébé", "flabébe" })
        {
            Assert.Equal([669], store.FilteredDexSpecies(query: query).Select(s => s.Id));
            Assert.Equal(["entry-flabebe"], store.FilteredDexEntries(query: query).Select(e => e.Id));
        }
    });

    [Fact]
    public void TestDexSpeciesFilterByShinyAndRarity() => MainActor.Run(() =>
    {
        var store = CreateTestStore();

        var shinyOnly = store.FilteredDexSpecies(shinyOnly: true);
        Assert.All(shinyOnly, s => Assert.True(s.IsShiny));
        Assert.Contains(shinyOnly, s => s.Id == 25);
        Assert.DoesNotContain(shinyOnly, s => s.Id == 150);

        var legendaries = store.FilteredDexSpecies(rarity: Rarity.Legendary);
        Assert.Single(legendaries);
        Assert.Equal(150, legendaries[0].Id);

        Assert.Equal(3, store.FilteredDexSpecies(rarity: Rarity.Common).Count); // 1, 2, 3
        Assert.Equal(2, store.FilteredDexSpecies(rarity: Rarity.Rare, shinyOnly: true).Count); // 25, 26
        Assert.Empty(store.FilteredDexSpecies(rarity: Rarity.Legendary, shinyOnly: true));
    });

    [Fact]
    public void TestDexSpeciesSorting() => MainActor.Run(() =>
    {
        var store = CreateTestStore();

        var numAsc = store.FilteredDexSpecies(sort: DexSortOption.NumberAsc).Select(s => s.Id).ToList();
        Assert.Equal(numAsc.Order(), numAsc);

        var numDesc = store.FilteredDexSpecies(sort: DexSortOption.NumberDesc).Select(s => s.Id).ToList();
        Assert.Equal(numDesc.OrderDescending(), numDesc);

        var names = store.FilteredDexSpecies(sort: DexSortOption.NameAsc).Select(s => s.Name).ToList();
        for (var i = 0; i < names.Count - 1; i++)
            Assert.True(string.Compare(names[i], names[i + 1], StringComparison.CurrentCulture) <= 0);

        var descNames = store.FilteredDexSpecies(sort: DexSortOption.NameDesc).Select(s => s.Name).ToList();
        for (var i = 0; i < descNames.Count - 1; i++)
            Assert.True(string.Compare(descNames[i], descNames[i + 1], StringComparison.CurrentCulture) >= 0);

        var rarityDesc = store.FilteredDexSpecies(sort: DexSortOption.RarityDesc);
        for (var i = 0; i < rarityDesc.Count - 1; i++)
            Assert.True(rarityDesc[i].Rarity.SortRank() >= rarityDesc[i + 1].Rarity.SortRank());
    });

    [Fact]
    public void TestCatchLogSearchAndFilters() => MainActor.Run(() =>
    {
        var store = CreateTestStore();

        var matchChain = store.FilteredDexEntries(query: "25");
        Assert.Single(matchChain);
        Assert.Equal(25, matchChain[0].BaseID);

        var matchName = store.FilteredDexEntries(query: "Raichu");
        Assert.Single(matchName);
        Assert.Equal(25, matchName[0].BaseID);

        var shinies = store.FilteredDexEntries(shinyOnly: true);
        Assert.Single(shinies);
        Assert.Equal(25, shinies[0].BaseID);

        var legendaryEntries = store.FilteredDexEntries(rarity: Rarity.Legendary);
        Assert.Single(legendaryEntries);
        Assert.Equal(150, legendaryEntries[0].BaseID);
    });

    [Fact]
    public void TestCatchLogSorting() => MainActor.Run(() =>
    {
        var store = CreateTestStore();

        var recent = store.FilteredDexEntries(sort: CatchLogSortOption.RecentFirst);
        Assert.Equal(150, recent[0].FinalID); // caughtAt 300 is most recent
        Assert.Equal(3, recent[^1].FinalID);  // caughtAt 100 is oldest

        var oldest = store.FilteredDexEntries(sort: CatchLogSortOption.OldestFirst);
        Assert.Equal(3, oldest[0].FinalID);
        Assert.Equal(150, oldest[^1].FinalID);

        Assert.Equal([3, 26, 150], store.FilteredDexEntries(sort: CatchLogSortOption.NumberAsc).Select(e => e.FinalID));
        Assert.Equal([150, 26, 3], store.FilteredDexEntries(sort: CatchLogSortOption.NumberDesc).Select(e => e.FinalID));
        Assert.Equal([Rarity.Legendary, Rarity.Rare, Rarity.Common],
            store.FilteredDexEntries(sort: CatchLogSortOption.RarityDesc).Select(e => e.Rarity));
    });
}
