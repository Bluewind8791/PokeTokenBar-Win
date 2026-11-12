using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// The save fields macOS added after the port (#466 egg prayers, #442 dex completions), ported ahead of their
/// features so a macOS save that passes through Windows keeps them. Port of the persistence half of
/// EggPrayerTests.swift; praying itself arrives with the feature, so these tests set the prayers directly.
/// </summary>
public class SaveFieldsTests
{
    private static CompanionStore Store() => StateFiles.Store(Lines.Linear3);

    private static void Graduate(CompanionStore s)
    {
        for (var stage = 0; stage < 3; stage++) s.ApplyUsage(PokemonBalance.PhaseThreshold(Rarity.Common, 3, stage));
    }

    [Fact]
    public void HatchCopiesPrayersAndClearsEggScope() => MainActor.Run(async () =>
    {
        var s = Store();
        s.State.EggPrayers = new PrayerRecord(2);
        await s.Hatch(1);
        Assert.Equal(new PrayerRecord(2), s.State.Active?.Prayers);
        Assert.Null(s.State.EggPrayers);
    });

    [Fact]
    public void GraduationKeepsPrayersOnTheDexEntryAndStartsAPlainEgg() => MainActor.Run(async () =>
    {
        var s = Store();
        s.State.EggPrayers = new PrayerRecord(1);
        await s.Hatch(1);
        // Only a damaged save carries egg prayers next to an active Pokémon; the new egg must not inherit them.
        s.State.EggPrayers = new PrayerRecord(9);
        Graduate(s);
        Assert.Null(s.State.Active);
        Assert.Equal(1, s.State.Dex[0].Prayers?.Count);
        Assert.Null(s.State.EggPrayers);
    });

    [Fact]
    public void ReleaseKeepsThePrayersOfTheIndividualAndStartsAPlainEgg() => MainActor.Run(() =>
    {
        var active = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3,
            prayers: new PrayerRecord(4));
        var s = new CompanionStore(new StubProvider(Lines.Linear3), clock: () => Lines.FixedNow,
            filePath: StateFiles.WriteText($$"""{"active":{{StateFiles.Json(active)}},"usedSinceInstall":1000000000}"""),
            rng: new SeededRng(7), dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));

        Assert.Equal(4, s.DexEntries.First(s.IsActiveDexEntry).Prayers?.Count);   // the detail view reads the synthesized entry
        // Loading already drops egg prayers next to an active Pokémon; set them after the load so the purchase is tested.
        s.State.EggPrayers = new PrayerRecord(3);
        Assert.True(s.BuyFreshEgg());
        Assert.Equal(4, s.State.Dex[^1].Prayers?.Count);
        Assert.Null(s.State.EggPrayers);
    });

    [Fact]
    public void ImportedSaveWithAnActivePokemonDropsEggPrayers()
    {
        var state = new CompanionState
        {
            Active = new MonState(4, [4], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1),
            EggPrayers = new PrayerRecord(9),
        };
        Assert.Null(SaveTransfer.Sanitized(state).EggPrayers);
    }

    [Fact]
    public void SanitizeClampsAnAbsurdPrayerCount()
    {
        var state = new CompanionState { EggPrayers = new PrayerRecord(long.MaxValue) };
        Assert.Equal(SaveTransfer.MaxTokenValue, SaveTransfer.Sanitized(state).EggPrayers?.Count);
        Assert.Equal(0, SaveTransfer.Sanitized(new CompanionState { EggPrayers = new PrayerRecord(-5) }).EggPrayers?.Count);
    }

    [Fact]
    public void NewFieldsSurviveTheStateFileAndSaveTransfer()
    {
        var state = new CompanionState
        {
            EggPrayers = new PrayerRecord(5),
            NotifiedDexCompletions = ["kanto", "hoenn"],
            DexCompletionsSeeded = true,
            Dex = [new DexEntry(1, 1, [1], Rarity.Common, null, prayers: new PrayerRecord(2))],
        };

        var file = CompanionJson.Deserialize(CompanionJson.Serialize(state));
        var export = SaveTransfer.Decode(SaveTransfer.Encode(state, "x", "d", Lines.FixedNow)).State;
        foreach (var back in new[] { file, export })
        {
            Assert.Equal(state.EggPrayers, back.EggPrayers);
            Assert.Equal(state.NotifiedDexCompletions, back.NotifiedDexCompletions);
            Assert.True(back.DexCompletionsSeeded);
            Assert.Equal(new PrayerRecord(2), back.Dex[0].Prayers);
        }

        var mon = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1,
            prayers: new PrayerRecord(2));
        Assert.Equal(mon.Prayers, CompanionJson.Deserialize($$"""{"active":{{StateFiles.Json(mon)}}}""").Active?.Prayers);
        Assert.Equal(mon.Prayers, mon.Clone().Prayers);
        Assert.Equal(state.EggPrayers, state.Clone().EggPrayers);
        Assert.Equal(state.NotifiedDexCompletions, state.Clone().NotifiedDexCompletions);
    }

    /// <summary>Dev builds once stored extra prayer fields; they are gone, but those saves must still load.</summary>
    [Fact]
    public void SavesWithRemovedPrayerKeysStillDecode()
    {
        const string record = """{"count":7,"wasFutile":true,"rolledSpeciesID":1,"eggWasPurchased":true}""";
        var state = CompanionJson.Deserialize($$$"""
            {"eggUsage":5,"eggWasPurchased":true,"eggPrayers":{{{record}}},
             "active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1,"prayers":{{{record}}}}}
            """);
        Assert.Equal(new PrayerRecord(7), state.EggPrayers);
        Assert.Equal(new PrayerRecord(7), state.Active?.Prayers);
    }

    [Fact]
    public void OldSavesDecodeWithoutTheNewFields()
    {
        var state = CompanionJson.Deserialize("""
            {"eggUsage":5,
             "active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1},
             "dex":[{"baseID":1,"finalID":1,"chainOrder":[1],"rarity":"common"}]}
            """);
        Assert.Null(state.EggPrayers);
        Assert.Null(state.Active?.Prayers);
        Assert.Null(state.Dex[0].Prayers);
        Assert.Empty(state.NotifiedDexCompletions);
        Assert.False(state.DexCompletionsSeeded);

        // A damaged field must not take the whole save (or the Pokémon) down.
        var broken = CompanionJson.Deserialize("""
            {"eggPrayers":"oops","notifiedDexCompletions":7,"dexCompletionsSeeded":"yes",
             "active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1,"prayers":[1]}}
            """);
        Assert.Null(broken.EggPrayers);
        Assert.Empty(broken.NotifiedDexCompletions);
        Assert.False(broken.DexCompletionsSeeded);
        Assert.NotNull(broken.Active);
        Assert.Null(broken.Active!.Prayers);
    }
}
