using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of PremiumEggTests.swift: rarity-guaranteed eggs (uncommon or better / rare or better).
//
// The guarantee rests on "capture_rate ceiling = rarity floor". Four paths can break it, each reproduced as a
// trigger branch:
//   ① Rarity.CaptureRateCeiling and Rarity.From thresholds drift      → exhaustive invariant test
//   ② the weighted pick (ChooseBase) draws below the floor            → repeated hatches with a control group
//   ③ only the REST fallback (ChooseBaseViaRest) skips the filter     → kill the index and verify the same
//   ④ a stale index makes the drawn species' real rarity too low      → the final gate right before hatching

/// <summary>Multi-species index split by capture_rate. Line rarity uses the <b>same rule</b> as the index.</summary>
file sealed class TieredProvider : IPokeProviding
{
    public static readonly BaseSpecies[] Entries =
    [
        new(1, 255), // common
        new(2, 100), // uncommon
        new(3, 30),  // rare
        new(4, 3),   // legendary (LegendaryIDs below)
    ];
    public HashSet<int> LegendaryIDs { get; init; } = [4];

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>(Entries);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));

    public Task<EvoLine> Line(int baseSpeciesID)
    {
        if (Entries.FirstOrDefault(e => e.Id == baseSpeciesID) is not { } e)
            return Task.FromException<EvoLine>(new HttpRequestException("bad url"));
        return Task.FromResult(new EvoLine(e.Id, new EvoNode(e.Id),
            RarityExtensions.From(e.CaptureRate, LegendaryIDs.Contains(e.Id), false),
            new Dictionary<int, Dictionary<string, string>> { [e.Id] = new() { ["en"] = $"M{e.Id}" } }));
    }
}

/// <summary>GraphQL index down, REST alive. Every id is a base; capture_rate from the id (multiple of 3 = rare 30,
/// other even = uncommon 100, rest = common 255), so the fallback's rejection sampling really loops through the filter.</summary>
file sealed class RestOnlyTieredProvider : IPokeProviding
{
    public static int CaptureRate(int id) => id % 3 == 0 ? 30 : id % 2 == 0 ? 100 : 255;

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new HttpRequestException("bad server response"));

    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(new BaseSpecies(id, CaptureRate(id)));

    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(new EvoLine(baseSpeciesID, new EvoNode(baseSpeciesID),
        RarityExtensions.From(CaptureRate(baseSpeciesID), false, false),
        new Dictionary<int, Dictionary<string, string>> { [baseSpeciesID] = new() { ["en"] = $"M{baseSpeciesID}" } }));
}

/// <summary>The index says "rare (capture_rate 30)" but the real line is common — a stale index.</summary>
file sealed class LyingIndexProvider : IPokeProviding
{
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(7, 30)]);

    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(new BaseSpecies(7, 30));

    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(new EvoLine(7, new EvoNode(7), Rarity.Common,
        new Dictionary<int, Dictionary<string, string>> { [7] = new() { ["en"] = "Liar" } }));
}

public class PremiumEggTests
{
    private static string NewPath() => StateFiles.TempPath("premium-egg");

    private static CompanionStore Open(IPokeProviding provider, string path, ulong seed) =>
        new(provider, clock: () => Lines.FixedNow, filePath: path, rng: new SeededRng(seed), settings: new AppSettings(null));

    /// <summary>An active Pokémon (reroll possible) + wallet.</summary>
    private static CompanionStore ActiveStore(long used = 20_000_000_000, IPokeProviding? provider = null, string? at = null)
    {
        var f = at ?? NewPath();
        var mon = "{\"baseID\":10,\"pathIDs\":[10],\"stageIndex\":0,\"usedAtStage\":200000000,"
            + "\"rarity\":\"common\",\"totalForms\":3,\"isShiny\":false}";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":" + used + ",\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"active\":" + mon + ",\"dex\":[],\"collectedFinals\":[]}";
        StateFiles.WriteText(json, f);
        return Open(provider ?? new TieredProvider(), f, 7);
    }

    /// <summary>An egg ready to hatch + a guarantee. The seed changes the roll.</summary>
    private static CompanionStore EggStore(Rarity? tier, ulong seed, IPokeProviding provider)
    {
        var f = NewPath();
        var tierJson = tier is { } t ? "\"" + t.RawValue() + "\"" : "null";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":10000000,\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"active\":null,\"dex\":[],\"collectedFinals\":[],"
            + "\"eggUsage\":" + PokemonBalance.EggHatchThreshold + ",\"eggTier\":" + tierJson + "}";
        StateFiles.WriteText(json, f);
        return Open(provider, f, seed);
    }

    // ① Single threshold source — a filter/classification drift silently breaks the guarantee

    /// <summary>Every capture_rate × rarity: Includes must equal "From classifies it at that rarity or higher".</summary>
    [Fact]
    public void CaptureRateCeilingIsTheSameThresholdAsClassification()
    {
        for (var cr = 0; cr <= 255; cr++)
        {
            var classified = RarityExtensions.From(cr, false, false);
            foreach (var tier in new[] { Rarity.Common, Rarity.Uncommon, Rarity.Rare })
                Assert.True(tier.Includes(cr) == (classified.SortRank() >= tier.SortRank()),
                    $"capture_rate {cr} vs {tier} disagrees with classification {classified}");
        }
    }

    /// <summary>Legendary has no capture_rate ceiling, yet real legendaries pass the uncommon/rare filters.</summary>
    [Fact]
    public void LegendaryHasNoCaptureRateCeilingButPassesLowerTierFilters()
    {
        Assert.Null(Rarity.Legendary.CaptureRateCeiling());
        Assert.False(Rarity.Legendary.Includes(3));
        foreach (var cr in new[] { 3, 30, 45 })
        {
            Assert.True(Rarity.Rare.Includes(cr));
            Assert.True(Rarity.Uncommon.Includes(cr));
        }
        Assert.DoesNotContain((Rarity?)Rarity.Legendary, FreshEgg.ShopTiers); // no legendary-only egg
    }

    // Prices — graduation-total ratio (no new constants)

    [Fact]
    public void PricesFollowGraduationTotalRatio()
    {
        Assert.Equal(1_000_000_000, FreshEgg.PriceGuaranteeing(null));
        Assert.Equal(2_500_000_000, FreshEgg.PriceGuaranteeing(Rarity.Uncommon));
        Assert.Equal(4_000_000_000, FreshEgg.PriceGuaranteeing(Rarity.Rare));
        Assert.Equal([null, Rarity.Uncommon, Rarity.Rare], FreshEgg.ShopTiers);
    }

    /// <summary>
    /// The higher tier must be cheaper per rare+ than repeating the lower tier, computed from the measured pool
    /// weights (PokéAPI GraphQL v1beta2, base species id ≤ 649 without Ditto, measured 2026-08-04, 328 bases), with
    /// a late-game safety margin where the uncommon band is 30% boost-reduced.
    /// </summary>
    [Fact]
    public void RareEggIsNotDominatedAtMeasuredPoolComposition()
    {
        var weight = new Dictionary<Rarity, double>
        {
            [Rarity.Common] = 37_240, [Rarity.Uncommon] = 3_135, [Rarity.Rare] = 3_053, [Rarity.Legendary] = 339,
        };
        var uncommonEggPool = weight[Rarity.Uncommon] + weight[Rarity.Rare] + weight[Rarity.Legendary];
        var rarePlusShare = (weight[Rarity.Rare] + weight[Rarity.Legendary]) / uncommonEggPool;

        double uncommonPrice = FreshEgg.PriceGuaranteeing(Rarity.Uncommon);
        double rarePrice = FreshEgg.PriceGuaranteeing(Rarity.Rare);
        Assert.True(rarePrice < uncommonPrice / rarePlusShare, "the rare egg would be strictly dominated");
        Assert.True(rarePlusShare < uncommonPrice / rarePrice, $"measured share {rarePlusShare} crossed break-even");

        var boostedUncommon = weight[Rarity.Uncommon] * 0.70;
        var boostedShare = (weight[Rarity.Rare] + weight[Rarity.Legendary])
                           / (boostedUncommon + weight[Rarity.Rare] + weight[Rarity.Legendary]);
        Assert.True(boostedShare < uncommonPrice / rarePrice, "dominated already with a partly saturated uncommon band");
    }

    // Purchase — gate, debit, guarantee record

    [Fact]
    public void BuyPremiumEggRecordsGuaranteeAndDebitsTierPrice() => MainActor.Run(() =>
    {
        var s = ActiveStore();
        Assert.True(s.CanBuyEgg(Rarity.Rare));
        Assert.True(s.BuyEgg(Rarity.Rare));
        Assert.Equal(Rarity.Rare, s.State.EggTier);
        Assert.Equal(Rarity.Rare, s.EggGuarantee);
        Assert.Equal(FreshEgg.PriceGuaranteeing(Rarity.Rare), s.State.SpentTokens);
        Assert.Null(s.State.Active);
        Assert.Equal(0, s.State.EggUsage);
        // Premium eggs use the same release path: the species stays, but it is not a graduation.
        Assert.Single(s.State.Dex);
        Assert.True(s.State.Dex[0].IsReleased);
        Assert.Empty(s.State.CollectedFinals);
    });

    /// <summary>No egg of any tier can be bought while incubating (same gate as the plain egg).</summary>
    [Fact]
    public void CannotBuyAnyEggWhileIncubating() => MainActor.Run(() =>
    {
        var s = EggStore(null, 1, new TieredProvider());
        Assert.False(s.HasActive);
        foreach (var tier in FreshEgg.ShopTiers)
        {
            Assert.False(s.CanBuyEgg(tier));
            Assert.False(s.BuyEgg(tier));
        }
        Assert.Equal(0, s.State.SpentTokens);
    });

    /// <summary>Funds are checked against the <b>tier's</b> price.</summary>
    [Fact]
    public void FundsAreCheckedAgainstTierPrice() => MainActor.Run(() =>
    {
        var s = ActiveStore(used: 3_000_000_000); // 1B and 2.5B fit, 4B does not
        Assert.True(s.CanBuyEgg(null));
        Assert.True(s.CanBuyEgg(Rarity.Uncommon));
        Assert.False(s.CanBuyEgg(Rarity.Rare));
        Assert.False(s.BuyEgg(Rarity.Rare));
        Assert.NotNull(s.State.Active);
        Assert.Equal(0, s.State.SpentTokens);
    });

    /// <summary>A bought egg starts from a clean roll state: guarantee set, no pre-rolled species.</summary>
    [Fact]
    public void PurchaseStartsFromCleanRollState() => MainActor.Run(() =>
    {
        var s = ActiveStore();
        Assert.Null(s.State.PendingHatchID);
        Assert.True(s.BuyEgg(Rarity.Rare));
        Assert.Equal(Rarity.Rare, s.State.EggTier);
        Assert.Null(s.State.PendingHatchID);
        Assert.Equal(0, s.State.EggUsage);
    });

    /// <summary>The guarantee survives a restart (the species cannot be chosen at purchase).</summary>
    [Fact]
    public void GuaranteeSurvivesRestart() => MainActor.Run(() =>
    {
        var f = NewPath();
        var s1 = ActiveStore(at: f);
        Assert.True(s1.BuyEgg(Rarity.Uncommon));
        var s2 = Open(new TieredProvider(), f, 7);
        Assert.Equal(Rarity.Uncommon, s2.State.EggTier);
        Assert.Equal(Rarity.Uncommon, s2.EggGuarantee);
    });

    // ② The weighted pick never goes below the guarantee (with a control group)

    [Fact]
    public void WeightedRollNeverGoesBelowGuarantee() => MainActor.Run(async () =>
    {
        var guaranteed = new List<Rarity>();
        var control = new List<Rarity>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var g = EggStore(Rarity.Rare, seed, new TieredProvider());
            await g.HatchIfNeeded();
            if (g.State.Active?.Rarity is { } r) guaranteed.Add(r);
            var c = EggStore(null, seed, new TieredProvider());
            await c.HatchIfNeeded();
            if (c.State.Active?.Rarity is { } cr) control.Add(cr);
        }
        Assert.Equal(20, guaranteed.Count);
        foreach (var r in guaranteed) Assert.True(r.SortRank() >= Rarity.Rare.SortRank(), $"{r} hatched from a rare egg");
        Assert.Contains(Rarity.Common, control); // otherwise the defect condition is not exercised
    });

    /// <summary>The uncommon egg excludes only common, and really hatches uncommon (catches over-filtering).</summary>
    [Fact]
    public void UncommonEggHatchesUncommonAndExcludesOnlyCommon() => MainActor.Run(async () =>
    {
        var sawUncommon = false;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var s = EggStore(Rarity.Uncommon, seed, new TieredProvider());
            await s.HatchIfNeeded();
            var r = s.State.Active?.Rarity ?? throw new Xunit.Sdk.XunitException($"hatch failed seed {seed}");
            Assert.NotEqual(Rarity.Common, r);
            if (r == Rarity.Uncommon) sawUncommon = true;
        }
        Assert.True(sawUncommon);
    });

    /// <summary>Legendaries stay reachable from premium eggs (only a legendary-only product is banned).</summary>
    [Fact]
    public void LegendaryStillReachableFromPremiumEgg() => MainActor.Run(async () =>
    {
        var sawLegendary = false;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var s = EggStore(Rarity.Rare, seed, new TieredProvider());
            await s.HatchIfNeeded();
            if (s.State.Active?.Rarity == Rarity.Legendary)
            {
                sawLegendary = true;
                break;
            }
        }
        Assert.True(sawLegendary);
    });

    // ③ REST fallback alone

    [Fact]
    public void RestFallbackRespectsGuarantee() => MainActor.Run(async () =>
    {
        var hatched = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var s = EggStore(Rarity.Rare, seed, new RestOnlyTieredProvider());
            await s.HatchIfNeeded();
            if (s.State.Active?.Rarity is not { } r) continue; // 16 misses (≈0.15%) keep the egg — fine
            hatched += 1;
            Assert.Equal(Rarity.Rare, r);
        }
        Assert.True(hatched > 0, "the fallback path never ran");
    });

    [Fact]
    public void RestFallbackHonoursUncommonTierWithoutOverFiltering() => MainActor.Run(async () =>
    {
        var sawUncommon = false;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var s = EggStore(Rarity.Uncommon, seed, new RestOnlyTieredProvider());
            await s.HatchIfNeeded();
            if (s.State.Active?.Rarity is not { } r) continue;
            Assert.NotEqual(Rarity.Common, r);
            if (r == Rarity.Uncommon) sawUncommon = true;
        }
        Assert.True(sawUncommon);
    });

    /// <summary>Control: without a guarantee the same fallback provider also hatches common.</summary>
    [Fact]
    public void RestFallbackWithoutGuaranteeCanHatchCommon() => MainActor.Run(async () =>
    {
        var sawCommon = false;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var s = EggStore(null, seed, new RestOnlyTieredProvider());
            await s.HatchIfNeeded();
            if (s.State.Active?.Rarity == Rarity.Common)
            {
                sawCommon = true;
                break;
            }
        }
        Assert.True(sawCommon);
    });

    // ④ Final gate — a lying index never hands out less than the bought guarantee

    [Fact]
    public void HatchDiscardsSpeciesBelowGuaranteeWhenIndexIsStale() => MainActor.Run(async () =>
    {
        var s = EggStore(Rarity.Rare, 1, new LyingIndexProvider());
        await s.HatchIfNeeded();
        Assert.Null(s.State.Active);
        Assert.Null(s.State.PendingHatchID);
        Assert.Equal(Rarity.Rare, s.State.EggTier);
        Assert.Equal(PokemonBalance.EggHatchThreshold, s.State.EggUsage);
    });

    /// <summary>Control: the same provider without a guarantee hatches that common normally.</summary>
    [Fact]
    public void SameSpeciesHatchesNormallyWithoutGuarantee() => MainActor.Run(async () =>
    {
        var s = EggStore(null, 1, new LyingIndexProvider());
        await s.HatchIfNeeded();
        Assert.Equal(Rarity.Common, s.State.Active?.Rarity);
    });

    // Guarantee consumption — leaking into the next egg would be a permanent premium

    [Fact]
    public void HatchConsumesGuarantee() => MainActor.Run(async () =>
    {
        var s = EggStore(Rarity.Rare, 1, new TieredProvider());
        await s.HatchIfNeeded();
        Assert.NotNull(s.State.Active);
        Assert.Null(s.State.EggTier);
        Assert.Null(s.EggGuarantee);
    });

    [Fact]
    public void GuaranteeDoesNotSurviveIntoTheNextEgg() => MainActor.Run(async () =>
    {
        var s = EggStore(Rarity.Rare, 1, new TieredProvider());
        await s.HatchIfNeeded();
        var active = s.State.Active ?? throw new Xunit.Sdk.XunitException("hatch failed");
        Assert.Null(s.State.EggTier);
        s.ApplyUsage(PokemonBalance.GraduationTotal(active.Rarity) * 2); // single form → graduation
        Assert.Null(s.State.Active);
        Assert.Single(s.State.Dex);
        Assert.Null(s.State.EggTier);
        Assert.Null(s.EggGuarantee);
    });

    // Save transfer / normalization

    /// <summary>The guarantee is a purchase, not a device ledger — it travels with the save.</summary>
    [Fact]
    public void GuaranteeTravelsWithSave()
    {
        var imported = new CompanionState { EggTier = Rarity.Uncommon, UsedSinceInstall = 1_000_000 };
        var rebased = SaveTransfer.RebasedForThisDevice(imported, new CompanionState(),
            new Dictionary<string, long> { ["test"] = 0 }, "d", hasUsageData: true);
        Assert.Equal(Rarity.Uncommon, rebased.EggTier);
    }

    /// <summary>An active Pokémon and an egg guarantee cannot coexist; both the guarantee and its pre-roll go.</summary>
    [Fact]
    public void SanitizedDropsGuaranteeAndItsPreRollWhenActiveExists()
    {
        var s = new CompanionState
        {
            EggTier = Rarity.Rare,
            PendingHatchID = 3,
            Active = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1),
        };
        var cleaned = SaveTransfer.Sanitized(s);
        Assert.Null(cleaned.EggTier);
        Assert.Null(cleaned.PendingHatchID);
        // Kept in the egg state (no active).
        var egg = new CompanionState { EggTier = Rarity.Rare, PendingHatchID = 3 };
        Assert.Equal(Rarity.Rare, SaveTransfer.Sanitized(egg).EggTier);
        Assert.Equal(3, SaveTransfer.Sanitized(egg).PendingHatchID);
    }

    // Unsatisfiable guarantees — prevent an egg that never hatches

    /// <summary>Checks that the egg actually hatches, not merely that the file decodes.</summary>
    [Fact]
    public void UnsatisfiableGuaranteeIsNormalizedAwayAndEggStillHatches() => MainActor.Run(async () =>
    {
        var s = EggStore(Rarity.Legendary, 1, new TieredProvider());
        Assert.Null(s.State.EggTier);
        await s.HatchIfNeeded();
        Assert.NotNull(s.State.Active);
    });

    /// <summary>Tiers that are not on sale cannot be bought.</summary>
    [Fact]
    public void CannotBuyTierThatIsNotSold() => MainActor.Run(() =>
    {
        var s = ActiveStore();
        Assert.DoesNotContain((Rarity?)Rarity.Legendary, FreshEgg.ShopTiers);
        Assert.False(s.CanBuyEgg(Rarity.Legendary));
        Assert.False(s.BuyEgg(Rarity.Legendary));
        Assert.Equal(0, s.State.SpentTokens);
        Assert.NotNull(s.State.Active);
        Assert.False(s.CanBuyEgg(Rarity.Common)); // the plain egg is sold only as tier null
    });

    /// <summary>An unknown rarity raw value demotes to "no guarantee".</summary>
    [Fact]
    public void UnknownGuaranteeDecodesAsNoGuarantee()
    {
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":0,\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"dex\":[],\"collectedFinals\":[],\"eggTier\":\"mythic\"}";
        Assert.Null(CompanionJson.Deserialize(json).EggTier);
    }
}
