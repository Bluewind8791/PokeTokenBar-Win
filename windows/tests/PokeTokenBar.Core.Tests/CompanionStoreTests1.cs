using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of CompanionTests.swift, part 1 (PokemonBalanceTests and the first half of CompanionStoreTests).

public class PokemonBalanceTests
{
    private static readonly Rarity[] AllRarities = [Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.Legendary];

    [Fact]
    public void GraduationTotalIsConstantPerRarityRegardlessOfStages()
    {
        foreach (var rarity in AllRarities)
        {
            var t = PokemonBalance.GraduationTotal(rarity);
            for (var k = 1; k <= 3; k++)
            {
                long sum = 0;
                for (var i = 0; i < k; i++) sum += PokemonBalance.PhaseThreshold(rarity, k, i);
                Assert.True(Math.Abs(sum - t) <= 2, $"rarity={rarity} k={k} sum={sum} T={t}"); // rounding tolerance
            }
        }
    }

    [Fact]
    public void RepeatGrowthHalvesEveryPhaseThreshold()
    {
        foreach (var rarity in AllRarities)
            for (var forms = 1; forms <= 3; forms++)
                for (var stage = 0; stage < forms; stage++)
                {
                    var standard = PokemonBalance.PhaseThreshold(rarity, forms, stage);
                    var boosted = PokemonBalance.PhaseThreshold(rarity, forms, stage, PokemonBalance.RepeatGrowthMultiplier);
                    Assert.Equal((long)Math.Round(standard / 2.0, MidpointRounding.AwayFromZero), boosted);
                }
        Assert.Equal(62_500_000, PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0, PokemonBalance.RepeatGrowthMultiplier));
    }

    [Fact]
    public void HigherStageCostsMore()
    {
        for (var k = 2; k <= 3; k++)
            for (var i = 0; i < k - 1; i++)
                Assert.True(PokemonBalance.PhaseThreshold(Rarity.Common, k, i) < PokemonBalance.PhaseThreshold(Rarity.Common, k, i + 1));
    }

    [Fact]
    public void RarerCostsMore()
    {
        Assert.True(PokemonBalance.GraduationTotal(Rarity.Common) < PokemonBalance.GraduationTotal(Rarity.Uncommon));
        Assert.True(PokemonBalance.GraduationTotal(Rarity.Uncommon) < PokemonBalance.GraduationTotal(Rarity.Rare));
        Assert.True(PokemonBalance.GraduationTotal(Rarity.Rare) < PokemonBalance.GraduationTotal(Rarity.Legendary));
    }

    [Fact]
    public void RarityDerivation()
    {
        Assert.Equal(Rarity.Common, RarityExtensions.From(255, false, false));
        Assert.Equal(Rarity.Uncommon, RarityExtensions.From(90, false, false));
        Assert.Equal(Rarity.Rare, RarityExtensions.From(45, false, false));
        Assert.Equal(Rarity.Legendary, RarityExtensions.From(3, true, false));
    }
}

/// <summary>Swift <c>FallbackOnlyProvider</c>: GraphQL index down (throws), REST fallback succeeds.</summary>
file sealed class FallbackOnlyProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(Lines.Make(baseSpeciesID, Lines.Node(baseSpeciesID)));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromException<IReadOnlyList<BaseSpecies>>(new PokeStubException());
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(new BaseSpecies(id, 100));
}

/// <summary>Counts Line() calls — verifies already-stored entries trigger no lookup.</summary>
file sealed class CountingLineProvider(EvoLine value) : IPokeProviding
{
    public int LineCalls { get; private set; }
    public Task<EvoLine> Line(int baseSpeciesID)
    {
        LineCalls += 1;
        return Task.FromResult(value);
    }
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(value.BaseID, 255)]);
}

/// <summary>Line() itself fails (offline) — Pokédex name lookup fallback.</summary>
file sealed class LineThrowsProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new PokeStubException());
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
}

public class CompanionStoreTests1
{
    private static readonly DateTimeOffset FixedNow = Lines.FixedNow;

    private static CompanionStore Store(EvoLine line, ulong seed = 7) => StateFiles.Store(line, seed);

    private static CompanionStore Open(string path, IPokeProviding? provider = null) =>
        new(provider ?? new StubProvider(Lines.Linear3), clock: () => FixedNow, filePath: path, rng: new SeededRng(7),
            dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));

    private static void AssertMap<TValue>(IReadOnlyDictionary<int, TValue> expected, IReadOnlyDictionary<int, TValue>? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.OrderBy(kv => kv.Key).ToList(), actual!.OrderBy(kv => kv.Key).ToList());
    }

    private static void AssertMap(IReadOnlyDictionary<string, long> expected, IReadOnlyDictionary<string, long>? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
            actual!.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList());
    }

    private static long Phase(int stage) => PokemonBalance.PhaseThreshold(Rarity.Common, 3, stage);

    // MARK: State file decode resilience (regression)

    /// [regression] One broken dex entry (old/missing fields) keeps the rest of the dex, companion and inventory.
    [Fact]
    public void CorruptDexEntryDroppedWhileRestSurvives() => MainActor.Run(() =>
    {
        // Two valid + one broken (finalID/chainOrder missing).
        var json = """{"dex":[{"baseID":1,"finalID":3,"chainOrder":[1,2,3],"rarity":"common"},"""
                   + """{"baseID":99,"rarity":"rare"},"""
                   + """{"baseID":7,"finalID":9,"chainOrder":[7,8,9],"rarity":"uncommon"}],"inventory":{"rareCandy":2}}""";
        var s = Open(StateFiles.WriteText(json, StateFiles.TempPath("poke-dex")));

        Assert.Equal(2, s.State.Dex.Count);
        Assert.Equal(new HashSet<int> { 1, 7 }, s.State.Dex.Select(e => e.BaseID).ToHashSet());
        Assert.Equal(2, s.State.Inventory["rareCandy"]);
    });

    /// [regression] A totally broken state file starts fresh, but the original is kept as `.corrupt` first.
    [Fact]
    public void CorruptStateFileBackedUpBeforeReset() => MainActor.Run(() =>
    {
        var url = StateFiles.TempPath("poke-corrupt");
        const string garbage = "this is not valid json {{{ 손상";
        File.WriteAllText(url, garbage);

        var s = Open(url);

        Assert.Empty(s.State.Dex);
        Assert.Null(s.State.Active);
        Assert.Equal(0, s.State.UsedSinceInstall);

        var backup = url + ".corrupt";
        Assert.True(File.Exists(backup));
        Assert.Equal(garbage, File.ReadAllText(backup));
        Assert.False(File.Exists(url));
        File.Delete(backup);
    });

    /// [regression] A broken active Pokémon falls back to the egg while dex, inventory and lifetime survive.
    [Fact]
    public void CorruptActiveFallsBackToEggWhileRestSurvives() => MainActor.Run(() =>
    {
        var json = "{\"active\":{\"baseID\":1},"
                   + "\"dex\":[{\"baseID\":1,\"finalID\":3,\"chainOrder\":[1,2,3],\"rarity\":\"common\"}],"
                   + "\"inventory\":{\"rareCandy\":3},\"usedSinceInstall\":5000}";
        var url = StateFiles.WriteText(json, StateFiles.TempPath("poke-active-corrupt"));

        var s = Open(url);

        Assert.Null(s.State.Active);
        Assert.Single(s.State.Dex);
        Assert.Equal(3, s.State.Inventory["rareCandy"]);
        Assert.Equal(5000, s.State.UsedSinceInstall);
        Assert.False(File.Exists(url + ".corrupt"));
    });

    // MARK: Pokédex names

    [Fact]
    public void DexStoredChainNamesResolvePerLanguage() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        var named = new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: null,
            names: Lines.Names((1, "ko", "포1"), (1, "en", "P1"), (2, "ko", "포2"), (2, "en", "P2"), (3, "ko", "포3"), (3, "en", "P3")));
        s.SetLanguage(AppLanguage.Ko);
        AssertMap(new Dictionary<int, string> { [1] = "포1", [2] = "포2", [3] = "포3" }, s.DexStoredChainNames(named));
        s.SetLanguage(AppLanguage.En);
        AssertMap(new Dictionary<int, string> { [1] = "P1", [2] = "P2", [3] = "P3" }, s.DexStoredChainNames(named));
        Assert.Null(s.DexStoredChainNames(new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3],
            rarity: Rarity.Common, caughtAt: null)));
    });

    [Fact]
    public void DexResolveChainNamesFetchesWhenUnstored() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        s.SetLanguage(AppLanguage.Ko);
        var bare = new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: null);
        var names = await s.DexResolveChainNames(bare);
        AssertMap(new Dictionary<int, string> { [1] = "포1", [2] = "포2", [3] = "포3" }, names);
    });

    [Fact]
    public void GraduationStoresChainNames() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        s.ApplyUsage(Phase(0));
        s.ApplyUsage(Phase(1));
        s.ApplyUsage(Phase(2));
        Assert.Single(s.State.Dex);
        Assert.Equal([1, 2, 3], s.State.Dex[0].ChainOrder);
        Assert.Equal("포1", s.State.Dex[0].Names?[1]["ko"]);
        Assert.Equal("ポ3", s.State.Dex[0].Names?[3]["ja"]);
        s.SetLanguage(AppLanguage.Ko);
        AssertMap(new Dictionary<int, string> { [1] = "포1", [2] = "포2", [3] = "포3" }, s.DexStoredChainNames(s.State.Dex[0]));
    });

    /// Backfill (trigger branch): loading a legacy JSON without names reproduces the real migration path.
    [Fact]
    public void DexResolveChainNamesBackfillsLegacyEntry() => MainActor.Run(async () =>
    {
        var url = StateFiles.WriteText("""{"dex":[{"id":"e1","baseID":1,"finalID":3,"chainOrder":[1,2,3],"rarity":"common"}]}""");
        var s = Open(url);
        s.SetLanguage(AppLanguage.Ko);
        Assert.Single(s.State.Dex);
        Assert.Null(s.State.Dex[0].Names);
        var names = await s.DexResolveChainNames(s.State.Dex[0]);
        AssertMap(new Dictionary<int, string> { [1] = "포1", [2] = "포2", [3] = "포3" }, names);
        Assert.Equal("포2", s.State.Dex[0].Names?[2]["ko"]);
    });

    // MARK: Pokédex (species aggregation)

    [Fact]
    public void DexSpeciesFoldsDuplicateLinesToOneCellPerSpecies() => MainActor.Run(() =>
    {
        DexEntry[] entries =
        [
            new(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: FixedNow,
                nature: PokemonNature.Rash, names: Lines.Names((1, "ko", "포1"), (2, "ko", "포2"), (3, "ko", "포3"))),
            new(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: FixedNow,
                nature: PokemonNature.Lax),
        ];
        var s = Open(StateFiles.WriteText($$"""{"dex":{{StateFiles.Json(entries)}},"language":"ko"}"""));
        Assert.Equal(2, s.State.Dex.Count);

        var folded = s.DexSpeciesList;
        Assert.Equal([1, 2, 3], folded.Select(f => f.Id));
        Assert.Equal(["포1", "포2", "포3"], folded.Select(f => f.Name));
        Assert.Equal([Rarity.Common, Rarity.Common, Rarity.Common], folded.Select(f => f.Rarity));
    });

    /// Only reached stages of the current Pokémon count: pathIDs would give [1,2], plannedPathIDs [1,2,3].
    [Fact]
    public void DexSpeciesCountsOnlyReachedStagesOfActive() => MainActor.Run(() =>
    {
        var active = new MonState(1, [1, 2], [1, 2, 3], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common,
            totalForms: 3, nature: PokemonNature.Brave);
        var s = Open(StateFiles.WriteText($$"""{"active":{{StateFiles.Json(active)}},"language":"ko"}"""));
        Assert.Empty(s.State.Dex);
        Assert.Equal(0, s.State.Active?.StageIndex);
        Assert.Equal([1], s.DexSpeciesList.Select(f => f.Id));
    });

    [Fact]
    public void DexSpeciesMarksShinyAcrossTheChain() => MainActor.Run(() =>
    {
        DexEntry[] entries =
        [
            new(baseID: 1, finalID: 2, chainOrder: [1, 2], rarity: Rarity.Common, caughtAt: FixedNow, isShiny: false),
            new(baseID: 1, finalID: 2, chainOrder: [1, 2], rarity: Rarity.Common, caughtAt: FixedNow, isShiny: true),
        ];
        var s = Open(StateFiles.WriteText($$"""{"dex":{{StateFiles.Json(entries)}},"language":"ko"}"""));
        var folded = s.DexSpeciesList;
        Assert.Equal([1, 2], folded.Select(f => f.Id));
        Assert.Equal([true, true], folded.Select(f => f.IsShiny));
        Assert.Equal([true, true], folded.Select(f => f.HasNormal));
    });

    [Fact]
    public void DexAppearancesPreserveBothColorsRegardlessOfCatchOrder() => MainActor.Run(() =>
    {
        DexEntry Normal() => new(baseID: 605, finalID: 606, chainOrder: [605, 606], rarity: Rarity.Rare, caughtAt: FixedNow);
        DexEntry Shiny() => new(baseID: 605, finalID: 606, chainOrder: [605, 606], rarity: Rarity.Rare, caughtAt: FixedNow, isShiny: true);
        foreach (var entries in new List<DexEntry>[] { [Normal(), Shiny()], [Shiny(), Normal()], [Normal()], [Shiny()] })
        {
            var url = StateFiles.Write(new CompanionState { Dex = entries });
            try
            {
                var s = Open(url);
                Assert.Equal([605, 606], s.DexSpeciesList.Select(f => f.Id));
                Assert.All(s.DexSpeciesList, f => Assert.Equal(entries.Any(e => !e.IsShiny), f.HasNormal));
                Assert.All(s.DexSpeciesList, f => Assert.Equal(entries.Any(e => e.IsShiny), f.IsShiny));
                Assert.Equal(entries.Select(e => e.IsShiny), s.DexEntries.Select(e => e.IsShiny));
                Assert.Empty(s.PokemonIndividuals(605)); // earlier evolution previews must not manufacture another individual
            }
            finally
            {
                File.Delete(url);
            }
        }
    });

    [Fact]
    public void DexAppearancesCombineActiveAndArchivedWithoutFutureForms() => MainActor.Run(() =>
    {
        foreach (var activeShiny in new[] { false, true })
        {
            var state = new CompanionState
            {
                Dex = [new DexEntry(baseID: 1, finalID: 1, chainOrder: [1], rarity: Rarity.Common, caughtAt: FixedNow, isShiny: !activeShiny)],
                Active = new MonState(1, [1, 2], [1, 2, 3], stageIndex: 1, usedAtStage: 0, rarity: Rarity.Common,
                    totalForms: 3, isShiny: activeShiny),
            };
            var url = StateFiles.Write(state);
            try
            {
                var s = Open(url);
                Assert.Equal([1, 2], s.DexSpeciesList.Select(f => f.Id));
                Assert.True(s.DexSpeciesList[0].HasNormal);
                Assert.True(s.DexSpeciesList[0].IsShiny);
                Assert.Equal(!activeShiny, s.DexSpeciesList[^1].HasNormal);
                Assert.Equal(activeShiny, s.DexSpeciesList[^1].IsShiny);
            }
            finally
            {
                File.Delete(url);
            }
        }
    });

    // MARK: Representative floating pet

    /// Old saves have no selection key; null keeps following the current Pokémon and its shininess.
    [Fact]
    public void RepresentativeDefaultsToCurrentCompanionForLegacySave() => MainActor.Run(() =>
    {
        var active = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3, isShiny: true);
        var s = Open(StateFiles.WriteText($$"""{"active":{{StateFiles.Json(active)}}}"""));
        Assert.Null(s.RepresentativeSpeciesID);
        Assert.Equal(new CompanionStore.RepresentativeSubjectInfo(1, true), s.RepresentativeSubject);
    });

    [Fact]
    public void RepresentativeSelectionUsesOwnedShinySpeciesAndPersists() => MainActor.Run(() =>
    {
        DexEntry[] dex =
        [
            new(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: FixedNow, isShiny: true,
                names: Lines.Names((1, "en", "P1"), (2, "en", "P2"), (3, "en", "P3"))),
        ];
        var active = new MonState(20, [20], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        var url = StateFiles.WriteText($$"""{"dex":{{StateFiles.Json(dex)}},"active":{{StateFiles.Json(active)}},"language":"en"}""");

        var s = Open(url);
        Assert.True(s.SetRepresentativeSpeciesID(2));
        Assert.Equal(20, s.CurrentSpeciesID);
        Assert.Equal(new CompanionStore.RepresentativeSubjectInfo(2, true), s.RepresentativeSubject);

        var reloaded = Open(url);
        Assert.Equal(2, reloaded.RepresentativeSpeciesID);
        Assert.Equal(new CompanionStore.RepresentativeSubjectInfo(2, true), reloaded.RepresentativeSubject);
    });

    [Fact]
    public void RepresentativeSelectionSurvivesGraduation() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        Assert.True(s.SetRepresentativeSpeciesID(1));

        for (var stage = 0; stage < 3; stage++)
        {
            s.ApplyUsage(Phase(stage));
            Assert.Equal(1, s.RepresentativeSubject.SpeciesID);
        }

        Assert.Null(s.State.Active);
        Assert.Equal([1, 2, 3], s.State.Dex[0].ChainOrder);
        Assert.Equal(1, s.RepresentativeSpeciesID);
        Assert.Equal(1, s.RepresentativeSubject.SpeciesID);
    });

    /// The menu bar observes this one cache: clearing the selection must notify immediately
    /// (Swift: withObservationTracking; here the Changed event).
    [Fact]
    public void RepresentativeSubjectNotifiesImmediatelyWhenSelectionChanges() => MainActor.Run(() =>
    {
        DexEntry[] dex = [new(baseID: 1, finalID: 1, chainOrder: [1], rarity: Rarity.Common, caughtAt: FixedNow)];
        var active = new MonState(20, [20], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        var s = Open(StateFiles.WriteText(
            $$"""{"dex":{{StateFiles.Json(dex)}},"active":{{StateFiles.Json(active)}},"representativeSpeciesID":1}"""));

        var didChange = false;
        s.Changed += () => didChange = true;

        Assert.True(s.SetRepresentativeSpeciesID(null));
        Assert.True(didChange);
        Assert.Equal(20, s.RepresentativeSubject.SpeciesID);
    });

    /// A released species stays in the dex now, so a representative pointing at it is kept.
    [Fact]
    public void FreshEggKeepsRepresentativeSelectionOfReleasedSpecies() => MainActor.Run(() =>
    {
        var active = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3);
        var s = Open(StateFiles.WriteText(
            $$"""{"active":{{StateFiles.Json(active)}},"representativeSpeciesID":1,"usedSinceInstall":1000000000}"""));
        Assert.Equal(1, s.RepresentativeSpeciesID);
        Assert.True(s.BuyFreshEgg());
        Assert.Equal(1, s.RepresentativeSpeciesID);
        Assert.Equal(1, s.RepresentativeSubject.SpeciesID);
    });

    [Fact]
    public void UnavailableRepresentativeSelectionIsDroppedAtLoad() => MainActor.Run(() =>
    {
        var s = Open(StateFiles.WriteText("""{"representativeSpeciesID":999}"""));
        Assert.Null(s.RepresentativeSpeciesID);
        Assert.False(s.SetRepresentativeSpeciesID(999));
    });

    /// A disguised Ditto hides its shininess until revealed — the Pokédex follows the same rule.
    [Fact]
    public void DexSpeciesHidesShinyWhileDittoIsDisguised() => MainActor.Run(() =>
    {
        CompanionStore Make(bool revealed)
        {
            var active = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3,
                isShiny: true, dittoDisguise: 1, dittoRevealed: revealed);
            return Open(StateFiles.WriteText($$"""{"active":{{StateFiles.Json(active)}},"language":"ko"}"""));
        }
        var disguised = Make(false);
        Assert.True(disguised.State.Active?.IsShiny ?? false);
        Assert.False(disguised.DexSpeciesList[0].IsShiny);
        Assert.True(disguised.DexSpeciesList[0].HasNormal);

        var revealed = Make(true);
        Assert.True(revealed.DexSpeciesList[0].IsShiny);
        Assert.False(revealed.DexSpeciesList[0].HasNormal);
    });

    /// The raised species' name comes from the loaded line (the hatch path; file fixtures have no line).
    [Fact]
    public void DexSpeciesNamesActiveSpeciesFromLoadedLine() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        s.SetLanguage(AppLanguage.Ko);
        await s.Hatch(1);
        var sp = s.DexSpeciesList;
        Assert.Equal([1], sp.Select(f => f.Id));
        Assert.Equal("포1", sp[0].Name);
    });

    // MARK: Pokédex name backfill

    [Fact]
    public void BackfillFillsNamesForEntriesSavedBeforeNamesExisted() => MainActor.Run(async () =>
    {
        var s = StoreWithNamelessEntry();
        Assert.Null(s.State.Dex[0].Names);
        Assert.Equal(["#1", "#2", "#3"], s.DexSpeciesList.Select(f => f.Name));

        await s.BackfillMissingDexNames();

        Assert.Equal(["포1", "포2", "포3"], s.DexSpeciesList.Select(f => f.Name));
        Assert.NotNull(s.State.Dex[0].Names);
    });

    [Fact]
    public void BackfillDoesNotFetchWhenNamesAreAlreadyStored() => MainActor.Run(async () =>
    {
        var entry = new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: FixedNow,
            names: Lines.Names((1, "ko", "포1"), (2, "ko", "포2"), (3, "ko", "포3")));
        var url = StateFiles.WriteText($$"""{"dex":{{StateFiles.Json([entry])}},"language":"ko"}""");
        var provider = new CountingLineProvider(Lines.Linear3);
        var s = Open(url, provider);

        await s.BackfillMissingDexNames();

        Assert.Equal(0, provider.LineCalls);
        Assert.Equal(["포1", "포2", "포3"], s.DexSpeciesList.Select(f => f.Name));
    });

    /// Offline, the fallback (#id) is never stored; the next (online) visit fills the names.
    [Fact]
    public void BackfillRetriesAfterAnOfflineAttempt() => MainActor.Run(async () =>
    {
        var bare = new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: FixedNow);
        var url = StateFiles.WriteText($$"""{"dex":{{StateFiles.Json([bare])}},"language":"ko"}""");

        var offline = Open(url, new LineThrowsProvider());
        await offline.BackfillMissingDexNames();
        Assert.Null(offline.State.Dex[0].Names);
        Assert.Equal(["#1", "#2", "#3"], offline.DexSpeciesList.Select(f => f.Name));

        var online = Open(url);
        await online.BackfillMissingDexNames();
        Assert.Equal(["포1", "포2", "포3"], online.DexSpeciesList.Select(f => f.Name));
    });

    // MARK: "Raising" mark (current form only)

    [Fact]
    public void DexSpeciesMarksOnlyCurrentEvolutionStageAsRaising() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        s.SetLanguage(AppLanguage.Ko);
        await s.Hatch(1);
        s.ApplyUsage(Phase(0));
        Assert.Equal(1, s.State.Active?.StageIndex);

        var sp = s.DexSpeciesList;
        Assert.Equal([1, 2], sp.Select(f => f.Id));
        Assert.Equal([false, true], sp.Select(f => f.IsRaising));
    });

    [Fact]
    public void AlreadyGraduatedLineStillMarksOnlyCurrentStageAsRaising() => MainActor.Run(() =>
    {
        var graduated = new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: FixedNow,
            names: Lines.Names((1, "ko", "포1"), (2, "ko", "포2"), (3, "ko", "포3")));
        var active = new MonState(1, [1, 2, 3], stageIndex: 1, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3,
            nature: PokemonNature.Brave);
        var s = Open(StateFiles.WriteText(
            $$"""{"dex":{{StateFiles.Json([graduated])}},"active":{{StateFiles.Json(active)}},"language":"ko"}"""));
        Assert.Equal([false, true, false], s.DexSpeciesList.Select(f => f.IsRaising));
    });

    // MARK: Release (permanent record of a Pokémon given up for a new egg)

    /// [regression·trigger] Releasing a 3-stage line at stage 2 credits only the two reached forms.
    [Fact]
    public void ReleasingMidChainCreditsOnlyReachedForms() => MainActor.Run(() =>
    {
        var active = new MonState(1, [1, 2], [1, 2, 3], stageIndex: 1, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3);
        var s = Open(StateFiles.WriteText($$"""{"active":{{StateFiles.Json(active)}},"usedSinceInstall":5000000000}"""));
        Assert.Equal([1, 2], s.DexSpeciesList.Select(f => f.Id));
        Assert.True(s.BuyFreshEgg());

        Assert.Equal([1, 2], s.DexSpeciesList.Select(f => f.Id));
        var released = s.State.Dex[^1];
        Assert.Equal([1, 2], released.ChainOrder);
        Assert.Equal(2, released.FinalID);
        Assert.DoesNotContain(s.DexSpeciesList, f => f.Id == 3);
    });

    /// Releasing a disguised Ditto keeps its shininess hidden (single CurrentIsShiny rule).
    [Fact]
    public void ReleasingDisguisedDittoKeepsShinyHidden() => MainActor.Run(() =>
    {
        var active = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3,
            isShiny: true, dittoDisguise: 1, dittoRevealed: false);
        var s = Open(StateFiles.WriteText($$"""{"active":{{StateFiles.Json(active)}},"usedSinceInstall":5000000000}"""));
        Assert.True(s.BuyFreshEgg());
        Assert.False(s.State.Dex[^1].IsShiny);
    });

    /// Entries saved before releasedAt existed all read as graduations.
    [Fact]
    public void LegacyDexEntriesDecodeAsGraduated()
    {
        const string json = """{"baseID":1,"finalID":3,"chainOrder":[1,2,3],"rarity":"common"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var entry = CompanionJson.DecodeDexEntry(doc.RootElement, SwiftDateEncoding.DeferredToDate);
        Assert.Null(entry.ReleasedAt);
        Assert.False(entry.IsReleased);
    }

    [Fact]
    public void GraduatedOnlyDexHasNoRaisingMark() => MainActor.Run(() =>
    {
        var s = StoreWithNamelessEntry();
        Assert.Null(s.State.Active);
        Assert.All(s.DexSpeciesList, f => Assert.False(f.IsRaising));
    });

    /// A store holding one nameless legacy graduation (chain 1→2→3).
    private static CompanionStore StoreWithNamelessEntry()
    {
        var bare = new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: FixedNow);
        return Open(StateFiles.WriteText($$"""{"dex":{{StateFiles.Json([bare])}},"language":"ko"}"""));
    }

    /// Offline (line fetch fails) and nothing stored → every chain species falls back to #id.
    [Fact]
    public void DexResolveChainNamesOfflineFallback() => MainActor.Run(async () =>
    {
        var s = Open(StateFiles.TempPath(), new LineThrowsProvider());
        var bare = new DexEntry(baseID: 1, finalID: 3, chainOrder: [1, 2, 3], rarity: Rarity.Common, caughtAt: null);
        var names = await s.DexResolveChainNames(bare);
        AssertMap(new Dictionary<int, string> { [1] = "#1", [2] = "#2", [3] = "#3" }, names);
    });

    // MARK: Usage ledger

    private static void Base(CompanionStore s) => UseMap(s, new() { ["test"] = 0 });
    private static void Use(CompanionStore s, long today, bool hasUsageData = true) =>
        UseMap(s, new() { ["test"] = today }, hasUsageData: hasUsageData);
    private static void UseMap(CompanionStore s, Dictionary<string, long> today, string date = "d1", bool hasUsageData = true) =>
        s.Update(today, date, 0, BurnTier.Idle, false, hasUsageData);

    [Fact]
    public void InstallBaselineExcludesPreInstallUsage() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        // Before data arrives → no baseline.
        Use(s, 0, hasUsageData: false);
        Assert.False(s.State.InstallBaselineSet);
        // First real data → baseline = today at that moment.
        Use(s, 48_000_000);
        Assert.True(s.State.InstallBaselineSet);
        Assert.Equal(0, s.State.UsedSinceInstall);
        // Only later increases accumulate.
        Use(s, 148_000_000);
        Assert.Equal(100_000_000, s.State.UsedSinceInstall);
    });

    /// [regression] After a valid drop in the local re-aggregation, increases past the new baseline still count.
    [Fact]
    public void UsageIncreaseAfterValidDropContinuesEggProgress() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        Base(s);
        Use(s, 200);
        Assert.Equal(200, s.State.EggUsage);

        Use(s, 40);
        Assert.Equal(200, s.State.EggUsage);
        Assert.Equal(40, s.State.ClaimedTodayTokensByProvider?["test"]);

        Use(s, 75);
        Assert.Equal(235, s.State.EggUsage);
        Assert.Equal(235, s.State.UsedSinceInstall);
    });

    /// [regression] A data gap (today=0 without data) does not lower the daily baseline to 0.
    [Fact]
    public void EmptyUsageSnapshotDoesNotRebaseDailyLedger() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        Base(s);
        Use(s, 200);

        Use(s, 0, hasUsageData: false);
        Assert.Equal(200, s.State.EggUsage);
        Assert.Equal(200, s.State.ClaimedTodayTokensByProvider?["test"]);

        Use(s, 250);
        Assert.Equal(250, s.State.EggUsage);
        Assert.Equal(250, s.State.UsedSinceInstall);
    });

    /// [regression] A provider missing for one refresh is not re-credited when it comes back.
    [Fact]
    public void ProviderLedgerDoesNotRecreditMissingProviderAfterPartialSnapshotLoss() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        UseMap(s, new() { ["claude_code"] = 0, ["codex"] = 0 });
        UseMap(s, new() { ["claude_code"] = 1_000, ["codex"] = 500 });
        Assert.Equal(1_500, s.State.UsedSinceInstall);

        UseMap(s, new() { ["claude_code"] = 1_000 });
        Assert.Equal(1_500, s.State.UsedSinceInstall);
        AssertMap(new Dictionary<string, long> { ["claude_code"] = 1_000, ["codex"] = 500 }, s.State.ClaimedTodayTokensByProvider);

        UseMap(s, new() { ["claude_code"] = 1_000, ["codex"] = 500 });
        Assert.Equal(1_500, s.State.UsedSinceInstall);
        UseMap(s, new() { ["claude_code"] = 1_000, ["codex"] = 700 });
        Assert.Equal(1_700, s.State.UsedSinceInstall);
    });

    /// [regression] An empty map from carrier-only refreshes does not lower provider baselines.
    [Fact]
    public void CarrierWithoutTodayDataDoesNotRebaseProviderLedger() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        UseMap(s, new() { ["codex"] = 0 });
        UseMap(s, new() { ["codex"] = 2_000 });
        Assert.Equal(2_000, s.State.UsedSinceInstall);

        UseMap(s, new(), hasUsageData: false);
        Assert.Equal(2_000, s.State.UsedSinceInstall);
        AssertMap(new Dictionary<string, long> { ["codex"] = 2_000 }, s.State.ClaimedTodayTokensByProvider);

        UseMap(s, new() { ["codex"] = 2_200 });
        Assert.Equal(2_200, s.State.UsedSinceInstall);
    });

    /// [regression] On a new date the whole current-day cumulative value is credited, then increases.
    [Fact]
    public void DateRolloverCreditsCurrentDayUsage() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        UseMap(s, new() { ["codex"] = 0 }, "d1");
        UseMap(s, new() { ["codex"] = 200 }, "d1");
        Assert.Equal(200, s.State.UsedSinceInstall);

        UseMap(s, new() { ["codex"] = 100 }, "d2");
        Assert.Equal(300, s.State.UsedSinceInstall);
        AssertMap(new Dictionary<string, long> { ["codex"] = 100 }, s.State.ClaimedTodayTokensByProvider);

        UseMap(s, new() { ["codex"] = 150 }, "d2");
        Assert.Equal(350, s.State.UsedSinceInstall);
    });

    /// [regression] A provider missing from the first refresh after midnight is credited in full when it recovers.
    [Fact]
    public void LateProviderRecoveryAfterDateRolloverCreditsCurrentDayUsage() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        UseMap(s, new() { ["claude_code"] = 0, ["codex"] = 0 }, "d1");
        UseMap(s, new() { ["claude_code"] = 1_000, ["codex"] = 500 }, "d1");
        Assert.Equal(1_500, s.State.UsedSinceInstall);

        UseMap(s, new() { ["claude_code"] = 100 }, "d2");
        Assert.Equal(1_600, s.State.UsedSinceInstall);
        AssertMap(new Dictionary<string, long> { ["claude_code"] = 100, ["codex"] = 0 }, s.State.ClaimedTodayTokensByProvider);

        UseMap(s, new() { ["claude_code"] = 100, ["codex"] = 700 }, "d2");
        Assert.Equal(2_300, s.State.UsedSinceInstall);

        UseMap(s, new() { ["claude_code"] = 100, ["codex"] = 900 }, "d2");
        Assert.Equal(2_500, s.State.UsedSinceInstall);
    });

    /// [regression] A stale refresh with an empty map does not consume the date boundary.
    [Fact]
    public void StaleSnapshotDoesNotConsumeDateBoundary() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        UseMap(s, new() { ["codex"] = 0 }, "d1");
        UseMap(s, new() { ["codex"] = 200 }, "d1");

        UseMap(s, new(), "d2", hasUsageData: true);
        Assert.Equal(200, s.State.UsedSinceInstall);
        Assert.Equal("d1", s.State.LastDate);
        AssertMap(new Dictionary<string, long> { ["codex"] = 200 }, s.State.ClaimedTodayTokensByProvider);

        UseMap(s, new() { ["codex"] = 100 }, "d2");
        Assert.Equal(300, s.State.UsedSinceInstall);
    });

    /// [migration] An old save with only the aggregate high-water mark seeds the provider map from the first
    /// valid snapshot, without retroactive credit.
    [Fact]
    public void LegacyAggregateLedgerSeedsProviderMapWithoutRetrospectiveCredit() => MainActor.Run(() =>
    {
        var url = StateFiles.WriteText("""{"installBaselineSet":true,"usedSinceInstall":10000,"claimedTodayTokens":9000,"lastDate":"d1"}""");
        var s = Open(url);
        Assert.Null(s.State.ClaimedTodayTokensByProvider);

        s.Update(new Dictionary<string, long> { ["codex"] = 500 }, "d1", 0, BurnTier.Idle, false, true);
        Assert.Equal(10_000, s.State.UsedSinceInstall);
        AssertMap(new Dictionary<string, long> { ["codex"] = 500 }, s.State.ClaimedTodayTokensByProvider);

        s.Update(new Dictionary<string, long> { ["codex"] = 700 }, "d1", 0, BurnTier.Idle, false, true);
        Assert.Equal(10_200, s.State.UsedSinceInstall);
    });

    // MARK: Hatching

    [Fact]
    public void EggDoesNotHatchBelowThreshold() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        Base(s);
        Use(s, 500_000);
        Assert.Equal(500_000, s.State.EggUsage);
        Assert.True(s.IsEgg);
        await s.HatchIfNeeded();
        Assert.Null(s.State.Active);
    });

    [Fact]
    public void EggHatchesAtThreshold() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        Base(s);
        Use(s, PokemonBalance.EggHatchThreshold);
        Assert.Equal(PokemonBalance.EggHatchThreshold, s.State.EggUsage);
        await s.HatchIfNeeded();
        Assert.NotNull(s.State.Active);
        Assert.Equal(0, s.State.EggUsage);
    });

    /// [regression] The hatched Pokémon shows in the dex before graduating, without a duplicate afterwards.
    [Fact]
    public void ActiveCompanionAppearsInDexBeforeGraduationWithoutDuplicate() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);

        Assert.Empty(s.State.Dex);
        Assert.Single(s.DexEntries);
        Assert.Equal([1], s.DexEntries[0].ChainOrder);
        Assert.Equal(1, s.DexEntries[0].FinalID);

        s.ApplyUsage(Phase(0));
        Assert.Single(s.DexEntries);
        Assert.Equal([1, 2], s.DexEntries[0].ChainOrder);
        Assert.Equal(2, s.DexEntries[0].FinalID);

        s.ApplyUsage(Phase(1));
        s.ApplyUsage(Phase(2));
        Assert.Null(s.State.Active);
        Assert.Single(s.State.Dex);
        Assert.Single(s.DexEntries);
        Assert.Equal([1, 2, 3], s.DexEntries[0].ChainOrder);
    });

    /// The user-reported restart state: active saved but dex=[] must not show an empty Pokédex.
    [Fact]
    public void LoadedActiveCompanionPreventsEmptyDexState() => MainActor.Run(() =>
    {
        const string json = """{"active":{"baseID":529,"pathIDs":[529],"stageIndex":0,"usedAtStage":148344233,"rarity":"uncommon","totalForms":2,"isShiny":false,"nature":"timid"},"dex":[]}""";
        var s = Open(StateFiles.WriteText(json, StateFiles.TempPath("poke-active")));
        Assert.Empty(s.State.Dex);
        Assert.Single(s.DexEntries);
        Assert.Equal(529, s.DexEntries[0].BaseID);
        Assert.Equal(1, s.DexCount(Rarity.Uncommon));
    });

    /// [regression] The raised common Pokémon is pinned above rarer graduations; caughtAt=nil is not "active".
    [Fact]
    public void ActiveCompanionPinnedBeforeGraduatedEntries() => MainActor.Run(() =>
    {
        const string json = """{"active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":5,"rarity":"common","totalForms":3},"dex":[{"id":"legacy-graduated","baseID":150,"finalID":150,"chainOrder":[150],"rarity":"legendary"}]}""";
        var s = Open(StateFiles.WriteText(json, StateFiles.TempPath("poke-active-sort")));
        var sorted = s.DexEntriesSorted;

        Assert.Equal(["active-1-1", "legacy-graduated"], sorted.Select(e => e.Id));
        Assert.True(s.IsActiveDexEntry(sorted[0]));
        Assert.False(s.IsActiveDexEntry(sorted[1]));
    });

    [Fact]
    public void EggOverflowCarriesToHatchedMon() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        Base(s);
        Use(s, PokemonBalance.EggHatchThreshold + 500_000);
        await s.HatchIfNeeded();
        Assert.Equal(500_000, s.State.Active?.UsedAtStage);
    });

    /// The REST fallback hatches even when the GraphQL base index endpoint is down (2026-07 outage).
    [Fact]
    public void EggHatchesViaRestFallbackWhenIndexDown() => MainActor.Run(async () =>
    {
        var s = Open(StateFiles.TempPath(), new FallbackOnlyProvider());
        Base(s);
        Use(s, PokemonBalance.EggHatchThreshold);
        await s.HatchIfNeeded();
        Assert.NotNull(s.State.Active);
        Assert.Equal(0, s.State.EggUsage);
    });

    [Fact]
    public void NewEggAfterGraduationReincubates() => MainActor.Run(async () =>
    {
        var s = Store(Lines.NoEvo);
        Base(s);
        Use(s, PokemonBalance.EggHatchThreshold);
        await s.HatchIfNeeded();
        Assert.NotNull(s.State.Active);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common)); // no-evolution graduation
        Assert.Null(s.State.Active);
        Assert.Equal(0, s.State.EggUsage);
        await s.HatchIfNeeded(); // eggUsage=0 → no immediate hatch
        Assert.Null(s.State.Active);
    });
}
