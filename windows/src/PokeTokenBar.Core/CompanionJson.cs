using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

/// <summary>How a Swift <c>JSONEncoder</c>/<c>JSONDecoder</c> represented <c>Date</c>.</summary>
public enum SwiftDateEncoding
{
    /// <summary>The default (<c>.deferredToDate</c>): seconds since 2001-01-01T00:00:00Z as a JSON number.
    /// Used by companion-state.json and the pre-import backups.</summary>
    DeferredToDate,
    /// <summary><c>.iso8601</c>: "yyyy-MM-ddTHH:mm:ssZ" strings without fractional seconds.
    /// Used by exported saves and snapshots (<see cref="SaveTransfer.Encode"/>).</summary>
    Iso8601,
}

/// <summary>A value did not match what the Swift <c>Decodable</c> implementation accepts.</summary>
public sealed class SwiftDecodingException(string message) : Exception(message);

/// <summary>
/// Reads and writes <see cref="CompanionState"/> exactly like the Swift <c>Codable</c> conformances in
/// CompanionModel.swift / PokemonProfile.swift / PokemonForm.swift, so a save moves between macOS and Windows.
///
/// Swift behaviour reproduced here:
/// <list type="bullet">
/// <item>Encoding is the synthesized one: every stored property under its Swift name, optionals omitted when nil
/// (<c>encodeIfPresent</c>), <c>[Int: …]</c> dictionaries as objects with decimal string keys, sets as arrays.</item>
/// <item><c>CompanionState</c> decoding is lenient per field (a broken field falls back to its default) and isolates
/// dex entries one by one; only a non-object top level throws.</item>
/// <item><c>MonState</c>, <c>DexEntry</c> and <c>PokemonProfile</c> decoding is strict except where the Swift code
/// wraps a field in <c>try?</c> (profile, names, namesVersion, form).</item>
/// </list>
/// </summary>
public static class CompanionJson
{
    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    // ---------------------------------------------------------------- byte-level entry points

    /// <summary>Swift <c>JSONEncoder().encode(state)</c>: compact, <see cref="SwiftDateEncoding.DeferredToDate"/>.</summary>
    public static byte[] Serialize(CompanionState state) =>
        Encoding.UTF8.GetBytes(EncodeState(state, SwiftDateEncoding.DeferredToDate).ToJsonString(Compact));

    /// <summary>Swift <c>JSONDecoder().decode(CompanionState.self, from:)</c>. Throws on invalid JSON or a non-object top level.</summary>
    public static CompanionState Deserialize(ReadOnlySpan<byte> utf8)
    {
        using var doc = Parse(utf8);
        return DecodeState(doc.RootElement, SwiftDateEncoding.DeferredToDate);
    }

    public static CompanionState Deserialize(string json) => Deserialize(Encoding.UTF8.GetBytes(json));

    /// <summary>Compact text of any encoded node (tests build legacy fixtures from it).</summary>
    public static string ToJsonString(JsonNode node, bool prettySortedKeys = false) =>
        prettySortedKeys ? SortedKeys(node)!.ToJsonString(Pretty) : node.ToJsonString(Compact);

    internal static JsonDocument Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException ex)
        {
            throw new SwiftDecodingException("invalid JSON: " + ex.Message);
        }
    }

    /// <summary>Recursively orders object keys, like <c>JSONEncoder.OutputFormatting.sortedKeys</c>.</summary>
    internal static JsonNode? SortedKeys(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, SortedKeys(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(SortedKeys).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    // ---------------------------------------------------------------- encoding

    public static JsonNode EncodeDate(DateTimeOffset date, SwiftDateEncoding dates) => dates switch
    {
        SwiftDateEncoding.Iso8601 => JsonValue.Create(Iso8601Parser.Format(date)),
        _ => JsonValue.Create((date - ReferenceDate).Ticks / (double)TimeSpan.TicksPerSecond),
    };

    public static JsonObject EncodeState(CompanionState s, SwiftDateEncoding dates)
    {
        var o = new JsonObject
        {
            ["installBaselineSet"] = s.InstallBaselineSet,
            ["usedSinceInstall"] = s.UsedSinceInstall,
            ["spentTokens"] = s.SpentTokens,
            ["eggUsage"] = s.EggUsage,
        };
        if (s.EggTier is { } tier) o["eggTier"] = tier.RawValue();
        if (s.PendingHatchID is { } pending) o["pendingHatchID"] = pending;
        if (s.PendingForm is { } pendingForm) o["pendingForm"] = pendingForm.RawValue;
        if (s.ClaimedTodayTokensByProvider is { } claimed) o["claimedTodayTokensByProvider"] = LongMap(claimed);
        o["lastDate"] = s.LastDate;
        if (s.Active is { } active) o["active"] = EncodeMon(active);
        if (s.RepresentativeSpeciesID is { } rep) o["representativeSpeciesID"] = rep;
        if (s.RepresentativeForm is { } repForm) o["representativeForm"] = repForm.RawValue;
        o["dex"] = new JsonArray(s.Dex.Select(e => (JsonNode)EncodeDexEntry(e, dates)).ToArray());
        o["collectedFinals"] = new JsonArray(s.CollectedFinals.Order(StringComparer.Ordinal)
            .Select(f => (JsonNode)JsonValue.Create(f)).ToArray());
        o["language"] = s.Language.RawValue();
        o["inventory"] = IntMap(s.Inventory);
        o["candyGrantTier"] = IntMap(s.CandyGrantTier);
        o["candyWindowEpoch"] = new JsonObject(s.CandyWindowEpoch.Select(kv =>
            KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
        o["candyFeatureSeeded"] = s.CandyFeatureSeeded;
        return o;
    }

    public static JsonObject EncodeMon(MonState m)
    {
        var o = new JsonObject
        {
            ["baseID"] = m.BaseID,
            ["pathIDs"] = IntArray(m.PathIDs),
            ["plannedPathIDs"] = IntArray(m.PlannedPathIDs),
            ["stageIndex"] = m.StageIndex,
            ["usedAtStage"] = m.UsedAtStage,
            ["rarity"] = m.Rarity.RawValue(),
            ["totalForms"] = m.TotalForms,
            ["isShiny"] = m.IsShiny,
        };
        if (m.Nature is { } nature) o["nature"] = nature.RawValue();
        if (m.Form is { } form) o["form"] = form.RawValue;
        if (m.Profile is { } profile) o["profile"] = EncodeProfile(profile);
        o["hasGrowthBoost"] = m.HasGrowthBoost;
        if (m.DittoDisguise is { } disguise) o["dittoDisguise"] = disguise;
        o["dittoRevealed"] = m.DittoRevealed;
        return o;
    }

    public static JsonObject EncodeDexEntry(DexEntry e, SwiftDateEncoding dates)
    {
        var o = new JsonObject
        {
            ["id"] = e.Id,
            ["baseID"] = e.BaseID,
            ["finalID"] = e.FinalID,
            ["chainOrder"] = IntArray(e.ChainOrder),
            ["rarity"] = e.Rarity.RawValue(),
        };
        if (e.CaughtAt is { } caught) o["caughtAt"] = EncodeDate(caught, dates);
        o["isShiny"] = e.IsShiny;
        if (e.Nature is { } nature) o["nature"] = nature.RawValue();
        if (e.Form is { } form) o["form"] = form.RawValue;
        if (e.Profile is { } profile) o["profile"] = EncodeProfile(profile);
        if (e.Names is { } names)
            o["names"] = new JsonObject(names.Select(kv => KeyValuePair.Create(
                kv.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)StringMap(kv.Value))));
        if (e.NamesVersion is { } version) o["namesVersion"] = version;
        if (e.ReleasedAt is { } released) o["releasedAt"] = EncodeDate(released, dates);
        return o;
    }

    public static JsonObject EncodeProfile(PokemonProfile p)
    {
        var o = new JsonObject
        {
            ["instanceID"] = p.InstanceID,
            ["seed"] = p.Seed,
        };
        if (p.Gender is { } gender) o["gender"] = gender.RawValue();
        o["ivs"] = new JsonObject
        {
            ["hp"] = p.Ivs.Hp,
            ["attack"] = p.Ivs.Attack,
            ["defense"] = p.Ivs.Defense,
            ["specialAttack"] = p.Ivs.SpecialAttack,
            ["specialDefense"] = p.Ivs.SpecialDefense,
            ["speed"] = p.Ivs.Speed,
        };
        if (p.AbilitySlot is { } slot) o["abilitySlot"] = slot;
        if (p.AbilityName is { } ability) o["abilityName"] = ability;
        o["abilityIsHidden"] = p.AbilityIsHidden;
        o["level"] = p.Level;
        o["growthTokens"] = p.GrowthTokens;
        o["moves"] = new JsonArray(p.Moves.Select(m => (JsonNode)new JsonObject
        {
            ["name"] = m.Name,
            ["learnedAtLevel"] = m.LearnedAtLevel,
        }).ToArray());
        return o;
    }

    private static JsonArray IntArray(IEnumerable<int> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

    private static JsonObject IntMap(IReadOnlyDictionary<string, int> map) =>
        new(map.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    private static JsonObject LongMap(IReadOnlyDictionary<string, long> map) =>
        new(map.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    private static JsonObject StringMap(IReadOnlyDictionary<string, string> map) =>
        new(map.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    // ---------------------------------------------------------------- decoding

    /// <summary>
    /// Backward-compatible, damage-tolerant decoding: missing keys, type mismatches and partly broken fields
    /// all fall back to defaults, so one broken field never wipes the whole state (Pokédex, inventory).
    /// Only a top level that is not a JSON object throws (the store then backs the file up as .corrupt).
    /// </summary>
    public static CompanionState DecodeState(JsonElement c, SwiftDateEncoding dates)
    {
        RequireObject(c);
        var s = new CompanionState
        {
            InstallBaselineSet = Lenient(() => ReqBool(c, "installBaselineSet"), false),
            UsedSinceInstall = Lenient(() => ReqLong(c, "usedSinceInstall"), 0),
            SpentTokens = Lenient(() => ReqLong(c, "spentTokens"), 0),
            EggUsage = Lenient(() => ReqLong(c, "eggUsage"), 0),
            // An unknown raw value becomes "no guarantee": lenient decoding never invents a guarantee.
            EggTier = Lenient(() => (Rarity?)ReqRarity(c, "eggTier"), null),
            PendingHatchID = Lenient(() => (int?)ReqInt(c, "pendingHatchID"), null),
        };
        s.PendingForm = PokemonForm.ResolvedForLine(s.PendingHatchID ?? 0, DecodedForm(c, "pendingForm", "pendingUnownForm"));
        // The legacy aggregate claimedTodayTokens key is deliberately not read: it cannot be split per
        // provider, so the next CompanionStore.Update seeds the ledger from the current snapshot.
        s.ClaimedTodayTokensByProvider = c.TryGetProperty("claimedTodayTokensByProvider", out _)
            ? Lenient(() => ReqMap(c, "claimedTodayTokensByProvider", ReadLong), new Dictionary<string, long>())
            : null;
        s.LastDate = Lenient(() => ReqString(c, "lastDate"), "");
        // A broken active Pokémon (empty pathIDs, …) falls back to the egg but keeps the Pokédex and inventory.
        s.Active = Lenient(() => DecodeMon(Req(c, "active")), null);
        s.RepresentativeSpeciesID = Lenient(() => (int?)ReqInt(c, "representativeSpeciesID"), null);
        s.RepresentativeForm = PokemonForm.ResolvedForSpecies(s.RepresentativeSpeciesID ?? 0,
            DecodedForm(c, "representativeForm", "representativeUnownForm"));
        // Pokédex entries decode one by one: one broken entry never drops the whole Pokédex.
        s.Dex = Lenient(() =>
        {
            var array = Req(c, "dex");
            if (array.ValueKind != JsonValueKind.Array) throw Mismatch("dex");
            return array.EnumerateArray().Select(e => Lenient(() => DecodeDexEntry(e, dates), null))
                .OfType<DexEntry>().ToList();
        }, []);
        s.CollectedFinals = Lenient(() => ReqArray(c, "collectedFinals", ReadString).ToHashSet(), []);
        s.Language = Lenient(() => AppLanguageRaw(ReqString(c, "language")) ?? throw Mismatch("language"),
            AppLanguageExtensions.SystemDefault());
        s.Inventory = Lenient(() => ReqMap(c, "inventory", ReadInt), []);
        s.CandyGrantTier = Lenient(() => ReqMap(c, "candyGrantTier", ReadInt), []);
        s.CandyWindowEpoch = Lenient(() => ReqMap(c, "candyWindowEpoch", ReadString), []);
        s.CandyFeatureSeeded = Lenient(() => ReqBool(c, "candyFeatureSeeded"), false);
        return s;
    }

    /// <summary>Backward-compatible decoding: hatch attributes missing from old saves get defaults.
    /// Empty pathIDs is corruption and throws, so the whole active Pokémon falls back to the egg.</summary>
    public static MonState DecodeMon(JsonElement c)
    {
        RequireObject(c);
        var baseID = ReqInt(c, "baseID");
        var pathIDs = ReqArray(c, "pathIDs", ReadInt);
        if (pathIDs.Count == 0) throw new SwiftDecodingException("empty pathIDs");
        var savedPlan = OptArray(c, "plannedPathIDs", ReadInt);
        var plannedPathIDs = savedPlan is { Count: > 0 } ? savedPlan : pathIDs.ToList();
        var stageIndex = Math.Min(Math.Max(0, ReqInt(c, "stageIndex")), pathIDs.Count - 1);
        var usedAtStage = ReqLong(c, "usedAtStage");
        var rarity = ReqRarity(c, "rarity");
        var totalForms = ReqInt(c, "totalForms");
        var isShiny = OptBool(c, "isShiny") ?? false;
        var nature = OptNature(c, "nature");
        // One broken new-format profile must not lose the growth state; null is re-migrated by the store.
        var profile = Lenient(() => Opt(c, "profile") is { } p ? DecodeProfile(p) : null, null);
        var hasGrowthBoost = OptBool(c, "hasGrowthBoost") ?? false;
        var dittoDisguise = OptInt(c, "dittoDisguise");
        var dittoRevealed = OptBool(c, "dittoRevealed") ?? false;
        var form = DecodedForm(c, "form", "unownForm");
        var m = new MonState(baseID, pathIDs, plannedPathIDs, stageIndex, usedAtStage, rarity, totalForms, isShiny,
            nature, profile, hasGrowthBoost, dittoDisguise, dittoRevealed, form);
        return m;
    }

    /// <summary>Backward-compatible decoding (same reasons as <see cref="DecodeMon"/>).</summary>
    public static DexEntry DecodeDexEntry(JsonElement c, SwiftDateEncoding dates)
    {
        RequireObject(c);
        var id = OptString(c, "id") ?? Guid.NewGuid().ToString().ToUpperInvariant();
        var baseID = ReqInt(c, "baseID");
        var finalID = ReqInt(c, "finalID");
        var chainOrder = ReqArray(c, "chainOrder", ReadInt);
        var rarity = ReqRarity(c, "rarity");
        var caughtAt = OptDate(c, "caughtAt", dates);
        var isShiny = OptBool(c, "isShiny") ?? false;
        var nature = OptNature(c, "nature");
        // A broken profile alone keeps the record and regenerates the profile.
        var profile = Lenient(() => Opt(c, "profile") is { } p ? DecodeProfile(p) : null, null);
        // try? — a leftover legacy format (a single [String:String] for the final form) demotes to null
        // without failing the entry; the view backfills through a line lookup.
        var names = Lenient(() => Opt(c, "names") is { } n ? ReadNames(n) : null, null);
        var namesVersion = Lenient(() => OptInt(c, "namesVersion"), null);
        // Entries saved before this field are all graduations: null already means "graduated".
        var releasedAt = OptDate(c, "releasedAt", dates);
        var form = DecodedForm(c, "form", "unownForm");
        var entry = new DexEntry(baseID, finalID, chainOrder, rarity, caughtAt, isShiny, nature, profile, names,
            releasedAt, form, id)
        {
            NamesVersion = namesVersion,
        };
        return entry;
    }

    /// <summary>Synthesized <c>Decodable</c>: every non-optional field is required.</summary>
    public static PokemonProfile DecodeProfile(JsonElement c)
    {
        RequireObject(c);
        var ivs = Req(c, "ivs");
        RequireObject(ivs);
        return new PokemonProfile(
            ReqString(c, "instanceID"),
            ReqULong(c, "seed"),
            Opt(c, "gender") is { } g ? PokemonGenderExtensions.FromRawValue(ReadString(g)) ?? throw Mismatch("gender") : null,
            new PokemonIVs(ReqInt(ivs, "hp"), ReqInt(ivs, "attack"), ReqInt(ivs, "defense"),
                ReqInt(ivs, "specialAttack"), ReqInt(ivs, "specialDefense"), ReqInt(ivs, "speed")),
            OptInt(c, "abilitySlot"),
            OptString(c, "abilityName"),
            ReqBool(c, "abilityIsHidden"),
            ReqInt(c, "level"),
            ReqLong(c, "growthTokens"),
            ReqArray(c, "moves", m =>
            {
                RequireObject(m);
                return new PokemonKnownMove(ReqString(m, "name"), ReqInt(m, "learnedAtLevel"));
            }));
    }

    /// <summary>
    /// <c>PokemonForm.decoded(from:keys:)</c>: the first key holding a string wins; saves written before other
    /// species had forms used <c>unownForm</c>. Malformed values read as null so the caller falls back to the default.
    /// </summary>
    private static PokemonForm? DecodedForm(JsonElement c, params string[] keys)
    {
        foreach (var key in keys)
            if (c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return new PokemonForm(v.GetString()!);
        return null;
    }

    public static DateTimeOffset DecodeDate(JsonElement v, SwiftDateEncoding dates)
    {
        if (dates == SwiftDateEncoding.Iso8601)
        {
            // ISO8601DateFormatter(.withInternetDateTime) rejects fractional seconds.
            if (v.ValueKind != JsonValueKind.String) throw Mismatch("date");
            var s = v.GetString()!;
            if (s.Contains('.') || Iso8601Parser.Date(s) is not { } parsed)
                throw new SwiftDecodingException("Expected date string to be ISO8601-formatted.");
            return parsed;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var seconds) || !double.IsFinite(seconds))
            throw Mismatch("date");
        return ReferenceDate.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }

    private static Dictionary<int, Dictionary<string, string>> ReadNames(JsonElement n)
    {
        RequireObject(n);
        var result = new Dictionary<int, Dictionary<string, string>>();
        foreach (var p in n.EnumerateObject())
        {
            if (!int.TryParse(p.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
                throw Mismatch("names key");
            RequireObject(p.Value);
            var byLang = new Dictionary<string, string>();
            foreach (var kv in p.Value.EnumerateObject()) byLang[kv.Name] = ReadString(kv.Value);
            result[id] = byLang;
        }
        return result;
    }

    // ---------------------------------------------------------------- primitive helpers

    internal static T Lenient<T>(Func<T> decode, T fallback)
    {
        try
        {
            return decode();
        }
        catch (SwiftDecodingException)
        {
            return fallback;
        }
    }

    internal static SwiftDecodingException Mismatch(string what) => new($"type mismatch: {what}");

    internal static void RequireObject(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Mismatch("expected an object");
    }

    /// <summary>Present and not null (Swift <c>decode</c> throws on both).</summary>
    internal static JsonElement Req(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
            ? v : throw new SwiftDecodingException($"keyNotFound: {key}");

    /// <summary><c>decodeIfPresent</c>: missing or null → null.</summary>
    internal static JsonElement? Opt(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    internal static long ReadLong(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : throw Mismatch("Int");

    internal static int ReadInt(JsonElement v)
    {
        var l = ReadLong(v);
        return l is >= int.MinValue and <= int.MaxValue ? (int)l : throw Mismatch("Int (out of range)");
    }

    internal static string ReadString(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Mismatch("String");

    internal static bool ReadBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Mismatch("Bool"),
    };

    internal static long ReqLong(JsonElement o, string key) => ReadLong(Req(o, key));
    internal static int ReqInt(JsonElement o, string key) => ReadInt(Req(o, key));
    internal static bool ReqBool(JsonElement o, string key) => ReadBool(Req(o, key));
    internal static string ReqString(JsonElement o, string key) => ReadString(Req(o, key));

    internal static ulong ReqULong(JsonElement o, string key)
    {
        var v = Req(o, key);
        return v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var u) ? u : throw Mismatch("UInt64");
    }

    internal static Rarity ReqRarity(JsonElement o, string key) =>
        RarityExtensions.FromRawValue(ReqString(o, key)) ?? throw Mismatch("Rarity");

    internal static int? OptInt(JsonElement o, string key) => Opt(o, key) is { } v ? ReadInt(v) : null;
    internal static bool? OptBool(JsonElement o, string key) => Opt(o, key) is { } v ? ReadBool(v) : null;
    internal static string? OptString(JsonElement o, string key) => Opt(o, key) is { } v ? ReadString(v) : null;

    private static PokemonNature? OptNature(JsonElement o, string key) =>
        Opt(o, key) is { } v ? PokemonNatureExtensions.FromRawValue(ReadString(v)) ?? throw Mismatch("PokemonNature") : null;

    private static DateTimeOffset? OptDate(JsonElement o, string key, SwiftDateEncoding dates) =>
        Opt(o, key) is { } v ? DecodeDate(v, dates) : null;

    internal static List<T> ReqArray<T>(JsonElement o, string key, Func<JsonElement, T> read) =>
        ReadArray(Req(o, key), read);

    private static List<T>? OptArray<T>(JsonElement o, string key, Func<JsonElement, T> read) =>
        Opt(o, key) is { } v ? ReadArray(v, read) : null;

    private static List<T> ReadArray<T>(JsonElement v, Func<JsonElement, T> read) =>
        v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(read).ToList() : throw Mismatch("Array");

    internal static Dictionary<string, T> ReqMap<T>(JsonElement o, string key, Func<JsonElement, T> read)
    {
        var v = Req(o, key);
        RequireObject(v);
        var result = new Dictionary<string, T>();
        foreach (var p in v.EnumerateObject()) result[p.Name] = read(p.Value);
        return result;
    }

    internal static AppLanguage? AppLanguageRaw(string raw)
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
            if (lang.RawValue() == raw) return lang;
        return null;
    }
}
