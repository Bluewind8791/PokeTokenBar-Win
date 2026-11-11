namespace PokeTokenBar.Core;

// Port of PokemonProfile.swift.

/// <summary>
/// Immutable PokéAPI combat metadata for one default Pokémon form. Cached separately from the user's
/// save; only rolled individual values live in <see cref="PokemonProfile"/>.
/// </summary>
public sealed record PokemonDetails(
    int SpeciesID,
    string Name,
    int Height,                 // PokéAPI decimetres
    int Weight,                 // PokéAPI hectograms
    int? BaseExperience,
    int GenderRate,             // female eighths; -1 = genderless
    IReadOnlyList<string> Types,
    IReadOnlyDictionary<string, int> BaseStats,
    IReadOnlyList<PokemonAbilityOption> Abilities,
    IReadOnlyList<PokemonMoveOption> Moves)
{
    public int BaseStatTotal => BaseStats.Values.Sum();

    /// <summary>Invariant: hatchable species are capped at Gen V by animated-sprite availability.
    /// Keep this learnset selection in sync if that species bound is ever raised.</summary>
    public const string PreferredVersionGroup = "black-2-white-2";

    public List<PokemonKnownMove> LevelUpMoves(int through)
    {
        var bestByName = new Dictionary<string, int>();
        foreach (var move in Moves)
        {
            var matching = move.LearnMethods.Where(m => m.Method == "level-up" && m.Level <= through).ToList();
            if (matching.Count == 0) continue;
            var learnedAt = matching.Min(m => m.Level);
            bestByName[move.Name] = Math.Min(bestByName.TryGetValue(move.Name, out var b) ? b : learnedAt, learnedAt);
        }
        return bestByName.Select(kv => new PokemonKnownMove(kv.Key, kv.Value))
            .OrderBy(m => m.LearnedAtLevel).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    // Records compare collections by reference; the cache tests compare contents.
    public bool Equals(PokemonDetails? other) =>
        other is not null && SpeciesID == other.SpeciesID && Name == other.Name && Height == other.Height
        && Weight == other.Weight && BaseExperience == other.BaseExperience && GenderRate == other.GenderRate
        && Types.SequenceEqual(other.Types)
        && BaseStats.Count == other.BaseStats.Count
        && BaseStats.All(kv => other.BaseStats.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && Abilities.SequenceEqual(other.Abilities) && Moves.SequenceEqual(other.Moves);

    public override int GetHashCode() => HashCode.Combine(SpeciesID, Name);
}

public sealed record PokemonAbilityOption(string Name, int Slot, bool IsHidden);

public sealed record PokemonMoveOption(string Name, IReadOnlyList<PokemonMoveLearnMethod> LearnMethods)
{
    public string Id => Name;
    public bool Equals(PokemonMoveOption? other) =>
        other is not null && Name == other.Name && LearnMethods.SequenceEqual(other.LearnMethods);
    public override int GetHashCode() => Name.GetHashCode();
}

public static class PokemonGenderExtensions
{
    public static string RawValue(this PokemonGender g) => g switch
    {
        PokemonGender.Male => "male",
        PokemonGender.Female => "female",
        _ => "genderless",
    };

    public static PokemonGender? FromRawValue(string? raw) => raw switch
    {
        "male" => PokemonGender.Male,
        "female" => PokemonGender.Female,
        "genderless" => PokemonGender.Genderless,
        _ => null,
    };
}

public record struct PokemonIVs(int Hp, int Attack, int Defense, int SpecialAttack, int SpecialDefense, int Speed)
{
    public readonly int this[string stat] => stat switch
    {
        "hp" => Hp,
        "attack" => Attack,
        "defense" => Defense,
        "special-attack" => SpecialAttack,
        "special-defense" => SpecialDefense,
        "speed" => Speed,
        _ => 0,
    };
}

public readonly record struct PokemonKnownMove(string Name, int LearnedAtLevel)
{
    public string Id => Name;
}

/// <summary>
/// Values that make one caught Pokémon distinct from another.
/// <see cref="Seed"/> keeps deferred/offline enrichment deterministic; explicit results are persisted once resolved.
/// Mutable with value equality and <see cref="Clone"/> (a Swift struct).
/// </summary>
public sealed class PokemonProfile : IEquatable<PokemonProfile>
{
    public string InstanceID { get; set; }
    public ulong Seed { get; set; }
    public PokemonGender? Gender { get; set; }
    public PokemonIVs Ivs { get; set; }
    public int? AbilitySlot { get; set; }
    public string? AbilityName { get; set; }
    public bool AbilityIsHidden { get; set; }
    public int Level { get; set; }
    /// <summary>Earned growth in standard-balance units, independent of difficulty and repeat boosts.</summary>
    public long GrowthTokens { get; set; }
    public List<PokemonKnownMove> Moves { get; set; }

    public PokemonProfile(string instanceID, ulong seed, PokemonGender? gender, PokemonIVs ivs, int? abilitySlot,
        string? abilityName, bool abilityIsHidden, int level, long growthTokens, IEnumerable<PokemonKnownMove> moves)
    {
        InstanceID = instanceID;
        Seed = seed;
        Gender = gender;
        Ivs = ivs;
        AbilitySlot = abilitySlot;
        AbilityName = abilityName;
        AbilityIsHidden = abilityIsHidden;
        Level = level;
        GrowthTokens = growthTokens;
        Moves = moves.ToList();
    }

    public PokemonProfile Clone() =>
        new(InstanceID, Seed, Gender, Ivs, AbilitySlot, AbilityName, AbilityIsHidden, Level, GrowthTokens, Moves);

    public static PokemonProfile Generate(ulong seed, long growthTokens = 0, string? instanceID = null)
    {
        var random = new ProfileRng(seed);
        var ivs = new PokemonIVs(
            Hp: (int)(random.Next() % 32),
            Attack: (int)(random.Next() % 32),
            Defense: (int)(random.Next() % 32),
            SpecialAttack: (int)(random.Next() % 32),
            SpecialDefense: (int)(random.Next() % 32),
            Speed: (int)(random.Next() % 32));
        return new PokemonProfile(instanceID ?? Guid.NewGuid().ToString().ToUpperInvariant(), seed, null, ivs, null,
            null, false, 5, Math.Max(0, growthTokens), []);
    }

    /// <summary>
    /// Ditto's disguise is a different species identity, not an evolution. Keep the individual values,
    /// but reroll every species-dependent field once Ditto's own metadata is available.
    /// </summary>
    public void RebaseForSpeciesIdentity(Rarity from, Rarity to)
    {
        var fraction = Math.Min(1, Math.Max(0, GrowthTokens) / (double)PokemonBalance.GraduationTotal(from));
        var rebasedGrowth = (long)Math.Floor(fraction * PokemonBalance.GraduationTotal(to));
        Gender = null;
        AbilitySlot = null;
        AbilityName = null;
        AbilityIsHidden = false;
        Moves = [];
        GrowthTokens = rebasedGrowth;
        AdvanceGrowth(rebasedGrowth, to);
    }

    public void Enrich(PokemonDetails details)
    {
        var random = new ProfileRng(Seed ^ 0xA11B_1E5D_9EEDUL);
        if (Gender == null)
        {
            if (details.GenderRate < 0) Gender = PokemonGender.Genderless;
            else Gender = (int)(random.Next() % 8) < details.GenderRate ? PokemonGender.Female : PokemonGender.Male;
        }

        var normal = details.Abilities.Where(a => !a.IsHidden).OrderBy(a => a.Slot).ToList();
        var hidden = details.Abilities.Where(a => a.IsHidden).OrderBy(a => a.Slot).ToList();
        if (AbilitySlot == null)
        {
            // Hidden abilities are deliberately rare; regular slots are otherwise evenly selected.
            if (hidden.Count > 0 && random.Next() % 128 == 0)
            {
                AbilitySlot = hidden[(int)(random.Next() % (ulong)hidden.Count)].Slot;
                AbilityIsHidden = true;
            }
            else if (normal.Count > 0)
            {
                AbilitySlot = normal[(int)(random.Next() % (ulong)normal.Count)].Slot;
                AbilityIsHidden = false;
            }
            else if (details.Abilities.OrderBy(a => a.Slot).FirstOrDefault() is { } fallback)
            {
                AbilitySlot = fallback.Slot;
                AbilityIsHidden = fallback.IsHidden;
            }
        }
        var chosen = details.Abilities.FirstOrDefault(a => a.Slot == AbilitySlot && a.IsHidden == AbilityIsHidden)
                     ?? details.Abilities.FirstOrDefault(a => a.Slot == AbilitySlot)
                     ?? normal.FirstOrDefault()
                     ?? hidden.FirstOrDefault();
        AbilityName = chosen?.Name;
        if (chosen != null)
        {
            AbilitySlot = chosen.Slot;
            AbilityIsHidden = chosen.IsHidden;
        }

        // Automatic moveset: the four most recently learned legal level-up moves.
        Moves = details.LevelUpMoves(Level).TakeLast(4).ToList();
    }

    public void ApplyGrowth(long delta, Rarity rarity) => AdvanceGrowth(GrowthTokens + Math.Max(0, delta), rarity);

    /// <summary>Difficulty changes and imports cannot undo an already-earned level.</summary>
    public void AdvanceGrowth(long candidate, Rarity rarity)
    {
        GrowthTokens = Math.Min(SaveTransfer.MaxTokenValue, Math.Max(GrowthTokens, Math.Max(0, candidate)));
        var total = Math.Max(1, PokemonBalance.GraduationTotal(rarity));
        var progress = Math.Min(1, GrowthTokens / (double)total);
        Level = Math.Min(100, Math.Max(Level, Math.Max(5, 5 + (int)Math.Floor(progress * 95))));
    }

    public void Sanitize()
    {
        static int Clamp31(int v) => Math.Min(31, Math.Max(0, v));
        Level = Math.Min(100, Math.Max(5, Level));
        GrowthTokens = Math.Min(SaveTransfer.MaxTokenValue, Math.Max(0, GrowthTokens));
        Ivs = new PokemonIVs(Clamp31(Ivs.Hp), Clamp31(Ivs.Attack), Clamp31(Ivs.Defense), Clamp31(Ivs.SpecialAttack),
            Clamp31(Ivs.SpecialDefense), Clamp31(Ivs.Speed));
        Moves = Moves.Take(4).Select(m => new PokemonKnownMove(
            PrefixCharacters(m.Name, 80), Math.Min(100, Math.Max(0, m.LearnedAtLevel)))).ToList();
        if (InstanceID.Length == 0) InstanceID = Guid.NewGuid().ToString().ToUpperInvariant();
    }

    /// <summary>Swift's <c>String.prefix(80)</c> counts grapheme clusters, not UTF-16 units.</summary>
    private static string PrefixCharacters(string s, int count)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        var taken = 0;
        while (e.MoveNext())
        {
            if (taken == count) return s[..e.ElementIndex];
            taken++;
        }
        return s;
    }

    public bool Equals(PokemonProfile? other) =>
        other is not null && InstanceID == other.InstanceID && Seed == other.Seed && Gender == other.Gender
        && Ivs == other.Ivs && AbilitySlot == other.AbilitySlot && AbilityName == other.AbilityName
        && AbilityIsHidden == other.AbilityIsHidden && Level == other.Level && GrowthTokens == other.GrowthTokens
        && Moves.SequenceEqual(other.Moves);

    public override bool Equals(object? obj) => Equals(obj as PokemonProfile);
    public override int GetHashCode() => HashCode.Combine(InstanceID, Seed, Level, GrowthTokens);
    public static bool operator ==(PokemonProfile? a, PokemonProfile? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(PokemonProfile? a, PokemonProfile? b) => !(a == b);
}

public sealed record PokemonComputedStat(string Name, int Base, int Iv, int Value)
{
    public string Id => Name;
}

public static class PokemonStatCalculator
{
    public static readonly string[] Order = ["hp", "attack", "defense", "special-attack", "special-defense", "speed"];
    /// <summary>Main-series rule: Shedinja's HP is always 1 regardless of level and IVs.</summary>
    public const int ShedinjaSpeciesID = 292;

    public static int DisplayScaleMaximum(IReadOnlyCollection<int> values)
    {
        var highest = Math.Max(300, values.Count == 0 ? 300 : values.Max());
        var remainder = highest % 100;
        return remainder == 0 ? highest : highest + (100 - remainder);
    }

    public static List<PokemonComputedStat> Stats(PokemonDetails details, PokemonProfile profile, PokemonNature? nature)
    {
        var result = new List<PokemonComputedStat>();
        foreach (var name in Order)
        {
            if (!details.BaseStats.TryGetValue(name, out var @base)) continue;
            var iv = profile.Ivs[name];
            var level = profile.Level;
            int value;
            if (name == "hp")
                value = details.SpeciesID == ShedinjaSpeciesID ? 1 : (2 * @base + iv) * level / 100 + level + 10;
            else
            {
                var neutral = (2 * @base + iv) * level / 100 + 5;
                value = (int)Math.Floor(neutral * (nature?.Modifier(name) ?? 1));
            }
            result.Add(new PokemonComputedStat(name, @base, iv, value));
        }
        return result;
    }
}

/// <summary>SplitMix64, identical to the Swift ProfileRNG so seeds give the same individuals on both platforms.</summary>
internal struct ProfileRng
{
    private ulong _state;
    public ProfileRng(ulong seed) => _state = seed == 0 ? 0x9E37_79B9_7F4A_7C15UL : seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return value ^ (value >> 31);
        }
    }
}

public static class PokemonProfileMigration
{
    /// <summary>FNV-1a over UTF-8: stable across processes (unlike Swift's randomized Hasher).</summary>
    public static ulong Seed(string text)
    {
        unchecked
        {
            var hash = 0xcbf2_9ce4_8422_2325UL;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
                hash = (hash ^ b) * 0x0000_0100_0000_01B3UL;
            return hash;
        }
    }
}
