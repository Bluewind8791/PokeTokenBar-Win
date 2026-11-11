using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of DifficultyTests.swift: the multipliers must change actual behaviour, not just the display.
// Same token input with different multipliers must change the outcome (evolve / hatch / purchase).

file sealed class StubDiffProvider(EvoLine value) : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(value);
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(value.BaseID, 255)]);
}

public class DifficultyTests
{
    private static EvoLine Line()
    {
        var names = new[] { 1, 2, 3 }.ToDictionary(id => id, id => new Dictionary<string, string> { ["en"] = $"P{id}" });
        return new EvoLine(1, new EvoNode(1, [new EvoNode(2, [new EvoNode(3)])]), Rarity.Common, names);
    }

    /// <summary>With <paramref name="used"/> the state file starts with that wallet balance (ShopTests' JSON seed pattern).</summary>
    private static CompanionStore Store(double growth = 1.0, double shop = 1.0, long used = 0)
    {
        var path = StateFiles.TempPath("ptb-diff");
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":" + used + ",\"spentTokens\":0,"
                   + "\"lastDate\":\"d\",\"dex\":[],\"collectedFinals\":[]}";
        File.WriteAllText(path, json);
        var settings = new AppSettings(null);
        settings.Set("growthDifficulty", growth);
        settings.Set("shopDifficulty", shop);
        return new CompanionStore(new StubDiffProvider(Line()), clock: () => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            filePath: path, rng: new SeededRng(7), dittoDisguiseRollingEnabled: false, settings: settings);
    }

    private static async Task<CompanionStore> Hatched(double growth = 1.0, double shop = 1.0, long used = 0)
    {
        var s = Store(growth, shop, used);
        await s.Hatch(1);
        return s;
    }

    [Fact]
    public void RepeatBoostComposesWithDifficultyAndLiveChanges() => MainActor.Run(async () =>
    {
        var s = await Hatched(growth: 0.5);
        s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common) / 2);
        Assert.Null(s.State.Active);
        await s.Hatch(1);
        Assert.True(s.State.Active?.HasGrowthBoost);
        var @base = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0);
        Assert.Equal(@base / 4, s.Threshold);
        s.ApplyUsage(@base / 4 - 1);
        Assert.Equal(0, s.State.Active?.StageIndex);
        Assert.Equal(1, s.TokensToNext);
        s.ApplyUsage(1);
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(0, s.State.Active?.UsedAtStage);
        var stageOne = s.Threshold;
        s.ApplyUsage(stageOne / 2);
        s.SetGrowthDifficulty(0.25);
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(0.5, s.Progress, 6);
        s.ApplyUsage(s.TokensToNext);
        Assert.Equal(2, s.State.Active?.StageIndex);
        s.ApplyUsage(s.TokensToNext);
        Assert.Null(s.State.Active);
        Assert.Equal(3, s.State.Dex[^1].FinalID);
    });

    // 1. Evolution — same tokens, different result (a state transition, not the display)

    [Fact]
    public void SameTokensEvolveOnlyWhenDifficultyLowered() => MainActor.Run(async () =>
    {
        var half = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0) / 2;

        var normal = await Hatched(growth: 1.0);
        normal.ApplyUsage(half);
        Assert.Equal(0, normal.State.Active?.StageIndex); // 1.0x: half must not evolve
        Assert.Equal(1, normal.State.Active?.CurrentID);

        var easy = await Hatched(growth: 0.5);
        easy.ApplyUsage(half);
        Assert.Equal(1, easy.State.Active?.StageIndex); // 0.5x: the same half evolves
        Assert.Equal(2, easy.State.Active?.CurrentID);  // the species really changed
    });

    [Fact]
    public void HarderDifficultyBlocksEvolutionThatWouldOtherwiseHappen() => MainActor.Run(async () =>
    {
        var baseThreshold = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0);

        var normal = await Hatched(growth: 1.0);
        normal.ApplyUsage(baseThreshold);
        Assert.Equal(1, normal.State.Active?.StageIndex);

        var hard = await Hatched(growth: 2.0);
        hard.ApplyUsage(baseThreshold);
        Assert.Equal(0, hard.State.Active?.StageIndex);
    });

    /// <summary>The displayed tokensToNext/progress use the same threshold as the transition.</summary>
    [Fact]
    public void DisplayedTokensToNextMatchesTheThresholdThatActuallyEvolves() => MainActor.Run(async () =>
    {
        var s = await Hatched(growth: 0.5);
        var shown = s.TokensToNext;
        Assert.Equal(PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0) / 2, shown);
        s.ApplyUsage(shown - 1);
        Assert.Equal(0, s.State.Active?.StageIndex);
        Assert.Equal(1, s.TokensToNext);
        s.ApplyUsage(1);
        Assert.Equal(1, s.State.Active?.StageIndex);
    });

    // 2. Hatching — the point where the egg actually hatches moves

    [Fact]
    public void EggHatchPointMovesWithDifficulty() => MainActor.Run(async () =>
    {
        var half = PokemonBalance.EggHatchThreshold / 2;

        var normal = Store(growth: 1.0);
        normal.Update(new Dictionary<string, long> { ["t"] = 0 }, "d1", 0, BurnTier.Idle, false, true);
        normal.Update(new Dictionary<string, long> { ["t"] = half }, "d1", 0, BurnTier.Idle, false, true);
        await normal.HatchIfNeeded();
        Assert.Null(normal.State.Active); // 1.0x: half does not hatch

        var easy = Store(growth: 0.5);
        easy.Update(new Dictionary<string, long> { ["t"] = 0 }, "d1", 0, BurnTier.Idle, false, true);
        easy.Update(new Dictionary<string, long> { ["t"] = half }, "d1", 0, BurnTier.Idle, false, true);
        await easy.HatchIfNeeded();
        Assert.NotNull(easy.State.Active); // 0.5x: the same amount hatches
    });

    /// <summary>The first-run hint threshold follows difficulty. (The localized hint text assertions are skipped:
    /// <c>L</c> is a temporary stub in this port.)</summary>
    [Fact]
    public void FirstRunEggHintShowsTheScaledHatchPoint() => MainActor.Run(() =>
    {
        var easy = Store(growth: 0.1);
        Assert.Equal(500_000, easy.EggHatchThreshold);
        Assert.Equal(PokemonBalance.EggHatchThreshold, Store(growth: 1.0).EggHatchThreshold);
        Assert.Equal("500K", TokenFormatter.Compact(easy.EggHatchThreshold));
    });

    // 3. Shop — charged amount and purchasability change

    [Fact]
    public void ShopGateAndChargedAmountFollowDifficulty() => MainActor.Run(async () =>
    {
        var halfPrice = RareCandy.Price / 2;

        var normal = await Hatched(shop: 1.0, used: halfPrice);
        Assert.False(normal.CanBuy(ItemKind.RareCandy));

        var cheap = await Hatched(shop: 0.5, used: halfPrice);
        Assert.True(cheap.CanBuy(ItemKind.RareCandy));
        Assert.True(cheap.Buy(ItemKind.RareCandy));
        Assert.Equal(halfPrice, cheap.State.SpentTokens);
        Assert.Equal(1, cheap.ItemCount(ItemKind.RareCandy));
    });

    [Fact]
    public void EggPriceScalesAndIsActuallyCharged() => MainActor.Run(async () =>
    {
        var s = await Hatched(shop: 0.5, used: FreshEgg.Price);
        Assert.Equal(FreshEgg.Price / 2, s.Price(ShopEntry.ForEgg(null)));
        Assert.True(s.BuyEgg(null));
        Assert.Equal(FreshEgg.Price / 2, s.State.SpentTokens);
    });

    // 4. The two sliders are independent (premium egg prices derive from graduationTotal ratios)

    [Fact]
    public void GrowthSliderDoesNotMoveShopPricesAndViceVersa() => MainActor.Run(async () =>
    {
        var growthOnly = await Hatched(growth: 0.1, shop: 1.0);
        Assert.Equal(RareCandy.Price, growthOnly.Price(ShopEntry.ForItem(ItemKind.RareCandy)));
        Assert.Equal(FreshEgg.PriceGuaranteeing(Rarity.Rare), growthOnly.Price(ShopEntry.ForEgg(Rarity.Rare)));

        var shopOnly = await Hatched(growth: 1.0, shop: 0.1);
        Assert.Equal(PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0), shopOnly.TokensToNext);
    });

    /// <summary>Premium egg price ratios (1 : 2.5 : 4) survive scaling.</summary>
    [Fact]
    public void GradedEggRatioSurvivesScaling() => MainActor.Run(async () =>
    {
        var s = await Hatched(shop: 0.4);
        var plain = (double)s.Price(ShopEntry.ForEgg(null));
        Assert.Equal(2.5, s.Price(ShopEntry.ForEgg(Rarity.Uncommon)) / plain, 3);
        Assert.Equal(4.0, s.Price(ShopEntry.ForEgg(Rarity.Rare)) / plain, 3);
    });

    // 5. Live changes — applied in place without a restart

    [Fact]
    public void LoweringDifficultyPreservesIncompleteStage() => MainActor.Run(async () =>
    {
        var s = await Hatched(growth: 1.0);
        var baseThreshold = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0);
        s.ApplyUsage(baseThreshold - 1);
        Assert.Equal(0, s.State.Active?.StageIndex);

        s.SetGrowthDifficulty(0.5);
        Assert.Equal(0, s.State.Active?.StageIndex);
        Assert.Equal(1, s.TokensToNext);
        s.ApplyUsage(0);
        Assert.Equal(0, s.State.Active?.StageIndex);
        s.ApplyUsage(1);
        Assert.Equal(1, s.State.Active?.StageIndex);
    });

    [Fact]
    public void ShopPriceChangesLiveWithoutRestart() => MainActor.Run(async () =>
    {
        var s = await Hatched(shop: 1.0);
        Assert.Equal(RareCandy.Price, s.Price(ShopEntry.ForItem(ItemKind.RareCandy)));
        s.SetShopDifficulty(0.5);
        Assert.Equal(RareCandy.Price / 2, s.Price(ShopEntry.ForItem(ItemKind.RareCandy)));
    });

    [Fact]
    public void DifficultyPersistsToDefaults() => MainActor.Run(async () =>
    {
        var s = await Hatched();
        s.SetGrowthDifficulty(0.3);
        s.SetShopDifficulty(1.7);
        Assert.Equal(0.3, s.GrowthDifficulty, 4);
        Assert.Equal(1.7, s.ShopDifficulty, 4);
    });

    // 6. Clamping — garbage written from outside

    /// <summary>Bounds derive from the constants, so tests follow a range change.</summary>
    [Fact]
    public void GarbageDefaultsAreClamped()
    {
        const double lo = PokemonBalance.DifficultyLowerBound;
        const double hi = PokemonBalance.DifficultyUpperBound;
        Assert.Equal(lo, PokemonBalance.ClampDifficulty(0), 9);
        Assert.Equal(lo, PokemonBalance.ClampDifficulty(-5), 9);
        Assert.Equal(hi, PokemonBalance.ClampDifficulty(hi * 10), 9);
        // Non-finite values are damage, not "very hard": back to the default, not a bound.
        Assert.Equal(PokemonBalance.DefaultDifficulty, PokemonBalance.ClampDifficulty(double.NaN), 9);
        Assert.Equal(PokemonBalance.DefaultDifficulty, PokemonBalance.ClampDifficulty(double.PositiveInfinity), 9);
        Assert.Equal(PokemonBalance.DefaultDifficulty, PokemonBalance.ClampDifficulty(double.NegativeInfinity), 9);
    }

    /// <summary>A stored 0 cannot make a threshold 0 (no division by zero / degenerate loop).</summary>
    [Fact]
    public void ZeroInDefaultsCannotProduceZeroThreshold() => MainActor.Run(async () =>
    {
        var s = await Hatched(growth: 0);
        Assert.Equal(PokemonBalance.DifficultyLowerBound, s.GrowthDifficulty, 9);
        Assert.True(s.TokensToNext > 0);
        Assert.True(s.EggTokensToHatch > 0);
    });

    /// <summary>At the lowest multiplier no threshold or price collapses to 0.</summary>
    [Fact]
    public void NothingCollapsesToZeroAtMinimumDifficulty() => MainActor.Run(async () =>
    {
        const double lo = PokemonBalance.DifficultyLowerBound;
        Assert.True(PokemonBalance.Scaled(PokemonBalance.EggHatchThreshold, lo) > 0);
        foreach (var rarity in new[] { Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.Legendary })
            for (var k = 1; k <= 3; k++)
                for (var i = 0; i < k; i++)
                    Assert.True(PokemonBalance.Scaled(PokemonBalance.PhaseThreshold(rarity, k, i), lo) > 0,
                        $"rarity={rarity} k={k} i={i}");
        var s = await Hatched(shop: lo);
        foreach (var entry in s.ShopEntries) Assert.True(s.Price(entry) > 0, $"{entry}");
    });

    // 7. Slider position mapping (log)

    [Fact]
    public void PositionRoundTripsThroughDifficulty()
    {
        foreach (var value in new[] { 0.1, 0.25, 0.5, 1.0, 1.5, 2.0 })
        {
            var back = PokemonBalance.Difficulty(PokemonBalance.DifficultyPosition(value));
            Assert.True(Math.Abs(back - value) <= value * 0.02, $"value={value} back={back}");
        }
    }

    [Fact]
    public void PositionEndpointsMapToRangeBounds()
    {
        Assert.Equal(PokemonBalance.DifficultyLowerBound, PokemonBalance.Difficulty(0), 9);
        Assert.Equal(PokemonBalance.DifficultyUpperBound, PokemonBalance.Difficulty(1), 6);
        Assert.Equal(0, PokemonBalance.DifficultyPosition(PokemonBalance.DifficultyLowerBound), 9);
        Assert.Equal(1, PokemonBalance.DifficultyPosition(PokemonBalance.DifficultyUpperBound), 9);
    }

    /// <summary>The default (100%) stays reachable on the log slider — unreachable without the snap.</summary>
    [Fact]
    public void DefaultIsReachableByDragging()
    {
        var exactPosition = PokemonBalance.DifficultyPosition(1.0);
        foreach (var offset in new[] { -0.009, -0.005, 0.0, 0.005, 0.009 })
            Assert.Equal(1.0, PokemonBalance.Difficulty(exactPosition + offset), 9);
    }

    /// <summary>Snapped values keep two significant digits.</summary>
    [Fact]
    public void SnapKeepsTwoSignificantDigits()
    {
        Assert.Equal(1.2, PokemonBalance.SnapDifficulty(1.234), 9);
        Assert.Equal(16, PokemonBalance.SnapDifficulty(15.7), 9);
        Assert.Equal(0.012, PokemonBalance.SnapDifficulty(0.0123), 9);
    }

    // testPercentFormatting is not ported: it only checks L.difficultyValue text (L is a stub here).
}
