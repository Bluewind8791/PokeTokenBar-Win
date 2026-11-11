using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of CompanionTests.swift, second half (testNewEggAfterGraduationReincubates → end):
// the rest of CompanionStoreTests, AppLanguageTests (persistence part), DexSortingTests,
// CompanionIdentityTests and PokeAPIGuardTests.

file static class H
{
    public static EvoLine Wurmple => Lines.Make(265, Lines.Node(265, Lines.Node(266, Lines.Node(267)), Lines.Node(268, Lines.Node(269))));
    public static EvoLine DelayedBranch => Lines.Make(43, Lines.Node(43, Lines.Node(44, Lines.Node(45), Lines.Node(182))));

    public static CompanionStore Store(EvoLine line, ulong seed = 7) => StateFiles.Store(line, seed);

    public static CompanionStore Store(IPokeProviding provider, IRandomNumberGenerator rng, string? path = null,
        Func<DateTimeOffset>? clock = null) =>
        new(provider, clock: clock ?? (() => Lines.FixedNow), filePath: path ?? StateFiles.TempPath(), rng: rng,
            dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));

    public static void Use(CompanionStore s, long today, bool hasUsageData = true) =>
        s.Update(new Dictionary<string, long> { ["test"] = today }, "d1", 0, BurnTier.Idle, false, hasUsageData);

    public static void Base(CompanionStore s) => Use(s, 0);

    public static EvoLineItem Item(int id, EvoLineItemState state) => new(EvoLineItemContent.Species(id), state);
    public static EvoLineItem Mystery(EvoLineItemState state) => new(EvoLineItemContent.Mystery, state);

    public static long Phase(int forms, int stage) => PokemonBalance.PhaseThreshold(Rarity.Common, forms, stage);

    public static bool IsOneOf(IEnumerable<int>? plan, params int[][] options) =>
        plan != null && options.Any(o => o.SequenceEqual(plan));
}

file sealed class SuspendedLineProvider(EvoLine value) : IPokeProviding
{
    private TaskCompletionSource<EvoLine>? _pending;
    public bool IsSuspended { get; private set; }

    public Task<EvoLine> Line(int baseSpeciesID)
    {
        if (_pending != null) throw new InvalidOperationException("only one line request may be suspended");
        _pending = new TaskCompletionSource<EvoLine>();
        IsSuspended = true;
        return _pending.Task;
    }

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(value.BaseID, 255)]);

    public void Resume()
    {
        var pending = _pending;
        _pending = null;
        IsSuspended = false;
        pending?.SetResult(value);
    }
}

file sealed class SuspendedFailingIndexProvider : IPokeProviding
{
    private TaskCompletionSource<IReadOnlyList<BaseSpecies>>? _pending;
    private bool _failNextRequest;
    public bool IsSuspended => _pending != null;

    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new HttpRequestException("offline"));

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex()
    {
        if (_failNextRequest)
        {
            _failNextRequest = false;
            return Task.FromException<IReadOnlyList<BaseSpecies>>(new HttpRequestException("offline"));
        }
        if (_pending != null) throw new InvalidOperationException("only one index request may be suspended");
        _pending = new TaskCompletionSource<IReadOnlyList<BaseSpecies>>();
        return _pending.Task;
    }

    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromException<BaseSpecies?>(new HttpRequestException("offline"));

    public void ResumeFailure()
    {
        if (_pending is not { } pending)
        {
            _failNextRequest = true;
            return;
        }
        _pending = null;
        pending.SetException(new HttpRequestException("offline"));
    }
}

file sealed class SuspendedFailingLineProvider : IPokeProviding
{
    private TaskCompletionSource<EvoLine>? _pending;
    private bool _failNextRequest;
    public bool IsSuspended => _pending != null;

    public Task<EvoLine> Line(int baseSpeciesID)
    {
        if (_failNextRequest)
        {
            _failNextRequest = false;
            return Task.FromException<EvoLine>(new HttpRequestException("offline"));
        }
        if (_pending != null) throw new InvalidOperationException("only one line request may be suspended");
        _pending = new TaskCompletionSource<EvoLine>();
        return _pending.Task;
    }

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(25, 255)]);

    public void ResumeFailure()
    {
        if (_pending is not { } pending)
        {
            _failNextRequest = true;
            return;
        }
        _pending = null;
        pending.SetException(new HttpRequestException("offline"));
    }
}

/// <summary>Swift <c>MutableProvider</c>: the line can be swapped before each call.</summary>
file sealed class MutableProvider : IPokeProviding
{
    public EvoLine CurrentLine { get; set; } = Lines.Make(1, Lines.Node(1));
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(CurrentLine);
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(CurrentLine.BaseID, 255)]);
}

file sealed class FailingPokeProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new HttpRequestException("offline"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new HttpRequestException("offline"));
}

public class CompanionStoreTests2
{
    [Fact]
    public void NewEggAfterGraduationReincubates() => MainActor.Run(async () =>
    {
        var s = H.Store(Lines.NoEvo);
        H.Base(s);
        H.Use(s, PokemonBalance.EggHatchThreshold);
        await s.HatchIfNeeded();
        Assert.NotNull(s.State.Active);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common)); // no-evolution graduation
        Assert.Null(s.State.Active);
        Assert.Equal(0, s.State.EggUsage); // new egg incubation reset
        await s.HatchIfNeeded(); // eggUsage = 0 → no immediate hatch
        Assert.Null(s.State.Active);
    });

    [Fact]
    public void StateDecodesWithoutEggUsage()
    {
        // Old saves (no field) load with eggUsage = 0 without breaking.
        var json = """{"installBaselineSet":true,"usedSinceInstall":5,"claimedTodayTokens":5,"lastDate":"d","active":null,"dex":[],"collectedFinals":[],"language":"ko"}""";
        var state = CompanionJson.Deserialize(json);
        Assert.Equal(0, state.EggUsage);
        Assert.Equal(5, state.UsedSinceInstall);
        Assert.Null(state.ClaimedTodayTokensByProvider);
    }

    [Fact]
    public void EvolvesThroughLineAndGraduatesWithFullChain() => MainActor.Run(async () =>
    {
        var s = H.Store(Lines.Linear3);
        s.SetLanguage(AppLanguage.Ko);
        await s.Hatch(1);
        Assert.Equal(1, s.CurrentSpeciesID);
        Assert.Equal(3, s.State.Active?.TotalForms);
        s.ApplyUsage(H.Phase(3, 0)); // →2
        Assert.Equal(2, s.CurrentSpeciesID);
        s.ApplyUsage(H.Phase(3, 1)); // →3 (final)
        Assert.Equal(3, s.CurrentSpeciesID);
        Assert.True(s.IsFinalStage);
        s.ApplyUsage(H.Phase(3, 2)); // graduate
        Assert.Null(s.State.Active);
        Assert.Single(s.DexEntries);
        Assert.Equal([1, 2, 3], s.DexEntries[0].ChainOrder); // whole line kept
        Assert.Equal("포3", s.JustGraduated);
    });

    [Fact]
    public void NoEvolutionGraduatesAtSingleThreshold() => MainActor.Run(async () =>
    {
        var s = H.Store(Lines.NoEvo);
        await s.Hatch(20);
        Assert.True(s.IsFinalStage);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common));
        Assert.Single(s.DexEntries);
        Assert.Equal([20], s.DexEntries[0].ChainOrder);
    });

    [Fact]
    public void LineNodesPreviewsCompleteLinearEvolution() => MainActor.Run(async () =>
    {
        var s = H.Store(Lines.Linear3);
        await s.Hatch(1);
        Assert.Equal([
            H.Item(1, EvoLineItemState.Current),
            H.Item(2, EvoLineItemState.Future),
            H.Item(3, EvoLineItemState.Future),
        ], s.LineNodes);
    });

    [Fact]
    public void RealizedLineItemsUsesStageIndexForCurrentMarker()
    {
        Assert.Equal([
            H.Item(1, EvoLineItemState.Current),
            H.Item(2, EvoLineItemState.Done),
        ], CompanionStore.RealizedLineItems([1, 2], 0));
    }

    [Fact]
    public void RepairedPlanAppendsFallbackRouteToCurrentPath()
    {
        Assert.Equal([265, 266, 267], CompanionStore.RepairedPlan([265], 0, [265, 266, 267]));
    }

    [Fact]
    public void LineNodesHidesUnresolvedWurmpleBranchAsSingleMystery() => MainActor.Run(async () =>
    {
        var s = H.Store(H.Wurmple);
        await s.Hatch(265);
        Assert.Equal([H.Item(265, EvoLineItemState.Current), H.Mystery(EvoLineItemState.Future)], s.LineNodes);
    });

    [Fact]
    public void LineNodesShowsKnownPrefixBeforeDownstreamBranchAsMystery() => MainActor.Run(async () =>
    {
        var s = H.Store(H.DelayedBranch);
        await s.Hatch(43);
        Assert.Equal([
            H.Item(43, EvoLineItemState.Current),
            H.Item(44, EvoLineItemState.Future),
            H.Mystery(EvoLineItemState.Future),
        ], s.LineNodes);
    });

    [Fact]
    public void LineNodesRevealsChosenWurmpleBranchAfterEvolution() => MainActor.Run(async () =>
    {
        var s = H.Store(H.Wurmple);
        await s.Hatch(265);
        var plan = s.State.Active!.PlannedPathIDs.ToList();
        Assert.Equal(3, plan.Count);
        s.ApplyUsage(H.Phase(plan.Count, 0));
        Assert.Equal([
            H.Item(265, EvoLineItemState.Done),
            H.Item(plan[1], EvoLineItemState.Current),
            H.Item(plan[2], EvoLineItemState.Future),
        ], s.LineNodes);
    });

    [Fact]
    public void BranchingPrefersUncollectedFinals() => MainActor.Run(async () =>
    {
        var s = H.Store(Lines.Branch3);
        var evo = H.Phase(2, 0);
        var grad = H.Phase(2, 1);
        var finals = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            await s.Hatch(10);
            s.ApplyUsage(evo);  // branch evolution
            s.ApplyUsage(grad); // graduate
            finals.Add(s.DexEntries[^1].FinalID);
        }
        Assert.Equal(3, finals.ToHashSet().Count); // a different branch on every re-hatch of the same base
        Assert.Equal(new HashSet<int> { 11, 12, 13 }, finals.ToHashSet());
    });

    [Fact]
    public void RepeatGrowthIsDecidedFromTheCollectedBaseNotThePlannedFinal() => MainActor.Run(async () =>
    {
        var s = H.Store(Lines.Branch3);
        var evolution = H.Phase(2, 0);
        var graduation = H.Phase(2, 1);

        await s.Hatch(10);
        Assert.Null(s.GrowthMultiplier);
        var firstFinal = s.State.Active!.PlannedPathIDs[^1];
        s.ApplyUsage(evolution);
        s.ApplyUsage(graduation);

        await s.Hatch(10);
        var repeatMon = s.State.Active!;
        var repeatFinal = repeatMon.PlannedPathIDs[^1];
        Assert.NotEqual(firstFinal, repeatFinal);
        Assert.True(repeatMon.HasGrowthBoost);
        Assert.Equal(PokemonBalance.RepeatGrowthMultiplier, s.GrowthMultiplier);
        Assert.Equal(evolution / PokemonBalance.RepeatGrowthMultiplier, s.Threshold);
        Assert.Equal(evolution / PokemonBalance.RepeatGrowthMultiplier, s.TokensToNext);
    });

    [Fact]
    public void HatchPreselectsWurmpleRouteAndEvolutionDoesNotConsumeRng() => MainActor.Run(async () =>
    {
        var rng = new CountingRng(7);
        var s = H.Store(new StubProvider(H.Wurmple), rng);

        await s.Hatch(265);

        var hatched = s.State.Active;
        Assert.NotNull(hatched); // Wurmple should hatch
        var plan = hatched.PlannedPathIDs.ToList();
        Assert.True(H.IsOneOf(plan, [265, 266, 267], [265, 268, 269]), "plan must be one complete root-to-leaf route");
        Assert.Equal([265], hatched.PathIDs); // realized path starts at the base only
        Assert.Equal(plan.Count, hatched.TotalForms);

        var callsAfterHatch = rng.CallCount;
        s.ApplyUsage(PokemonBalance.PhaseThreshold(hatched.Rarity, hatched.TotalForms, 0));

        Assert.Equal(callsAfterHatch, rng.CallCount); // evolution must consume the stored plan without rolling RNG
        Assert.Equal(plan.Take(2), s.State.Active!.PathIDs);
        Assert.Equal(plan[1], s.CurrentSpeciesID);
    });

    [Fact]
    public void PersistenceRoundTrip() => MainActor.Run(async () =>
    {
        var path = StateFiles.TempPath("poke-persist");
        var s1 = H.Store(new StubProvider(Lines.Linear3), new SeededRng(1), path);
        await s1.Hatch(1);
        s1.ApplyUsage(H.Phase(3, 0));
        s1.SetLanguage(AppLanguage.Ja);
        var s2 = H.Store(new StubProvider(Lines.Linear3), new SeededRng(1), path);
        Assert.Equal(2, s2.State.Active?.CurrentID);
        Assert.Equal(1, s2.State.Active?.StageIndex);
        Assert.Equal(AppLanguage.Ja, s2.Language);
    });

    [Fact]
    public void RepeatGrowthPersistsAcrossRestartWhileLegacyActiveDefaultsToStandardGrowth() => MainActor.Run(async () =>
    {
        var path = StateFiles.TempPath("poke-repeat-persist");
        var s1 = H.Store(new StubProvider(Lines.Linear3), new SeededRng(1), path);
        await s1.Hatch(1);
        for (var stage = 0; stage < 3; stage++) s1.ApplyUsage(H.Phase(3, stage));
        await s1.Hatch(1);
        Assert.True(s1.State.Active?.HasGrowthBoost == true);

        var s2 = H.Store(new StubProvider(Lines.Linear3), new SeededRng(1), path);
        Assert.True(s2.State.Active?.HasGrowthBoost == true);
        Assert.Equal(62_500_000, s2.TokensToNext);

        var legacy = """{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":3}""";
        using var doc = System.Text.Json.JsonDocument.Parse(legacy);
        var decoded = CompanionJson.DecodeMon(doc.RootElement);
        Assert.False(decoded.HasGrowthBoost);
        Assert.Equal(125_000_000, decoded.PhaseThreshold);
    });

    [Fact]
    public void ReloadPreservesCompleteShortPlannedRouteLength() => MainActor.Run(async () =>
    {
        var line = Lines.Make(1, Lines.Node(1, Lines.Node(2), Lines.Node(3, Lines.Node(4))));
        var path = StateFiles.TempPath("poke-reload-plan");
        var s1 = H.Store(new StubProvider(line), new SeededRng(7), path);
        await s1.Hatch(1);
        Assert.Equal([1, 2], s1.State.Active?.PlannedPathIDs); // seed selects the short complete route

        var opposing = new SeededRng(1);
        Assert.Equal(1UL, opposing.Next() % 2); // a reroll would select the opposite branch (3)
        var rng = new CountingRng(1);
        var s2 = H.Store(new StubProvider(line), rng, path);
        H.Base(s2);
        Assert.True(await MainActor.WaitUntil(() => s2.CurrentLine != null), "line should load before evolution");

        Assert.Equal([1, 2], s2.State.Active?.PlannedPathIDs);
        Assert.Equal(2, s2.State.Active?.TotalForms);
        var callsAfterLoad = rng.CallCount;
        s2.ApplyUsage(H.Phase(2, 0));
        Assert.Equal(2, s2.CurrentSpeciesID); // persisted route must beat the post-restart RNG branch
        Assert.Equal(callsAfterLoad, rng.CallCount); // load and evolution must not reroll the persisted route
    });

    [Fact]
    public void ReloadLegacyIncompletePlanMigratesToPersistedCompleteRoute() => MainActor.Run(async () =>
    {
        var path = StateFiles.WriteText("""{"active":{"baseID":265,"pathIDs":[265],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1}}""");
        var rng = new CountingRng(7);
        var s = H.Store(new StubProvider(H.Wurmple), rng, path);

        H.Base(s);
        Assert.True(await MainActor.WaitUntil(() => s.CurrentLine != null), "line should load legacy state");

        var migratedPlan = s.State.Active?.PlannedPathIDs.ToList();
        Assert.True(H.IsOneOf(migratedPlan, [265, 266, 267], [265, 268, 269]));
        Assert.Equal([265], s.State.Active?.PathIDs); // realized path must remain intact
        Assert.Equal(migratedPlan?.Count, s.State.Active?.TotalForms);
        Assert.Equal(2, rng.CallCount); // Wurmple suffix selection rolls once per evolution edge

        var reloadRng = new CountingRng(1);
        var reloaded = H.Store(new StubProvider(H.Wurmple), reloadRng, path);
        H.Base(reloaded);
        Assert.True(await MainActor.WaitUntil(() => reloaded.CurrentLine != null));
        Assert.Equal(migratedPlan, reloaded.State.Active?.PlannedPathIDs); // migration must persist its one-time choice
        Assert.Equal(0, reloadRng.CallCount); // a persisted complete route must not reroll on restart
    });

    [Fact]
    public void ReloadRepairsInvalidPlanSuffixWithoutRewindingRealizedPath() => MainActor.Run(async () =>
    {
        var path = StateFiles.WriteText("""{"active":{"baseID":265,"pathIDs":[265,266],"plannedPathIDs":[265,266,269],"stageIndex":1,"usedAtStage":42,"rarity":"common","totalForms":3}}""");
        var s = H.Store(new StubProvider(H.Wurmple), new SeededRng(9), path);

        H.Base(s);
        Assert.True(await MainActor.WaitUntil(() => s.CurrentLine != null));

        Assert.Equal([265, 266], s.State.Active?.PathIDs);
        Assert.Equal([265, 266, 267], s.State.Active?.PlannedPathIDs);
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(3, s.State.Active?.TotalForms);
        Assert.Equal(42, s.State.Active?.UsedAtStage);
    });

    [Fact]
    public void ReloadWrongRootNormalizesPathWithoutChangingIdentityOrDisguise() => MainActor.Run(async () =>
    {
        var path = StateFiles.WriteText("""{"active":{"baseID":265,"pathIDs":[999],"plannedPathIDs":[999],"stageIndex":0,"usedAtStage":42,"rarity":"common","totalForms":1,"isShiny":true,"nature":"timid","dittoDisguise":265}}""");
        var s = H.Store(new StubProvider(H.Wurmple), new SeededRng(9), path);

        H.Base(s);
        Assert.True(await MainActor.WaitUntil(() => s.CurrentLine != null));

        Assert.Equal([265], s.State.Active?.PathIDs);
        Assert.True(H.IsOneOf(s.State.Active?.PlannedPathIDs, [265, 266, 267], [265, 268, 269]));
        Assert.Equal(42, s.State.Active?.UsedAtStage);
        Assert.True(s.State.Active?.IsShiny ?? false);
        Assert.Equal(PokemonNature.Timid, s.State.Active?.Nature);
        Assert.Equal(265, s.State.Active?.DittoDisguise);
        Assert.False(s.State.Active?.DittoRevealed ?? true);
    });

    [Fact]
    public void ReloadLeafCurrentPlanDoesNotConsumeRng() => MainActor.Run(async () =>
    {
        var path = StateFiles.WriteText("""{"active":{"baseID":265,"pathIDs":[265,266,267],"plannedPathIDs":[265,266,267],"stageIndex":2,"usedAtStage":42,"rarity":"common","totalForms":3}}""");
        var rng = new CountingRng(9);
        var s = H.Store(new StubProvider(H.Wurmple), rng, path);

        H.Base(s);
        Assert.True(await MainActor.WaitUntil(() => s.CurrentLine != null));

        Assert.Equal([265, 266, 267], s.State.Active?.PathIDs);
        Assert.Equal([265, 266, 267], s.State.Active?.PlannedPathIDs);
        Assert.Equal(0, rng.CallCount);
    });

    [Fact]
    public void LineLoadPreservesUpdatesMadeWhileProviderIsSuspended() => MainActor.Run(async () =>
    {
        var path = StateFiles.WriteText("""{"active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1,"nature":"adamant"},"inventory":{"mint":1}}""");
        var provider = new SuspendedLineProvider(Lines.Linear3);
        var s = H.Store(provider, new SeededRng(7), path);

        H.Base(s);
        Assert.True(await MainActor.WaitUntil(() => provider.IsSuspended), "line fetch should be suspended");

        s.ApplyUsage(42);
        var changedNature = s.UseMint();
        Assert.NotNull(changedNature);
        Assert.Equal(42, s.State.Active?.UsedAtStage);
        Assert.Equal(changedNature, s.State.Active?.Nature);

        provider.Resume();
        Assert.True(await MainActor.WaitUntil(() => s.CurrentLine != null));

        Assert.Equal(42, s.State.Active?.UsedAtStage);
        Assert.Equal(changedNature, s.State.Active?.Nature);
        var persisted = CompanionJson.Deserialize(File.ReadAllBytes(path));
        Assert.Equal(42, persisted.Active?.UsedAtStage);
        Assert.Equal(changedNature, persisted.Active?.Nature);
    });

    [Fact]
    public void LocalizedName() => MainActor.Run(async () =>
    {
        var s = H.Store(Lines.Linear3);
        await s.Hatch(1);
        s.SetLanguage(AppLanguage.Ko);
        Assert.Equal("포1", s.DisplayName);
        s.SetLanguage(AppLanguage.En);
        Assert.Equal("P1", s.DisplayName);
        s.SetLanguage(AppLanguage.Ja);
        Assert.Equal("ポ1", s.DisplayName);
    });

    /// <summary>[Documentation] An asymmetric-depth branch fixes totalForms to the route chosen at hatch and
    /// graduates at the final form without crashing or looping, keeping the real path.</summary>
    [Fact]
    public void AsymmetricBranchGraduatesSafely() => MainActor.Run(async () =>
    {
        var line = Lines.Make(1, Lines.Node(1, Lines.Node(2), Lines.Node(3, Lines.Node(4)))); // depth 3, branch {2, 3→4}
        var s = H.Store(new StubProvider(line), new SeededRng(7));
        await s.Hatch(1);
        Assert.Equal(s.State.Active?.PlannedPathIDs.Count, s.State.Active?.TotalForms);
        var guardCount = 0;
        while (s.State.Active != null && guardCount < 12)
        {
            guardCount += 1;
            var stage = s.State.Active.StageIndex;
            s.ApplyUsage(PokemonBalance.PhaseThreshold(Rarity.Common, s.State.Active.TotalForms, stage));
        }
        Assert.Null(s.State.Active); // graduates at a final form on either branch
        Assert.Single(s.DexEntries);
        var chain = s.DexEntries[0].ChainOrder;
        Assert.True(H.IsOneOf(chain, [1, 2], [1, 3, 4]), $"real evolution path kept: {string.Join(",", chain)}");
    });
}

/// <summary>Persistence part of AppLanguageTests (metadata/system-locale parts belong to the localization port).</summary>
public class AppLanguageStateTests2
{
    [Theory]
    [InlineData(AppLanguage.De, "de")]
    [InlineData(AppLanguage.Ru, "ru")]
    public void LanguagePersistsInCompanionState(AppLanguage language, string raw)
    {
        var state = new CompanionState { Language = language };
        var data = CompanionJson.Serialize(state);
        Assert.Contains($"\"language\":\"{raw}\"", System.Text.Encoding.UTF8.GetString(data));
        Assert.Equal(language, CompanionJson.Deserialize(data).Language);
    }
}

public class DexSortingTests2
{
    /// <summary>SortRank is not a list key (the log is by time), but the premium egg rarity gate uses it.</summary>
    [Fact]
    public void SortRankOrdersRarityAscendingByValue()
    {
        Assert.True(Rarity.Common.SortRank() < Rarity.Uncommon.SortRank());
        Assert.True(Rarity.Uncommon.SortRank() < Rarity.Rare.SortRank());
        Assert.True(Rarity.Rare.SortRank() < Rarity.Legendary.SortRank());
    }

    /// <summary>The log is a time record: higher rarity still sinks when older. The legendary graduates first so a
    /// rarity-first order would fail.</summary>
    [Fact]
    public void DexEntriesSortedByRecencyRegardlessOfRarity() => MainActor.Run(async () =>
    {
        var tick = 0;
        var provider = new MutableProvider();
        var s = H.Store(provider, new SeededRng(3), clock: () => Lines.FixedNow.AddSeconds(tick));

        provider.CurrentLine = Lines.Make(200, Lines.Node(200), Rarity.Legendary);
        tick = 1;
        await s.Hatch(200);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Legendary));

        provider.CurrentLine = Lines.Make(100, Lines.Node(100), Rarity.Common);
        tick = 2;
        await s.Hatch(100);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common));

        provider.CurrentLine = Lines.Make(101, Lines.Node(101), Rarity.Common);
        tick = 3;
        await s.Hatch(101);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common));

        Assert.Equal(3, s.DexEntries.Count);
        var sorted = s.DexEntriesSorted;
        Assert.Equal([101, 100, 200], sorted.Select(e => e.FinalID));
        Assert.Equal(Rarity.Legendary, sorted[2].Rarity); // the oldest legendary is last

        Assert.Equal(2, s.DexCount(Rarity.Common));
        Assert.Equal(1, s.DexCount(Rarity.Legendary));
        Assert.Equal(0, s.DexCount(Rarity.Rare));
    });
}

public class CompanionIdentityTests2
{
    private static CompanionStore Store(EvoLine line, ulong seed) => StateFiles.Store(line, seed);

    /// <summary>Hatch(baseID) consumes RNG as shiny → nature; replaying the seed gives the expectation.</summary>
    private static (bool Shiny, PokemonNature Nature) ExpectedRoll(ulong seed)
    {
        var rng = new SeededRng(seed);
        var shiny = rng.Next() % PokemonOdds.ShinyDenominator == 0;
        var nature = PokemonNatureExtensions.AllCases[(int)(rng.Next() % (ulong)PokemonNatureExtensions.AllCases.Length)];
        return (shiny, nature);
    }

    [Fact]
    public void HatchAssignsDeterministicShinyAndNature() => MainActor.Run(async () =>
    {
        foreach (var seed in new ulong[] { 1, 7, 42, 12345 })
        {
            var s = Store(Lines.Linear3, seed);
            var expected = ExpectedRoll(seed);
            await s.Hatch(1);
            Assert.Equal(expected.Shiny, s.State.Active?.IsShiny);
            Assert.Equal(expected.Nature, s.State.Active?.Nature);
        }
    });

    [Fact]
    public void ShinyPathReachable() => MainActor.Run(async () =>
    {
        ulong? shinySeed = null;
        for (ulong seed = 0; seed < 5000; seed++)
            if (ExpectedRoll(seed).Shiny) { shinySeed = seed; break; }
        Assert.NotNull(shinySeed); // no shiny in 5000 seeds — check the denominator
        var s = Store(Lines.Linear3, shinySeed.Value);
        await s.Hatch(1);
        Assert.Equal(true, s.State.Active?.IsShiny);
        Assert.True(s.CurrentIsShiny);
    });

    [Fact]
    public void GraduateCarriesIdentityToDex() => MainActor.Run(async () =>
    {
        var s = Store(Lines.NoEvo, 3);
        await s.Hatch(20);
        var shiny = s.State.Active!.IsShiny;
        var nature = s.State.Active!.Nature;
        Assert.NotNull(nature);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common));
        Assert.Null(s.State.Active);
        Assert.Single(s.State.Dex);
        Assert.Equal(shiny, s.State.Dex[0].IsShiny);
        Assert.Equal(nature, s.State.Dex[0].Nature);
    });

    /// <summary>Old saves (no shiny/nature keys) decode with defaults (false/null).</summary>
    [Fact]
    public void BackwardCompatibleDecode()
    {
        var old = """
        {"installBaselineSet":true,"usedSinceInstall":100,"eggUsage":0,
         "claimedTodayTokens":100,"lastDate":"d1",
         "active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":5,"rarity":"common","totalForms":3},
         "dex":[{"id":"x","baseID":4,"finalID":6,"chainOrder":[4,5,6],"rarity":"rare"}],
         "collectedFinals":["4:6"],"language":"ko"}
        """;
        var s = CompanionJson.Deserialize(old);
        Assert.Equal([1], s.Active?.PlannedPathIDs);
        Assert.Equal(false, s.Active?.IsShiny);
        Assert.Null(s.Active?.Nature);
        Assert.Null(s.ClaimedTodayTokensByProvider); // the old aggregate ledger is not split per provider
        Assert.False(s.Dex[0].IsShiny);
        Assert.Null(s.Dex[0].Nature);
        // Re-encode and decode again (round trip).
        var round = CompanionJson.Deserialize(CompanionJson.Serialize(s));
        Assert.Equal(false, round.Active?.IsShiny);
    }

    /// <summary>[Release safety] Empty active.pathIDs: only that active falls back to null (egg); the rest stays.</summary>
    [Fact]
    public void EmptyPathIDsActiveFallsBackToNilPreservingRest()
    {
        var corrupt = """
        {"installBaselineSet":true,"eggUsage":0,"lastDate":"d1",
         "active":{"baseID":1,"pathIDs":[],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":3}}
        """;
        var state = CompanionJson.Deserialize(corrupt);
        Assert.Null(state.Active);
        Assert.True(state.InstallBaselineSet);
    }

    [Fact]
    public void CurrentIDFallsBackToBaseWhenPathEmpty()
    {
        var m = new MonState(42, [], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        Assert.Equal(42, m.CurrentID);
    }

    [Fact]
    public void SystemDefaultLanguageResolves()
    {
        Assert.Contains(AppLanguageExtensions.SystemDefault(), Enum.GetValues<AppLanguage>());
        Assert.Equal(AppLanguageExtensions.SystemDefault(), new CompanionState().Language);
    }

    [Fact]
    public void CelebrationFiresOnHatchAndEvolve() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3, 9);
        Assert.Equal(0, s.CelebrationSeq);
        await s.Hatch(1);
        Assert.Equal(1, s.CelebrationSeq);
        Assert.Equal(CompanionStore.CelebrationKind.Hatch, s.CurrentCelebration?.Kind);
        s.ConsumeCelebration();
        Assert.Null(s.CurrentCelebration);
        s.ApplyUsage(H.Phase(3, 0));
        Assert.Equal(2, s.CelebrationSeq);
        Assert.Equal(CompanionStore.Celebration.Evolved, s.CurrentCelebration);
    });

    /// <summary>[Regression] Deltas while the line is unloaded (restart/offline) are banked; evolution runs on load.</summary>
    [Fact]
    public void UsageAccruesWhileLineUnloadedThenEvolvesOnLoad() => MainActor.Run(async () =>
    {
        var path = StateFiles.TempPath();
        var s1 = H.Store(new StubProvider(Lines.Linear3), new SeededRng(5), path);
        H.Base(s1);
        await s1.Hatch(1);
        Assert.NotNull(s1.State.Active);

        // Restart: active loaded, CurrentLine null.
        var s2 = H.Store(new StubProvider(Lines.Linear3), new SeededRng(5), path);
        Assert.NotNull(s2.State.Active);
        Assert.Null(s2.CurrentLine);
        s2.ApplyUsage(300_000_000);
        Assert.Equal(300_000_000, s2.State.Active?.UsedAtStage); // must not be lost while the line is missing
        Assert.Equal(0, s2.State.Active?.StageIndex);
        H.Base(s2);
        Assert.True(await MainActor.WaitUntil(() => s2.CurrentLine != null));
        Assert.Equal(1, s2.State.Active?.StageIndex); // evolves from the banked usage after the line loads
        Assert.Equal(300_000_000 - 125_000_000, s2.State.Active?.UsedAtStage); // overflow carried over
    });

    /// <summary>[Regression] An old state past a GIF-less later evolution recovers to the last supported form.</summary>
    [Fact]
    public void LineLoadMigratesPersistedUnsupportedEvolution() => MainActor.Run(async () =>
    {
        var path = StateFiles.WriteText("""{"installBaselineSet":true,"lastDate":"d1","active":{"baseID":56,"pathIDs":[56,57,979],"stageIndex":2,"usedAtStage":123,"rarity":"common","totalForms":3},"dex":[]}""");
        var supportedLine = Lines.Make(56, Lines.Node(56, Lines.Node(57, Lines.Node(979))));
        var s = H.Store(new StubProvider(supportedLine), new SeededRng(5), path);

        H.Base(s);
        Assert.True(await MainActor.WaitUntil(() => s.CurrentLine != null));

        Assert.Equal([56, 57], s.State.Active?.PathIDs);
        Assert.Equal([56, 57], s.State.Active?.PlannedPathIDs);
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(2, s.State.Active?.TotalForms);
        Assert.Equal(123, s.State.Active?.UsedAtStage);
    });

    /// <summary>[Regression] An overflow evolution right after hatching must not cover the shiny hatch burst.</summary>
    [Fact]
    public void ShinyBurstSurvivesOverflowEvolve() => MainActor.Run(async () =>
    {
        // HatchIfNeeded consumes RNG as chooseBase(1) → shiny(2) → nature(3).
        static bool RollsShinyViaHatchIfNeeded(ulong seed)
        {
            var r = new SeededRng(seed);
            _ = r.Next(); // chooseBase: exactly one weighted roll
            return r.Next() % PokemonOdds.ShinyDenominator == 0;
        }
        ulong? found = null;
        for (ulong candidate = 0; candidate < 20000; candidate++)
            if (RollsShinyViaHatchIfNeeded(candidate)) { found = candidate; break; }
        Assert.NotNull(found);

        var s = Store(Lines.Linear3, found.Value);
        H.Base(s);
        // Egg threshold (5M) + stage-0 threshold (125M) exceeded → one evolution right after hatching.
        H.Use(s, 135_000_000);
        await s.HatchIfNeeded();
        Assert.Equal(true, s.State.Active?.IsShiny);
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(CompanionStore.Celebration.Hatched(true), s.CurrentCelebration);
    });

    /// <summary>[Regression] Overflow beyond the graduation total graduates at once — no hatch celebration.</summary>
    [Fact]
    public void HatchCelebrationSkippedOnInstantGraduate() => MainActor.Run(async () =>
    {
        var s = Store(Lines.NoEvo, 11);
        H.Base(s);
        H.Use(s, 800_000_000);
        await s.HatchIfNeeded();
        Assert.Null(s.State.Active);
        Assert.Single(s.State.Dex);
        Assert.Null(s.CurrentCelebration);
    });

    // Hatch sampler (PokéAPI weighted pick — replaces the hard-coded pool)

    private static CompanionStore SamplerStore(IPokeProviding provider, ulong seed, CompanionState? preloadState = null)
    {
        var path = StateFiles.TempPath();
        if (preloadState != null) StateFiles.Write(preloadState, path);
        return H.Store(provider, new SeededRng(seed), path);
    }

    /// <summary>An egg filled past the threshold (with the install baseline) — no RNG used.</summary>
    private static CompanionState EggReadyState(params string[] collected) => new()
    {
        InstallBaselineSet = true,
        LastDate = "d1",
        EggUsage = PokemonBalance.EggHatchThreshold + 1,
        CollectedFinals = [.. collected],
    };

    [Fact]
    public void SamplerWeightedPickDeterministic() => MainActor.Run(async () =>
    {
        var index = new List<BaseSpecies> { new(10, 100), new(20, 100), new(30, 100) };
        foreach (var seed in new ulong[] { 1, 7, 42, 999 })
        {
            var r = new SeededRng(seed);
            var roll = (int)(r.Next() % 300);
            var expected = index[roll / 100].Id; // [0,100)→10, [100,200)→20, [200,300)→30
            var p = new IndexProvider { Index = index };
            var s = SamplerStore(p, seed, EggReadyState());
            await s.HatchIfNeeded();
            Assert.Equal(expected, s.State.Active?.BaseID);
        }
    });

    [Fact]
    public void SamplerCaptureRateIsWeight() => MainActor.Run(async () =>
    {
        var index = new List<BaseSpecies> { new(100, 254), new(200, 2) };
        ulong? legendarySeed = null, commonSeed = null;
        for (ulong seed = 0; seed < 3000; seed++)
        {
            var roll = (int)(new SeededRng(seed).Next() % 256);
            if (roll >= 254 && legendarySeed == null) legendarySeed = seed;
            if (roll < 254 && commonSeed == null) commonSeed = seed;
            if (legendarySeed != null && commonSeed != null) break;
        }
        foreach (var (seed, expected) in new[] { (commonSeed!.Value, 100), (legendarySeed!.Value, 200) })
        {
            var s = SamplerStore(new IndexProvider { Index = index }, seed, EggReadyState());
            await s.HatchIfNeeded();
            Assert.Equal(expected, s.State.Active?.BaseID);
        }
    });

    [Fact]
    public void SamplerHalvesCollectedWeight() => MainActor.Run(async () =>
    {
        var index = new List<BaseSpecies> { new(1, 200), new(2, 200) };
        ulong? found = null;
        for (ulong seed = 0; seed < 5000; seed++)
        {
            var v = new SeededRng(seed).Next();
            if (v % 400 < 200 && v % 300 >= 100) { found = seed; break; }
        }
        Assert.NotNull(found);
        var s1 = SamplerStore(new IndexProvider { Index = index }, found.Value, EggReadyState());
        await s1.HatchIfNeeded();
        Assert.Equal(1, s1.State.Active?.BaseID);
        var s2 = SamplerStore(new IndexProvider { Index = index }, found.Value, EggReadyState("1:1"));
        await s2.HatchIfNeeded();
        Assert.Equal(2, s2.State.Active?.BaseID); // halved weight moves the pick
    });

    [Fact]
    public void EggPrefetchStoresPendingAndHatchUsesIt() => MainActor.Run(async () =>
    {
        var p = new IndexProvider { Index = [new BaseSpecies(77, 255)] };
        var st = new CompanionState { InstallBaselineSet = true, LastDate = "d1" };
        var s = SamplerStore(p, 5, st);
        // Below the threshold: no hatch, but the prefetch must run.
        H.Use(s, 1_000);
        Assert.True(await MainActor.WaitUntil(() => s.State.PendingHatchID != null));
        Assert.Equal(77, s.State.PendingHatchID);
        H.Use(s, 6_000_000);
        await s.HatchIfNeeded();
        Assert.Equal(77, s.State.Active?.BaseID);
        Assert.Null(s.State.PendingHatchID);
        Assert.NotNull(s.State.Active?.Nature);
    });

    [Fact]
    public void FailedHatchRetriesAfterProviderRecovers() => MainActor.Run(async () =>
    {
        var p = new IndexProvider { Index = [new BaseSpecies(88, 255)], FailAll = true };
        var s = SamplerStore(p, 9, EggReadyState());

        await s.HatchIfNeeded();
        Assert.Null(s.State.Active);
        Assert.True(s.IsHatchRetryDelayed);

        p.FailAll = false;
        await s.HatchIfNeeded();

        Assert.Equal(88, s.State.Active?.BaseID);
        Assert.False(s.IsHatchRetryDelayed);
    });

    [Fact]
    public void SamplerOfflineKeepsEgg() => MainActor.Run(async () =>
    {
        var s = SamplerStore(new IndexProvider { FailAll = true }, 1, EggReadyState());
        await s.HatchIfNeeded();
        Assert.Null(s.State.Active);
        Assert.True(s.State.EggUsage >= PokemonBalance.EggHatchThreshold); // egg progress kept
        Assert.False(s.IsHatching);
    });

    [Fact]
    public void GermanNatureNamesMatchOfficialMainlineNames()
    {
        string[] expected =
        [
            "Robust", "Solo", "Mutig", "Hart", "Frech",
            "Kühn", "Sanft", "Locker", "Pfiffig", "Lasch",
            "Scheu", "Hastig", "Ernst", "Froh", "Naiv",
            "Mäßig", "Mild", "Ruhig", "Zaghaft", "Hitzig",
            "Still", "Zart", "Forsch", "Sacht", "Kauzig",
        ];
        Assert.Equal(expected, PokemonNatureExtensions.AllCases.Select(n => n.Name(AppLanguage.De)));
    }

    [Fact]
    public void RussianNatureNamesFollowDocumentedCommunityConvention()
    {
        string[] expected =
        [
            "Выносливый", "Одинокий", "Храбрый", "Непреклонный", "Непослушный",
            "Смелый", "Послушный", "Расслабленный", "Проказливый", "Беспечный",
            "Робкий", "Торопливый", "Серьёзный", "Весёлый", "Наивный",
            "Скромный", "Мягкий", "Тихий", "Застенчивый", "Опрометчивый",
            "Спокойный", "Кроткий", "Нахальный", "Внимательный", "Ловкий",
        ];
        Assert.Equal(expected, PokemonNatureExtensions.AllCases.Select(n => n.Name(AppLanguage.Ru)));
    }

    [Fact]
    public void NatureNamesComplete()
    {
        Assert.Equal(25, PokemonNatureExtensions.AllCases.Length);
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var names = PokemonNatureExtensions.AllCases.Select(n => n.Name(lang)).ToList();
            Assert.Equal(25, names.ToHashSet().Count);
            Assert.DoesNotContain(names, string.IsNullOrEmpty);
        }
    }

    [Fact]
    public void FailedHatchShowsDelayUntilNextRefresh() => MainActor.Run(async () =>
    {
        var path = StateFiles.Write(new CompanionState
        {
            InstallBaselineSet = true,
            EggUsage = PokemonBalance.EggHatchThreshold,
        });
        try
        {
            var store = H.Store(new FailingPokeProvider(), new SeededRng(42), path, () => DateTimeOffset.Now);
            Assert.True(store.IsEgg);
            Assert.Equal(1.0, store.EggProgress);
            Assert.False(store.IsHatchRetryDelayed);

            // A failed attempt shows the delay until the next refresh, whatever the cause.
            await store.HatchIfNeeded();
            Assert.True(store.IsEgg);
            Assert.True(store.IsHatchRetryDelayed);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void ApplySaveResetsHatchRetryDelay() => MainActor.Run(async () =>
    {
        var path = StateFiles.Write(new CompanionState
        {
            InstallBaselineSet = true,
            EggUsage = PokemonBalance.EggHatchThreshold,
        });
        try
        {
            var store = H.Store(new FailingPokeProvider(), new SeededRng(42), path, () => DateTimeOffset.Now);
            await store.HatchIfNeeded();
            Assert.True(store.IsHatchRetryDelayed);

            var imported = new CompanionState { InstallBaselineSet = true, EggUsage = 0 };
            var envelope = new SaveEnvelope(SaveEnvelope.FormatID, SaveEnvelope.SchemaVersion, "2.5.3",
                DateTimeOffset.Now, "Mac", imported);
            store.ApplySave(envelope, new Dictionary<string, long>(), "2026-09-11", hasUsageData: false);
            Assert.False(store.IsHatchRetryDelayed); // import clears the previous waiting flag
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void PrefetchFailureMarksEggThatBecameReadyDuringRequest() => MainActor.Run(async () =>
    {
        var provider = new SuspendedFailingIndexProvider();
        var state = new CompanionState
        {
            InstallBaselineSet = true,
            LastDate = "d1",
            EggUsage = PokemonBalance.EggHatchThreshold - 1,
            ClaimedTodayTokensByProvider = new Dictionary<string, long> { ["test"] = 0 },
        };
        var store = SamplerStore(provider, 42, state);

        H.Use(store, 0);
        Assert.True(await MainActor.WaitUntil(() => provider.IsSuspended));

        H.Use(store, 1);
        provider.ResumeFailure();

        Assert.True(await MainActor.WaitUntil(() => store.IsHatchRetryDelayed));
        Assert.True(store.IsEgg);
        Assert.Equal(1, store.EggProgress);
    });

    [Fact]
    public void StaleSpeciesSelectionFailureDoesNotMarkImportedEggDelayed() => MainActor.Run(async () =>
    {
        var provider = new SuspendedFailingIndexProvider();
        var store = SamplerStore(provider, 42, EggReadyState());
        var hatch = store.HatchIfNeeded();
        Assert.True(await MainActor.WaitUntil(() => provider.IsSuspended));

        store.ApplySave(ImportedFreshEggEnvelope(), new Dictionary<string, long>(), "2026-09-11", hasUsageData: false);
        provider.ResumeFailure();
        await hatch;

        Assert.False(store.IsHatchRetryDelayed);
        Assert.Equal(0, store.EggProgress);
    });

    [Fact]
    public void StaleLineFailureDoesNotMarkImportedEggDelayed() => MainActor.Run(async () =>
    {
        var provider = new SuspendedFailingLineProvider();
        var state = EggReadyState();
        state.PendingHatchID = 25;
        var store = SamplerStore(provider, 42, state);
        var hatch = store.HatchIfNeeded();
        Assert.True(await MainActor.WaitUntil(() => provider.IsSuspended));

        store.ApplySave(ImportedFreshEggEnvelope(), new Dictionary<string, long>(), "2026-09-11", hasUsageData: false);
        provider.ResumeFailure();
        await hatch;

        Assert.False(store.IsHatchRetryDelayed);
        Assert.Equal(0, store.EggProgress);
    });

    private static SaveEnvelope ImportedFreshEggEnvelope() =>
        new(SaveEnvelope.FormatID, SaveEnvelope.SchemaVersion, "2.5.4", DateTimeOffset.Now, "Mac",
            new CompanionState { InstallBaselineSet = true });
}

/// <summary>PokéAPI SSRF guard: the evolution_chain URL is pinned to https + pokeapi.co.</summary>
public class PokeApiGuardTests2
{
    [Fact]
    public void ValidatedChainUrlAcceptsPokeapiHttps() =>
        Assert.NotNull(PokeApiClient.ValidatedChainUrl("https://pokeapi.co/api/v2/evolution-chain/1/"));

    [Fact]
    public void ValidatedChainUrlRejectsUntrusted()
    {
        Assert.Null(PokeApiClient.ValidatedChainUrl("https://evil.example.com/x"));
        Assert.Null(PokeApiClient.ValidatedChainUrl("https://pokeapi.co.evil.com/x"));
        Assert.Null(PokeApiClient.ValidatedChainUrl("http://pokeapi.co/x"));
        Assert.Null(PokeApiClient.ValidatedChainUrl(""));
    }
}
