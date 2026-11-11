using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of PokemonProfileTests.swift.

file static class ProfileFixtures
{
    public static readonly PokemonDetails Slowpoke = new(
        SpeciesID: 79,
        Name: "slowpoke",
        Height: 12,
        Weight: 360,
        BaseExperience: 63,
        GenderRate: 8,
        Types: ["water", "psychic"],
        BaseStats: new Dictionary<string, int>
        {
            ["hp"] = 90, ["attack"] = 65, ["defense"] = 65,
            ["special-attack"] = 40, ["special-defense"] = 40, ["speed"] = 15,
        },
        Abilities: [new PokemonAbilityOption("oblivious", 1, false)],
        Moves:
        [
            new PokemonMoveOption("tackle", [new PokemonMoveLearnMethod("level-up", 1)]),
            new PokemonMoveOption("curse", [new PokemonMoveLearnMethod("level-up", 1)]),
            new PokemonMoveOption("yawn", [new PokemonMoveLearnMethod("level-up", 1)]),
            new PokemonMoveOption("growl", [new PokemonMoveLearnMethod("level-up", 5)]),
            new PokemonMoveOption("water-gun", [new PokemonMoveLearnMethod("level-up", 9)]),
            new PokemonMoveOption("surf", [new PokemonMoveLearnMethod("machine", 0)]),
        ]);

    public static PokemonDetails Details(int genderRate, IReadOnlyList<PokemonAbilityOption> abilities) =>
        new(999, "test", 10, 100, null, genderRate, ["normal"], new Dictionary<string, int> { ["hp"] = 50 },
            abilities, []);

    public static EvoLine SingleLine(int baseSpeciesID, string name) =>
        new(baseSpeciesID, new EvoNode(baseSpeciesID), Rarity.Common,
            new Dictionary<int, Dictionary<string, string>> { [baseSpeciesID] = new() { ["en"] = name } });

    public static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

file sealed class ProfileLineProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(ProfileFixtures.SingleLine(baseSpeciesID, "Slowpoke"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

file sealed class ProfileDetailProvider : IPokemonDetailProviding
{
    public Task<PokemonDetails> PokemonDetails(int speciesID) => Task.FromResult(ProfileFixtures.Slowpoke);
}

file sealed class ProfileDetailTestException : Exception;

file sealed class ThrowingProfileDetailProvider : IPokemonDetailProviding
{
    public Task<PokemonDetails> PokemonDetails(int speciesID) =>
        Task.FromException<PokemonDetails>(new ProfileDetailTestException());
}

file sealed class RecordingProfileProvider : IPokeProviding, IPokemonDetailProviding
{
    public List<int> DetailIDs { get; } = [];
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(ProfileFixtures.SingleLine(baseSpeciesID, "Test"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);

    public Task<PokemonDetails> PokemonDetails(int speciesID)
    {
        DetailIDs.Add(speciesID);
        return Task.FromResult(new PokemonDetails(speciesID, "test", 10, 100, null, -1, ["normal"],
            new Dictionary<string, int> { ["hp"] = 50 }, [], []));
    }
}

file sealed class SuspendedProfileProvider : IPokeProviding, IPokemonDetailProviding
{
    private TaskCompletionSource<PokemonDetails>? _pending;
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(ProfileFixtures.SingleLine(baseSpeciesID, "Test"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);

    public Task<PokemonDetails> PokemonDetails(int speciesID)
    {
        _pending = new TaskCompletionSource<PokemonDetails>();
        return _pending.Task;
    }

    public bool IsDetailSuspended => _pending != null;

    public void ResumeDetails()
    {
        var pending = _pending;
        _pending = null;
        pending?.SetResult(ProfileFixtures.Slowpoke);
    }
}

public class PokemonProfileLogicTests
{
    [Fact]
    public void GenerationIsDeterministicAndIVsStayInRange()
    {
        var a = PokemonProfile.Generate(42, instanceID: "same");
        var b = PokemonProfile.Generate(42, instanceID: "same");
        Assert.Equal(a, b);
        foreach (var iv in new[] { a.Ivs.Hp, a.Ivs.Attack, a.Ivs.Defense, a.Ivs.SpecialAttack, a.Ivs.SpecialDefense, a.Ivs.Speed })
            Assert.InRange(iv, 0, 31);
    }

    [Fact]
    public void EnrichmentRollsPersistentIdentityAndFourLegalMoves()
    {
        var profile = PokemonProfile.Generate(7, instanceID: "p");
        profile.Level = 9;
        profile.Enrich(ProfileFixtures.Slowpoke);

        Assert.Equal(PokemonGender.Female, profile.Gender);
        Assert.Equal("oblivious", profile.AbilityName);
        Assert.Equal(1, profile.AbilitySlot);
        Assert.Equal(4, profile.Moves.Count);
        Assert.Contains(profile.Moves, m => m.Name == "water-gun");
        Assert.DoesNotContain(profile.Moves, m => m.Name == "surf"); // TM moves are listed but not auto-equipped

        var first = profile.Clone();
        profile.Enrich(ProfileFixtures.Slowpoke);
        Assert.Equal(first, profile); // enrichment must not reroll an individual
    }

    [Fact]
    public void NatureChangesCalculatedNonHPStat()
    {
        var neutral = PokemonProfile.Generate(1, instanceID: "neutral");
        neutral.Level = 50;
        var stats = PokemonStatCalculator.Stats(ProfileFixtures.Slowpoke, neutral, PokemonNature.Adamant);
        var attack = stats.Single(s => s.Name == "attack");
        var specialAttack = stats.Single(s => s.Name == "special-attack");
        var neutralAttack = (2 * 65 + neutral.Ivs.Attack) * 50 / 100 + 5;
        var neutralSpecial = (2 * 40 + neutral.Ivs.SpecialAttack) * 50 / 100 + 5;
        Assert.Equal((int)Math.Floor(neutralAttack * 1.1), attack.Value);
        Assert.Equal((int)Math.Floor(neutralSpecial * 0.9), specialAttack.Value);
    }

    /// <summary>Shedinja (#292) always has exactly 1 HP, regardless of level and IVs.</summary>
    [Fact]
    public void ShedinjaAlwaysHasOneHP()
    {
        var shedinja = new PokemonDetails(292, "shedinja", 8, 12, 83, -1, ["bug", "ghost"],
            new Dictionary<string, int>
            {
                ["hp"] = 1, ["attack"] = 90, ["defense"] = 45,
                ["special-attack"] = 30, ["special-defense"] = 30, ["speed"] = 40,
            }, [], []);
        foreach (var level in new[] { 1, 50, 100 })
        {
            var profile = PokemonProfile.Generate(3, instanceID: "shedinja");
            profile.Level = level;
            var stats = PokemonStatCalculator.Stats(shedinja, profile, PokemonNature.Hardy);
            Assert.Equal(1, stats.Single(s => s.Name == "hp").Value);
            var attack = stats.Single(s => s.Name == "attack");
            Assert.Equal((2 * 90 + profile.Ivs.Attack) * level / 100 + 5, attack.Value);
        }
        var other = PokemonProfile.Generate(3, instanceID: "base-1");
        other.Level = 50;
        var oneBase = new PokemonDetails(999, "test", 1, 1, null, -1, ["normal"],
            new Dictionary<string, int> { ["hp"] = 1 }, [], []);
        // only Shedinja is special, not base HP 1
        Assert.Equal((2 + other.Ivs.Hp) * 50 / 100 + 60, PokemonStatCalculator.Stats(oneBase, other, null).First().Value);
    }

    [Fact]
    public void ActualStatScaleExpandsForHighHPPokemon()
    {
        Assert.Equal(300, PokemonStatCalculator.DisplayScaleMaximum([180, 299]));
        Assert.Equal(700, PokemonStatCalculator.DisplayScaleMaximum([651, 300]));
    }

    [Fact]
    public void EveryNatureChangesExactlyItsExpectedNumberOfStats()
    {
        var neutral = new HashSet<PokemonNature>
        {
            PokemonNature.Hardy, PokemonNature.Docile, PokemonNature.Serious, PokemonNature.Bashful, PokemonNature.Quirky,
        };
        var affectedStats = PokemonStatCalculator.Order.Where(s => s != "hp").ToList();
        Assert.Equal(25, PokemonNatureExtensions.AllCases.Length);
        foreach (var nature in PokemonNatureExtensions.AllCases)
        {
            var raised = affectedStats.Where(s => nature.Modifier(s) == 1.1).ToList();
            var lowered = affectedStats.Where(s => nature.Modifier(s) == 0.9).ToList();
            if (neutral.Contains(nature))
            {
                Assert.True(raised.Count == 0 && lowered.Count == 0, $"{nature} must be neutral");
            }
            else
            {
                Assert.Single(raised);
                Assert.Single(lowered);
                Assert.NotEqual(raised[0], lowered[0]);
            }
        }
    }

    [Fact]
    public void GrowthMapsHatchToFiveAndGraduationToHundred()
    {
        var profile = PokemonProfile.Generate(1, instanceID: "growth");
        Assert.Equal(5, profile.Level);
        profile.ApplyGrowth(PokemonBalance.GraduationTotal(Rarity.Common), Rarity.Common);
        Assert.Equal(100, profile.Level);
    }

    [Fact]
    public void GenderlessAndMaleBranchesAreResolved()
    {
        var genderless = PokemonProfile.Generate(1, instanceID: "genderless");
        genderless.Enrich(ProfileFixtures.Details(-1, []));
        Assert.Equal(PokemonGender.Genderless, genderless.Gender);

        var male = PokemonProfile.Generate(1, instanceID: "male");
        male.Enrich(ProfileFixtures.Details(0, []));
        Assert.Equal(PokemonGender.Male, male.Gender);
    }

    [Fact]
    public void HiddenAbilityRollCanSelectAHiddenSlot()
    {
        var details = ProfileFixtures.Details(-1,
        [
            new PokemonAbilityOption("hidden-one", 3, true),
            new PokemonAbilityOption("hidden-two", 4, true),
        ]);
        var profile = PokemonProfile.Generate(115, instanceID: "hidden-roll");
        profile.Enrich(details);
        Assert.Equal("hidden-two", profile.AbilityName);
        Assert.Equal(4, profile.AbilitySlot);
        Assert.True(profile.AbilityIsHidden);
    }

    [Fact]
    public void AllHiddenAbilitiesFallBackWhenRareRollMisses()
    {
        var details = ProfileFixtures.Details(-1,
        [
            new PokemonAbilityOption("first-hidden", 2, true),
            new PokemonAbilityOption("second-hidden", 3, true),
        ]);
        var profile = PokemonProfile.Generate(1, instanceID: "hidden-fallback");
        profile.Enrich(details);
        Assert.Equal("first-hidden", profile.AbilityName);
        Assert.Equal(2, profile.AbilitySlot);
        Assert.True(profile.AbilityIsHidden);
    }

    [Fact]
    public void AbilityNameFallbackChainRepairsPersistedSelections()
    {
        var normal = new PokemonAbilityOption("normal", 1, false);
        var hidden = new PokemonAbilityOption("hidden", 3, true);

        var slotOnly = PokemonProfile.Generate(2, instanceID: "slot-only");
        slotOnly.AbilitySlot = 1;
        slotOnly.AbilityIsHidden = true;
        slotOnly.Enrich(ProfileFixtures.Details(-1, [normal]));
        Assert.Equal("normal", slotOnly.AbilityName); // same slot should repair a stale hidden flag
        Assert.False(slotOnly.AbilityIsHidden);

        var normalFallback = PokemonProfile.Generate(3, instanceID: "normal-fallback");
        normalFallback.AbilitySlot = 99;
        normalFallback.AbilityIsHidden = true;
        normalFallback.Enrich(ProfileFixtures.Details(-1, [hidden, normal]));
        Assert.Equal("normal", normalFallback.AbilityName);
        Assert.False(normalFallback.AbilityIsHidden);

        var hiddenFallback = PokemonProfile.Generate(4, instanceID: "hidden-fallback-chain");
        hiddenFallback.AbilitySlot = 99;
        hiddenFallback.Enrich(ProfileFixtures.Details(-1, [hidden]));
        Assert.Equal("hidden", hiddenFallback.AbilityName);
        Assert.True(hiddenFallback.AbilityIsHidden);
    }

    [Fact]
    public void SpeciesIdentityRebaseKeepsIVsButClearsDisguiseMetadata()
    {
        var profile = PokemonProfile.Generate(7, instanceID: "ditto");
        profile.Enrich(ProfileFixtures.Slowpoke);
        var ivs = profile.Ivs;

        profile.ApplyGrowth(125_000_000, Rarity.Common);
        profile.RebaseForSpeciesIdentity(Rarity.Common, Rarity.Rare);

        Assert.Equal("ditto", profile.InstanceID);
        Assert.Equal(ivs, profile.Ivs);
        Assert.Null(profile.Gender);
        Assert.Null(profile.AbilitySlot);
        Assert.Null(profile.AbilityName);
        Assert.False(profile.AbilityIsHidden);
        Assert.Empty(profile.Moves);
        Assert.Equal(500_000_000, profile.GrowthTokens);
        Assert.Equal(20, profile.Level);
    }
}

public class PokemonDetailNormalizationTests
{
    [Fact]
    public void ParserKeepsOnlySupportedVersionGroupBeforeCaching()
    {
        var supported = new PokeApiClient.PokemonMoveVersionDto(5,
            new PokeApiClient.NamedRef("level-up", null),
            new PokeApiClient.NamedRef(PokemonDetails.PreferredVersionGroup, null));
        var unsupported = new PokeApiClient.PokemonMoveVersionDto(9,
            new PokeApiClient.NamedRef("level-up", null),
            new PokeApiClient.NamedRef("scarlet-violet", null));
        var moves = new[]
        {
            new PokeApiClient.PokemonMoveDto(new PokeApiClient.NamedRef("mixed", null), [supported, unsupported]),
            new PokeApiClient.PokemonMoveDto(new PokeApiClient.NamedRef("future-only", null), [unsupported]),
        };

        var normalized = PokeApiClient.NormalizedMoves(moves);

        Assert.Equal(["mixed"], normalized.Select(m => m.Name));
        Assert.Equal([new PokemonMoveLearnMethod("level-up", 5)], normalized[0].LearnMethods);
    }
}

/// <summary>
/// An imported save comes from another device and PokemonProfile's decoder takes the file's values as-is, so
/// SaveTransfer.Sanitized is the only gate between a hand-edited save and the detail screen. Both call sites
/// (active and dex) are verified separately.
/// </summary>
public class PokemonProfileSanitizationTests
{
    private static PokemonProfile HostileProfile()
    {
        var profile = PokemonProfile.Generate(5, instanceID: "hostile");
        profile.InstanceID = "";
        profile.Level = 9_999;
        profile.GrowthTokens = long.MaxValue;
        profile.Ivs = new PokemonIVs(999, -5, 31, 64, -1, 500);
        profile.Moves = Enumerable.Range(0, 400)
            .Select(i => new PokemonKnownMove($"move-{i}-" + new string('x', 200), 900 + i)).ToList();
        return profile;
    }

    private static void AssertClamped(PokemonProfile? profile, string label)
    {
        Assert.NotNull(profile);
        var p = profile!;
        Assert.Equal(100, p.Level);
        Assert.Equal(SaveTransfer.MaxTokenValue, p.GrowthTokens);
        Assert.Equal([31, 0, 31, 31, 0, 31],
            new[] { p.Ivs.Hp, p.Ivs.Attack, p.Ivs.Defense, p.Ivs.SpecialAttack, p.Ivs.SpecialDefense, p.Ivs.Speed });
        Assert.Equal(4, p.Moves.Count); // unbounded imported move list
        Assert.All(p.Moves, m => Assert.True(m.Name.Length <= 80, $"{label}: move name length"));
        Assert.All(p.Moves, m => Assert.InRange(m.LearnedAtLevel, 0, 100));
        Assert.False(string.IsNullOrEmpty(p.InstanceID), $"{label}: instanceID");
    }

    [Fact]
    public void ImportClampsHostileActiveProfile()
    {
        var state = new CompanionState
        {
            Active = new MonState(79, [79], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1,
                profile: HostileProfile()),
        };
        AssertClamped(SaveTransfer.Sanitized(state).Active?.Profile, "active");
    }

    [Fact]
    public void ImportClampsHostileDexProfile()
    {
        var state = new CompanionState
        {
            Dex = [new DexEntry(79, 79, [79], Rarity.Common, DateTimeOffset.Now, profile: HostileProfile(), id: "imported")],
        };
        AssertClamped(SaveTransfer.Sanitized(state).Dex.FirstOrDefault()?.Profile, "dex");
    }

    [Fact]
    public void ImportRaisesProfileLevelToHatchMinimum()
    {
        var profile = PokemonProfile.Generate(5, instanceID: "below-hatch-level");
        profile.Level = -99;
        var state = new CompanionState
        {
            Active = new MonState(79, [79], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1,
                profile: profile),
        };
        var sanitized = SaveTransfer.Sanitized(state).Active?.Profile;
        Assert.NotNull(sanitized);
        Assert.Equal(5, sanitized!.Level);
    }
}

public class PokemonProfileMigrationTests
{
    private static CompanionStore Store(IPokeProviding provider, string file, IPokemonDetailProviding? detailProvider = null) =>
        new(provider, detailProvider, filePath: file, settings: new AppSettings(null));

    [Fact]
    public void CorruptProfileRegeneratesWithoutDroppingPokemon() => MainActor.Run(() =>
    {
        var dir = ProfileFixtures.TempDir("profile-recovery");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            const string json = """{"active":{"baseID":79,"pathIDs":[79],"plannedPathIDs":[79],"stageIndex":0,"usedAtStage":10,"rarity":"common","totalForms":1,"profile":"broken"},"dex":[{"id":"kept","baseID":1,"finalID":1,"chainOrder":[1],"rarity":"common","profile":"broken"}]}""";
            File.WriteAllText(file, json);

            var store = Store(new ProfileLineProvider(), file);
            Assert.Equal(79, store.State.Active?.CurrentID);
            Assert.NotNull(store.State.Active?.Profile);
            Assert.Equal("kept", store.State.Dex.FirstOrDefault()?.Id);
            Assert.NotNull(store.State.Dex.FirstOrDefault()?.Profile);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void LegacyActiveAndDexProfilesMigrateOnceAndAreBackedUp() => MainActor.Run(async () =>
    {
        var dir = ProfileFixtures.TempDir("profile-migration");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            var legacy = new CompanionState
            {
                Active = new MonState(79, [79], stageIndex: 0, usedAtStage: 10_000, rarity: Rarity.Common,
                    totalForms: 2, nature: PokemonNature.Sassy),
                Dex = [new DexEntry(1, 3, [1, 2, 3], Rarity.Common, DateTimeOffset.Now, id: "old-catch")],
            };
            StateFiles.Write(legacy, file);

            var store = Store(new ProfileLineProvider(), file, new ProfileDetailProvider());
            Assert.NotNull(store.State.Active?.Profile);
            Assert.Equal(100, store.State.Dex.FirstOrDefault()?.Profile?.Level);
            Assert.True(File.Exists(Path.Combine(dir, "companion-state.pre-profiles-v1.json")));

            var instanceID = store.State.Active!.Profile!.InstanceID;
            await store.LoadPokemonDetails(79);
            Assert.Equal(instanceID, store.State.Active?.Profile?.InstanceID);
            Assert.Equal(PokemonGender.Female, store.State.Active?.Profile?.Gender);
            Assert.Equal("oblivious", store.State.Active?.Profile?.AbilityName);

            var reloaded = Store(new ProfileLineProvider(), file);
            // a migrated individual must not be regenerated on restart
            Assert.Equal(instanceID, reloaded.State.Active?.Profile?.InstanceID);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void ReleasedLegacyEntryUsesReachedChainAsGrowthUpperBound() => MainActor.Run(() =>
    {
        var dir = ProfileFixtures.TempDir("released-profile-migration");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            var legacy = new CompanionState
            {
                Dex = [new DexEntry(1, 2, [1, 2], Rarity.Common, DateTimeOffset.Now, releasedAt: DateTimeOffset.Now, id: "released")],
            };
            StateFiles.Write(legacy, file);

            var store = Store(new ProfileLineProvider(), file);
            var profile = store.State.Dex.First().Profile;
            Assert.NotNull(profile);
            var expected = CompanionStore.ReconstructedGrowthTokens(Rarity.Common, 2, 1, 0);
            var actualThreeFormThreshold = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0);
            Assert.Equal(expected, profile!.GrowthTokens);
            // without planned totalForms the estimate is intentionally an upper bound
            Assert.True(expected > actualThreeFormThreshold);
            Assert.True(profile.Level > 5);
            Assert.True(profile.Level < 100);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void PokemonIndividualsIncludesSyntheticActiveAndMatchingStoredIndividuals() => MainActor.Run(() =>
    {
        var dir = ProfileFixtures.TempDir("profile-individuals");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            var saved = new CompanionState
            {
                Active = new MonState(79, [79], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1,
                    profile: PokemonProfile.Generate(1, instanceID: "active-instance")),
                Dex =
                [
                    new DexEntry(79, 79, [79], Rarity.Common, DateTimeOffset.FromUnixTimeSeconds(20),
                        profile: PokemonProfile.Generate(2, instanceID: "stored-new"), id: "stored-new"),
                    new DexEntry(79, 79, [79], Rarity.Common, DateTimeOffset.FromUnixTimeSeconds(10),
                        profile: PokemonProfile.Generate(3, instanceID: "stored-old"), id: "stored-old"),
                ],
            };
            StateFiles.Write(saved, file);
            var store = Store(new ProfileLineProvider(), file);

            var individuals = store.PokemonIndividuals(79);

            Assert.Equal(["active-79-79", "stored-new", "stored-old"], individuals.Select(e => e.Id));
            Assert.Equal("active-instance", individuals.First().Profile?.InstanceID);
            // the active row has a synthetic UI identity but keeps its profile identity
            Assert.NotEqual(individuals.First().Id, individuals.First().Profile?.InstanceID);
            Assert.Empty(store.PokemonIndividuals(80));
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void DetailLoadEnrichesEveryMatchingStoredIndividual() => MainActor.Run(async () =>
    {
        var dir = ProfileFixtures.TempDir("profile-dex-enrichment");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            var saved = new CompanionState
            {
                Dex =
                [
                    new DexEntry(79, 79, [79], Rarity.Common, DateTimeOffset.Now,
                        profile: PokemonProfile.Generate(1, instanceID: "one"), id: "one"),
                    new DexEntry(79, 79, [79], Rarity.Common, DateTimeOffset.Now,
                        profile: PokemonProfile.Generate(2, instanceID: "two"), id: "two"),
                ],
            };
            StateFiles.Write(saved, file);
            var store = Store(new ProfileLineProvider(), file, new ProfileDetailProvider());

            await store.LoadPokemonDetails(79);

            Assert.Equal([PokemonGender.Female, PokemonGender.Female],
                store.State.Dex.Select(e => e.Profile?.Gender).OfType<PokemonGender>());
            Assert.All(store.State.Dex, e => Assert.Equal("oblivious", e.Profile?.AbilityName));
            Assert.All(store.State.Dex, e => Assert.NotEmpty(e.Profile?.Moves ?? []));
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void DetailFetchFailureSetsRetryStateAndClearsLoadingState() => MainActor.Run(async () =>
    {
        var file = StateFiles.TempPath("profile-detail-error");
        try
        {
            var store = Store(new ProfileLineProvider(), file, new ThrowingProfileDetailProvider());

            await store.LoadPokemonDetails(79);

            Assert.Contains(79, store.FailedPokemonDetailIDs);
            Assert.DoesNotContain(79, store.LoadingPokemonDetailIDs);
            Assert.False(store.PokemonDetailsByID.ContainsKey(79));
        }
        finally { File.Delete(file); }
    });

    [Fact]
    public void StartupWarmupLoadsOnlyTheActivePokemon() => MainActor.Run(async () =>
    {
        var dir = ProfileFixtures.TempDir("profile-warmup");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            var saved = new CompanionState
            {
                Active = new MonState(79, [79], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1),
                Dex = [new DexEntry(1, 3, [1, 2, 3], Rarity.Common, DateTimeOffset.Now, id: "old")],
            };
            StateFiles.Write(saved, file);
            var provider = new RecordingProfileProvider();
            var store = Store(provider, file);

            await store.PreparePokemonProfiles();

            Assert.Equal([79], provider.DetailIDs);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void PathNormalizationLoadsDetailsForTheRecoveredCurrentSpecies() => MainActor.Run(async () =>
    {
        var dir = ProfileFixtures.TempDir("profile-normalization");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            var saved = new CompanionState
            {
                Active = new MonState(79, [79, 999], [79, 999], stageIndex: 1, usedAtStage: 0, rarity: Rarity.Common,
                    totalForms: 2),
            };
            StateFiles.Write(saved, file);
            var provider = new RecordingProfileProvider();
            var store = Store(provider, file);

            store.Update(new Dictionary<string, long> { ["test"] = 0 }, "d1", 0, BurnTier.Idle, false, true);
            await MainActor.WaitUntil(() => provider.DetailIDs.Count > 0);

            Assert.Equal(79, store.CurrentSpeciesID);
            Assert.Equal([79], provider.DetailIDs);
            Assert.Equal(PokemonGender.Genderless, store.State.Active?.Profile?.Gender);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void LineLoadReleasesHatchingLockBeforeDetailRequestCompletes() => MainActor.Run(async () =>
    {
        var dir = ProfileFixtures.TempDir("profile-lock-lifetime");
        try
        {
            var file = Path.Combine(dir, "companion-state.json");
            var saved = new CompanionState
            {
                Active = new MonState(79, [79], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1),
            };
            StateFiles.Write(saved, file);
            var provider = new SuspendedProfileProvider();
            var store = Store(provider, file);

            store.Update(new Dictionary<string, long> { ["test"] = 0 }, "d1", 0, BurnTier.Idle, false, true);
            await MainActor.WaitUntil(() => provider.IsDetailSuspended);

            Assert.True(provider.IsDetailSuspended);
            Assert.False(store.IsHatching); // detail enrichment must not extend the evolution-line lock
            provider.ResumeDetails();
            await MainActor.Drain();
        }
        finally { Directory.Delete(dir, true); }
    });
}
