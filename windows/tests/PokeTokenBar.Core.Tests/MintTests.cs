using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of MintTests.swift.

/// <summary>Mints do not need the line (nature lives in MonState only), so the provider always fails.</summary>
file sealed class MintNoProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new HttpRequestException("offline"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

public class MintTests
{
    private static CompanionStore Open(string path, ulong seed) =>
        new(new MintNoProvider(), clock: () => Lines.FixedNow, filePath: path, rng: new SeededRng(seed),
            dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));

    /// <summary>State file with an active Pokémon and mint stock (works without a loaded line).
    /// nature null reproduces an old individual without a nature.</summary>
    private static CompanionStore Store(string? nature = "adamant", int mint = 1, long used = 1_000_000_000,
        long spent = 0, long usedAtStage = 50_000_000, bool shiny = false, ulong seed = 7)
    {
        var natureField = nature != null ? $",\"nature\":\"{nature}\"" : "";
        var active = $"{{\"baseID\":1,\"pathIDs\":[1],\"stageIndex\":0,\"usedAtStage\":{usedAtStage},"
                     + $"\"rarity\":\"common\",\"totalForms\":3,\"isShiny\":{(shiny ? "true" : "false")}{natureField}}}";
        var inv = mint > 0 ? $",\"inventory\":{{\"mint\":{mint}}}" : "";
        var json = $"{{\"installBaselineSet\":true,\"usedSinceInstall\":{used},\"spentTokens\":{spent},"
                   + $"\"lastDate\":\"d\",\"active\":{active},\"dex\":[],\"collectedFinals\":[]{inv}}}";
        return Open(StateFiles.WriteText(json, StateFiles.TempPath("mint")), seed);
    }

    // Use

    [Fact]
    public void UseMintChangesNatureToDifferent() => MainActor.Run(() =>
    {
        var s = Store(nature: "adamant", mint: 1);
        Assert.Equal(PokemonNature.Adamant, s.State.Active?.Nature);
        var @new = s.UseMint();
        Assert.NotNull(@new);
        Assert.NotEqual(PokemonNature.Adamant, @new);
        Assert.Equal(@new, s.State.Active?.Nature);
        Assert.Equal(0, s.ItemCount(ItemKind.Mint));
    });

    /// <summary>Every use changes to a nature different from the one just before.</summary>
    [Fact]
    public void MintNeverRepeatsCurrentAcrossUses() => MainActor.Run(() =>
    {
        var s = Store(nature: "adamant", mint: 6);
        for (var i = 0; i < 6; i++)
        {
            var before = s.State.Active?.Nature;
            var @new = s.UseMint();
            Assert.NotNull(@new);
            Assert.NotEqual(before, @new);
        }
    });

    [Fact]
    public void UseMintFromNilNatureSetsValid() => MainActor.Run(() =>
    {
        var s = Store(nature: null, mint: 1);
        Assert.Null(s.State.Active?.Nature);
        var @new = s.UseMint();
        Assert.NotNull(@new);
        Assert.Equal(@new, s.State.Active?.Nature);
    });

    /// <summary>Growth, species, shiny, usedAtStage and statistics all unchanged (purely cosmetic).</summary>
    [Fact]
    public void UseMintDoesNotAffectGrowthOrIdentity() => MainActor.Run(() =>
    {
        var s = Store(nature: "adamant", mint: 1, used: 1_000_000_000, usedAtStage: 50_000_000, shiny: true);
        var beforeStage = s.State.Active?.StageIndex;
        var beforeUsed = s.State.UsedSinceInstall;
        var beforeSpecies = s.CurrentSpeciesID;
        s.UseMint();
        Assert.Equal(50_000_000, s.State.Active?.UsedAtStage);
        Assert.Equal(beforeStage, s.State.Active?.StageIndex);
        Assert.Equal(beforeSpecies, s.CurrentSpeciesID);
        Assert.True(s.CurrentIsShiny);
        Assert.Equal(beforeUsed, s.State.UsedSinceInstall);
    });

    // Gates

    [Fact]
    public void CannotUseMintOnEgg() => MainActor.Run(() =>
    {
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":1,\"lastDate\":\"d\","
                   + "\"dex\":[],\"collectedFinals\":[],\"inventory\":{\"mint\":2}}";
        var s = Open(StateFiles.WriteText(json, StateFiles.TempPath("mint-egg")), 1);
        Assert.True(s.IsEgg);
        Assert.False(s.CanUseMint);
        Assert.Null(s.UseMint());
        Assert.Equal(2, s.ItemCount(ItemKind.Mint));
    });

    [Fact]
    public void CannotUseMintWithoutStock() => MainActor.Run(() =>
    {
        var s = Store(nature: "adamant", mint: 0);
        Assert.False(s.CanUseMint);
        Assert.Null(s.UseMint());
    });

    // Feedback

    [Fact]
    public void MintFeedbackSetAndConsumed() => MainActor.Run(() =>
    {
        var s = Store(nature: "adamant", mint: 1);
        var before = s.MintFeedbackSeq;
        var @new = s.UseMint();
        Assert.Equal(@new, s.MintFeedbackNature);
        Assert.Equal(before + 1, s.MintFeedbackSeq);
        s.ConsumeMintFeedback();
        Assert.Null(s.MintFeedbackNature);
    });

    // Persistence

    [Fact]
    public void UseMintPersistsAcrossRestart() => MainActor.Run(() =>
    {
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":1,\"lastDate\":\"d\","
                   + "\"active\":{\"baseID\":1,\"pathIDs\":[1],\"stageIndex\":0,\"usedAtStage\":0,\"rarity\":\"common\",\"totalForms\":3,\"nature\":\"adamant\"},"
                   + "\"inventory\":{\"mint\":2},\"dex\":[],\"collectedFinals\":[]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("mint-persist"));
        var s1 = Open(path, 3);
        var @new = s1.UseMint();
        Assert.NotNull(@new);

        var s2 = Open(path, 3);
        Assert.Equal(@new, s2.State.Active?.Nature);
        Assert.Equal(1, s2.ItemCount(ItemKind.Mint));
    });

    // Shop

    [Fact]
    public void MintShopPriceAndPurchasable() => MainActor.Run(() =>
    {
        Assert.Equal(Mint.Price, ItemKind.Mint.ShopPrice());
        Assert.Equal(100_000_000, ItemKind.Mint.ShopPrice());
        var s = Store(mint: 0);
        Assert.Contains(ItemKind.RareCandy, s.PurchasableItems);
        Assert.Contains(ItemKind.Mint, s.PurchasableItems);
    });

    [Fact]
    public void BuyMintDebitsWalletAndCredits() => MainActor.Run(() =>
    {
        var s = Store(mint: 0, used: 300_000_000);
        Assert.True(s.CanBuy(ItemKind.Mint));
        Assert.True(s.Buy(ItemKind.Mint));
        Assert.Equal(1, s.ItemCount(ItemKind.Mint));
        Assert.Equal(Mint.Price, s.State.SpentTokens);
        Assert.Equal(300_000_000 - Mint.Price, s.AvailableTokens);
    });

    [Fact]
    public void CannotBuyMintBelowPrice() => MainActor.Run(() =>
    {
        var s = Store(mint: 0, used: Mint.Price - 1);
        Assert.False(s.CanBuy(ItemKind.Mint));
        Assert.False(s.Buy(ItemKind.Mint));
        Assert.Equal(0, s.ItemCount(ItemKind.Mint));
    });
}
