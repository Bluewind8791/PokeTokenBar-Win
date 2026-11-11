using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of DittoTests.swift: Ditto disguise / reveal.

file static class DittoLines
{
    public static EvoLine Line(int @base, EvoNode tree, Rarity rarity) => Lines.Make(@base, tree, rarity);

    /// <summary>Common, 3 forms: first evolution at 125M.</summary>
    public static readonly EvoLine Disguise = Line(1, Lines.Node(1, Lines.Node(2, Lines.Node(3))), Rarity.Common);
    public static readonly EvoLine PrunedDisguise = Line(206, Lines.Node(206, Lines.Node(982)), Rarity.Common);
    /// <summary>Ditto: rare, single form.</summary>
    public static readonly EvoLine Ditto = Line(132, Lines.Node(132), Rarity.Rare);
}

/// <summary>Base 1 = the disguise line, 132 = Ditto's line (needed by the reveal).</summary>
file sealed class DittoTestProvider : IPokeProviding, IPokemonDetailProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) =>
        Task.FromResult(baseSpeciesID == PokemonOdds.DittoSpeciesID ? DittoLines.Ditto : DittoLines.Disguise);

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(1, 255)]);

    public Task<PokemonDetails> PokemonDetails(int speciesID)
    {
        var isDitto = speciesID == PokemonOdds.DittoSpeciesID;
        return Task.FromResult(new PokemonDetails(
            speciesID, isDitto ? "ditto" : "bulbasaur", isDitto ? 3 : 7, isDitto ? 40 : 69, 64,
            isDitto ? -1 : 1, [isDitto ? "normal" : "grass"],
            new Dictionary<string, int> { ["hp"] = isDitto ? 48 : 45 },
            [new PokemonAbilityOption(isDitto ? "limber" : "overgrow", 1, false)],
            [new PokemonMoveOption(isDitto ? "transform" : "tackle", [new PokemonMoveLearnMethod("level-up", 1)])]));
    }
}

/// <summary>#982 has no animated asset and is pruned by EvoLine, leaving #206 as a leaf.</summary>
file sealed class PrunedDittoTestProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) =>
        Task.FromResult(baseSpeciesID == PokemonOdds.DittoSpeciesID ? DittoLines.Ditto : DittoLines.PrunedDisguise);

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(206, 190)]);
}

file sealed class DelayedDittoProvider : IPokeProviding
{
    private TaskCompletionSource<EvoLine>? _pending;
    public bool IsSuspended => _pending != null;

    public Task<EvoLine> Line(int baseSpeciesID)
    {
        if (baseSpeciesID != PokemonOdds.DittoSpeciesID) return Task.FromResult(DittoLines.Disguise);
        if (_pending != null) throw new InvalidOperationException("only one Ditto request may be suspended");
        _pending = new TaskCompletionSource<EvoLine>();
        return _pending.Task;
    }

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(1, 255)]);

    public void Resume()
    {
        var pending = _pending;
        _pending = null;
        pending?.SetResult(DittoLines.Ditto);
    }
}

// Disguise roll (pure). The hatch roll is gated to the installed app; the decision is verified as a pure function.

public class DittoDisguiseRollTests
{
    [Fact]
    public void HitCommonMultiFormOnMultipleOfDenominator()
    {
        Assert.True(CompanionStore.DittoDisguiseHit(Rarity.Common, 2, 0));
        Assert.True(CompanionStore.DittoDisguiseHit(Rarity.Common, 3, 128));
        Assert.True(CompanionStore.DittoDisguiseHit(Rarity.Common, 3, 256));
    }

    [Fact]
    public void MissWhenRollNotMultipleOfDenominator()
    {
        Assert.False(CompanionStore.DittoDisguiseHit(Rarity.Common, 3, 1));
        Assert.False(CompanionStore.DittoDisguiseHit(Rarity.Common, 3, 127));
        Assert.False(CompanionStore.DittoDisguiseHit(Rarity.Common, 3, 129));
    }

    /// <summary>Single-form lines are excluded: without evolving there is no reveal trigger.</summary>
    [Fact]
    public void ExcludesSingleForm() => Assert.False(CompanionStore.DittoDisguiseHit(Rarity.Common, 1, 0));

    /// <summary>Common only.</summary>
    [Fact]
    public void ExcludesNonCommon()
    {
        Assert.False(CompanionStore.DittoDisguiseHit(Rarity.Uncommon, 3, 0));
        Assert.False(CompanionStore.DittoDisguiseHit(Rarity.Rare, 3, 0));
        Assert.False(CompanionStore.DittoDisguiseHit(Rarity.Legendary, 3, 0));
    }

    [Fact]
    public void DenominatorIs128() => Assert.Equal(128UL, PokemonOdds.DittoDisguiseDenominator);
}

// Disguise → reveal state transition

public class DittoRevealTests
{
    private static readonly CompanionStore.Celebration RevealNormal = CompanionStore.Celebration.DittoRevealed(false);
    private static readonly CompanionStore.Celebration RevealShiny = CompanionStore.Celebration.DittoRevealed(true);

    /// <summary>Active = a common 3-form disguised Ditto. CurrentLine is null (restart-like) → Update loads it and
    /// triggers the reveal.</summary>
    private static CompanionStore SeedDisguise(long usedAtStage = 0, bool shiny = false, bool revealed = false,
        bool boosted = false, string[]? collectedFinals = null, AppSettings? settings = null)
    {
        static string B(bool b) => b ? "true" : "false";
        var active = "{\"baseID\":1,\"pathIDs\":[1],\"stageIndex\":0,\"usedAtStage\":" + usedAtStage + ","
            + "\"rarity\":\"common\",\"totalForms\":3,\"isShiny\":" + B(shiny) + ",\"hasGrowthBoost\":" + B(boosted) + ","
            + "\"dittoDisguise\":1,\"dittoRevealed\":" + B(revealed) + "}";
        var finals = string.Join(",", (collectedFinals ?? []).Select(f => "\"" + f + "\""));
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":1000000000,\"spentTokens\":0,"
            + "\"lastDate\":\"d1\",\"active\":" + active + ",\"dex\":[],\"collectedFinals\":[" + finals + "]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("ditto"));
        return new CompanionStore(new DittoTestProvider(), clock: () => Lines.FixedNow, filePath: path,
            rng: new SeededRng(7), settings: settings ?? new AppSettings(null));
    }

    private static void TestUpdate(CompanionStore s) =>
        s.Update(new Dictionary<string, long> { ["test"] = 0 }, "d1", 0, BurnTier.Idle, false, true);

    /// <summary>Drains the Update → LoadCurrentLine → ApplyUsage(0) → (over threshold) RevealDitto chain.</summary>
    private static async Task DrainReveal(CompanionStore s)
    {
        TestUpdate(s);
        for (var i = 0; i < 200 && !(s.State.Active?.DittoRevealed ?? false); i++) await Task.Yield();
    }

    private static async Task YieldUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Yield();
    }

    /// <summary>Shininess is hidden while disguised (internal IsShiny kept — revealed later).</summary>
    [Fact]
    public void ShinyHiddenDuringDisguise() => MainActor.Run(() =>
    {
        var s = SeedDisguise(shiny: true);
        Assert.True(s.State.Active?.IsShiny ?? false);
        Assert.False(s.CurrentIsShiny);
    });

    /// <summary>[Trigger branch] Reveals as Ditto at the first evolution threshold instead of evolving.</summary>
    [Fact]
    public void RevealAtFirstEvolution() => MainActor.Run(async () =>
    {
        var s = SeedDisguise();
        s.ApplyUsage(300_000_000); // banked while the line is unloaded (above 125M) — evolution/reveal deferred
        Assert.Equal(300_000_000, s.State.Active?.UsedAtStage);
        Assert.Equal(1, s.CurrentSpeciesID);
        Assert.False(s.State.Active?.DittoRevealed ?? true);
        await DrainReveal(s);
        Assert.True(s.State.Active?.DittoRevealed ?? false);
        Assert.Equal(PokemonOdds.DittoSpeciesID, s.State.Active?.BaseID);
        Assert.Equal(PokemonOdds.DittoSpeciesID, s.CurrentSpeciesID);
        Assert.NotEqual(2, s.CurrentSpeciesID); // the disguise never evolves
        Assert.Equal(Rarity.Rare, s.State.Active?.Rarity);
        Assert.Equal(1, s.State.Active?.TotalForms);
        Assert.Equal(0, s.State.Active?.StageIndex);
        Assert.Equal([132], s.State.Active!.PathIDs);
        Assert.Equal([132], s.State.Active!.PlannedPathIDs);
        Assert.Equal(300_000_000 - 125_000_000, s.State.Active?.UsedAtStage); // overflow carries over
        Assert.NotNull(s.State.Active?.DittoDisguise);
        Assert.Equal(RevealNormal, s.CurrentCelebration);
    });

    [Fact]
    public void RevealRebasesDisguiseProfileAndLoadsDittoDetails() => MainActor.Run(async () =>
    {
        var s = SeedDisguise();
        await s.LoadPokemonDetails(1);
        Assert.NotEqual(PokemonGender.Genderless, s.State.Active?.Profile?.Gender);
        Assert.Equal("overgrow", s.State.Active?.Profile?.AbilityName);
        Assert.Equal(["tackle"], s.State.Active!.Profile!.Moves.Select(m => m.Name));

        s.ApplyUsage(300_000_000);
        await DrainReveal(s);
        await YieldUntil(() => s.State.Active?.Profile?.Gender == PokemonGender.Genderless);

        var profile = s.State.Active?.Profile;
        Assert.Equal(PokemonGender.Genderless, profile?.Gender);
        Assert.Equal("limber", profile?.AbilityName);
        Assert.Equal(["transform"], profile!.Moves.Select(m => m.Name));
        Assert.Equal(500_000_000, profile.GrowthTokens);
        Assert.Equal(20, profile.Level); // revealing Ditto preserves earned phase progress
    });

    [Fact]
    public void BoostedEasyDittoRetainsLevelAndIdentityAcrossReveal() => MainActor.Run(async () =>
    {
        var s = SeedDisguise(boosted: true, settings: new AppSettings(null));
        s.SetGrowthDifficulty(0.1);
        await s.LoadPokemonDetails(1);
        var initial = s.State.Active!.Profile!.Clone();
        s.ApplyUsage(7_250_000); // 6.25M reveal threshold plus 1M raw carryover.
        var earnedLevel = s.State.Active?.Profile?.Level;
        await DrainReveal(s);
        await s.LoadPokemonDetails(PokemonOdds.DittoSpeciesID);
        await YieldUntil(() => s.State.Active?.Profile?.Gender == PokemonGender.Genderless);
        var revealed = s.State.Active!;
        Assert.Equal(1_000_000, revealed.UsedAtStage);
        Assert.False(revealed.HasGrowthBoost); // a new Ditto drops the disguise's boost but keeps its level
        Assert.Equal(initial.InstanceID, revealed.Profile?.InstanceID);
        Assert.Equal(initial.Ivs, revealed.Profile?.Ivs);
        Assert.Equal(earnedLevel, revealed.Profile?.Level);
        Assert.Equal(PokemonGender.Genderless, revealed.Profile?.Gender);
        Assert.Equal(["transform"], revealed.Profile!.Moves.Select(m => m.Name));
        s.ApplyUsage(s.TokensToNext);
        Assert.Equal(100, s.State.Dex[^1].Profile?.Level);
    });

    /// <summary>The disguise hatches with its own line's boost. After the reveal only a repeat Ditto keeps it.</summary>
    [Fact]
    public void BoostedDisguiseRevealsAtTheHalvedThresholdAndANewDittoDropsTheBoost() => MainActor.Run(async () =>
    {
        var s = SeedDisguise(boosted: true);
        s.ApplyUsage(62_500_000);
        await DrainReveal(s);
        var revealed = s.State.Active!;
        Assert.True(revealed.DittoRevealed);
        Assert.False(revealed.HasGrowthBoost);
        Assert.Equal(0, revealed.UsedAtStage);
        Assert.Equal(3_000_000_000, revealed.PhaseThreshold);
    });

    [Fact]
    public void RevealingAnAlreadyGraduatedDittoGrantsTheRepeatBoost() => MainActor.Run(async () =>
    {
        var s = SeedDisguise(collectedFinals: [$"{PokemonOdds.DittoSpeciesID}:{PokemonOdds.DittoSpeciesID}"]);
        s.ApplyUsage(125_000_000);
        await DrainReveal(s);
        var revealed = s.State.Active!;
        Assert.True(revealed.DittoRevealed);
        Assert.True(revealed.HasGrowthBoost);
        Assert.Equal(1_500_000_000, revealed.PhaseThreshold);
    });

    /// <summary>[Regression] A representative pinned only to the disguise species is cleared by the reveal and
    /// automatic tracking shows the revealed (shiny) Ditto.</summary>
    [Fact]
    public void RevealClearsRepresentativePinnedToDisguiseAndFollowsDitto() => MainActor.Run(async () =>
    {
        var s = SeedDisguise(shiny: true);
        Assert.True(s.SetRepresentativeSpeciesID(1));
        Assert.Equal(1, s.RepresentativeSpeciesID);
        Assert.Equal(new CompanionStore.RepresentativeSubjectInfo(1, false), s.RepresentativeSubject);

        s.ApplyUsage(300_000_000);
        await DrainReveal(s);

        Assert.True(s.State.Active?.DittoRevealed ?? false);
        Assert.Null(s.RepresentativeSpeciesID);
        Assert.Equal(new CompanionStore.RepresentativeSubjectInfo(PokemonOdds.DittoSpeciesID, true), s.RepresentativeSubject);
    });

    /// <summary>[Regression] A disguise pruned into a leaf reveals at its single-form threshold instead of graduating.</summary>
    [Fact]
    public void PrunedLeafDisguiseRevealsBeforeGraduation() => MainActor.Run(async () =>
    {
        var threshold = PokemonBalance.PhaseThreshold(Rarity.Common, 1, 0);
        Assert.Equal(750_000_000, threshold);
        Assert.Empty(DittoLines.PrunedDisguise.Tree.Children); // #206 must be a leaf once #982 is pruned
        var active = "{\"baseID\":206,\"pathIDs\":[206],\"plannedPathIDs\":[206,982],\"stageIndex\":0,"
            + "\"usedAtStage\":" + threshold + ",\"rarity\":\"common\",\"totalForms\":2,\"isShiny\":true,"
            + "\"nature\":\"timid\",\"dittoDisguise\":206,\"dittoRevealed\":false}";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":1000000000,\"lastDate\":\"d1\","
            + "\"active\":" + active + ",\"dex\":[],\"collectedFinals\":[]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("ditto-pruned"));
        var s = new CompanionStore(new PrunedDittoTestProvider(), clock: () => Lines.FixedNow, filePath: path,
            rng: new SeededRng(7), settings: new AppSettings(null));

        await DrainReveal(s);

        var revealed = s.State.Active!;
        Assert.Equal(PokemonOdds.DittoSpeciesID, revealed.BaseID);
        Assert.Equal([PokemonOdds.DittoSpeciesID], revealed.PathIDs);
        Assert.Equal([PokemonOdds.DittoSpeciesID], revealed.PlannedPathIDs);
        Assert.Equal(0, revealed.UsedAtStage);
        Assert.Equal(206, revealed.DittoDisguise);
        Assert.True(revealed.DittoRevealed);
        Assert.True(revealed.IsShiny);
        Assert.Equal(PokemonNature.Timid, revealed.Nature);
        Assert.DoesNotContain(s.State.Dex, e => e.FinalID == 206);
        Assert.DoesNotContain("206:206", s.State.CollectedFinals);
    });

    [Fact]
    public void DelayedRevealDoesNotConvertSameBaseReplacementDisguise() => MainActor.Run(async () =>
    {
        var active = """{"baseID":1,"pathIDs":[1],"plannedPathIDs":[1,2,3],"stageIndex":0,"usedAtStage":125000000,"rarity":"common","totalForms":3,"dittoDisguise":1}""";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":2000000000,\"lastDate\":\"d1\",\"active\":" + active + ",\"dex\":[],\"collectedFinals\":[]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("ditto-race"));
        var provider = new DelayedDittoProvider();
        ulong? seed = null;
        for (ulong candidate = 0; candidate < 100_000; candidate++)
        {
            var rng = new SeededRng(candidate);
            rng.Next();
            rng.Next();
            if (rng.Next() % PokemonOdds.DittoDisguiseDenominator == 0)
            {
                seed = candidate;
                break;
            }
        }
        Assert.NotNull(seed);
        var s = new CompanionStore(provider, clock: () => Lines.FixedNow, filePath: path, rng: new SeededRng(seed.Value),
            dittoDisguiseRollingEnabled: true, settings: new AppSettings(null));

        TestUpdate(s);
        Assert.True(await MainActor.WaitUntil(() => provider.IsSuspended));

        Assert.True(s.BuyFreshEgg());
        await s.Hatch(1);
        Assert.Equal(1, s.State.Active?.BaseID);
        Assert.False(s.State.Active?.DittoRevealed ?? true);
        Assert.NotNull(s.State.Active?.DittoDisguise);
        Assert.Equal(0, s.State.Active?.UsedAtStage);

        provider.Resume();
        for (var i = 0; i < 200; i++) await Task.Yield();
        Assert.Equal(1, s.State.Active?.BaseID);
        Assert.False(s.State.Active?.DittoRevealed ?? true);
        Assert.NotNull(s.State.Active?.DittoDisguise);
        Assert.Equal(0, s.State.Active?.UsedAtStage);
        Assert.NotEqual(PokemonOdds.DittoSpeciesID, s.CurrentSpeciesID);
    });

    /// <summary>Shininess is revealed with Ditto, with the shiny reveal celebration.</summary>
    [Fact]
    public void ShinyUnmaskedAfterReveal() => MainActor.Run(async () =>
    {
        var s = SeedDisguise(shiny: true);
        s.ApplyUsage(300_000_000);
        Assert.False(s.CurrentIsShiny);
        await DrainReveal(s);
        Assert.True(s.State.Active?.DittoRevealed ?? false);
        Assert.True(s.CurrentIsShiny);
        Assert.Equal(RevealShiny, s.CurrentCelebration);
    });

    /// <summary>Below the threshold the disguise stays.</summary>
    [Fact]
    public void NoRevealBelowThreshold() => MainActor.Run(async () =>
    {
        var s = SeedDisguise();
        s.ApplyUsage(100_000_000); // below the first evolution (125M)
        await DrainReveal(s);
        Assert.False(s.State.Active?.DittoRevealed ?? true);
        Assert.Equal(1, s.CurrentSpeciesID);
    });

    private static void GrantWeekly(CompanionStore s, string key) =>
        s.GrantCandies([new CandyWindow(key, "Test", WindowClass.Weekly, 100)], limitsReady: true);

    [Fact]
    public void CandyPickerMatchesApparentPokemonButOnlySpendsEnoughToReveal() => MainActor.Run(async () =>
    {
        var s = SeedDisguise(usedAtStage: 100_000_000);
        await DrainReveal(s);
        var normal = new CompanionStore(new DittoTestProvider(), clock: () => Lines.FixedNow,
            filePath: StateFiles.TempPath("normal-candy"), dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));
        await normal.Hatch(1);
        normal.ApplyUsage(100_000_000);
        foreach (var store in new[] { s, normal })
        {
            store.GrantCandies([], limitsReady: true);
            GrantWeekly(store, "batch.weekly");
        }
        Assert.Equal(normal.MaxRareCandyUseCount, s.MaxRareCandyUseCount);
        for (var count = 1; count <= 5; count++)
        {
            var disguised = s.PlanRareCandyUse(count)!;
            var apparent = normal.PlanRareCandyUse(count)!;
            Assert.Equal(apparent.Count, disguised.Count);
            Assert.Equal(apparent.Xp, disguised.Xp);
            Assert.Equal(apparent.Evolves, disguised.Evolves);
            Assert.Equal(apparent.Graduates, disguised.Graduates);
            Assert.Equal(apparent.CarryoverXP, disguised.CarryoverXP);
            Assert.Equal(apparent.DiscardedXP, disguised.DiscardedXP);
        }
        s.UseRareCandy(5);
        Assert.Equal(4, s.RareCandyCount);
        Assert.Equal(RareCandy.Xp, s.CandyFeedbackAmount);
        Assert.Equal(CompanionStore.CandyUseResult.Unavailable, s.UseRareCandy(5)); // do not spend while the reveal is pending
        await DrainReveal(s);
        Assert.Equal(PokemonOdds.DittoSpeciesID, s.CurrentSpeciesID);
        Assert.Equal(75_000_000, s.State.Active?.UsedAtStage);
        Assert.Equal(4, s.MaxRareCandyUseCount);
        Assert.Equal(CompanionStore.CandyUseResult.Progressed, s.UseRareCandy(2));
        Assert.Equal(275_000_000, s.State.Active?.UsedAtStage);
        Assert.Equal(2, s.RareCandyCount);
    });

    [Fact]
    public void ApparentGraduationBatchPreservesUnusedCandiesAtDittoReveal() => MainActor.Run(async () =>
    {
        foreach (var difficulty in new[] { 0.1, 1.0, 2.0 })
        {
            foreach (var boosted in new[] { false, true })
            {
                var settings = new AppSettings(null);
                settings.Set("growthDifficulty", difficulty);
                var s = SeedDisguise(boosted: boosted, settings: settings);
                await DrainReveal(s);
                s.GrantCandies([], limitsReady: true);
                for (var i = 0; i < 4; i++) GrantWeekly(s, $"batch.{i}");
                var revealThreshold = s.Threshold;
                var preview = s.PlanRareCandyUse(20)!;
                Assert.True(preview.Graduates, "Preview must use the apparent path");
                var consumed = (revealThreshold + RareCandy.Xp - 1) / RareCandy.Xp;
                s.UseRareCandy(20);
                await DrainReveal(s);
                Assert.Equal(PokemonOdds.DittoSpeciesID, s.CurrentSpeciesID);
                Assert.Equal(consumed * RareCandy.Xp - revealThreshold, s.State.Active?.UsedAtStage);
                Assert.Equal(consumed * RareCandy.Xp, s.CandyFeedbackAmount);
                Assert.Equal(20 - consumed, s.RareCandyCount);
                Assert.Empty(s.State.Dex);
            }
        }
    });

    [Fact]
    public void CandyBatchStopsExactlyAtRevealWithoutOverflow() => MainActor.Run(async () =>
    {
        var s = SeedDisguise(usedAtStage: 25_000_000);
        await DrainReveal(s);
        s.GrantCandies([], limitsReady: true);
        GrantWeekly(s, "batch.exact");
        s.UseRareCandy(5);
        await DrainReveal(s);
        Assert.Equal(PokemonOdds.DittoSpeciesID, s.CurrentSpeciesID);
        Assert.Equal(0, s.State.Active?.UsedAtStage);
        Assert.Equal(4, s.RareCandyCount);
    });

    /// <summary>Old saves (no ditto fields) → null/false, behaving as a normal Pokémon (shiny shown).</summary>
    [Fact]
    public void BackwardCompatDecodeNoDittoFields() => MainActor.Run(() =>
    {
        var active = "{\"baseID\":1,\"pathIDs\":[1],\"stageIndex\":0,\"usedAtStage\":0,"
            + "\"rarity\":\"common\",\"totalForms\":3,\"isShiny\":true}";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":0,\"spentTokens\":0,"
            + "\"lastDate\":\"d1\",\"active\":" + active + ",\"dex\":[],\"collectedFinals\":[]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("ditto-bc"));
        var s = new CompanionStore(new DittoTestProvider(), clock: () => Lines.FixedNow, filePath: path,
            rng: new SeededRng(7), settings: new AppSettings(null));
        Assert.Null(s.State.Active?.DittoDisguise);
        Assert.False(s.State.Active?.DittoRevealed ?? true);
        Assert.True(s.CurrentIsShiny);
    });

    /// <summary>Ditto (#132) is excluded from the REST fallback; the guard returns before any network call.</summary>
    [Fact]
    public async Task DittoExcludedFromRestFallback()
    {
        var result = await new PokeApiClient(cacheDirectory: null).BaseSpecies(PokemonOdds.DittoSpeciesID);
        Assert.Null(result);
    }
}
