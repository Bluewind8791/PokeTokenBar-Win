using System.Text.RegularExpressions;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Fails when the macOS save model gains a stored field the Windows encoder does not write. Such a field is
/// dropped silently when a macOS save is imported here and exported back (#466 prayers and #442 dex completions
/// went unnoticed this way). Parses the Swift structs, like <see cref="LocalizationParityTests"/> parses the strings.
/// </summary>
public class SaveFieldParityTests
{
    private static readonly string Swift = LocalizationSource.SwiftCore("CompanionModel.swift");

    /// <summary>Stored properties of a Swift struct: <c>var</c>/<c>let</c> at member indentation, minus computed ones
    /// (a <c>{</c> with no <c>=</c> before it). Every stored property is encoded: the structs use synthesized
    /// <c>encode(to:)</c> without <c>CodingKeys</c>.</summary>
    internal static SortedSet<string> SwiftStoredProperties(string source, string structName)
    {
        var start = source.IndexOf($"\nstruct {structName}:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"struct {structName} not found in CompanionModel.swift");
        var end = source.IndexOf("\n}", start + 1, StringComparison.Ordinal);
        var body = source[start..end];
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(body, @"^    (?:var|let) (\w+)(.*)$", RegexOptions.Multiline))
        {
            var code = m.Groups[2].Value.Split("//")[0];
            var brace = code.IndexOf('{');
            var assign = code.IndexOf('=');
            var computed = brace >= 0 && (assign < 0 || brace < assign);
            if (!computed) fields.Add(m.Groups[1].Value);
        }
        return fields;
    }

    private static SortedSet<string> Keys(System.Text.Json.Nodes.JsonObject o) =>
        new(o.Select(kv => kv.Key), StringComparer.Ordinal);

    // Species with forms (Burmy 412, Unown 201): an unknown form for a formless species is dropped, not written.
    private static MonState FullMon() =>
        new(412, [412], [412, 413], stageIndex: 0, usedAtStage: 1, rarity: Rarity.Common, totalForms: 2, isShiny: true,
            nature: PokemonNatureExtensions.AllCases[0], profile: PokemonProfile.Generate(1), hasGrowthBoost: true,
            dittoDisguise: 412, dittoRevealed: true, form: new PokemonForm("sandy"), prayers: new PrayerRecord(1));

    [Fact]
    public void ParserFindsKnownFieldsAndSkipsComputedOnes()
    {
        var mon = SwiftStoredProperties(Swift, "MonState");
        Assert.Contains("baseID", mon);
        Assert.Contains("dittoRevealed", mon);
        Assert.DoesNotContain("displaysShiny", mon);   // computed
        Assert.DoesNotContain("currentID", mon);       // computed
        Assert.DoesNotContain("isReleased", SwiftStoredProperties(Swift, "DexEntry"));
    }

    [Fact]
    public void MonStateWritesEverySwiftField() =>
        Assert.Equal(SwiftStoredProperties(Swift, "MonState"), Keys(CompanionJson.EncodeMon(FullMon())));

    [Fact]
    public void DexEntryWritesEverySwiftField()
    {
        var entry = new DexEntry(201, 201, [201], Rarity.Uncommon, Lines.FixedNow, isShiny: true,
            nature: PokemonNatureExtensions.AllCases[0], profile: PokemonProfile.Generate(1),
            names: new() { [201] = new() { ["en"] = "Unown" } },
            releasedAt: Lines.FixedNow, form: new PokemonForm("question"), prayers: new PrayerRecord(1));
        Assert.Equal(SwiftStoredProperties(Swift, "DexEntry"),
            Keys(CompanionJson.EncodeDexEntry(entry, SwiftDateEncoding.DeferredToDate)));
    }

    [Fact]
    public void CompanionStateWritesEverySwiftField()
    {
        var state = new CompanionState
        {
            EggTier = Rarity.Rare,
            PendingHatchID = 201,
            PendingForm = new PokemonForm("question"),
            EggPrayers = new PrayerRecord(1),
            ClaimedTodayTokensByProvider = [],
            Active = FullMon(),
            RepresentativeSpeciesID = 412,
            RepresentativeForm = new PokemonForm("sandy"),
        };
        Assert.Equal(SwiftStoredProperties(Swift, "CompanionState"),
            Keys(CompanionJson.EncodeState(state, SwiftDateEncoding.DeferredToDate)));
    }
}
