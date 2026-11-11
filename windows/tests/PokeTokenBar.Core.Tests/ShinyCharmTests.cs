using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of ShinyCharmTests.swift. SpriteShinyReloadTests (SwiftUI SpriteView) is not ported.

file sealed class CharmStubProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(new EvoLine(baseSpeciesID, new EvoNode(baseSpeciesID),
        Rarity.Common, new Dictionary<int, Dictionary<string, string>>
        {
            [baseSpeciesID] = new() { ["en"] = $"P{baseSpeciesID}", ["ko"] = $"포{baseSpeciesID}", ["ja"] = $"ポ{baseSpeciesID}" },
        }));

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(1, 255)]);
}

public class ShinyCharmTests
{
    /// <summary>An egg-state store (active null) with the given wallet and charm ownership.</summary>
    private static CompanionStore Store(long used = 5_000_000_000, long spent = 0, bool charm = false, ulong seed = 7)
    {
        var inv = charm ? ",\"inventory\":{\"shinyCharm\":1}" : "";
        var json = $"{{\"installBaselineSet\":true,\"usedSinceInstall\":{used},\"spentTokens\":{spent},"
                   + $"\"lastDate\":\"d\",\"active\":null,\"dex\":[],\"collectedFinals\":[]{inv}}}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("charm"));
        return new CompanionStore(new CharmStubProvider(), clock: () => Lines.FixedNow, filePath: path,
            rng: new SeededRng(seed), dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));
    }

    // Pure decision: the charm switches the denominator 48/64 and flips the roll.

    [Fact]
    public void RollsShinyFlipsWithCharm()
    {
        Assert.True(CompanionStore.RollsShiny(48, charmOwned: true));
        Assert.False(CompanionStore.RollsShiny(48, charmOwned: false));
        Assert.True(CompanionStore.RollsShiny(64, charmOwned: false));
        Assert.False(CompanionStore.RollsShiny(64, charmOwned: true));
        Assert.True(CompanionStore.RollsShiny(96, charmOwned: true));
        Assert.False(CompanionStore.RollsShiny(96, charmOwned: false));
        Assert.True(CompanionStore.RollsShiny(0, charmOwned: true));
        Assert.True(CompanionStore.RollsShiny(0, charmOwned: false));
        Assert.False(CompanionStore.RollsShiny(1, charmOwned: true));
        Assert.False(CompanionStore.RollsShiny(1, charmOwned: false));
    }

    /// <summary>The notification shows the odds the hatch used (was stuck at "(1/64)" with the charm).
    /// The per-language text checks run against the temporary English-only L stub.</summary>
    [Fact]
    public void ShinyNotificationShowsTheOddsTheHatchUsed() => MainActor.Run(() =>
    {
        Assert.Equal(PokemonOdds.ShinyDenominator, CompanionStore.ShinyDenominatorFor(charmOwned: false));
        Assert.Equal(ShinyCharm.ShinyDenominator, CompanionStore.ShinyDenominatorFor(charmOwned: true));
        Assert.Equal(64UL, Store(charm: false).ShinyDenominator);
        Assert.Equal(48UL, Store(charm: true).ShinyDenominator);
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var withCharm = new L(language).NotifShinyHatchBody("P", 48);
            Assert.Contains("48", withCharm);
            Assert.DoesNotContain("64", withCharm);
        }
    });

    /// <summary>At the Ditto reveal the charm ownership at hatch is unknown, so no odds are claimed.</summary>
    [Fact]
    public void ShinyDittoRevealDoesNotClaimFixedOdds()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var body = new L(language).NotifShinyDittoRevealBody("P");
            Assert.False(body.Contains("64") || body.Contains("48"), $"{language}: {body}");
        }
    }

    [Fact]
    public void ConstantsAndPassiveFlag()
    {
        Assert.Equal(3_000_000_000, ShinyCharm.Price);
        Assert.Equal(48UL, ShinyCharm.ShinyDenominator);
        Assert.True(ItemKind.ShinyCharm.IsPassive());
        Assert.False(ItemKind.RareCandy.IsPassive());
        Assert.False(ItemKind.Mint.IsPassive());
        Assert.Equal("shiny-charm", ItemKind.ShinyCharm.SpriteName());
    }

    // Purchase / ownership (passive = bought once)

    [Fact]
    public void BuyDeductsAndOwns() => MainActor.Run(() =>
    {
        var s = Store(used: 5_000_000_000, spent: 0, charm: false);
        Assert.False(s.OwnsShinyCharm);
        Assert.Contains(ItemKind.ShinyCharm, s.PurchasableItems);
        Assert.True(s.CanBuy(ItemKind.ShinyCharm));
        Assert.True(s.Buy(ItemKind.ShinyCharm));
        Assert.True(s.OwnsShinyCharm);
        Assert.Equal(1, s.ItemCount(ItemKind.ShinyCharm));
        Assert.Equal(ShinyCharm.Price, s.State.SpentTokens);
        Assert.Equal(5_000_000_000 - ShinyCharm.Price, s.AvailableTokens);
    });

    /// <summary>Passive items cannot be bought again: canBuy false, buy no-op, spending/count unchanged.</summary>
    [Fact]
    public void BuyOnceNoRepurchase() => MainActor.Run(() =>
    {
        var s = Store(used: 10_000_000_000, charm: true);
        Assert.True(s.OwnsShinyCharm);
        Assert.False(s.CanBuy(ItemKind.ShinyCharm));
        var spentBefore = s.State.SpentTokens;
        Assert.False(s.Buy(ItemKind.ShinyCharm));
        Assert.Equal(spentBefore, s.State.SpentTokens);
        Assert.Equal(1, s.ItemCount(ItemKind.ShinyCharm));
    });

    [Fact]
    public void CanBuyNeedsEnoughTokens() => MainActor.Run(() =>
    {
        var s = Store(used: 2_000_000_000);
        Assert.False(s.CanBuy(ItemKind.ShinyCharm));
        Assert.False(s.Buy(ItemKind.ShinyCharm));
        Assert.False(s.OwnsShinyCharm);
    });

    [Fact]
    public void OwnsReflectsInventory() => MainActor.Run(() =>
    {
        Assert.False(Store(charm: false).OwnsShinyCharm);
        Assert.True(Store(charm: true).OwnsShinyCharm);
    });

    // Hatch smoke test with the charm owned.

    [Fact]
    public void HatchWorksWithCharmOwned() => MainActor.Run(async () =>
    {
        var s = Store(used: 5_000_000_000, charm: true, seed: 7);
        await s.Hatch(1);
        Assert.NotNull(s.State.Active);
        Assert.Equal(1, s.CurrentSpeciesID);
    });
}
