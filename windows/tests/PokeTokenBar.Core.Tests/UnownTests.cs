using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Unown letter helpers (Swift PokemonFormTestSupport.swift).</summary>
file static class U
{
    public static List<PokemonForm> Letters => PokemonForm.Forms(PokemonForm.UnownSpeciesID);
    public static PokemonForm F(string raw) => new(raw);
    public static readonly PokemonForm A = F("a"), B = F("b"), C = F("c"), K = F("k"), Q = F("q"), Z = F("z");
    public static readonly PokemonForm Exclamation = F("exclamation"), Question = F("question");
}

file sealed class UnownTestException : Exception;

/// <summary>Hatch lines come from memory; the new-egg prefetch fails so no network is used.</summary>
file class UnownTestProvider : IPokeProviding
{
    public virtual Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(new EvoLine(baseSpeciesID,
        new EvoNode(baseSpeciesID), Rarity.Common,
        new Dictionary<int, Dictionary<string, string>>
        {
            [baseSpeciesID] = new() { ["ko"] = "안농", ["en"] = "Unown", ["ja"] = "アンノーン" },
        }));

    public virtual Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new UnownTestException());

    public virtual Task<BaseSpecies?> BaseSpecies(int id) => Task.FromException<BaseSpecies?>(new UnownTestException());
}

file sealed class UnownSelectionProvider : UnownTestProvider
{
    public override Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(25, 255), new BaseSpecies(201, 225)]);
}

file sealed class UnownPrefetchProvider : UnownTestProvider
{
    public TaskCompletionSource LineRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task<EvoLine> Line(int baseSpeciesID)
    {
        LineRequested.TrySetResult();
        return base.Line(baseSpeciesID);
    }

    public override Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(201, 255)]);

    public override Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(new BaseSpecies(201, 255));
}

file sealed class UnownRetryProvider : UnownTestProvider
{
    private int _lineAttempts;

    public override Task<EvoLine> Line(int baseSpeciesID)
    {
        _lineAttempts += 1;
        if (_lineAttempts == 1) return Task.FromException<EvoLine>(new UnownTestException());
        return base.Line(baseSpeciesID);
    }
}

public class UnownTests
{
    private static readonly DateTimeOffset Now = Lines.FixedNow;

    private static MonState Mon(PokemonForm? form, bool shiny = false, int speciesID = 201) =>
        new(speciesID, [speciesID], stageIndex: 0, usedAtStage: 123, rarity: Rarity.Common, totalForms: 1,
            isShiny: shiny, nature: PokemonNature.Brave, form: form);

    private static DexEntry Entry(PokemonForm? form, bool shiny = false, int speciesID = 201) =>
        new(speciesID, speciesID, [speciesID], Rarity.Common, Now, isShiny: shiny, nature: PokemonNature.Brave,
            names: new Dictionary<int, Dictionary<string, string>> { [speciesID] = new() { ["ko"] = "안농", ["en"] = "Unown" } },
            form: form);

    private static string StateUrl()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"unown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "companion-state.json");
    }

    private static CompanionStore Store(CompanionState? state = null, ulong seed = 7, string? url = null,
        IPokeProviding? provider = null)
    {
        var destination = url ?? StateUrl();
        File.WriteAllBytes(destination, CompanionJson.Serialize(state ?? new CompanionState()));
        return Reload(destination, seed, provider);
    }

    private static CompanionStore Reload(string url, ulong seed = 7, IPokeProviding? provider = null)
    {
        var settings = new AppSettings(null);
        settings.Set("growthDifficulty", 1.0);
        settings.Set("shopDifficulty", 1.0);
        return new CompanionStore(provider ?? new UnownTestProvider(), clock: () => Now, filePath: url,
            rng: new SeededRng(seed), dittoDisguiseRollingEnabled: false, settings: settings);
    }

    private static readonly Dictionary<string, long> NoProviders = new();

    [Fact]
    public void TestAllLettersAndPunctuationAreSelectable()
    {
        var symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()).Concat(["!", "?"]).ToList();
        Assert.Equal(symbols, U.Letters.Select(f => f.Symbol));
        Assert.Equal(symbols.SelectMany(s => new[] { s, s }),
            Enumerable.Range(0, 56).Select(i => PokemonForm.Roll((ulong)i, U.Letters, new HashSet<PokemonForm>()).Symbol));
        Assert.Equal(U.A, PokemonForm.Roll(56, U.Letters, new HashSet<PokemonForm>()));
        Assert.Contains(PokemonForm.Roll(ulong.MaxValue, U.Letters, new HashSet<PokemonForm>()), U.Letters);
    }

    [Fact]
    public void TestFormWeightsAreTwoForMissingAndOneForCollectedWithoutGuaranteeingNewForms()
    {
        foreach (var collected in new[]
                 {
                     new HashSet<PokemonForm>(), new HashSet<PokemonForm> { U.A, U.B, U.Exclamation },
                     U.Letters.ToHashSet(),
                 })
        {
            var total = 56 - collected.Count;
            var samples = Enumerable.Range(0, total).Select(i => PokemonForm.Roll((ulong)i, U.Letters, collected)).ToList();
            foreach (var form in U.Letters)
                Assert.Equal(collected.Contains(form) ? 1 : 2, samples.Count(s => s == form));
            Assert.Equal(U.A, PokemonForm.Roll((ulong)total, U.Letters, collected));
        }
        // Completion returns selection to uniform weights.
        Assert.Equal(U.Letters,
            Enumerable.Range(0, 28).Select(i => PokemonForm.Roll((ulong)i, U.Letters, U.Letters.ToHashSet())));
    }

    [Fact]
    public void TestSharedCollectedAdjustmentPreservesCaptureRateRoundingAndMinimum()
    {
        for (var weight = 0; weight <= 255; weight++)
        {
            Assert.Equal(Math.Max(1, weight), CollectionWeight.Adjusted(weight, false));
            Assert.Equal(Math.Max(1, weight / 2), CollectionWeight.Adjusted(weight, true));
        }
    }

    [Fact]
    public void TestCollectionIncludesLegacyActiveAndReleasedFormsButCountsShinyOnlyOnce() => MainActor.Run(() =>
    {
        var original = new CompanionState();
        var legacy = Entry(null);
        legacy.Form = null;
        var released = Entry(U.Exclamation, shiny: true);
        released.ReleasedAt = Now;
        original.Dex = [legacy, Entry(U.B), Entry(U.B, shiny: true), released, Entry(null, speciesID: 25)];
        original.Active = Mon(U.Question, shiny: true);
        Assert.Equal(new HashSet<PokemonForm> { U.A, U.B, U.Exclamation, U.Question },
            original.CollectedForms(PokemonForm.UnownSpeciesID));
        var s = Store(original);
        Assert.Equal([25, 201], s.DexSpeciesList.Select(x => x.Id));
        var forms = s.FormSpeciesList(PokemonForm.UnownSpeciesID);
        Assert.Equal([U.A, U.B, U.Exclamation, U.Question], forms.Select(x => x.Form!.Value));
        Assert.Equal([false, true, true, true], forms.Select(x => x.IsShiny));
        Assert.Equal([false, false, false, true], forms.Select(x => x.IsRaising));
    });

    [Fact]
    public void TestMissingADoesNotCreateAnOwnedFormOrPermitAnUncollectedRepresentative() => MainActor.Run(() =>
    {
        var original = new CompanionState { Dex = [Entry(U.Question, shiny: true)] };
        var s = Store(original);
        Assert.Equal([201], s.DexSpeciesList.Select(x => x.Id));
        Assert.Equal([U.Question], s.FormSpeciesList(PokemonForm.UnownSpeciesID).Select(x => x.Form!.Value));
        Assert.False(s.SetRepresentativeSpeciesID(201, U.A));
        Assert.True(s.SetRepresentativeSpeciesID(201, U.Question));
        Assert.True(s.IsRepresentative(s.DexSpeciesList.First()));
    });

    [Fact]
    public void TestHatchUsesOwnedFormWeightsAfterUnchangedShinyNatureAndProfileRolls() => MainActor.Run(async () =>
    {
        var original = new CompanionState { Dex = U.Letters.SkipLast(1).Select(f => Entry(f)).ToList() };
        var seed = Enumerable.Range(0, 1_000).Select(i => (ulong)i).First(sd =>
        {
            var rng = new SeededRng(sd);
            rng.Next(); rng.Next(); rng.Next();
            var roll = rng.Next();
            return roll % 29 == 27 && roll % 56 / 2 != 27;
        });
        var s = Store(original, seed);
        await s.Hatch(201);
        // The missing form occupies both final slots of the weighted pool.
        Assert.Equal(U.Question, s.CurrentForm);
    });

    [Fact]
    public void TestPrefetchUsesOwnedFormWeights() => MainActor.Run(async () =>
    {
        var original = new CompanionState
        {
            InstallBaselineSet = true,
            LastDate = "d1",
            Dex = U.Letters.SkipLast(1).Select(f => Entry(f)).ToList(),
        };
        var seed = Enumerable.Range(0, 1_000).Select(i => (ulong)i).First(sd =>
        {
            var rng = new SeededRng(sd);
            rng.Next(); // Species selection precedes form selection.
            var roll = rng.Next();
            return roll % 29 == 27 && roll % 56 / 2 != 27;
        });
        var provider = new UnownPrefetchProvider();
        var s = Store(original, seed, provider: provider);
        s.Update(new Dictionary<string, long> { ["test"] = 1_000 }, "d1", 0, BurnTier.Idle, false, true);
        await provider.LineRequested.Task;
        Assert.Equal(201, s.State.PendingHatchID);
        Assert.Equal(U.Question, s.State.PendingForm);
    });

    [Fact]
    public void TestFormOwnershipDoesNotChangeSpeciesSelectionOrShinyRolls() => MainActor.Run(async () =>
    {
        var selectedSpecies = new HashSet<int>();
        foreach (var seed in new ulong[] { 1, 7, 17, 42, 128, 999 })
        {
            foreach (var forms in new[] { new List<PokemonForm>(), [U.B], U.Letters })
            {
                var original = new CompanionState
                {
                    EggUsage = PokemonBalance.EggHatchThreshold,
                    CollectedFinals = ["201:201"],
                    Dex = forms.Select(f => Entry(f)).ToList(),
                };
                var s = Store(original, seed, provider: new UnownSelectionProvider());
                var rng = new SeededRng(seed);
                // Existing species weights: Pikachu 255, collected Unown floor(225 / 2) = 112.
                var expectedSpecies = rng.Next() % 367 < 255 ? 25 : 201;
                var expectedShiny = rng.Next() % PokemonOdds.ShinyDenominator == 0;
                var expectedNature = PokemonNatureExtensions.AllCases[(int)(rng.Next() % (ulong)PokemonNatureExtensions.AllCases.Length)];
                var expectedProfileSeed = rng.Next();
                await s.HatchIfNeeded();
                Assert.Equal(expectedSpecies, s.CurrentSpeciesID);
                Assert.Equal(expectedShiny, s.CurrentIsShiny);
                Assert.Equal(expectedNature, s.CurrentNature);
                Assert.Equal(expectedProfileSeed, s.State.Active?.Profile?.Seed);
                selectedSpecies.Add(s.CurrentSpeciesID!.Value);
            }
        }
        Assert.Equal(new HashSet<int> { 25, 201 }, selectedSpecies);
    });

    /// <summary>One missing or invalid form field must not lose the existing Pokémon, Pokédex or currency.</summary>
    [Fact]
    public void TestLegacyAndInvalidFormFieldsDefaultToAWithoutLosingProgress()
    {
        // null entry = key removed (Swift `dict[key] = nil`)
        var invalidFields = new Func<JsonNode?>?[] { null, () => null, () => JsonValue.Create("future-form"), () => JsonValue.Create(42) };
        foreach (var invalid in invalidFields)
        {
            var original = new CompanionState
            {
                Active = Mon(U.Z, shiny: true),
                Dex = [Entry(U.Question, shiny: true)],
                RepresentativeSpeciesID = 201,
                RepresentativeForm = U.Question,
                UsedSinceInstall = 123_456_789,
                Inventory = new() { ["rareCandy"] = 3 },
            };
            var json = (JsonObject)JsonNode.Parse(CompanionJson.Serialize(original))!;
            var active = (JsonObject)json["active"]!;
            var dex0 = (JsonObject)((JsonArray)json["dex"]!)[0]!;
            foreach (var (obj, key) in new[] { (active, "form"), (dex0, "form"), (json, "representativeForm") })
            {
                if (invalid == null) obj.Remove(key);
                else obj[key] = invalid();
            }

            var restored = CompanionJson.Deserialize(json.ToJsonString());
            Assert.Equal(U.A, restored.Active?.Form);
            Assert.Equal(123, restored.Active?.UsedAtStage);
            Assert.True(restored.Active?.IsShiny);
            Assert.Single(restored.Dex);
            Assert.Equal(original.Dex[0].Id, restored.Dex[0].Id);
            Assert.Equal(U.A, restored.Dex[0].Form);
            Assert.True(restored.Dex[0].IsShiny);
            Assert.Equal(U.A, restored.RepresentativeForm);
            Assert.Equal(original.UsedSinceInstall, restored.UsedSinceInstall);
            Assert.Equal(original.Inventory, restored.Inventory);
        }
    }

    [Fact]
    public void TestAppearanceOwnershipIsIndependentForEachUnownForm() => MainActor.Run(() =>
    {
        var state = new CompanionState { Dex = [Entry(U.A), Entry(U.A, shiny: true), Entry(U.B, shiny: true), Entry(U.C)] };
        var s = Store(state);
        var forms = s.FormSpeciesList(PokemonForm.UnownSpeciesID);
        Assert.Equal([U.A, U.B, U.C], forms.Select(x => x.Form!.Value));
        Assert.Equal([true, false, true], forms.Select(x => x.HasNormal));
        Assert.Equal([true, true, false], forms.Select(x => x.IsShiny));
        Assert.True(s.DexSpeciesList.First().HasNormal);
        Assert.True(s.DexSpeciesList.First().IsShiny);
    });

    [Fact]
    public void TestEveryFormRoundTripsAndSharesOneSpeciesCell() => MainActor.Run(() =>
    {
        foreach (var form in U.Letters)
        {
            var restored = CompanionJson.DecodeMon(
                System.Text.Json.JsonDocument.Parse(StateFiles.Json(Mon(form, shiny: true))).RootElement);
            Assert.Equal(form, restored.Form);
            Assert.True(restored.IsShiny);
        }

        var original = new CompanionState
        {
            Language = AppLanguage.Ko,
            Dex = U.Letters.Select(f => Entry(f, shiny: true)).ToList(),
        };
        var s = Store(original);
        Assert.Equal(U.Letters, s.State.Dex.Select(e => e.Form!.Value));
        Assert.Single(s.DexSpeciesList);
        Assert.Equal("안농", s.DexSpeciesList[0].Name);
        Assert.Equal("201", s.DexSpeciesList[0].CollectionID);
        Assert.Null(s.DexSpeciesList[0].Form);
        Assert.Equal(28, s.State.CollectedForms(PokemonForm.UnownSpeciesID).Count);
        var formSpecies = s.FormSpeciesList(PokemonForm.UnownSpeciesID);
        Assert.Equal(U.Letters, formSpecies.Select(x => x.Form!.Value));
        Assert.Equal(28, formSpecies.Select(x => x.CollectionID).Distinct().Count());
        Assert.All(s.DexSpeciesList, x => Assert.True(x.Id == 201 && x.IsShiny && !x.IsRaising));
        Assert.Equal(U.Letters.Select(f => $"안농 [{f.Symbol}]"), formSpecies.Select(x => x.Name));
    });

    [Fact]
    public void TestFormIsOnlyAppliedToUnown()
    {
        Assert.Equal(U.A, Mon(null).Form);
        Assert.Equal(U.A, Entry(null).Form);
        var otherMon = Mon(U.Question, speciesID: 25);
        var otherEntry = Entry(U.Question, speciesID: 25);
        Assert.Null(otherMon.Form);
        Assert.Null(otherEntry.Form);
        Assert.Null(CompanionJson.DecodeMon(System.Text.Json.JsonDocument.Parse(StateFiles.Json(otherMon)).RootElement).Form);
        Assert.Null(CompanionJson.DecodeDexEntry(System.Text.Json.JsonDocument.Parse(
                CompanionJson.ToJsonString(CompanionJson.EncodeDexEntry(otherEntry, SwiftDateEncoding.DeferredToDate))).RootElement,
            SwiftDateEncoding.DeferredToDate).Form);
        Assert.Equal("Pikachu", PokemonForm.DisplayName("Pikachu", 25, U.Question, (string?)null));
    }

    [Fact]
    public void TestHatchGraduationAndReloadPreserveLetterAndShiny() => MainActor.Run(async () =>
    {
        foreach (var shiny in new[] { false, true })
        {
            var seed = Enumerable.Range(0, 20_000).Select(i => (ulong)i).First(sd =>
            {
                var r = new SeededRng(sd);
                var matchesShiny = (r.Next() % PokemonOdds.ShinyDenominator == 0) == shiny;
                r.Next(); // nature
                r.Next(); // profile seed
                return matchesShiny && r.Next() % 56 / 2 != 0;
            });
            var rng = new SeededRng(seed);
            rng.Next();
            var nature = PokemonNatureExtensions.AllCases[(int)(rng.Next() % (ulong)PokemonNatureExtensions.AllCases.Length)];
            var profileSeed = rng.Next();
            var form = U.Letters[(int)(rng.Next() % 56 / 2)];
            var url = StateUrl();
            var s = Store(seed: seed, url: url);

            await s.Hatch(201);
            Assert.Equal(form, s.CurrentForm);
            Assert.Equal(shiny, s.CurrentIsShiny);
            Assert.Equal(nature, s.CurrentNature);
            var profile = s.State.Active!.Profile!.Clone();
            Assert.Equal(profileSeed, profile.Seed);
            Assert.True(s.SetRepresentativeSpeciesID(201, form));
            Assert.Equal(form, Reload(url).State.Active?.Form);

            s.ApplyUsage(PokemonBalance.GraduationTotal(Rarity.Common));
            Assert.Null(s.State.Active);
            Assert.Single(s.State.Dex);
            var graduated = s.State.Dex[0];
            Assert.Equal(form, graduated.Form);
            Assert.Equal(shiny, graduated.IsShiny);
            Assert.Equal(nature, graduated.Nature);
            Assert.Equal(profile.InstanceID, graduated.Profile?.InstanceID);
            Assert.Equal(profile.Ivs, graduated.Profile?.Ivs);
            Assert.Equal(100, graduated.Profile?.Level);
            Assert.Equal(form, s.RepresentativeForm);

            var restored = Reload(url);
            Assert.Equal(form, restored.State.Dex[0].Form);
            Assert.Equal(shiny, restored.State.Dex[0].IsShiny);
            Assert.Equal(form, restored.RepresentativeForm);
            Assert.Equal(shiny, restored.RepresentativeSubject.IsShiny);
        }
    });

    /// <summary>Other species get no extra form roll, so later random results (mint, …) stay unchanged.</summary>
    [Fact]
    public void TestNonUnownHatchDoesNotConsumeAnExtraRandomDraw() => MainActor.Run(async () =>
    {
        var original = new CompanionState { Inventory = new() { ["mint"] = 1 } };
        var s = Store(original, 17);
        var rng = new SeededRng(17);
        var shiny = rng.Next() % PokemonOdds.ShinyDenominator == 0;
        var nature = PokemonNatureExtensions.AllCases[(int)(rng.Next() % (ulong)PokemonNatureExtensions.AllCases.Length)];
        var profileSeed = rng.Next();
        var mintPool = PokemonNatureExtensions.AllCases.Where(n => n != nature).ToList();
        var nextNature = mintPool[(int)(rng.Next() % (ulong)mintPool.Count)];

        await s.Hatch(25);
        Assert.Null(s.CurrentForm);
        Assert.Equal(shiny, s.CurrentIsShiny);
        Assert.Equal(nature, s.CurrentNature);
        Assert.Equal(profileSeed, s.State.Active?.Profile?.Seed);
        Assert.Equal(nextNature, s.UseMint());
    });

    [Fact]
    public void TestDexDeduplicatesPerLetterAndScopesShinyAndRaisingFlags() => MainActor.Run(() =>
    {
        var original = new CompanionState { Dex = [Entry(U.A), Entry(U.B), Entry(U.B, shiny: true)], Active = Mon(U.C) };
        var s = Store(original);
        Assert.Equal(3, s.State.Dex.Count); // each catch record of the same letter is kept
        Assert.Single(s.DexSpeciesList);
        Assert.True(s.DexSpeciesList[0].IsShiny);
        Assert.True(s.DexSpeciesList[0].IsRaising);
        Assert.Equal(new HashSet<PokemonForm> { U.A, U.B, U.C }, s.State.CollectedForms(PokemonForm.UnownSpeciesID));
        var forms = s.FormSpeciesList(PokemonForm.UnownSpeciesID);
        Assert.Equal([U.A, U.B, U.C], forms.Select(x => x.Form!.Value));
        Assert.Equal([false, true, false], forms.Select(x => x.IsShiny));
        Assert.Equal([false, false, true], forms.Select(x => x.IsRaising));
        Assert.True(s.State.OwnsSpecies(201, U.C));
        Assert.False(s.State.OwnsSpecies(201, U.Z));
        Assert.False(s.State.OwnsShinySpecies(201, U.A));
        Assert.True(s.State.OwnsShinySpecies(201, U.B));
        Assert.False(s.State.OwnsShinySpecies(201, U.C));
    });

    [Fact]
    public void TestRepresentativeUsesExactLetterAndPersistsSelection() => MainActor.Run(() =>
    {
        var original = new CompanionState { Language = AppLanguage.En, Dex = [Entry(U.A), Entry(U.B, shiny: true)] };
        var url = StateUrl();
        var s = Store(original, url: url);

        Assert.True(s.SetRepresentativeSpeciesID(201, U.A));
        Assert.False(s.RepresentativeSubject.IsShiny); // a shiny B must not recolor a normal A
        Assert.Equal("Unown [A]", s.RepresentativeDexSpecies?.Name);
        Assert.False(s.RepresentativeDexSpecies?.IsShiny);
        Assert.Equal([true, false], s.FormSpeciesList(PokemonForm.UnownSpeciesID).Select(s.IsRepresentative));
        Assert.True(s.SetRepresentativeSpeciesID(201, U.B));
        Assert.True(s.RepresentativeSubject.IsShiny);
        Assert.Equal("Unown [B]", s.RepresentativeDexSpecies?.Name);
        Assert.True(s.RepresentativeDexSpecies?.IsShiny);
        Assert.True(s.IsRepresentative(s.DexSpeciesList.First())); // the single species cell marks any representative form
        Assert.Equal([false, true], s.FormSpeciesList(PokemonForm.UnownSpeciesID).Select(s.IsRepresentative));
        Assert.False(s.SetRepresentativeSpeciesID(201, U.Z));
        Assert.Equal(U.B, s.RepresentativeForm); // an unowned letter keeps the existing choice

        var restored = Reload(url);
        Assert.Equal(201, restored.RepresentativeSpeciesID);
        Assert.Equal(U.B, restored.RepresentativeForm);
        Assert.True(restored.RepresentativeSubject.IsShiny);
        Assert.True(restored.SetRepresentativeSpeciesID(null));
        Assert.Null(restored.RepresentativeForm);
        Assert.Null(restored.RepresentativeDexSpecies);
    });

    [Fact]
    public void TestReleasingActiveLetterPreservesFormProfileAndRepresentative() => MainActor.Run(() =>
    {
        var original = new CompanionState
        {
            Dex = [Entry(U.A, shiny: true)],
            Active = Mon(U.B),
            RepresentativeSpeciesID = 201,
            RepresentativeForm = U.B,
            UsedSinceInstall = FreshEgg.Price,
        };
        var url = StateUrl();
        var s = Store(original, url: url);
        var profile = s.State.Active!.Profile!.Clone();

        Assert.True(s.BuyFreshEgg());
        Assert.Equal(201, s.RepresentativeSpeciesID);
        Assert.Equal(U.B, s.RepresentativeForm);
        Assert.False(s.RepresentativeSubject.IsShiny);
        Assert.Equal(2, s.State.Dex.Count);
        Assert.Equal([U.A, U.B], s.FormSpeciesList(PokemonForm.UnownSpeciesID).Select(x => x.Form!.Value));
        Assert.All(s.DexSpeciesList, x => Assert.False(x.IsRaising));
        Assert.True(s.State.OwnsShinySpecies(201, U.A));
        Assert.True(s.State.OwnsSpecies(201, U.B));
        var released = s.State.Dex[^1];
        Assert.True(released.IsReleased);
        Assert.Equal(U.B, released.Form);
        Assert.Equal(profile, released.Profile);
        Assert.Equal(U.B, Reload(url).RepresentativeForm);
        Assert.Empty(s.State.CollectedFinals); // a release does not count as a completed graduation
    });

    [Fact]
    public void TestDetailIndividualsAndRaisingFlagAreScopedToExactLetter() => MainActor.Run(() =>
    {
        var original = new CompanionState { Dex = [Entry(U.A), Entry(U.B), Entry(U.B, shiny: true)], Active = Mon(U.B) };
        var s = Store(original);

        // Raising the same letter shows as raising even if that letter already graduated.
        Assert.Equal([false, true], s.FormSpeciesList(PokemonForm.UnownSpeciesID).Select(x => x.IsRaising));
        Assert.Single(s.PokemonIndividuals(201));
        var bIndividuals = s.PokemonIndividuals(201, U.B);
        Assert.Equal(3, bIndividuals.Count);
        Assert.True(s.IsActiveDexEntry(bIndividuals[0]));
        Assert.All(bIndividuals, e => Assert.True(e.Form == U.B && e.Profile != null));
        Assert.Equal(3, bIndividuals.Select(e => e.Profile!.InstanceID).Distinct().Count());
        Assert.Empty(s.PokemonIndividuals(201, U.Question));
    });

    [Fact]
    public void TestDifferentLettersKeepSpeciesBasedRepeatGrowthAtEveryDifficulty() => MainActor.Run(async () =>
    {
        foreach (var difficulty in new[] { 0.1, 1.0, 2.0 })
        {
            var s = Store(seed: 17);
            s.SetGrowthDifficulty(difficulty);
            await s.Hatch(201);
            Assert.False(s.State.Active!.HasGrowthBoost);
            var firstForm = s.CurrentForm!.Value;
            var unboostedThreshold = s.Threshold;
            s.ApplyUsage(s.TokensToNext);
            Assert.Null(s.State.Active);
            Assert.Equal(new HashSet<string> { "201:201" }, s.State.CollectedFinals);
            var nextEgg = s.State.Clone();
            nextEgg.PendingHatchID = 201;
            nextEgg.PendingForm = firstForm == U.A ? U.B : U.A;
            nextEgg.EggUsage = PokemonBalance.EggHatchThreshold;
            var repeated = Store(nextEgg);
            repeated.SetGrowthDifficulty(difficulty);
            await repeated.HatchIfNeeded();
            Assert.Equal(nextEgg.PendingForm, repeated.CurrentForm);
            Assert.NotEqual(firstForm, repeated.CurrentForm);
            Assert.True(repeated.State.Active!.HasGrowthBoost);
            Assert.Equal(unboostedThreshold / PokemonBalance.RepeatGrowthMultiplier, repeated.Threshold);
            Assert.Equal(firstForm, repeated.State.Dex[0].Form);
            repeated.ApplyUsage(repeated.TokensToNext);
            Assert.Equal(2, repeated.State.Dex.Count);
            Assert.All(repeated.State.Dex, e => Assert.Equal(100, e.Profile?.Level));
        }
    });

    [Fact]
    public void TestSaveExportAndImportPreserveActiveDexAndRepresentativeForms() => MainActor.Run(() =>
    {
        var original = new CompanionState
        {
            Active = Mon(U.Question, shiny: true),
            Dex = [Entry(U.A), Entry(U.Exclamation, shiny: true)],
            RepresentativeSpeciesID = 201,
            RepresentativeForm = U.Exclamation,
        };
        var source = Store(original);
        var data = source.ExportedSaveData("test", "Source");
        var envelope = SaveTransfer.Decode(data);
        var destinationUrl = StateUrl();
        var destination = Store(url: destinationUrl);

        destination.ApplySave(envelope, NoProviders, "2026-09-11", false);
        var restored = Reload(destinationUrl);
        Assert.Equal(U.Question, restored.State.Active?.Form);
        Assert.True(restored.State.Active?.IsShiny);
        Assert.Equal([U.A, U.Exclamation], restored.State.Dex.Select(e => e.Form!.Value));
        Assert.Equal([false, true], restored.State.Dex.Select(e => e.IsShiny));
        Assert.Equal(U.Exclamation, restored.RepresentativeForm);
        Assert.True(restored.RepresentativeSubject.IsShiny);
    });

    /// <summary>The real update → pre-roll path stores the letter, and a restart still hatches that letter.</summary>
    [Fact]
    public void TestPrefetchPersistsLetterAndHatchConsumesItAfterRNGChanges() => MainActor.Run(async () =>
    {
        var original = new CompanionState { InstallBaselineSet = true, LastDate = "d1" };
        var seed = Enumerable.Range(0, 1_000).Select(i => (ulong)i).First(sd =>
        {
            var r = new SeededRng(sd);
            r.Next();
            return r.Next() % 56 / 2 != 0;
        });
        var prefetchRng = new SeededRng(seed);
        prefetchRng.Next(); // species choice
        var expectedForm = U.Letters[(int)(prefetchRng.Next() % 56 / 2)];
        var provider = new UnownPrefetchProvider();
        var url = StateUrl();
        var s = Store(original, seed, url, provider);

        s.Update(new Dictionary<string, long> { ["test"] = 1_000 }, "d1", 0, BurnTier.Idle, false, true);
        await provider.LineRequested.Task; // the prefetch stored species + letter and entered the line warm-up
        Assert.Null(s.State.Active);
        Assert.Equal(201, s.State.PendingHatchID);
        Assert.Equal(expectedForm, s.State.PendingForm);
        var persisted = CompanionJson.Deserialize(File.ReadAllBytes(url));
        Assert.Equal(expectedForm, persisted.PendingForm);

        // A restart with a seed that would roll a different letter still uses the pending one.
        var restartedSeed = Enumerable.Range(0, 1_000).Select(i => (ulong)i).First(sd =>
        {
            var r = new SeededRng(sd);
            r.Next(); // shiny
            r.Next(); // nature
            r.Next(); // profile seed
            return U.Letters[(int)(r.Next() % 56 / 2)] != expectedForm;
        });
        persisted.EggUsage = PokemonBalance.EggHatchThreshold;
        persisted.UsedSinceInstall = FreshEgg.Price;
        var restarted = Store(persisted, restartedSeed);
        await restarted.HatchIfNeeded();

        Assert.Equal(expectedForm, restarted.CurrentForm);
        Assert.Null(restarted.State.PendingHatchID);
        Assert.Null(restarted.State.PendingForm);
        Assert.True(restarted.BuyFreshEgg());
        Assert.Null(restarted.State.Active);
        Assert.Null(restarted.State.PendingHatchID);
        Assert.Null(restarted.State.PendingForm); // the previous letter must not carry into the new egg
    });

    [Fact]
    public void TestLegacyPendingUnownKeepsDefaultAAtHatch() => MainActor.Run(async () =>
    {
        var decoded = CompanionJson.Deserialize("""{"pendingHatchID":201,"eggUsage":5000000}""");
        Assert.Equal(U.A, decoded.PendingForm);
        var s = Store(decoded, 17);

        await s.HatchIfNeeded();
        Assert.Equal(U.A, s.CurrentForm);
        Assert.Null(s.State.PendingForm);
    });

    [Fact]
    public void TestPendingLetterSurvivesFailedLineLoadAndHatchesOnRetry() => MainActor.Run(async () =>
    {
        var original = new CompanionState
        {
            PendingHatchID = 201,
            PendingForm = U.Question,
            EggUsage = PokemonBalance.EggHatchThreshold + 123,
        };
        var url = StateUrl();
        var s = Store(original, 17, url, new UnownRetryProvider());

        await s.HatchIfNeeded(); // the first line request fails offline
        Assert.Null(s.State.Active);
        Assert.False(s.IsHatching);
        Assert.Equal(201, s.State.PendingHatchID);
        Assert.Equal(U.Question, s.State.PendingForm);
        Assert.Equal(original.EggUsage, s.State.EggUsage);
        s.SetLanguage(AppLanguage.Ko); // a regular save must not lose the warmed species and letter
        var saved = Reload(url);
        Assert.Equal(201, saved.State.PendingHatchID);
        Assert.Equal(U.Question, saved.State.PendingForm);

        await s.HatchIfNeeded();
        Assert.Equal(201, s.CurrentSpeciesID);
        Assert.Equal(U.Question, s.CurrentForm);
        Assert.Equal(123, s.State.Active?.UsedAtStage);
        Assert.Null(s.State.PendingHatchID);
        Assert.Null(s.State.PendingForm);
    });

    [Fact]
    public void TestSanitizationClearsPendingLetterWithoutMatchingEgg()
    {
        var original = new CompanionState { Active = Mon(U.B), PendingHatchID = 201, PendingForm = U.Question };
        var activeState = SaveTransfer.Sanitized(original);
        Assert.Equal(U.B, activeState.Active?.Form);
        Assert.Null(activeState.PendingHatchID);
        Assert.Null(activeState.PendingForm);

        original.Active = null;
        original.PendingHatchID = 25;
        Assert.Null(SaveTransfer.Sanitized(original).PendingForm);
        original.PendingHatchID = null;
        Assert.Null(SaveTransfer.Sanitized(original).PendingForm);
    }

    [Fact]
    public void TestSaveExportAndImportPreservePendingEggLetter() => MainActor.Run(() =>
    {
        var original = new CompanionState { PendingHatchID = 201, PendingForm = U.Exclamation, EggUsage = 1_234 };
        var source = Store(original);
        var data = source.ExportedSaveData("test", "Source");
        var destinationUrl = StateUrl();
        var destination = Store(url: destinationUrl);

        destination.ApplySave(SaveTransfer.Decode(data), NoProviders, "2026-09-11", false);
        var restored = Reload(destinationUrl);
        Assert.Null(restored.State.Active);
        Assert.Equal(201, restored.State.PendingHatchID);
        Assert.Equal(U.Exclamation, restored.State.PendingForm);
        Assert.Equal(1_234, restored.State.EggUsage);
    });
}
