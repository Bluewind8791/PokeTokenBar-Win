using System.Text;
using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Cross-platform save compatibility. The fixtures below are written by hand from the Swift Codable definitions
/// (synthesized encode of CompanionState / MonState / DexEntry / PokemonProfile, JSONEncoder defaults for
/// companion-state.json, <c>.iso8601</c> + <c>.sortedKeys</c> + <c>.prettyPrinted</c> for exported saves), so they
/// are what the macOS app writes. Decoding them and re-encoding must reproduce the same JSON, which is what lets a
/// save cross between macOS and Windows in both directions.
/// </summary>
public class SaveFormatCompatibilityTests
{
    // 2023-11-14T22:13:20Z = 1_700_000_000 unix = 721_692_800 seconds since 2001-01-01 (Swift .deferredToDate).
    private static readonly DateTimeOffset Caught = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    /// <summary>companion-state.json as Swift's default JSONEncoder writes it (every key Swift emits, no nils).</summary>
    private const string SwiftStateFile = """
    {"installBaselineSet":true,"usedSinceInstall":5123456789,"spentTokens":1000000000,"eggUsage":0,
     "claimedTodayTokensByProvider":{"claude_code":48000000,"codex":0},"lastDate":"2026-11-03",
     "active":{"baseID":412,"pathIDs":[412,413],"plannedPathIDs":[412,413],"stageIndex":1,"usedAtStage":250000000,
       "rarity":"uncommon","totalForms":2,"isShiny":true,"nature":"timid","form":"sandy",
       "profile":{"instanceID":"7D3F1A0E-0000-4000-8000-000000000001","seed":18446744073709551615,"gender":"female",
         "ivs":{"hp":31,"attack":0,"defense":15,"specialAttack":7,"specialDefense":22,"speed":30},
         "abilitySlot":1,"abilityName":"anticipation","abilityIsHidden":false,"level":42,"growthTokens":1000000000,
         "moves":[{"name":"tackle","learnedAtLevel":1},{"name":"protect","learnedAtLevel":10}]},
       "hasGrowthBoost":true,"dittoRevealed":false},
     "representativeSpeciesID":3,
     "dex":[{"id":"A1B2C3D4-0000-4000-8000-000000000002","baseID":1,"finalID":3,"chainOrder":[1,2,3],"rarity":"common",
       "caughtAt":721692800.5,"isShiny":false,"nature":"adamant",
       "profile":{"instanceID":"A1B2C3D4-0000-4000-8000-000000000002","seed":42,
         "ivs":{"hp":1,"attack":2,"defense":3,"specialAttack":4,"specialDefense":5,"speed":6},
         "abilityIsHidden":true,"level":100,"growthTokens":750000000,"moves":[]},
       "names":{"1":{"en":"Bulbasaur","ko":"이상해씨","ja-Hrkt":"フシギダネ"},"2":{"en":"Ivysaur"},"3":{"en":"Venusaur"}},
       "namesVersion":1},
      {"id":"released-1","baseID":201,"finalID":201,"chainOrder":[201],"rarity":"uncommon","caughtAt":721699999,
       "isShiny":true,"form":"question","releasedAt":721699999}],
     "collectedFinals":["1:3","133:134"],"language":"ja",
     "inventory":{"rareCandy":3,"mint":1,"shinyCharm":1},
     "candyGrantTier":{"claude:seven_day":1},"candyWindowEpoch":{"claude:seven_day":"2026-11-10T00:00:00Z"},
     "candyFeatureSeeded":true}
    """;

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    private static JsonNode Node(byte[] json) => JsonNode.Parse(json)!;

    /// <summary>Set&lt;String&gt; has no order in Swift; compare collectedFinals as sorted arrays.</summary>
    private static JsonNode Normalized(JsonNode node)
    {
        var copy = node.DeepClone();
        if (copy is not JsonObject obj) return copy;
        var state = obj["state"] ?? obj;
        if (state["collectedFinals"] is JsonArray finals)
            state["collectedFinals"] = new JsonArray(finals.Select(f => (string)f!).Order(StringComparer.Ordinal)
                .Select(f => (JsonNode)JsonValue.Create(f)).ToArray());
        return copy;
    }

    private static void AssertSameJson(JsonNode expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(Normalized(expected), Normalized(actual)),
            $"expected:\n{expected.ToJsonString()}\nactual:\n{actual.ToJsonString()}");

    [Fact]
    public void SwiftStateFileDecodesEveryField()
    {
        var s = CompanionJson.Deserialize(SwiftStateFile);

        Assert.True(s.InstallBaselineSet);
        Assert.Equal(5_123_456_789, s.UsedSinceInstall);
        Assert.Equal(1_000_000_000, s.SpentTokens);
        Assert.Equal(new Dictionary<string, long> { ["claude_code"] = 48_000_000, ["codex"] = 0 }, s.ClaimedTodayTokensByProvider);
        Assert.Equal("2026-11-03", s.LastDate);
        var a = Assert.IsType<MonState>(s.Active);
        Assert.Equal([412, 413], a.PathIDs);
        Assert.Equal(1, a.StageIndex);
        Assert.Equal(413, a.CurrentID);
        Assert.Equal(Rarity.Uncommon, a.Rarity);
        Assert.Equal(PokemonNature.Timid, a.Nature);
        Assert.Equal(new PokemonForm("sandy"), a.Form);
        Assert.True(a.IsShiny && a.HasGrowthBoost && !a.DittoRevealed);
        Assert.Null(a.DittoDisguise);
        var p = Assert.IsType<PokemonProfile>(a.Profile);
        Assert.Equal(ulong.MaxValue, p.Seed);
        Assert.Equal(PokemonGender.Female, p.Gender);
        Assert.Equal(new PokemonIVs(31, 0, 15, 7, 22, 30), p.Ivs);
        Assert.Equal("anticipation", p.AbilityName);
        Assert.Equal(42, p.Level);
        Assert.Equal([new PokemonKnownMove("tackle", 1), new PokemonKnownMove("protect", 10)], p.Moves);
        Assert.Equal(3, s.RepresentativeSpeciesID);
        Assert.Equal(2, s.Dex.Count);
        var graduated = s.Dex[0];
        Assert.Equal(Caught.AddMilliseconds(500), graduated.CaughtAt);
        Assert.Equal("フシギダネ", graduated.Names![1]["ja-Hrkt"]);
        Assert.Equal(1, graduated.NamesVersion);
        Assert.Null(graduated.Profile!.Gender);
        Assert.False(graduated.IsReleased);
        var released = s.Dex[1];
        Assert.True(released.IsReleased);
        Assert.Equal(new PokemonForm("question"), released.Form);
        Assert.Null(released.Names);
        Assert.Equal(new HashSet<string> { "1:3", "133:134" }, s.CollectedFinals);
        Assert.Equal(AppLanguage.Ja, s.Language);
        Assert.Equal(3, s.Inventory["rareCandy"]);
        Assert.Equal("2026-11-10T00:00:00Z", s.CandyWindowEpoch["claude:seven_day"]);
        Assert.True(s.CandyFeatureSeeded);
    }

    /// <summary>Decoding a Swift file and writing it again yields the same JSON, so macOS reads what Windows writes.</summary>
    [Fact]
    public void SwiftStateFileRoundTripsToIdenticalJson()
    {
        var reencoded = CompanionJson.Serialize(CompanionJson.Deserialize(SwiftStateFile));
        AssertSameJson(Node(SwiftStateFile), Node(reencoded));
    }

    /// <summary>Swift omits nil optionals (encodeIfPresent); a fresh state writes exactly the non-optional keys.</summary>
    [Fact]
    public void FreshStateWritesSwiftKeySetWithoutNils()
    {
        var json = Node(CompanionJson.Serialize(new CompanionState { Language = AppLanguage.Ko }));
        var expected = Node("""
            {"installBaselineSet":false,"usedSinceInstall":0,"spentTokens":0,"eggUsage":0,"lastDate":"","dex":[],
             "collectedFinals":[],"language":"ko","inventory":{},"candyGrantTier":{},"candyWindowEpoch":{},
             "candyFeatureSeeded":false}
            """);
        AssertSameJson(expected, json);
    }

    /// <summary>An exported save exactly as macOS writes it: ISO 8601 dates, sorted keys, pretty printed.</summary>
    private const string SwiftExport = """
    {
      "appVersion" : "2.6.0",
      "exportedAt" : "2026-11-04T09:30:00Z",
      "format" : "poketokenbar.save",
      "schema" : 2,
      "sourceDevice" : "Bluewind's MacBook Pro",
      "state" : {
        "active" : {
          "baseID" : 25,
          "dittoDisguise" : 25,
          "dittoRevealed" : false,
          "hasGrowthBoost" : false,
          "isShiny" : false,
          "nature" : "jolly",
          "pathIDs" : [
            25
          ],
          "plannedPathIDs" : [
            25,
            26
          ],
          "profile" : {
            "abilityIsHidden" : false,
            "growthTokens" : 12,
            "instanceID" : "X",
            "ivs" : {
              "attack" : 1,
              "defense" : 1,
              "hp" : 1,
              "specialAttack" : 1,
              "specialDefense" : 1,
              "speed" : 1
            },
            "level" : 5,
            "moves" : [

            ],
            "seed" : 9
          },
          "rarity" : "common",
          "stageIndex" : 0,
          "totalForms" : 2,
          "usedAtStage" : 12
        },
        "candyFeatureSeeded" : false,
        "candyGrantTier" : {

        },
        "candyWindowEpoch" : {

        },
        "collectedFinals" : [
          "1:3"
        ],
        "dex" : [
          {
            "baseID" : 1,
            "caughtAt" : "2023-11-14T22:13:20Z",
            "chainOrder" : [
              1,
              2,
              3
            ],
            "finalID" : 3,
            "id" : "E1",
            "isShiny" : false,
            "rarity" : "common",
            "releasedAt" : "2023-11-14T22:13:20Z"
          }
        ],
        "eggUsage" : 0,
        "installBaselineSet" : true,
        "inventory" : {
          "rareCandy" : 2
        },
        "language" : "en",
        "lastDate" : "2026-11-04",
        "spentTokens" : 0,
        "usedSinceInstall" : 9000000000
      }
    }
    """;

    [Fact]
    public void SwiftExportDecodesAndReencodesIdentically()
    {
        var envelope = SaveTransfer.Decode(Encoding.UTF8.GetBytes(SwiftExport));
        Assert.Equal("poketokenbar.save", envelope.Format);
        Assert.Equal(2, envelope.Schema);
        Assert.Equal("Bluewind's MacBook Pro", envelope.SourceDevice);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero), envelope.ExportedAt);
        var s = envelope.State;
        Assert.Equal(25, s.Active!.DittoDisguise);
        Assert.Equal([25, 26], s.Active.PlannedPathIDs);
        Assert.Equal(Caught, s.Dex[0].CaughtAt);
        Assert.True(s.Dex[0].IsReleased);
        Assert.Equal(9_000_000_000, s.UsedSinceInstall);

        // Windows writes the export the same way (ISO dates, same keys), so macOS can read it back.
        var reencoded = SaveTransfer.Encode(s, envelope.AppVersion, envelope.SourceDevice, envelope.ExportedAt);
        // The profile gains nothing, the dex entry's missing namesVersion/profile stay absent: identical JSON.
        AssertSameJson(Node(SwiftExport), Node(reencoded));
        var text = Encoding.UTF8.GetString(reencoded);
        Assert.Contains("\"exportedAt\": \"2026-11-04T09:30:00Z\"", text);
        Assert.True(text.IndexOf("\"appVersion\"", StringComparison.Ordinal) < text.IndexOf("\"format\"", StringComparison.Ordinal),
            "keys are sorted like .sortedKeys");
    }

    /// <summary>A save written on Windows imports on Windows (and therefore on macOS, see the identity test above).</summary>
    [Fact]
    public void WindowsExportRoundTripsThroughDecode()
    {
        var state = CompanionJson.Deserialize(SwiftStateFile);
        var data = SaveTransfer.Encode(state, "2.6.0", "WINBOX", Caught);
        var back = SaveTransfer.Decode(data);
        // One trip is lossless apart from ISO 8601's whole seconds (Swift's .iso8601 truncates the same way)…
        Assert.Equal(Caught, back.State.Dex[0].CaughtAt);
        Assert.Equal(state.Active!.Profile, back.State.Active!.Profile);
        Assert.Equal(state.Dex[0].Names!.Keys, back.State.Dex[0].Names!.Keys);
        // …and from then on it is stable byte for byte.
        Assert.Equal(data, SaveTransfer.Encode(back.State, "2.6.0", "WINBOX", Caught));
    }

    // MARK: legacy formats and migrations the Swift decoders accept

    /// <summary>Saves from before other species had forms used unownForm / pendingUnownForm / representativeUnownForm.</summary>
    [Fact]
    public void LegacyUnownFormKeysAreRead()
    {
        var s = CompanionJson.Deserialize("""
            {"pendingHatchID":201,"pendingUnownForm":"question","representativeSpeciesID":201,"representativeUnownForm":"b",
             "dex":[{"baseID":201,"finalID":201,"chainOrder":[201],"rarity":"uncommon","unownForm":"b"}]}
            """);
        Assert.Equal(new PokemonForm("question"), s.PendingForm);
        Assert.Equal(new PokemonForm("b"), s.RepresentativeForm);
        Assert.Equal(new PokemonForm("b"), s.Dex[0].Form);
        // The current key wins over the legacy one, and is what gets written.
        var json = Node(CompanionJson.Serialize(s));
        Assert.Equal("b", (string)json["dex"]![0]!["form"]!);
        Assert.Null(json["dex"]![0]!["unownForm"]);
    }

    /// <summary>Older Unown records without any form keep their A; species without forms stay null.</summary>
    [Fact]
    public void MissingFormResolvesToLineDefault()
    {
        var s = CompanionJson.Deserialize("""
            {"dex":[{"baseID":201,"finalID":201,"chainOrder":[201],"rarity":"uncommon"},
                    {"baseID":1,"finalID":3,"chainOrder":[1,2,3],"rarity":"common","form":"sandy"}]}
            """);
        Assert.Equal(new PokemonForm("a"), s.Dex[0].Form);
        Assert.Null(s.Dex[1].Form);
    }

    /// <summary>The aggregate claimedTodayTokens of old saves is ignored (null = seed on first update);
    /// a present but broken per-provider map is an empty, already-seeded ledger.</summary>
    [Fact]
    public void ProviderLedgerMigration()
    {
        Assert.Null(CompanionJson.Deserialize("""{"claimedTodayTokens":123}""").ClaimedTodayTokensByProvider);
        Assert.Empty(CompanionJson.Deserialize("""{"claimedTodayTokensByProvider":null}""").ClaimedTodayTokensByProvider!);
        Assert.Empty(CompanionJson.Deserialize("""{"claimedTodayTokensByProvider":{"a":"x"}}""").ClaimedTodayTokensByProvider!);
    }

    /// <summary>Pre-profile (schema 1) saves import; MonState defaults fill the hatch attributes old saves lacked.</summary>
    [Fact]
    public void SchemaOneSaveWithoutProfilesImports()
    {
        var json = """
            {"format":"poketokenbar.save","schema":1,"appVersion":"2.0.0","exportedAt":"2025-01-01T00:00:00Z",
             "sourceDevice":"old","state":{"active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":5,
             "rarity":"common","totalForms":3},"dex":[{"baseID":7,"finalID":9,"chainOrder":[7,8,9],"rarity":"uncommon"}]}}
            """;
        var envelope = SaveTransfer.Decode(Encoding.UTF8.GetBytes(json));
        var a = envelope.State.Active!;
        Assert.Equal([1], a.PlannedPathIDs);
        Assert.False(a.IsShiny);
        Assert.Null(a.Nature);
        Assert.Null(a.Profile);
        Assert.False(a.HasGrowthBoost);
        Assert.Null(envelope.State.Dex[0].CaughtAt);
        Assert.True(envelope.State.Dex[0].NeedsNamesRefresh);
    }

    /// <summary>Per-field leniency: a broken nested value only drops what Swift drops.</summary>
    [Fact]
    public void BrokenFieldsDegradeLikeSwift()
    {
        var s = CompanionJson.Deserialize("""
            {"usedSinceInstall":"lots","eggTier":"mythic","language":"xx",
             "active":{"baseID":1,"pathIDs":[1],"stageIndex":9,"usedAtStage":0,"rarity":"common","totalForms":1,
                       "profile":{"instanceID":"p"}},
             "dex":[{"baseID":1,"finalID":1,"chainOrder":[1],"rarity":"common","names":{"en":"Legacy"}},
                    {"baseID":2,"finalID":2,"chainOrder":[2],"rarity":"common","nature":"grumpy"},
                    null,
                    {"baseID":3,"finalID":3,"chainOrder":[3],"rarity":"common","caughtAt":"yesterday"}],
             "inventory":{"rareCandy":2}}
            """);
        Assert.Equal(0, s.UsedSinceInstall);                // wrong type → default
        Assert.Null(s.EggTier);                             // unknown raw value → no guarantee
        Assert.Equal(AppLanguageExtensions.SystemDefault(), s.Language);
        Assert.Equal(0, s.Active!.StageIndex);              // clamped into pathIDs
        Assert.Null(s.Active.Profile);                      // broken profile alone → null (re-migrated by the store)
        Assert.Single(s.Dex);                               // invalid nature / null / non-numeric date drop that entry
        Assert.Null(s.Dex[0].Names);                        // legacy single-map names → null, entry kept
        Assert.Equal(2, s.Inventory["rareCandy"]);
    }

    /// <summary>An unknown nature fails the whole MonState (Swift's decodeIfPresent throws on a bad raw value).</summary>
    [Fact]
    public void InvalidActiveFallsBackToEgg()
    {
        Assert.Null(CompanionJson.Deserialize("""{"active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1,"nature":"grumpy"}}""").Active);
        Assert.Null(CompanionJson.Deserialize("""{"active":{"baseID":1,"pathIDs":[],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":1}}""").Active);
    }

    [Fact]
    public void NonObjectTopLevelThrows()
    {
        Assert.Throws<SwiftDecodingException>(() => CompanionJson.Deserialize("[]"));
        Assert.Throws<SwiftDecodingException>(() => CompanionJson.Deserialize("not json"));
    }

    /// <summary>Swift's .iso8601 strategy rejects fractional seconds, so such a date breaks the export the same way.</summary>
    [Fact]
    public void ExportDatesFollowSwiftIso8601Rules()
    {
        var withFraction = SwiftExport.Replace("2026-11-04T09:30:00Z", "2026-11-04T09:30:00.123Z");
        var error = Assert.Throws<SaveTransferException>(() => SaveTransfer.Decode(Encoding.UTF8.GetBytes(withFraction)));
        Assert.Equal(SaveTransferErrorKind.NotASaveFile, error.Kind);

        var withOffset = SwiftExport.Replace("2026-11-04T09:30:00Z", "2026-11-04T18:30:00+09:00");
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero),
            SaveTransfer.Decode(Encoding.UTF8.GetBytes(withOffset)).ExportedAt.ToUniversalTime());

        // A sub-second date written on Windows is truncated to whole seconds, which Swift can read.
        var data = SaveTransfer.Encode(new CompanionState(), "1", "d", Caught.AddMilliseconds(999));
        Assert.Contains("\"2023-11-14T22:13:20Z\"", Encoding.UTF8.GetString(data));
    }

    /// <summary>Date encodings of the two Swift strategies.</summary>
    [Fact]
    public void DateEncodings()
    {
        Assert.Equal("721692800", CompanionJson.EncodeDate(Caught, SwiftDateEncoding.DeferredToDate).ToJsonString());
        Assert.Equal("\"2023-11-14T22:13:20Z\"", CompanionJson.EncodeDate(Caught, SwiftDateEncoding.Iso8601).ToJsonString());
    }

    /// <summary>Raw values persisted by both platforms.</summary>
    [Fact]
    public void EnumRawValues()
    {
        Assert.Equal(["common", "uncommon", "rare", "legendary"], Enum.GetValues<Rarity>().Select(r => r.RawValue()));
        Assert.Equal(["rareCandy", "mint", "shinyCharm"], ItemKindExtensions.AllCases.Select(k => k.RawValue()));
        Assert.Equal(["male", "female", "genderless"], Enum.GetValues<PokemonGender>().Select(g => g.RawValue()));
        Assert.Equal(25, PokemonNatureExtensions.AllCases.Length);
        Assert.Equal("hardy", PokemonNatureExtensions.AllCases[0].RawValue());
        Assert.Equal("quirky", PokemonNatureExtensions.AllCases[24].RawValue());
        Assert.Equal(["ko", "en", "ja", "es", "fr", "pt", "de", "ru"], Enum.GetValues<AppLanguage>().Select(l => l.RawValue()));
    }

    /// <summary>The store loads a Swift state file, migrates and rewrites it in a form Swift reads.</summary>
    [Fact]
    public void StoreLoadsSwiftFileAndWritesSwiftReadableFile() => MainActor.Run(() =>
    {
        var path = StateFiles.WriteText(SwiftStateFile);
        var store = new CompanionStore(new StubProvider(Lines.Linear3), clock: () => Caught, filePath: path,
            rng: new SeededRng(7), settings: new AppSettings(null));
        Assert.Equal(413, store.CurrentSpeciesID);
        Assert.Equal(new PokemonForm("sandy"), store.CurrentForm);
        store.SetLanguage(AppLanguage.Ja); // forces a save
        var written = CompanionJson.Deserialize(File.ReadAllBytes(path));
        AssertSameJson(Node(SwiftStateFile)["dex"]![0]!, Node(CompanionJson.Serialize(written))["dex"]![0]!);
        // The pre-profile release record gained a migrated profile, keyed by the record id like Swift's migration.
        Assert.Equal("released-1", written.Dex[1].Profile!.InstanceID);
        Assert.Equal(PokemonProfileMigration.Seed("dex:released-1:201"), written.Dex[1].Profile!.Seed);
        Assert.Equal(ulong.MaxValue, written.Active!.Profile!.Seed);
    });
}
