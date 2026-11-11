namespace PokeTokenBar.Core;

// Port of CompanionModel.swift (everything except AppLanguage) and CollectionWeight.swift.
// The persisted types (MonState, DexEntry, CompanionState) are classes with an explicit Clone():
// Swift relies on value semantics ("var s = state; s.x = …" never touches the original), so every
// place that copies a Swift struct and then mutates the copy calls Clone() here.

/// <summary>Display state, decided by usage/burn (sprite motion strength and status line).</summary>
public enum CompanionStateKind { Egg, Idle, Working, Focus, Tired, Sleep, LevelUp }

/// <summary>Injectable randomness, the counterpart of Swift's <c>RandomNumberGenerator</c>.</summary>
public interface IRandomNumberGenerator
{
    ulong Next();
}

/// <summary>Counterpart of <c>SystemRandomNumberGenerator</c>: cryptographically random 64-bit words.</summary>
public sealed class SystemRandomNumberGenerator : IRandomNumberGenerator
{
    public ulong Next()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}

/// <summary>Rarity, decided from PokéAPI capture_rate / is_legendary. Raw values are persisted.</summary>
public enum Rarity { Common, Uncommon, Rare, Legendary }

public static class RarityExtensions
{
    public static string RawValue(this Rarity r) => r switch
    {
        Rarity.Common => "common",
        Rarity.Uncommon => "uncommon",
        Rarity.Rare => "rare",
        _ => "legendary",
    };

    public static Rarity? FromRawValue(string? raw) => raw switch
    {
        "common" => Rarity.Common,
        "uncommon" => Rarity.Uncommon,
        "rare" => Rarity.Rare,
        "legendary" => Rarity.Legendary,
        _ => null,
    };

    /// <summary>
    /// Rank used to compare two rarities (higher = rarer). Not for list ordering: the catch log is
    /// ordered by time and the Pokédex by number. Its only consumer is the premium egg's guarantee
    /// gate in hatch (<c>line.Rarity.SortRank() &lt; tier.SortRank()</c>); flipping the order would
    /// silently let premium eggs hatch a lower rarity.
    /// </summary>
    public static int SortRank(this Rarity r) => (int)r;

    /// <summary>
    /// The capture_rate ceiling of this rarity: at or below it a species is this rarity <b>or higher</b>.
    /// Single source for both <see cref="From"/> and the premium egg candidate filter, so the two
    /// thresholds cannot drift apart. Null for legendary, which capture_rate cannot express (it comes
    /// from is_legendary/is_mythical, which the hatch candidate index does not carry), so no
    /// legendary-only egg can exist. Legendaries all have capture_rate ≤ 45 and therefore naturally
    /// pass the uncommon/rare egg filters.
    /// </summary>
    public static int? CaptureRateCeiling(this Rarity r) => r switch
    {
        Rarity.Rare => 45,
        Rarity.Uncommon => 120,
        Rarity.Common => 255,
        _ => null,
    };

    /// <summary>Whether the capture rate means this rarity or higher. Always false for legendary.</summary>
    public static bool Includes(this Rarity r, int captureRate) =>
        r.CaptureRateCeiling() is { } ceiling && captureRate <= ceiling;

    public static Rarity From(int captureRate, bool isLegendary, bool isMythical)
    {
        if (isLegendary || isMythical) return Rarity.Legendary;
        if (Rarity.Rare.Includes(captureRate)) return Rarity.Rare;
        if (Rarity.Uncommon.Includes(captureRate)) return Rarity.Uncommon;
        return Rarity.Common;
    }
}

/// <summary>
/// Token economy, tuned to the measured average (~253M/day). The graduation total T is the same for a
/// rarity regardless of how many evolution stages the line has. For a k-form line the i-th form costs
/// T·i / (k(k+1)/2), so the sum is T and later stages cost more.
/// </summary>
public static class PokemonBalance
{
    /// <summary>Tokens needed before the egg hatches (anticipation instead of an instant hatch). Overflow carries over.</summary>
    public const long EggHatchThreshold = 5_000_000;
    public const int RepeatGrowthMultiplier = 2;

    public static long GraduationTotal(Rarity rarity) => rarity switch
    {
        Rarity.Common => 750_000_000,
        Rarity.Uncommon => 1_875_000_000,
        Rarity.Rare => 3_000_000_000,
        _ => 6_000_000_000,
    };

    /// <summary>Tokens needed at <paramref name="stageIndex"/> (0-based) to reach the next stage / graduation.</summary>
    public static long PhaseThreshold(Rarity rarity, int totalForms, int stageIndex, int growthMultiplier = 1)
    {
        var kk = Math.Max(1, totalForms);
        var i = stageIndex + 1;
        var total = (double)GraduationTotal(rarity);
        var denom = (double)(kk * (kk + 1)) / 2.0;
        var standardThreshold = (long)Math.Round(total * i / denom, MidpointRounding.AwayFromZero);
        return Math.Max(1, (long)Math.Round(standardThreshold / (double)Math.Max(1, growthMultiplier),
            MidpointRounding.AwayFromZero));
    }

    // Difficulty multipliers (Settings sliders).

    /// <summary>Allowed range of the user multiplier. Below 1 is faster/cheaper, above 1 slower/pricier.</summary>
    public const double DifficultyLowerBound = 0.1;
    public const double DifficultyUpperBound = 2.0;
    /// <summary>At this value every balance constant applies unchanged.</summary>
    public const double DefaultDifficulty = 1.0;

    /// <summary>
    /// Clamps a stored value into range. Settings can be edited outside the app, so 0, negatives and NaN
    /// really occur; a multiplier of 0 would make thresholds 0 (division by zero, a degenerate evolution loop).
    /// </summary>
    public static double ClampDifficulty(double value)
    {
        if (!double.IsFinite(value)) return DefaultDifficulty;
        return Math.Min(Math.Max(value, DifficultyLowerBound), DifficultyUpperBound);
    }

    /// <summary>
    /// Base value × difficulty, for thresholds and prices alike. The constant tables themselves are not
    /// scaled: premium egg prices derive from <see cref="GraduationTotal"/> ratios, so scaling the table
    /// would drag shop prices along with growth. Multiplying at the consumer keeps the two independent.
    /// </summary>
    public static long Scaled(long @base, double difficulty) =>
        (long)Math.Round(@base * ClampDifficulty(difficulty), MidpointRounding.AwayFromZero);

    // Slider position ↔ multiplier (log mapping): equal ratios take equal distance.
    // The default snaps within ±1% of the track.
    private const double DefaultSnapWidth = 0.01;

    /// <summary>Slider position (0…1) → multiplier.</summary>
    public static double Difficulty(double atPosition)
    {
        const double lo = DifficultyLowerBound, hi = DifficultyUpperBound;
        var p = Math.Min(Math.Max(atPosition, 0), 1);
        if (Math.Abs(p - DifficultyPosition(DefaultDifficulty)) < DefaultSnapWidth) return DefaultDifficulty;
        return SnapDifficulty(lo * Math.Pow(hi / lo, p));
    }

    /// <summary>Multiplier → slider position (0…1).</summary>
    public static double DifficultyPosition(double value)
    {
        const double lo = DifficultyLowerBound, hi = DifficultyUpperBound;
        return Math.Log(ClampDifficulty(value) / lo) / Math.Log(hi / lo);
    }

    /// <summary>
    /// Rounds a log-slider value to two significant digits, otherwise values like 1.0473 show up.
    /// Snapping back to the default is <see cref="Difficulty"/>'s job, not this one's.
    /// </summary>
    public static double SnapDifficulty(double value)
    {
        if (!(value > 0)) return DifficultyLowerBound;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        return Math.Round(value / magnitude, MidpointRounding.AwayFromZero) * magnitude;
    }
}

/// <summary>Inventory item kinds. The raw value is the key in <see cref="CompanionState.Inventory"/>.</summary>
public enum ItemKind { RareCandy, Mint, ShinyCharm }

public static class ItemKindExtensions
{
    public static readonly ItemKind[] AllCases = [ItemKind.RareCandy, ItemKind.Mint, ItemKind.ShinyCharm];

    public static string RawValue(this ItemKind k) => k switch
    {
        ItemKind.RareCandy => "rareCandy",
        ItemKind.Mint => "mint",
        _ => "shinyCharm",
    };

    /// <summary>PokéAPI item sprite name (.../sprites/items/{name}.png). Null = no sprite (emoji fallback only).</summary>
    public static string? SpriteName(this ItemKind k) => k switch
    {
        ItemKind.RareCandy => "rare-candy",
        ItemKind.Mint => null, // PokéAPI has no mint sprite (Gen 8 item) → emoji fallback
        _ => "shiny-charm",
    };

    /// <summary>Emoji shown before the sprite loads, or when there is none.</summary>
    public static string FallbackEmoji(this ItemKind k) => k switch
    {
        ItemKind.RareCandy => "🍬",
        ItemKind.Mint => "🌿",
        _ => "✨",
    };

    /// <summary>Shop price (currency = used tokens). Null = not sold.</summary>
    public static long? ShopPrice(this ItemKind k) => k switch
    {
        ItemKind.RareCandy => RareCandy.Price,
        ItemKind.Mint => Mint.Price,
        _ => ShinyCharm.Price,
    };

    /// <summary>Passive items work while held instead of being consumed: bought once, shown as "active" in the bag.</summary>
    public static bool IsPassive(this ItemKind k) => k == ItemKind.ShinyCharm;
}

/// <summary>Rare Candy balance constants.</summary>
public static class RareCandy
{
    /// <summary>
    /// XP injected into the current Pokémon per candy. At the default difficulty on a first hatch the
    /// overflow carried past one stage (&lt;100M) is smaller than the next threshold (125M), so a single
    /// candy advances at most one stage; lower difficulty or the repeat-hatch boost can advance more.
    /// </summary>
    public const long Xp = 100_000_000;
    /// <summary>Candies granted when a weekly limit reaches 100% (session windows grant one).</summary>
    public const int WeeklyGrant = 5;
    /// <summary>
    /// Shop price, 5× the XP value. Tokens are both the growth meter and the wallet, so pricing a candy
    /// at its XP would make buying free extra growth. At 500M the effective bonus is held to +20%, and
    /// the free limit-reward candy is always worth more than its price.
    /// </summary>
    public const long Price = 500_000_000;
}

/// <summary>Mint balance constants.</summary>
public static class Mint
{
    /// <summary>
    /// Shop price. Changing nature is purely cosmetic, so this is a "feel" value: 1/5 of a candy, cheap
    /// enough to reroll until the nature feels right. It grants no growth, so no double counting.
    /// </summary>
    public const long Price = 100_000_000;
}

/// <summary>Shiny Charm balance constants: passive (bought once, permanent, never consumed).</summary>
public static class ShinyCharm
{
    /// <summary>Shop price: a permanent luck upgrade for every future hatch, priced like a rare graduation (3B).</summary>
    public const long Price = 3_000_000_000;
    /// <summary>Shiny hatch denominator while held: 1/64 → 1/48 (+33%). ×2 (1/32) was too much. Not retroactive.</summary>
    public const ulong ShinyDenominator = 48;
}

/// <summary>Fresh Egg (reroll) balance constants: buying one discards the current Pokémon for a new egg.</summary>
public static class FreshEgg
{
    /// <summary>
    /// Shop price: a premium for rerolling an unwanted hatch. The released Pokémon does not graduate, so
    /// collected finals and odds are unaffected; the new egg must incubate again (5M) and growth is lost,
    /// which naturally limits farming.
    /// </summary>
    public const long Price = 1_000_000_000;

    /// <summary>
    /// Eggs on sale: no guarantee, uncommon or better, rare or better (null = no guarantee).
    /// No legendary-only egg: capture_rate cannot express that floor, and the top rarity is never a sure
    /// product. Legendaries still come out of uncommon/rare eggs at their natural weight (~10% for rare).
    /// </summary>
    public static readonly Rarity?[] ShopTiers = [null, Rarity.Uncommon, Rarity.Rare];

    /// <summary>
    /// Price of a guaranteed egg, scaled by the existing graduation-total table (1 : 2.5 : 4 → 1B / 2.5B / 4B).
    /// Scaling by probability instead would make two uncommon eggs beat one rare egg on every axis and
    /// leave the higher tier strictly dominated.
    /// </summary>
    public static long PriceGuaranteeing(Rarity? tier)
    {
        if (tier is not { } t) return Price;
        var multiplier = (double)PokemonBalance.GraduationTotal(t) / PokemonBalance.GraduationTotal(Rarity.Common);
        return (long)Math.Round(Price * multiplier, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// One shop row: an item for sale or an egg reroll (an instant action, so not an ItemKind).
/// For eggs, <see cref="EggTier"/> is the guaranteed rarity floor (null = the plain egg).
/// </summary>
public readonly record struct ShopEntry
{
    public ItemKind? Item { get; private init; }
    public bool IsEgg { get; private init; }
    public Rarity? EggTier { get; private init; }

    public static ShopEntry ForItem(ItemKind kind) => new() { Item = kind };
    public static ShopEntry ForEgg(Rarity? tier) => new() { IsEgg = true, EggTier = tier };

    public long Price => IsEgg ? FreshEgg.PriceGuaranteeing(EggTier) : Item!.Value.ShopPrice() ?? 0;
}

/// <summary>Limit window class for candy grants: session = 1 candy, weekly = <see cref="RareCandy.WeeklyGrant"/>.</summary>
public enum WindowClass { Session, Weekly }

/// <summary>Candy grant input: one provider-independent limit window (built by UsageStore.candyEligibleWindows).</summary>
/// <param name="Key">Stable identifier for tier tracking; volatile fields such as resets_at never go in the key.</param>
/// <param name="Name">Display name (why the candy was granted).</param>
/// <param name="Utilization">0…100+.</param>
/// <param name="Epoch">Window epoch (resets_at / reset unix). The key stays stable; a new epoch re-arms (#326).</param>
public sealed record CandyWindow(string Key, string Name, WindowClass Kind, double Utilization, string? Epoch = null);

/// <summary>One candy grant (pure decision), kept apart from side effects so it can be tested.</summary>
public sealed record CandyGrant(string WindowKey, string WindowName, int Count);

/// <summary>Animated sprites exist for national dex #1…649 only (PokéAPI Gen-V animated assets).</summary>
public static class PokemonAssets
{
    public const int AnimatedSpeciesMin = 1;
    public const int AnimatedSpeciesMax = 649;

    public static bool HasAnimatedSprite(int speciesID) =>
        speciesID >= AnimatedSpeciesMin && speciesID <= AnimatedSpeciesMax;
}

/// <summary>Parsed PokéAPI evolution chain. Branches (several evolves_to) become children.</summary>
public sealed class EvoNode
{
    public int SpeciesID { get; }
    public IReadOnlyList<EvoNode> Children { get; }

    public EvoNode(int speciesID, IReadOnlyList<EvoNode>? children = null)
    {
        SpeciesID = speciesID;
        Children = children ?? [];
    }

    /// <summary>Longest path length (number of forms). Branches are usually equally deep.</summary>
    public int Depth => 1 + (Children.Count == 0 ? 0 : Children.Max(c => c.Depth));

    public EvoNode? Node(int withID)
    {
        if (SpeciesID == withID) return this;
        foreach (var c in Children)
            if (c.Node(withID) is { } f) return f;
        return null;
    }

    /// <summary>Every final form reachable from this node.</summary>
    public List<int> FinalIDs => Children.Count == 0 ? [SpeciesID] : Children.SelectMany(c => c.FinalIDs).ToList();

    /// <summary>The tree restricted to species with GIF assets; an unsupported species drops its whole subtree.</summary>
    public EvoNode? KeepingAnimatedSprites()
    {
        if (!PokemonAssets.HasAnimatedSprite(SpeciesID)) return null;
        return new EvoNode(SpeciesID, Children.Select(c => c.KeepingAnimatedSprites()).OfType<EvoNode>().ToList());
    }
}

/// <summary>An evolution-line slot: a species, or the hidden "?" before a branch.</summary>
public readonly record struct EvoLineItemContent(int? SpeciesID)
{
    public static EvoLineItemContent Species(int id) => new(id);
    public static readonly EvoLineItemContent Mystery = new((int?)null);
    public bool IsMystery => SpeciesID == null;
}

public enum EvoLineItemState { Done, Current, Future }

public readonly record struct EvoLineItem(EvoLineItemContent Content, EvoLineItemState State);

/// <summary>Line data fixed at hatch: tree + rarity + multilingual names.</summary>
public sealed class EvoLine
{
    public int BaseID { get; }
    public EvoNode Tree { get; }
    public Rarity Rarity { get; }
    /// <summary>speciesID → (langCode → name)</summary>
    public IReadOnlyDictionary<int, Dictionary<string, string>> Names { get; }
    public int TotalForms => Tree.Depth;

    public EvoLine(int baseID, EvoNode tree, Rarity rarity, IReadOnlyDictionary<int, Dictionary<string, string>> names)
    {
        BaseID = baseID;
        Tree = tree.KeepingAnimatedSprites() ?? new EvoNode(baseID);
        Rarity = rarity;
        Names = names;
    }

    /// <summary>The fallback order lives in <c>AppLanguage.ResolveName</c> only.</summary>
    public string LocalizedName(int id, AppLanguage lang) =>
        lang.ResolveName(Names.TryGetValue(id, out var n) ? n : []) ?? $"#{id}";
}

/// <summary>Nature: the 25 main-series natures. Fixed at hatch; display identity only.</summary>
public enum PokemonNature
{
    Hardy, Lonely, Brave, Adamant, Naughty,
    Bold, Docile, Relaxed, Impish, Lax,
    Timid, Hasty, Serious, Jolly, Naive,
    Modest, Mild, Quiet, Bashful, Rash,
    Calm, Gentle, Sassy, Careful, Quirky,
}

public static class PokemonNatureExtensions
{
    /// <summary>Declaration order matters: hatch rolls index into it.</summary>
    public static readonly PokemonNature[] AllCases = Enum.GetValues<PokemonNature>();

    public static string RawValue(this PokemonNature n) => n.ToString().ToLowerInvariant();

    public static PokemonNature? FromRawValue(string? raw)
    {
        foreach (var n in AllCases)
            if (n.RawValue() == raw) return n;
        return null;
    }

    /// <summary>
    /// Official translated names (ko/en/ja/es/fr/de). pt is an exception: the main series has no Portuguese
    /// release, so these are in-house names matching "natureza" (feminine), chosen not to collide.
    /// ru is also an exception: no official source was found, so community translations (Russian Pixelmon
    /// wiki) in the masculine to match "характер"; Lax and Gentle were corrected for meaning.
    /// </summary>
    public static string Name(this PokemonNature n, AppLanguage lang)
    {
        string[] names = n switch
        {
            PokemonNature.Hardy => ["노력", "Hardy", "がんばりや", "Fuerte", "Hardi", "Esforçada", "Robust", "Выносливый"],
            PokemonNature.Lonely => ["외로움", "Lonely", "さみしがり", "Huraña", "Solo", "Carente", "Solo", "Одинокий"],
            PokemonNature.Brave => ["용감", "Brave", "ゆうかん", "Audaz", "Brave", "Corajosa", "Mutig", "Храбрый"],
            PokemonNature.Adamant => ["고집", "Adamant", "いじっぱり", "Firme", "Rigide", "Teimosa", "Hart", "Непреклонный"],
            PokemonNature.Naughty => ["개구쟁이", "Naughty", "やんちゃ", "Pícara", "Mauvais", "Levada", "Frech", "Непослушный"],
            PokemonNature.Bold => ["대담", "Bold", "ずぶとい", "Osada", "Assuré", "Ousada", "Kühn", "Смелый"],
            PokemonNature.Docile => ["온순", "Docile", "すなお", "Dócil", "Docile", "Dócil", "Sanft", "Послушный"],
            PokemonNature.Relaxed => ["무사태평", "Relaxed", "のんき", "Plácida", "Relax", "Descontraída", "Locker", "Расслабленный"],
            PokemonNature.Impish => ["장난꾸러기", "Impish", "わんぱく", "Agitada", "Malin", "Travessa", "Pfiffig", "Проказливый"],
            PokemonNature.Lax => ["촐랑", "Lax", "のうてんき", "Floja", "Lâche", "Despreocupada", "Lasch", "Беспечный"],
            PokemonNature.Timid => ["겁쟁이", "Timid", "おくびょう", "Miedosa", "Timide", "Medrosa", "Scheu", "Робкий"],
            PokemonNature.Hasty => ["성급", "Hasty", "せっかち", "Activa", "Pressé", "Apressada", "Hastig", "Торопливый"],
            PokemonNature.Serious => ["성실", "Serious", "まじめ", "Seria", "Sérieux", "Séria", "Ernst", "Серьёзный"],
            PokemonNature.Jolly => ["명랑", "Jolly", "ようき", "Alegre", "Jovial", "Alegre", "Froh", "Весёлый"],
            PokemonNature.Naive => ["천진난만", "Naive", "むじゃき", "Ingenua", "Naïf", "Ingênua", "Naiv", "Наивный"],
            PokemonNature.Modest => ["조심", "Modest", "ひかえめ", "Modesta", "Modeste", "Modesta", "Mäßig", "Скромный"],
            PokemonNature.Mild => ["의젓", "Mild", "おっとり", "Afable", "Doux", "Meiga", "Mild", "Мягкий"],
            PokemonNature.Quiet => ["냉정", "Quiet", "れいせい", "Mansa", "Discret", "Discreta", "Ruhig", "Тихий"],
            PokemonNature.Bashful => ["수줍음", "Bashful", "てれや", "Tímida", "Pudique", "Tímida", "Zaghaft", "Застенчивый"],
            PokemonNature.Rash => ["덜렁", "Rash", "うっかりや", "Alocada", "Foufou", "Impulsiva", "Hitzig", "Опрометчивый"],
            PokemonNature.Calm => ["차분", "Calm", "おだやか", "Serena", "Calme", "Calma", "Still", "Спокойный"],
            PokemonNature.Gentle => ["얌전", "Gentle", "おとなしい", "Amable", "Gentil", "Gentil", "Zart", "Кроткий"],
            PokemonNature.Sassy => ["건방", "Sassy", "なまいき", "Grosera", "Malpoli", "Atrevida", "Forsch", "Нахальный"],
            PokemonNature.Careful => ["신중", "Careful", "しんちょう", "Cauta", "Prudent", "Cautelosa", "Sacht", "Внимательный"],
            _ => ["변덕", "Quirky", "きまぐれ", "Rara", "Bizarre", "Excêntrica", "Kauzig", "Ловкий"],
        };
        return names[lang switch
        {
            AppLanguage.Ko => 0, AppLanguage.En => 1, AppLanguage.Ja => 2, AppLanguage.Es => 3,
            AppLanguage.Fr => 4, AppLanguage.Pt => 5, AppLanguage.De => 6, _ => 7,
        }];
    }

    /// <summary>Main-series nature modifiers. Neutral natures return 1.0 for every stat.</summary>
    public static double Modifier(this PokemonNature n, string stat)
    {
        (string Up, string Down)? pair = n switch
        {
            PokemonNature.Lonely => ("attack", "defense"),
            PokemonNature.Brave => ("attack", "speed"),
            PokemonNature.Adamant => ("attack", "special-attack"),
            PokemonNature.Naughty => ("attack", "special-defense"),
            PokemonNature.Bold => ("defense", "attack"),
            PokemonNature.Relaxed => ("defense", "speed"),
            PokemonNature.Impish => ("defense", "special-attack"),
            PokemonNature.Lax => ("defense", "special-defense"),
            PokemonNature.Timid => ("speed", "attack"),
            PokemonNature.Hasty => ("speed", "defense"),
            PokemonNature.Jolly => ("speed", "special-attack"),
            PokemonNature.Naive => ("speed", "special-defense"),
            PokemonNature.Modest => ("special-attack", "attack"),
            PokemonNature.Mild => ("special-attack", "defense"),
            PokemonNature.Quiet => ("special-attack", "speed"),
            PokemonNature.Rash => ("special-attack", "special-defense"),
            PokemonNature.Calm => ("special-defense", "attack"),
            PokemonNature.Gentle => ("special-defense", "defense"),
            PokemonNature.Sassy => ("special-defense", "speed"),
            PokemonNature.Careful => ("special-defense", "special-attack"),
            _ => null,
        };
        if (pair is not { } p) return 1;
        if (stat == p.Up) return 1.1;
        if (stat == p.Down) return 0.9;
        return 1;
    }
}

/// <summary>Game balance: individual roll odds.</summary>
public static class PokemonOdds
{
    /// <summary>Shiny hatch denominator, 1/64 (the main series' 1/4096 would never show in a desktop app).</summary>
    public const ulong ShinyDenominator = 64;
    /// <summary>Ditto disguise denominator: 1/128, only for common hatches of lines with ≥2 forms.</summary>
    public const ulong DittoDisguiseDenominator = 128;
    /// <summary>Ditto's species id: reveal only, excluded from the normal hatch pool.</summary>
    public const int DittoSpeciesID = 132;
}

/// <summary>Shared duplicate reduction for species and form selection. Every candidate stays selectable.</summary>
public static class CollectionWeight
{
    public static int Adjusted(int weight, bool isCollected) =>
        isCollected ? Math.Max(1, weight / 2) : Math.Max(1, weight);
}

/// <summary>The Pokémon currently being raised.</summary>
public sealed class MonState
{
    public int BaseID { get; set; }
    /// <summary>The evolution path actually taken (branch choices applied).</summary>
    public List<int> PathIDs { get; set; }
    /// <summary>The full path chosen in advance.</summary>
    public List<int> PlannedPathIDs { get; set; }
    /// <summary>Current position within <see cref="PathIDs"/>.</summary>
    public int StageIndex { get; set; }
    /// <summary>Usage accumulated in the current form.</summary>
    public long UsedAtStage { get; set; }
    public Rarity Rarity { get; set; }
    public int TotalForms { get; set; }
    /// <summary>Fixed at hatch, kept through evolution.</summary>
    public bool IsShiny { get; set; }
    /// <summary>Fixed at hatch (null in old saves).</summary>
    public PokemonNature? Nature { get; set; }
    /// <summary>Chosen at hatch for the whole line and resolved per species (<see cref="PokemonForm.ResolvedForSpecies"/>).</summary>
    public PokemonForm? Form { get; set; }
    /// <summary>The individual's combat profile. Null in old saves; <c>CompanionStore</c> migrates it once.</summary>
    public PokemonProfile? Profile { get; set; }
    public bool HasGrowthBoost { get; set; }
    /// <summary>Ditto disguise: null = normal. A value = actually Ditto, disguised as this species
    /// (equal to BaseID while disguised; kept after the reveal).</summary>
    public int? DittoDisguise { get; set; }
    /// <summary>Whether the disguise has been revealed.</summary>
    public bool DittoRevealed { get; set; }

    /// <summary>Whether the screen may show it shiny: a disguised Ditto hides it until revealed.
    /// Every display path uses this.</summary>
    public bool DisplaysShiny => IsShiny && (DittoDisguise == null || DittoRevealed);

    /// <summary>Falls back to BaseID when PathIDs is empty (corrupt state file): read on every render,
    /// so it must never index out of bounds.</summary>
    public int CurrentID => PathIDs.Count == 0 ? BaseID : PathIDs[Math.Max(0, Math.Min(StageIndex, PathIDs.Count - 1))];

    public long PhaseThreshold => PokemonBalance.PhaseThreshold(Rarity, TotalForms, StageIndex,
        HasGrowthBoost ? PokemonBalance.RepeatGrowthMultiplier : 1);

    public MonState(int baseID, IEnumerable<int> pathIDs, IEnumerable<int>? plannedPathIDs = null, int stageIndex = 0,
        long usedAtStage = 0, Rarity rarity = Rarity.Common, int totalForms = 1, bool isShiny = false,
        PokemonNature? nature = null, PokemonProfile? profile = null, bool hasGrowthBoost = false,
        int? dittoDisguise = null, bool dittoRevealed = false, PokemonForm? form = null)
    {
        BaseID = baseID;
        PathIDs = pathIDs.ToList();
        var planned = plannedPathIDs?.ToList();
        PlannedPathIDs = planned is { Count: > 0 } ? planned : PathIDs.ToList();
        StageIndex = stageIndex;
        UsedAtStage = usedAtStage;
        Rarity = rarity;
        TotalForms = totalForms;
        IsShiny = isShiny;
        Nature = nature;
        Profile = profile;
        HasGrowthBoost = hasGrowthBoost;
        DittoDisguise = dittoDisguise;
        DittoRevealed = dittoRevealed;
        Form = PokemonForm.ResolvedForLine(baseID, form);
    }

    private MonState(MonState o)
    {
        BaseID = o.BaseID;
        PathIDs = o.PathIDs.ToList();
        PlannedPathIDs = o.PlannedPathIDs.ToList();
        StageIndex = o.StageIndex;
        UsedAtStage = o.UsedAtStage;
        Rarity = o.Rarity;
        TotalForms = o.TotalForms;
        IsShiny = o.IsShiny;
        Nature = o.Nature;
        Form = o.Form;
        Profile = o.Profile?.Clone();
        HasGrowthBoost = o.HasGrowthBoost;
        DittoDisguise = o.DittoDisguise;
        DittoRevealed = o.DittoRevealed;
    }

    public MonState Clone() => new(this);
}

/// <summary>Pokédex record: the whole line (first → final) in order.</summary>
public sealed class DexEntry
{
    /// <summary>Version 1 preserves every API language; earlier saves kept only app-supported names.</summary>
    public const int CurrentNamesVersion = 1;

    public string Id { get; set; }
    public int BaseID { get; set; }
    public int FinalID { get; set; }
    /// <summary>First → final species ids.</summary>
    public List<int> ChainOrder { get; set; }
    public Rarity Rarity { get; set; }
    public DateTimeOffset? CaughtAt { get; set; }
    public bool IsShiny { get; set; }
    public PokemonNature? Nature { get; set; }
    public PokemonForm? Form { get; set; }
    /// <summary>The profile at graduation/release. Null only for pre-profile saves until migration.</summary>
    public PokemonProfile? Profile { get; set; }
    /// <summary>
    /// Multilingual names of every chain species (speciesID → langCode → name), stored at graduation so the
    /// per-stage names show instantly offline and follow language changes. Absent in old saves; the view
    /// backfills them through a line lookup.
    /// </summary>
    public Dictionary<int, Dictionary<string, string>>? Names { get; set; }
    public int? NamesVersion { get; set; }
    /// <summary>
    /// When it was released (a new egg bought instead of finishing). Null = graduated (including old saves).
    /// Both kinds live in one list because the Pokédex folds owned species however they were obtained;
    /// leaving releases out would make species disappear from a screen that promises to only grow.
    /// </summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    public bool NeedsNamesRefresh =>
        NamesVersion != CurrentNamesVersion
        || ChainOrder.Any(id => Names == null || !Names.TryGetValue(id, out var n) || n.Count == 0);

    /// <summary>A release record rather than a graduation: the catch log badges on this.</summary>
    public bool IsReleased => ReleasedAt != null;

    public DexEntry(int baseID, int finalID, IEnumerable<int> chainOrder, Rarity rarity, DateTimeOffset? caughtAt,
        bool isShiny = false, PokemonNature? nature = null, PokemonProfile? profile = null,
        Dictionary<int, Dictionary<string, string>>? names = null, DateTimeOffset? releasedAt = null,
        PokemonForm? form = null, string? id = null)
    {
        Id = id ?? Guid.NewGuid().ToString().ToUpperInvariant();
        BaseID = baseID;
        FinalID = finalID;
        ChainOrder = chainOrder.ToList();
        Rarity = rarity;
        CaughtAt = caughtAt;
        IsShiny = isShiny;
        Nature = nature;
        Profile = profile;
        Names = names;
        NamesVersion = ChainOrder.All(c => names != null && names.TryGetValue(c, out var n) && n.Count > 0)
            ? CurrentNamesVersion : null;
        ReleasedAt = releasedAt;
        Form = PokemonForm.ResolvedForLine(baseID, form);
    }

    private DexEntry(DexEntry o)
    {
        Id = o.Id;
        BaseID = o.BaseID;
        FinalID = o.FinalID;
        ChainOrder = o.ChainOrder.ToList();
        Rarity = o.Rarity;
        CaughtAt = o.CaughtAt;
        IsShiny = o.IsShiny;
        Nature = o.Nature;
        Form = o.Form;
        Profile = o.Profile?.Clone();
        Names = CloneNames(o.Names);
        NamesVersion = o.NamesVersion;
        ReleasedAt = o.ReleasedAt;
    }

    public DexEntry Clone() => new(this);

    internal static Dictionary<int, Dictionary<string, string>>? CloneNames(
        IReadOnlyDictionary<int, Dictionary<string, string>>? names) =>
        names?.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
}

/// <summary>Persisted state (companion-state.json).</summary>
public sealed class CompanionState
{
    // Tokens: measured since install only.
    public bool InstallBaselineSet { get; set; }
    public long UsedSinceInstall { get; set; }
    /// <summary>Shop spending ledger. Spendable = UsedSinceInstall − SpentTokens. Purchases only raise this,
    /// so the growth meter never rewinds.</summary>
    public long SpentTokens { get; set; }
    /// <summary>Tokens used since the current egg appeared (incubation); 0 for every new egg.</summary>
    public long EggUsage { get; set; }
    /// <summary>
    /// Rarity floor the current (premium) egg guarantees; null = none. Must be persisted: the species
    /// cannot be rolled at purchase (the roll needs the network), so the roll reads the guarantee from
    /// here across offline periods and restarts. Consumed (null) at hatch/graduation.
    /// </summary>
    public Rarity? EggTier { get; set; }
    /// <summary>Species pre-rolled while still an egg (prefetch), so hatching has no network delay. Survives restarts.</summary>
    public int? PendingHatchID { get; set; }
    /// <summary>The form is chosen with the species so prefetch warms the exact sprite that will hatch.</summary>
    public PokemonForm? PendingForm { get; set; }
    /// <summary>
    /// Today's per-provider crediting baselines, keyed by <c>UsageProvider.id</c>.
    /// Null means an old save that only had the aggregate <c>claimedTodayTokens</c> and has not been seeded
    /// from a first valid snapshot yet: the first update only records baselines, never retroactive usage.
    /// An empty map (seeded, no provider reported today) must stay distinct from null.
    /// </summary>
    public Dictionary<string, long>? ClaimedTodayTokensByProvider { get; set; }
    public string LastDate { get; set; } = "";
    /// <summary>Current Pokémon (null = egg).</summary>
    public MonState? Active { get; set; }
    /// <summary>
    /// Species pinned to the menu bar and floating pet; null follows the raised Pokémon (or egg). A species
    /// choice, so it carries no individual data. Selectable range = the Pokédex (graduated + reached stages
    /// of the current one); <see cref="ReconcileRepresentativeSelection"/> resets it to null when it leaves that range.
    /// </summary>
    public int? RepresentativeSpeciesID { get; set; }
    public PokemonForm? RepresentativeForm { get; set; }
    public List<DexEntry> Dex { get; set; } = [];
    /// <summary>Owned "base:final" pairs, for branch diversity.</summary>
    public HashSet<string> CollectedFinals { get; set; } = [];
    /// <summary>New installs follow the system locale.</summary>
    public AppLanguage Language { get; set; } = AppLanguageExtensions.SystemDefault();
    /// <summary>ItemKind raw value → count.</summary>
    public Dictionary<string, int> Inventory { get; set; } = [];
    /// <summary>Candy grant edge state (window key → granted tier). Persisted so restarts cannot grant forever.</summary>
    public Dictionary<string, int> CandyGrantTier { get; set; } = [];
    /// <summary>Window key → last seen epoch (resets_at). Re-arms on a new epoch even if util stays at 100% (#326).</summary>
    public Dictionary<string, string> CandyWindowEpoch { get; set; } = [];
    /// <summary>First-run seeding done: windows already at 100% right after the update grant nothing retroactively.</summary>
    public bool CandyFeatureSeeded { get; set; }

    public CompanionState() { }

    public CompanionState Clone() => new()
    {
        InstallBaselineSet = InstallBaselineSet,
        UsedSinceInstall = UsedSinceInstall,
        SpentTokens = SpentTokens,
        EggUsage = EggUsage,
        EggTier = EggTier,
        PendingHatchID = PendingHatchID,
        PendingForm = PendingForm,
        ClaimedTodayTokensByProvider = ClaimedTodayTokensByProvider == null ? null : new(ClaimedTodayTokensByProvider),
        LastDate = LastDate,
        Active = Active?.Clone(),
        RepresentativeSpeciesID = RepresentativeSpeciesID,
        RepresentativeForm = RepresentativeForm,
        Dex = Dex.Select(d => d.Clone()).ToList(),
        CollectedFinals = [.. CollectedFinals],
        Language = Language,
        Inventory = new(Inventory),
        CandyGrantTier = new(CandyGrantTier),
        CandyWindowEpoch = new(CandyWindowEpoch),
        CandyFeatureSeeded = CandyFeatureSeeded,
    };

    private static IEnumerable<int> ReachedPath(MonState a) => a.PathIDs.Take(a.StageIndex + 1);

    /// <summary>
    /// Whether a graduation record or a stage the current Pokémon actually reached includes this species.
    /// A light path that checks one species without building the whole Pokédex model.
    /// </summary>
    public bool OwnsSpecies(int speciesID, PokemonForm? form = null)
    {
        var resolved = PokemonForm.ResolvedForSpecies(speciesID, form);
        if (Dex.Any(e => e.ChainOrder.Contains(speciesID)
                         && PokemonForm.ResolvedForSpecies(speciesID, e.Form) == resolved)) return true;
        if (Active is not { } a) return false;
        return ReachedPath(a).Contains(speciesID) && PokemonForm.ResolvedForSpecies(speciesID, a.Form) == resolved;
    }

    public bool HasCollectedFinal(int forBaseID) => CollectedFinals.Any(f => f.StartsWith($"{forBaseID}:", StringComparison.Ordinal));

    /// <summary>Matches Pokédex ownership, including the active Pokémon and released catch records.
    /// Normal and shiny individuals of the same form contribute once.</summary>
    public HashSet<PokemonForm> CollectedForms(int speciesID)
    {
        if (!PokemonForm.HasForms(speciesID)) return [];
        var forms = Dex.Where(e => e.ChainOrder.Contains(speciesID))
            .Select(e => PokemonForm.ResolvedForSpecies(speciesID, e.Form))
            .OfType<PokemonForm>().ToHashSet();
        if (Active is { } a && ReachedPath(a).Contains(speciesID)
            && PokemonForm.ResolvedForSpecies(speciesID, a.Form) is { } f)
            forms.Add(f);
        return forms;
    }

    /// <summary>
    /// Hatch roll weights: a form counts as collected once every species of the line that shows it is
    /// owned in it, so a Sandy Burmy that became Mothim still leaves Sandy Wormadam to find.
    /// </summary>
    public HashSet<PokemonForm> CollectedLineForms(int baseID)
    {
        var owned = PokemonForm.FormSpecies(baseID).Select(id => (id, forms: CollectedForms(id))).ToList();
        return PokemonForm.LineForms(baseID).Where(form =>
            owned.All(o => !PokemonForm.Forms(o.id).Contains(form) || o.forms.Contains(form))).ToHashSet();
    }

    /// <summary>
    /// Whether an owned specimen of this species is shiny. Scans only graduation records and reached stages;
    /// a disguised Ditto keeps its shininess hidden until revealed.
    /// </summary>
    public bool OwnsShinySpecies(int speciesID, PokemonForm? form = null)
    {
        var resolved = PokemonForm.ResolvedForSpecies(speciesID, form);
        if (Dex.Any(e => e.IsShiny && e.ChainOrder.Contains(speciesID)
                         && PokemonForm.ResolvedForSpecies(speciesID, e.Form) == resolved)) return true;
        if (Active is not { } a || !ReachedPath(a).Contains(speciesID)
            || PokemonForm.ResolvedForSpecies(speciesID, a.Form) != resolved) return false;
        return a.DisplaysShiny;
    }

    /// <summary>
    /// The representative may only point at a species the user owns, so Fresh Eggs, Ditto reveals and
    /// hand-edited saves cannot leave a ghost species on the menu bar and floating pet forever.
    /// </summary>
    public void ReconcileRepresentativeSelection()
    {
        if (RepresentativeSpeciesID is not { } selected)
        {
            RepresentativeForm = null;
            return;
        }
        RepresentativeForm = PokemonForm.ResolvedForSpecies(selected, RepresentativeForm);
        if (!OwnsSpecies(selected, RepresentativeForm))
        {
            RepresentativeSpeciesID = null;
            RepresentativeForm = null;
        }
    }
}

// NOTE: hatch candidates are no longer hard-coded — CompanionStore.ChooseBase() picks from every PokéAPI
// base species (Gen 1–5) with capture_rate-weighted sampling.
