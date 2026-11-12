using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of DexCompletionStoreTests.swift (#442). Swift checks the recorded keys only; these tests also count the
// notifications, because a seeding fault that still records the keys shows up only as a notification burst.

file sealed class DexStoreTestException : Exception;

/// <summary>Returns lines from memory and fails the species index so no network is touched.</summary>
file sealed class OfflineProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(new EvoLine(baseSpeciesID, new EvoNode(baseSpeciesID),
        Rarity.Common, new Dictionary<int, Dictionary<string, string>> { [baseSpeciesID] = new() { ["en"] = "Mon" } }));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new DexStoreTestException());
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromException<BaseSpecies?>(new DexStoreTestException());
}

public class DexCompletionStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static CompanionStore Load(string path) =>
        new(new OfflineProvider(), clock: () => Now, filePath: path, rng: new SeededRng(7),
            dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));

    /// <summary>One DexEntry whose chainOrder spans a dex range registers every species in it.</summary>
    private static CompanionStore Store(DexNumberRange range, bool shiny = false) => Store(range, out _, shiny);

    private static CompanionStore Store(DexNumberRange range, out string path, bool shiny = false)
    {
        var state = new CompanionState { Language = AppLanguage.En };
        state.Dex = [new DexEntry(range.LowerBound, range.UpperBound, range.Numbers, Rarity.Common, Now, isShiny: shiny)];
        path = StateFiles.Write(state);
        return Load(path);
    }

    /// <summary>Titles of the dex completion notifications fired from now on.</summary>
    private static List<string> DexNotifications(CompanionStore store)
    {
        var l = new L(AppLanguage.En);
        var dexTitles = PokedexRegions.AllCases
            .SelectMany(r => new[] { l.NotifDexRegionCompleteTitle(l.RegionName(r)), l.NotifDexRegionShinyCompleteTitle(l.RegionName(r)) })
            .Concat([l.NotifDexUnownCompleteTitle, l.NotifDexNationalCompleteTitle, l.NotifDexNationalShinyCompleteTitle])
            .ToHashSet();
        var fired = new List<string>();
        store.CompanionNotification += (title, _) => { if (dexTitles.Contains(title)) fired.Add(title); };
        return fired;
    }

    [Fact]
    public void TestEmptyStoreHasNoCompletionsAndIsSeeded()
    {
        var path = StateFiles.Write(new CompanionState());
        var s = Load(path);
        Assert.All(s.DexCompletions, c => Assert.False(c.IsComplete));
        Assert.Empty(s.EarnedDexBadges);
        Assert.True(s.State.DexCompletionsSeeded, "fresh load seeds the completion tracker");
        Assert.Empty(s.State.NotifiedDexCompletions);
    }

    [Fact]
    public void TestCompleteRegionIsReportedComplete()
    {
        var s = Store(PokedexRegion.Kanto.DexRange());
        var kanto = s.DexCompletions.Single(c => c.Region == PokedexRegion.Kanto);
        Assert.Equal(151, kanto.Owned);
        Assert.True(kanto.IsComplete);
        Assert.Contains(DexBadge.ForRegion(PokedexRegion.Kanto), s.EarnedDexBadges);
    }

    /// <summary>A save that was already complete before the feature shipped must not fire a notification burst:
    /// seeding records the earned badges as already notified on first load.</summary>
    [Fact]
    public void TestExistingCompletionsAreSeededAsNotifiedWithoutFiring()
    {
        var s = Store(PokedexRegion.Kanto.DexRange(), out var path);
        var fired = DexNotifications(s);
        Assert.True(s.State.DexCompletionsSeeded);
        Assert.Contains(DexBadge.ForRegion(PokedexRegion.Kanto).NotificationKey, s.State.NotifiedDexCompletions);
        // Nothing is "newly earned" after seeding, so a re-check adds no keys and fires nothing.
        var before = s.State.NotifiedDexCompletions.ToHashSet();
        s.CheckDexCompletions();
        Assert.Equal(before, s.State.NotifiedDexCompletions);
        Assert.Empty(fired);
        // The seed is written right away, so a restart does not seed (or announce) again.
        Assert.True(CompanionJson.Deserialize(File.ReadAllBytes(path)).DexCompletionsSeeded);
    }

    [Fact]
    public void TestRegionFilterReturnsOnlySpeciesInThatGeneration()
    {
        var s = Store(new DexNumberRange(1, 200)); // all of Kanto + Johto 152–200
        var johto = s.FilteredDexSpecies(region: PokedexRegion.Johto);
        Assert.Equal(49, johto.Count);
        Assert.All(johto, sp => Assert.Equal(PokedexRegion.Johto, PokedexRegions.Region(sp.Id)));
        Assert.Equal(151, s.FilteredDexSpecies(region: PokedexRegion.Kanto).Count);
        Assert.Equal(200, s.FilteredDexSpecies(region: null).Count);
        // The region filter combines with the others instead of replacing them.
        Assert.Equal([152], s.FilteredDexSpecies(query: "152", region: PokedexRegion.Johto).Select(sp => sp.Id));
        Assert.Empty(s.FilteredDexSpecies(query: "152", region: PokedexRegion.Kanto));
    }

    [Fact]
    public void TestNationalProgressCountsAcrossAllRegions()
    {
        var s = Store(PokedexRegions.NationalRange);
        Assert.Equal(PokedexRegions.NationalTotal, s.NationalDexProgress.Owned);
        Assert.Contains(DexBadge.National, s.EarnedDexBadges);
        Assert.All(PokedexRegions.AllCases, r => Assert.Contains(DexBadge.ForRegion(r), s.EarnedDexBadges));
    }

    [Fact]
    public void TestCurrentFormsCountUnownLettersWithoutInflatingRegionalSpecies()
    {
        var state = new CompanionState();
        var letters = PokemonForm.Forms(PokemonForm.UnownSpeciesID);
        state.Dex = letters.SkipLast(1)
            .Select(form => new DexEntry(201, 201, [201], Rarity.Rare, Now, form: form)).ToList();
        state.Dex.AddRange(PokemonForm.Forms(351).Select(form => new DexEntry(351, 351, [351], Rarity.Common, Now, form: form)));
        var path = StateFiles.Write(state);
        var partial = Load(path);
        Assert.DoesNotContain(DexBadge.Unown, partial.EarnedDexBadges);

        // The last letter belongs to the active individual, which is owned before graduation.
        state.Active = new MonState(201, [201], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Rare, totalForms: 1, form: letters[^1]);
        StateFiles.Write(state, path);
        var complete = Load(path);
        Assert.Contains(DexBadge.Unown, complete.EarnedDexBadges);
        Assert.Equal(1, complete.DexCompletions.Single(c => c.Region == PokedexRegion.Johto).Owned);
        Assert.Equal(1, complete.DexCompletions.Single(c => c.Region == PokedexRegion.Hoenn).Owned);
        Assert.Equal(2, complete.NationalDexProgress.Owned);
        Assert.Equal(28, complete.FormSpeciesList(201).Count(sp => sp.HasNormal || sp.IsShiny));
        Assert.Contains("unown", complete.State.NotifiedDexCompletions);
    }

    /// <summary>The completion that a catch causes is announced once, through the save it is persisted with — and only
    /// while the notifications preference is on (it is recorded either way, so turning it on later stays quiet).</summary>
    [Fact]
    public void TestCompletingARegionLiveNotifiesOnceAndRespectsThePreference() => MainActor.Run(async () =>
    {
        foreach (var notificationsOn in new[] { true, false })
        {
            var settings = new AppSettings(null);
            settings.Set(CompanionStore.CompanionNotificationsKey, notificationsOn);
            var state = new CompanionState { Language = AppLanguage.En, EggUsage = PokemonBalance.EggHatchThreshold, PendingHatchID = 251 };
            state.Dex = [new DexEntry(152, 250, new DexNumberRange(152, 250).Numbers, Rarity.Common, Now)];
            var store = new CompanionStore(new OfflineProvider(), clock: () => Now, filePath: StateFiles.Write(state),
                rng: new SeededRng(7), dittoDisguiseRollingEnabled: false, settings: settings);
            var fired = DexNotifications(store);

            await store.HatchIfNeeded();
            Assert.Equal(251, store.State.Active?.BaseID);
            Assert.Contains("region.johto", store.State.NotifiedDexCompletions);
            Assert.Equal(notificationsOn ? ["🏅 Johto Dex complete!"] : [], fired);
            store.CheckDexCompletions();
            Assert.Equal(notificationsOn ? 1 : 0, fired.Count);
        }
    });

    [Fact]
    public void TestLegacyImportSeedsExistingCompletionsBeforeANewHatch() =>
        MainActor.Run(() => AssertLegacyImportTracksTheNextCompletion(existingKanto: true));

    [Fact]
    public void TestLegacyImportWithoutEarnedBadgesStillEnablesFutureCompletions() =>
        MainActor.Run(() => AssertLegacyImportTracksTheNextCompletion(existingKanto: false));

    private static async Task AssertLegacyImportTracksTheNextCompletion(bool existingKanto)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ptb-dex-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "companion-state.json");
            var lower = existingKanto ? 1 : 152;
            var legacy = new CompanionState
            {
                Language = AppLanguage.En,
                EggUsage = PokemonBalance.EggHatchThreshold,
                PendingHatchID = 251, // the final missing Johto species, ready to hatch after import
            };
            legacy.Dex = [new DexEntry(lower, 250, new DexNumberRange(lower, 250).Numbers, Rarity.Common, Now)];
            var json = JsonNode.Parse(SaveTransfer.Encode(legacy, "2.5.0", "Old Mac", Now))!.AsObject();
            var oldState = json["state"]!.AsObject();
            oldState.Remove("notifiedDexCompletions");
            oldState.Remove("dexCompletionsSeeded");
            var envelope = SaveTransfer.Decode(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
            Assert.False(envelope.State.DexCompletionsSeeded); // the exported save predates completion tracking

            var store = Load(path);
            store.SetLanguage(AppLanguage.En); // language is this device's preference: the import keeps it
            var fired = DexNotifications(store);
            store.ApplySave(envelope, new Dictionary<string, long> { ["test"] = 0 }, "2026-11-05", hasUsageData: true);
            HashSet<string> preexisting = existingKanto ? ["region.kanto"] : [];
            Assert.True(store.State.DexCompletionsSeeded);
            Assert.Equal(preexisting, store.State.NotifiedDexCompletions); // imported completions are seeded, not announced
            Assert.Empty(fired);
            var persisted = CompanionJson.Deserialize(File.ReadAllBytes(path));
            Assert.True(persisted.DexCompletionsSeeded, "even an empty seed must persist during import");

            await store.HatchIfNeeded();
            Assert.Equal(251, store.State.Active?.BaseID);
            Assert.Contains(DexBadge.ForRegion(PokedexRegion.Johto), store.EarnedDexBadges);
            // A newly completed region is announced in the same session without restarting.
            Assert.Equal(preexisting.Union(["region.johto"]).ToHashSet(), store.State.NotifiedDexCompletions);
            Assert.Equal(["🏅 Johto Dex complete!"], fired);
            var after = store.State.NotifiedDexCompletions.ToHashSet();
            store.CheckDexCompletions();
            Assert.Equal(after, store.State.NotifiedDexCompletions); // completion is announced once
            Assert.Single(fired);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
