using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of DexNameMigrationTests.swift.

file sealed class OfflineNamesException : Exception;

file sealed class MigratingNameProvider : IPokeProviding
{
    private EvoLine _value;
    private bool _offline;
    public int Calls { get; private set; }

    public MigratingNameProvider(Dictionary<int, Dictionary<string, string>> names) =>
        _value = new EvoLine(1, new EvoNode(1, [new EvoNode(2)]), Rarity.Common, names);

    public void Configure(Dictionary<int, Dictionary<string, string>> names, bool offline = false)
    {
        _value = new EvoLine(1, _value.Tree, Rarity.Common, names);
        _offline = offline;
    }

    public async Task<EvoLine> Line(int baseSpeciesID)
    {
        Calls += 1;
        await Task.Delay(20);
        if (_offline) throw new OfflineNamesException();
        return _value;
    }

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
}

public class DexNameMigrationTests
{
    private static Dictionary<int, Dictionary<string, string>> AllNames() => new()
    {
        [1] = new() { ["ko"] = "이상해씨", ["en"] = "Bulbasaur", ["it"] = "Bulbasaur" },
        [2] = new() { ["ko"] = "이상해풀", ["en"] = "Ivysaur", ["it"] = "Ivysaur" },
    };

    private static DexEntry Entry(string id = "old-catch") =>
        new(1, 2, [1, 2], Rarity.Common, DateTimeOffset.FromUnixTimeSeconds(123), isShiny: true,
            nature: PokemonNature.Brave,
            names: new() { [1] = new() { ["en"] = "Bulbasaur" }, [2] = new() { ["en"] = "Ivysaur" } }, id: id);

    private static string Fixture(IEnumerable<DexEntry> entries, bool legacy = true)
    {
        var state = new CompanionState
        {
            Dex = entries.ToList(),
            Language = AppLanguage.Ko,
            UsedSinceInstall = 1234567,
            Inventory = new() { ["rareCandy"] = 3 },
        };
        var json = CompanionJson.EncodeState(state, SwiftDateEncoding.DeferredToDate);
        if (legacy)
            foreach (var row in json["dex"]!.AsArray()) row!.AsObject().Remove("namesVersion");
        var path = Path.Combine(Path.GetTempPath(), $"dex-language-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private static CompanionStore Store(IPokeProviding provider, string path) =>
        new(provider, filePath: path, rng: new SeededRng(7), settings: new AppSettings(null));

    [Fact]
    public void TestLegacyNamedEntriesRefreshAllLanguagesOnceAndPreserveProgress() => MainActor.Run(async () =>
    {
        var path = Fixture([Entry(), Entry("second-catch")]);
        var provider = new MigratingNameProvider(AllNames());
        var store = Store(provider, path);
        var old = store.State.Dex[0].Clone();
        Assert.Null(old.NamesVersion); // old JSON did not encode a name version
        Assert.Equal("Bulbasaur", store.DexStoredChainNames(old)?[1]);
        await store.BackfillMissingDexNames();
        Assert.Equal(2, store.State.Dex.Count);
        Assert.Equal(old.Id, store.State.Dex[0].Id);
        Assert.Equal(old.CaughtAt, store.State.Dex[0].CaughtAt);
        Assert.Equal(old.IsShiny, store.State.Dex[0].IsShiny);
        Assert.Equal(old.Nature, store.State.Dex[0].Nature);
        Assert.Equal(1234567, store.State.UsedSinceInstall);
        Assert.Equal(3, store.State.Inventory["rareCandy"]);
        Assert.All(store.State.Dex, e => Assert.False(e.NeedsNamesRefresh));
        Assert.Equal("이상해씨", store.DexStoredChainNames(store.State.Dex[0])?[1]);
        Assert.Equal("Ivysaur", PokemonNameLocalization.Resolve(store.State.Dex[0].Names![2], ["it"]));
        await store.DexResolveChainNames(old); // a mounted row can still hold its old value
        await store.BackfillMissingDexNames();
        Assert.Equal(1, provider.Calls); // duplicate catches and stale row values reuse the refreshed names

        var restored = Store(provider, path);
        restored.SetLanguage(AppLanguage.Pt); // no Portuguese in the API response: English is the complete fallback
        await restored.BackfillMissingDexNames();
        Assert.Equal("Bulbasaur", restored.DexStoredChainNames(restored.State.Dex[0])?[1]);
        Assert.Equal(1, provider.Calls); // an absent translation is not an expired cache
    });

    [Fact]
    public void TestOfflineLegacyCacheRemainsVisibleAndRetriesAfterRecovery() => MainActor.Run(async () =>
    {
        var path = Fixture([Entry()]);
        var provider = new MigratingNameProvider(AllNames());
        provider.Configure(AllNames(), offline: true);
        var store = Store(provider, path);
        var old = store.State.Dex[0];
        var fallback = await store.DexResolveChainNames(old);
        Assert.Equal(new Dictionary<int, string> { [1] = "Bulbasaur", [2] = "Ivysaur" }, fallback);
        Assert.Null(store.State.Dex[0].NamesVersion);
        provider.Configure(AllNames());
        await store.BackfillMissingDexNames();
        Assert.Equal("이상해풀", store.DexStoredChainNames(store.State.Dex[0])?[2]);
        Assert.False(store.State.Dex[0].NeedsNamesRefresh);
    });

    [Fact]
    public void TestSimultaneouslyMountedCatchRowsShareOneRequest() => MainActor.Run(async () =>
    {
        var path = Fixture([Entry(), Entry("second-catch")]);
        var provider = new MigratingNameProvider(AllNames());
        var store = Store(provider, path);
        var first = store.State.Dex[0];
        var second = store.State.Dex[1];
        var a = store.DexResolveChainNames(first);
        var b = store.DexResolveChainNames(second);
        var names = await Task.WhenAll(a, b);
        Assert.Equal(names[0], names[1]);
        Assert.Equal("이상해씨", names[0][1]);
        Assert.Equal(1, provider.Calls);
        Assert.All(store.State.Dex, e => Assert.False(e.NeedsNamesRefresh));
    });

    [Fact]
    public void TestPartialResponsePreservesOldNamesWithoutMarkingMigrationComplete() => MainActor.Run(async () =>
    {
        var path = Fixture([Entry()]);
        var provider = new MigratingNameProvider(new() { [1] = AllNames()[1] });
        var store = Store(provider, path);
        await store.BackfillMissingDexNames();
        Assert.Equal(new Dictionary<int, string> { [1] = "이상해씨", [2] = "Ivysaur" }, store.DexStoredChainNames(store.State.Dex[0]));
        Assert.True(store.State.Dex[0].NeedsNamesRefresh);
        provider.Configure(AllNames());
        await store.BackfillMissingDexNames();
        Assert.False(store.State.Dex[0].NeedsNamesRefresh);
        Assert.Equal("Ivysaur", store.State.Dex[0].Names?[2]["it"]);
    });

    [Fact]
    public void TestNewNamesSkipMigrationButEmptyAndMissingSpeciesRetry()
    {
        var fresh = Entry();
        Assert.False(fresh.NeedsNamesRefresh);
        var roundTrip = CompanionJson.DecodeDexEntry(
            System.Text.Json.JsonDocument.Parse(CompanionJson.EncodeDexEntry(fresh, SwiftDateEncoding.DeferredToDate).ToJsonString()).RootElement,
            SwiftDateEncoding.DeferredToDate);
        Assert.False(roundTrip.NeedsNamesRefresh);
        fresh.Names = [];
        Assert.True(fresh.NeedsNamesRefresh);
        fresh.Names = new() { [1] = new() { ["en"] = "Bulbasaur" } };
        Assert.True(fresh.NeedsNamesRefresh);
        fresh.NamesVersion = null;
        Assert.True(fresh.NeedsNamesRefresh);
    }
}
