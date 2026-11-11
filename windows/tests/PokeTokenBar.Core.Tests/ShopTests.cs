using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of ShopTests.swift: shop (currency = usedSinceInstall − spentTokens).

/// <summary>Shop tests need no line; always throws (wallet/purchases are line-independent).</summary>
file sealed class ShopNoProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new HttpRequestException("offline"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

public class ShopTests
{
    private static CompanionStore Open(string path) =>
        new(new ShopNoProvider(), clock: () => Lines.FixedNow, filePath: path, rng: new SeededRng(1),
            settings: new AppSettings(null));

    /// <summary>Seeds used/spent directly in a state file (bypasses Update's delta crediting).</summary>
    private static CompanionStore Store(long used, long spent = 0, int rareCandy = 0)
    {
        var inv = rareCandy > 0 ? ",\"inventory\":{\"rareCandy\":" + rareCandy + "}" : "";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":" + used + ",\"spentTokens\":" + spent + ","
            + "\"lastDate\":\"d\",\"dex\":[],\"collectedFinals\":[]" + inv + "}";
        return Open(StateFiles.WriteText(json, StateFiles.TempPath("shop")));
    }

    // Balance

    [Fact]
    public void AvailableEqualsUsedWhenNothingSpent() => MainActor.Run(() =>
        Assert.Equal(1_000_000_000, Store(1_000_000_000).AvailableTokens));

    [Fact]
    public void AvailableSubtractsSpent() => MainActor.Run(() =>
        Assert.Equal(700_000_000, Store(1_000_000_000, spent: 300_000_000).AvailableTokens));

    /// <summary>spent &gt; used (abnormal file) never goes negative.</summary>
    [Fact]
    public void AvailableNeverNegative() => MainActor.Run(() =>
        Assert.Equal(0, Store(100_000_000, spent: 500_000_000).AvailableTokens));

    /// <summary>Old saves without spentTokens load as 0.</summary>
    [Fact]
    public void DecodesWithoutSpentTokens()
    {
        var json = """{"installBaselineSet":true,"usedSinceInstall":900,"lastDate":"d","dex":[]}""";
        var s = CompanionJson.Deserialize(json);
        Assert.Equal(0, s.SpentTokens);
        Assert.Equal(900, s.UsedSinceInstall);
    }

    [Fact]
    public void SpentTokensRoundTrip()
    {
        var st = new CompanionState { UsedSinceInstall = 1000, SpentTokens = 400 };
        var round = CompanionJson.Deserialize(CompanionJson.Serialize(st));
        Assert.Equal(400, round.SpentTokens);
    }

    // Purchase gate (boundaries)

    [Fact]
    public void CanBuyAtExactPrice() => MainActor.Run(() => Assert.True(Store(RareCandy.Price).CanBuyRareCandy));

    [Fact]
    public void CannotBuyOneBelowPrice() => MainActor.Run(() => Assert.False(Store(RareCandy.Price - 1).CanBuyRareCandy));

    // Purchase (debit + credit + persistence)

    [Fact]
    public void BuyDebitsWalletAndCreditsInventory() => MainActor.Run(() =>
    {
        var s = Store(1_000_000_000);
        Assert.True(s.BuyRareCandy());
        Assert.Equal(1, s.RareCandyCount);
        Assert.Equal(RareCandy.Price, s.State.SpentTokens);
        Assert.Equal(1_000_000_000 - RareCandy.Price, s.AvailableTokens);
        Assert.Equal(1_000_000_000, s.State.UsedSinceInstall); // growth meter unchanged
    });

    [Fact]
    public void BuyInsufficientIsNoOp() => MainActor.Run(() =>
    {
        var s = Store(400_000_000);
        Assert.False(s.BuyRareCandy());
        Assert.Equal(0, s.RareCandyCount);
        Assert.Equal(0, s.State.SpentTokens);
    });

    [Fact]
    public void MultipleBuysUntilBroke() => MainActor.Run(() =>
    {
        var s = Store(1_200_000_000); // two fit (1B), the third fails (200M left)
        Assert.True(s.BuyRareCandy());
        Assert.True(s.BuyRareCandy());
        Assert.False(s.BuyRareCandy());
        Assert.Equal(2, s.RareCandyCount);
        Assert.Equal(2 * RareCandy.Price, s.State.SpentTokens);
        Assert.Equal(200_000_000, s.AvailableTokens);
    });

    [Fact]
    public void BuyAddsToExistingStock() => MainActor.Run(() =>
    {
        var s = Store(1_000_000_000, rareCandy: 3);
        Assert.True(s.BuyRareCandy());
        Assert.Equal(4, s.RareCandyCount);
        Assert.Equal(ItemKind.RareCandy, s.OwnedItems[0].Kind);
        Assert.Equal(4, s.OwnedItems[0].Count);
    });

    /// <summary>[Persistence] Spending and stock survive a restart (same file reloaded).</summary>
    [Fact]
    public void BuyPersistsAcrossRestart() => MainActor.Run(() =>
    {
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":1000000000,\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"dex\":[],\"collectedFinals\":[]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("shop-persist"));
        var s1 = Open(path);
        Assert.True(s1.BuyRareCandy());

        var s2 = Open(path);
        Assert.Equal(1, s2.RareCandyCount);
        Assert.Equal(RareCandy.Price, s2.State.SpentTokens);
        Assert.Equal(1_000_000_000 - RareCandy.Price, s2.AvailableTokens);
    });

    // Bulk purchase (Buy(kind, count) / MaxBuyCount)

    [Fact]
    public void MaxBuyCountIsFloorOfWalletOverPrice() => MainActor.Run(() =>
    {
        Assert.Equal(2, Store(1_200_000_000).MaxBuyCount(ItemKind.RareCandy));
        Assert.Equal(12, Store(1_200_000_000).MaxBuyCount(ItemKind.Mint));
        Assert.Equal(0, Store(RareCandy.Price - 1).MaxBuyCount(ItemKind.RareCandy));
    });

    [Fact]
    public void MaxBuyCountCapsPassiveAtOne() => MainActor.Run(() =>
    {
        var s = Store(10 * ShinyCharm.Price);
        Assert.Equal(1, s.MaxBuyCount(ItemKind.ShinyCharm));
        Assert.False(s.Buy(ItemKind.ShinyCharm, count: 2));
        Assert.Equal(0, s.State.SpentTokens);
        Assert.True(s.Buy(ItemKind.ShinyCharm));
        Assert.Equal(0, s.MaxBuyCount(ItemKind.ShinyCharm));
    });

    [Fact]
    public void BuyCountDebitsTotalAndCreditsInventory() => MainActor.Run(() =>
    {
        var s = Store(2_000_000_000, rareCandy: 1);
        Assert.True(s.Buy(ItemKind.RareCandy, count: 3));
        Assert.Equal(4, s.RareCandyCount);
        Assert.Equal(3 * RareCandy.Price, s.State.SpentTokens);
        Assert.Equal(2_000_000_000 - 3 * RareCandy.Price, s.AvailableTokens);
        Assert.Equal(2_000_000_000, s.State.UsedSinceInstall);
    });

    [Fact]
    public void BuyExactlyMaxCountEmptiesWallet() => MainActor.Run(() =>
    {
        var s = Store(3 * RareCandy.Price);
        Assert.True(s.Buy(ItemKind.RareCandy, count: s.MaxBuyCount(ItemKind.RareCandy)));
        Assert.Equal(3, s.RareCandyCount);
        Assert.Equal(0, s.AvailableTokens);
    });

    /// <summary>More than affordable is rejected entirely — never a partial purchase.</summary>
    [Fact]
    public void BuyCountAboveAffordableIsNoOp() => MainActor.Run(() =>
    {
        var s = Store(1_200_000_000);
        Assert.False(s.Buy(ItemKind.RareCandy, count: 3));
        Assert.Equal(0, s.RareCandyCount);
        Assert.Equal(0, s.State.SpentTokens);
    });

    /// <summary>Zero/negative counts are a no-op (a negative debit would grow the balance).</summary>
    [Fact]
    public void BuyNonPositiveCountIsNoOp() => MainActor.Run(() =>
    {
        var s = Store(1_000_000_000);
        Assert.False(s.Buy(ItemKind.RareCandy, count: 0));
        Assert.False(s.Buy(ItemKind.RareCandy, count: -1));
        Assert.Equal(0, s.RareCandyCount);
        Assert.Equal(0, s.State.SpentTokens);
    });

    [Fact]
    public void BuyCountPersistsAcrossRestart() => MainActor.Run(() =>
    {
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":1000000000,\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"dex\":[],\"collectedFinals\":[]}";
        var path = StateFiles.WriteText(json, StateFiles.TempPath("shop-bulk"));
        var s1 = Open(path);
        Assert.True(s1.Buy(ItemKind.Mint, count: 4));

        var s2 = Open(path);
        Assert.Equal(4, s2.ItemCount(ItemKind.Mint));
        Assert.Equal(4 * Mint.Price, s2.State.SpentTokens);
    });

    // Ordering (cheapest first, bought passive items last)

    [Fact]
    public void ItemsSortedByPriceAscending() => MainActor.Run(() =>
    {
        var items = Store(0).PurchasableItems;
        Assert.Equal([ItemKind.Mint, ItemKind.RareCandy, ItemKind.ShinyCharm], items);
        var prices = items.Select(i => i.ShopPrice()!.Value).ToList();
        Assert.Equal(prices.Order(), prices);
    });

    [Fact]
    public void OwnedPassiveSinksToBottom() => MainActor.Run(() =>
    {
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":0,\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"dex\":[],\"collectedFinals\":[],\"inventory\":{\"shinyCharm\":1}}";
        var s = Open(StateFiles.WriteText(json, StateFiles.TempPath("shop-sort")));
        Assert.True(s.ItemCount(ItemKind.ShinyCharm) > 0);
        Assert.Equal(ItemKind.ShinyCharm, s.PurchasableItems[^1]);
    });

    // ShopEntries (items + three eggs merged into one price-ascending list)

    private static readonly ShopEntry[] ExpectedEntries =
    [
        ShopEntry.ForItem(ItemKind.Mint),        // 100M
        ShopEntry.ForItem(ItemKind.RareCandy),   // 500M
        ShopEntry.ForEgg(null),                  // 1B
        ShopEntry.ForEgg(Rarity.Uncommon),       // 2.5B
        ShopEntry.ForItem(ItemKind.ShinyCharm),  // 3B
        ShopEntry.ForEgg(Rarity.Rare),           // 4B
    ];

    /// <summary>Eggs interleave by price (regression: eggs were appended below the 3B charm). Premium eggs are
    /// deliberately not grouped together.</summary>
    [Fact]
    public void ShopEntriesInterleavesFreshEggByPrice() => MainActor.Run(() =>
    {
        var mon = "{\"baseID\":10,\"pathIDs\":[10],\"stageIndex\":0,\"usedAtStage\":200000000,"
            + "\"rarity\":\"common\",\"totalForms\":3,\"isShiny\":false}";
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":5000000000,\"spentTokens\":0,"
            + "\"lastDate\":\"d\",\"active\":" + mon + ",\"dex\":[],\"collectedFinals\":[]}";
        var s = Open(StateFiles.WriteText(json, StateFiles.TempPath("shop-entries")));
        Assert.True(s.HasActive);
        Assert.Equal(ExpectedEntries, s.ShopEntries);
        var prices = s.ShopEntries.Select(e => e.Price).ToList();
        Assert.Equal(prices.Order(), prices);
    });

    /// <summary>Without an active Pokémon the eggs stay listed but the HasActive gate blocks every purchase
    /// (checked with a balance that covers every tier).</summary>
    [Fact]
    public void ShopEntriesKeepsEggsVisibleButUnbuyableWhenNoActive() => MainActor.Run(() =>
    {
        var s = Store(5_000_000_000);
        Assert.False(s.HasActive);
        Assert.Equal(ExpectedEntries, s.ShopEntries);
        foreach (var tier in FreshEgg.ShopTiers)
        {
            Assert.Contains(ShopEntry.ForEgg(tier), s.ShopEntries);
            Assert.False(s.CanBuyEgg(tier));
            Assert.False(s.BuyEgg(tier));
        }
        Assert.Equal(5_000_000_000, s.AvailableTokens);
    });
}
