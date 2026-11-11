using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

file sealed class FormTestException : Exception;

file static class FormLines
{
    private static EvoNode N(int id, params EvoNode[] children) => new(id, children);

    private static Dictionary<int, Dictionary<string, string>> Names(params (int Id, Dictionary<string, string> ByLang)[] items) =>
        items.ToDictionary(i => i.Id, i => i.ByLang);

    /// <summary>Burmy (#412) branches into Wormadam (#413, cloaks) and Mothim (#414, single look).</summary>
    public static readonly EvoLine Burmy = new(412, N(412, N(413), N(414)), Rarity.Common,
        Names((412, new() { ["en"] = "Burmy", ["fr"] = "Cheniti" }), (413, new() { ["en"] = "Wormadam" }),
            (414, new() { ["en"] = "Mothim" })));
    public static readonly EvoLine Shaymin = new(492, N(492), Rarity.Legendary,
        Names((492, new() { ["en"] = "Shaymin", ["fr"] = "Shaymin" })));
    public static readonly EvoLine Ditto = new(132, N(132), Rarity.Rare, Names((132, new() { ["en"] = "Ditto" })));
    public static readonly EvoLine Shellos = new(422, N(422, N(423)), Rarity.Common,
        Names((422, new() { ["en"] = "Shellos" }), (423, new() { ["en"] = "Gastrodon" })));
}

file sealed class FormTestProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => baseSpeciesID switch
    {
        412 => Task.FromResult(FormLines.Burmy),
        492 => Task.FromResult(FormLines.Shaymin),
        422 => Task.FromResult(FormLines.Shellos),
        PokemonOdds.DittoSpeciesID => Task.FromResult(FormLines.Ditto),
        _ => Task.FromException<EvoLine>(new FormTestException()),
    };

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new FormTestException());

    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromException<BaseSpecies?>(new FormTestException());
}

public class PokemonFormTests
{
    private static readonly DateTimeOffset Now = Lines.FixedNow;
    private static readonly PokemonForm Sky = new("sky"), Land = new("land");
    private static readonly PokemonForm Sandy = new("sandy"), Plant = new("plant");

    private static AppSettings Settings()
    {
        var settings = new AppSettings(null);
        settings.Set("growthDifficulty", 1.0);
        settings.Set("shopDifficulty", 1.0);
        return settings;
    }

    private static CompanionStore Store(CompanionState state, ulong seed = 7)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"forms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var url = Path.Combine(directory, "companion-state.json");
        File.WriteAllBytes(url, CompanionJson.Serialize(state));
        return new CompanionStore(new FormTestProvider(), clock: () => Now, filePath: url, rng: new SeededRng(seed),
            dittoDisguiseRollingEnabled: false, settings: Settings());
    }

    private static DexEntry Burmy(int[] path, PokemonForm? form, bool shiny = false) =>
        new(412, path[^1], path, Rarity.Common, Now, isShiny: shiny, nature: PokemonNature.Brave, form: form);

    // Catalog

    [Fact]
    public void TestCatalogListsDefaultFirstWithinTheHatchableRange()
    {
        Assert.Equal(23, PokemonForm.Catalog.Count);
        foreach (var (speciesID, entries) in PokemonForm.Catalog)
        {
            Assert.True(PokemonAssets.HasAnimatedSprite(speciesID));
            Assert.True(entries.Count >= 2, $"#{speciesID}");
            Assert.True(entries.Select(e => e.Form).Distinct().Count() == entries.Count, $"#{speciesID} repeats a form");
            Assert.True(entries[0].FormID == speciesID, $"#{speciesID} default is the species' own form");
            Assert.True(entries.Skip(1).All(e => e.FormID > 10000), $"#{speciesID}");
        }
        Assert.Equal(18, PokemonForm.Forms(493).Count);
        Assert.Equal([Land, Sky], PokemonForm.Forms(492));
        Assert.False(PokemonForm.HasForms(414), "Mothim's cloaks share one sprite");
        Assert.False(PokemonForm.HasForms(172), "Spiky-eared Pichu has no Gen V animation");
        Assert.False(PokemonForm.HasForms(592), "Gender differences are not forms");
    }

    [Fact]
    public void TestLinesCarryFormsFromTheirBaseEvenWhenTheBaseLooksTheSame()
    {
        Assert.Equal([412, 413], PokemonForm.FormSpecies(412));
        Assert.Equal([Plant, Sandy, new PokemonForm("trash")], PokemonForm.LineForms(412));
        Assert.Equal([421], PokemonForm.FormSpecies(420));
        Assert.Equal([new PokemonForm("overcast"), new PokemonForm("sunshine")], PokemonForm.LineForms(420));
        Assert.Equal([new PokemonForm("standard"), new PokemonForm("zen")], PokemonForm.LineForms(554));
        Assert.Empty(PokemonForm.LineForms(1));
        Assert.Equal(new PokemonForm("overcast"), PokemonForm.ResolvedForLine(420, null));
        Assert.Null(PokemonForm.ResolvedForSpecies(420, new PokemonForm("sunshine"))); // Cherubi has one look
    }

    [Fact]
    public void TestFormResolvesPerSpeciesOfTheLine()
    {
        Assert.Equal(Sandy, PokemonForm.ResolvedForSpecies(412, Sandy));
        Assert.Equal(Sandy, PokemonForm.ResolvedForSpecies(413, Sandy));
        Assert.Null(PokemonForm.ResolvedForSpecies(414, Sandy));
        Assert.Equal(Land, PokemonForm.ResolvedForSpecies(492, null));
        Assert.Equal(Land, PokemonForm.ResolvedForSpecies(492, new PokemonForm("future")));
        Assert.Null(PokemonForm.ResolvedForSpecies(25, Sky));
    }

    // Sprites: the URL/cache-key tests exercise SpriteStore (UI layer), so only the asset names are checked here.

    [Fact]
    public void TestAssetNamesBehindSpriteUrlsAndCacheKeys()
    {
        Assert.Equal("10006", PokemonForm.StaticAssetName(492, Sky));
        Assert.Equal("492-sky", PokemonForm.AssetName(492, Sky));
        Assert.Equal("412-sandy", PokemonForm.StaticAssetName(412, Sandy));
        Assert.Equal("492", PokemonForm.StaticAssetName(492, Land));
        Assert.Equal("414", PokemonForm.AssetName(414, Sandy));
        Assert.Equal("413-sandy", PokemonForm.AssetName(413, Sandy));
        Assert.Equal(PokemonForm.AssetName(492, null), PokemonForm.AssetName(492, Land));
    }

    // Saves

    [Fact]
    public void TestSavesWrittenBeforeGenericFormsKeepTheirUnownLetters()
    {
        var json = """
        {"pendingHatchID":201,"pendingUnownForm":"k","representativeSpeciesID":201,"representativeUnownForm":"q",
         "active":{"baseID":201,"pathIDs":[201],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1,
                   "unownForm":"z"},
         "dex":[{"baseID":201,"finalID":201,"chainOrder":[201],"rarity":"common","unownForm":"q"}]}
        """;
        var state = CompanionJson.Deserialize(json);
        Assert.Equal(new PokemonForm("k"), state.PendingForm);
        Assert.Equal(new PokemonForm("z"), state.Active?.Form);
        Assert.Equal(new PokemonForm("q"), state.Dex[0].Form);
        Assert.Equal(new PokemonForm("q"), state.RepresentativeForm);

        var reencoded = (JsonObject)JsonNode.Parse(CompanionJson.Serialize(state))!;
        Assert.Equal("z", reencoded["active"]?["form"]?.GetValue<string>());
        Assert.Equal("q", reencoded["representativeForm"]?.GetValue<string>());
    }

    [Fact]
    public void TestStoredFormIsNormalizedAgainstTheLine()
    {
        var json = """
        {"active":{"baseID":412,"pathIDs":[412,413],"stageIndex":1,"usedAtStage":0,"rarity":"common",
                   "totalForms":2,"form":"sky"},
         "dex":[{"baseID":1,"finalID":1,"chainOrder":[1],"rarity":"common","form":"sandy"}]}
        """;
        var state = CompanionJson.Deserialize(json);
        Assert.Equal(Plant, state.Active?.Form); // a form from another line falls back to the line default
        Assert.Null(state.Dex[0].Form);          // lines without forms store none
    }

    // Collection

    [Fact]
    public void TestEvolutionKeepsTheCloakAndMothimKeepsItsSingleLook()
    {
        var state = new CompanionState
        {
            Active = new MonState(412, [412, 413], stageIndex: 1, usedAtStage: 0, rarity: Rarity.Common, totalForms: 2,
                form: Sandy),
            Dex = [Burmy([412, 414], Sandy)],
        };
        Assert.True(state.OwnsSpecies(413, Sandy));
        Assert.False(state.OwnsSpecies(413, Plant));
        Assert.True(state.OwnsSpecies(414));
        Assert.Equal(new HashSet<PokemonForm> { Sandy }, state.CollectedForms(412));
        Assert.Equal(new HashSet<PokemonForm> { Sandy }, state.CollectedForms(413));
        Assert.Empty(state.CollectedForms(414));
    }

    [Fact]
    public void TestLineFormCountsOnlyOnceEverySpeciesShowingItIsOwned()
    {
        var state = new CompanionState { Dex = [Burmy([412, 414], Sandy)] };
        Assert.DoesNotContain(Sandy, state.CollectedLineForms(412)); // Sandy Wormadam is still missing
        state.Dex.Add(Burmy([412, 413], Sandy));
        Assert.Equal(new HashSet<PokemonForm> { Sandy }, state.CollectedLineForms(412));
        Assert.Empty(state.CollectedLineForms(201));
    }

    [Fact]
    public void TestDetailListsOwnedFormsAndMainDexKeepsOneCell() => MainActor.Run(() =>
    {
        var state = new CompanionState
        {
            Dex = [Burmy([412, 413], Sandy), Burmy([412, 413], Plant, shiny: true), Burmy([412, 414], new PokemonForm("trash"))],
        };
        var s = Store(state);
        Assert.Equal([412, 413, 414], s.DexSpeciesList.Select(x => x.Id));
        Assert.Equal([Plant, Sandy], s.FormSpeciesList(413).Select(x => x.Form!.Value));
        Assert.Equal([true, false], s.FormSpeciesList(413).Select(x => x.IsShiny));
        Assert.Equal([Plant, Sandy, new PokemonForm("trash")], s.FormSpeciesList(412).Select(x => x.Form!.Value));
        Assert.Empty(s.FormSpeciesList(414));

        Assert.True(s.SetRepresentativeSpeciesID(413, Sandy));
        Assert.Equal(Sandy, s.RepresentativeSubject.Form);
        Assert.False(s.SetRepresentativeSpeciesID(413, new PokemonForm("trash"))); // Trash Wormadam is not owned
        Assert.Equal(Sandy, s.RepresentativeDexSpecies?.Form);
    });

    // Hatch

    [Fact]
    public void TestHatchUsesThePreparedFormOfAnyLine() => MainActor.Run(async () =>
    {
        var state = new CompanionState { PendingHatchID = 492, PendingForm = Sky, EggUsage = PokemonBalance.EggHatchThreshold };
        var s = Store(state);
        await s.HatchIfNeeded();
        Assert.Equal(492, s.State.Active?.BaseID);
        Assert.Equal(Sky, s.State.Active?.Form);
        Assert.Equal(Sky, s.CurrentForm);
        Assert.Null(s.State.PendingForm);
    });

    [Fact]
    public void TestEvolutionNamesTheFormLikeHatchAndGraduation() => MainActor.Run(async () =>
    {
        var east = new PokemonForm("east");
        var state = new CompanionState
        {
            Language = AppLanguage.En,
            PendingHatchID = 422,
            PendingForm = east,
            EggUsage = PokemonBalance.EggHatchThreshold,
        };
        var s = Store(state);
        await s.HatchIfNeeded();
        s.ApplyUsage(PokemonBalance.PhaseThreshold(Rarity.Common, 2, 0));
        Assert.Equal(423, s.State.Active?.CurrentID);
        Assert.Equal(PokemonForm.DisplayName("Gastrodon", 423, east, AppLanguage.En), s.JustEvolvedTo);
        Assert.NotEqual("Gastrodon", s.JustEvolvedTo); // East Sea Gastrodon must not read as the default West Sea
    });

    [Fact]
    public void TestHatchWithoutPreparationRollsAmongTheLineForms() => MainActor.Run(async () =>
    {
        var seen = new HashSet<PokemonForm>();
        for (ulong seed = 1; seed <= 24; seed++)
        {
            var s = Store(new CompanionState(), seed);
            await s.Hatch(412);
            var form = s.State.Active!.Form!.Value;
            Assert.Contains(form, PokemonForm.LineForms(412));
            seen.Add(form);
        }
        Assert.Equal(PokemonForm.LineForms(412).ToHashSet(), seen);
    });

    [Fact]
    public void TestRevealedDittoDropsTheDisguiseForm() => MainActor.Run(async () =>
    {
        var json = """
        {"installBaselineSet":true,"usedSinceInstall":1000000000,"lastDate":"d1","dex":[],"collectedFinals":[],
         "active":{"baseID":412,"pathIDs":[412],"stageIndex":0,"usedAtStage":900000000,"rarity":"common",
                   "totalForms":2,"form":"sandy","dittoDisguise":412,"dittoRevealed":false}}
        """;
        var url = Path.Combine(Path.GetTempPath(), $"form-ditto-{Guid.NewGuid():N}.json");
        File.WriteAllText(url, json);
        var settings = new AppSettings(null);
        settings.Set("growthDifficulty", 1.0);
        var s = new CompanionStore(new FormTestProvider(), clock: () => Now, filePath: url, rng: new SeededRng(7),
            dittoDisguiseRollingEnabled: false, settings: settings);
        Assert.Equal(Sandy, s.CurrentForm);
        s.Update(new Dictionary<string, long> { ["test"] = 0 }, "d1", 0, BurnTier.Idle, false, true);
        await MainActor.WaitUntil(() => s.State.Active?.DittoRevealed ?? false);
        Assert.Equal(PokemonOdds.DittoSpeciesID, s.State.Active?.BaseID);
        Assert.Null(s.State.Active?.Form);
        Assert.Null(s.CurrentForm);
    });

    // Names

    /// <summary>The PokemonNameClient half of the Swift test (form_names decoding, request URL) belongs to the
    /// name-localization port; only the resources the companion derives are checked here.</summary>
    [Fact]
    public void TestFormNameResourcesPointAtPokemonForm()
    {
        var resource = PokemonForm.NameResource(492, Sky);
        Assert.Equal(new PokemonNameResource(PokemonNameResource.ResourceKind.PokemonForm, "10064"), resource);
        Assert.Null(PokemonForm.NameResource(201, new PokemonForm("b"))); // Unown shows its letter
        Assert.Null(PokemonForm.NameResource(25, null));
        Assert.Equal(["492", "10064"], PokemonForm.NameResources(492).Select(r => r.Name));
        Assert.Empty(PokemonForm.NameResources(201));
        // Rotom's usual look has no official name.
        Assert.Equal(["10058", "10059", "10060", "10061", "10062"], PokemonForm.NameResources(479).Select(r => r.Name));
    }

    /// <summary>The Swift test checks French/German strings; the L stub is English-only, so English is checked.</summary>
    [Fact]
    public void TestUnnamedUsualLooksUseLocalizedNormalForm()
    {
        var names = new Dictionary<PokemonNameResource, Dictionary<string, string>>();
        foreach (var id in new[] { 351, 479, 646, 649 })
            Assert.Equal(new L(AppLanguage.En).NormalForm, PokemonForm.Label(id, null, AppLanguage.En, names));
        Assert.Equal(new L(AppLanguage.En).NormalForm, PokemonForm.Label(479, new PokemonForm("normal"), AppLanguage.En, names));
        // Named forms keep the PokéAPI name.
        Assert.Equal("Heat", PokemonForm.Label(479, new PokemonForm("heat"), AppLanguage.Fr, names));
        Assert.Equal("Motisma", PokemonForm.DisplayName("Motisma", 479, null, AppLanguage.Fr));
    }

    [Fact]
    public void TestDisplayNameShowsOnlyNonDefaultFormsExceptUnown()
    {
        var names = new Dictionary<PokemonNameResource, Dictionary<string, string>>();
        var skyName = PokemonForm.NameResource(492, Sky)!.Value;
        Assert.Equal("Sky", PokemonForm.Label(492, Sky, AppLanguage.Fr, names)); // identifier until PokéAPI answers
        names[skyName] = new() { ["fr"] = "Forme Céleste", ["en"] = "Sky Forme" };
        Assert.Equal("Forme Céleste", PokemonForm.Label(492, Sky, AppLanguage.Fr, names));
        Assert.Equal("?", PokemonForm.Label(201, new PokemonForm("question"), AppLanguage.Fr, names));
        Assert.Null(PokemonForm.Label(25, Sky, AppLanguage.Fr, names));

        Assert.Equal("Shaymin [Forme Céleste]", PokemonForm.DisplayName("Shaymin", 492, Sky, "Forme Céleste"));
        Assert.Equal("Shaymin", PokemonForm.DisplayName("Shaymin", 492, Land, "Forme Terrestre"));
        Assert.Equal("Zarbi [A]", PokemonForm.DisplayName("Zarbi", 201, new PokemonForm("a"), (string?)null));
        Assert.Equal("Pikachu", PokemonForm.DisplayName("Pikachu", 25, Sky, "Sky"));
    }
}
