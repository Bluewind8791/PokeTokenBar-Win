namespace PokeTokenBar.Core;

// Port of PokedexRegion.swift.

/// <summary>
/// A Pokémon generation, identified by its home region. Named "region" (not "generation") on purpose: the companion
/// code already uses <c>generation</c>/<c>activeGeneration</c> for egg-hatch attempt tokens.
///
/// <para>Membership is derived purely from the National Dex number — Gen 1 = 1–151, Gen 2 = 152–251,
/// Gen 3 = 252–386, Gen 4 = 387–493, Gen 5 = 494–649 — so no PokéAPI call is needed to place a species.</para>
/// </summary>
public enum PokedexRegion { Kanto, Johto, Hoenn, Sinnoh, Unova }

/// <summary>Swift <c>ClosedRange&lt;Int&gt;</c> of National Dex numbers.</summary>
public readonly record struct DexNumberRange(int LowerBound, int UpperBound)
{
    public int Count => UpperBound - LowerBound + 1;
    public bool Contains(int dexNumber) => dexNumber >= LowerBound && dexNumber <= UpperBound;
    public IEnumerable<int> Numbers => Enumerable.Range(LowerBound, Count);
}

public static class PokedexRegions
{
    /// <summary>Swift <c>allCases</c>, in National Dex order.</summary>
    public static IReadOnlyList<PokedexRegion> AllCases { get; } = Enum.GetValues<PokedexRegion>();

    public static string RawValue(this PokedexRegion region) => region switch
    {
        PokedexRegion.Kanto => "kanto",
        PokedexRegion.Johto => "johto",
        PokedexRegion.Hoenn => "hoenn",
        PokedexRegion.Sinnoh => "sinnoh",
        PokedexRegion.Unova => "unova",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, null),
    };

    public static string Id(this PokedexRegion region) => region.RawValue();

    /// <summary>National Dex numbers that belong to this region.</summary>
    public static DexNumberRange DexRange(this PokedexRegion region) => region switch
    {
        PokedexRegion.Kanto => new(1, 151),
        PokedexRegion.Johto => new(152, 251),
        PokedexRegion.Hoenn => new(252, 386),
        PokedexRegion.Sinnoh => new(387, 493),
        PokedexRegion.Unova => new(494, 649),
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, null),
    };

    /// <summary>Number of species in the generation (what a complete regional dex requires).</summary>
    public static int Total(this PokedexRegion region) => region.DexRange().Count;

    /// <summary>The region a National Dex number belongs to, or null outside Gen 1–5.</summary>
    public static PokedexRegion? Region(int dexNumber) =>
        AllCases.Where(r => r.DexRange().Contains(dexNumber)).Select(r => (PokedexRegion?)r).FirstOrDefault();

    /// <summary>The full Gen 1–5 National Dex.</summary>
    public static readonly DexNumberRange NationalRange = new(1, 649);
    public static int NationalTotal => NationalRange.Count;
}

/// <summary>Owned/total progress for one region, including the harder shiny tier.</summary>
public sealed record DexCompletion(PokedexRegion Region, int Owned, int ShinyOwned)
{
    public int Total => Region.Total();
    public bool IsComplete => Owned >= Total;
    public bool IsShinyComplete => ShinyOwned >= Total;
    public string Id => Region.RawValue();
}

/// <summary>The case of a <see cref="DexBadge"/> (Swift enum without its associated value).</summary>
public enum DexBadgeKind { Region, RegionShiny, Unown, National, NationalShiny }

/// <summary>
/// A cosmetic completion achievement. Cases carry a stable <see cref="NotificationKey"/> because the "already notified"
/// set is persisted — the keys must never drift across releases. <see cref="Region"/> is set for the two regional
/// kinds only.
/// </summary>
public readonly record struct DexBadge
{
    public DexBadgeKind Kind { get; }
    public PokedexRegion? Region { get; }

    private DexBadge(DexBadgeKind kind, PokedexRegion? region = null)
    {
        Kind = kind;
        Region = region;
    }

    /// <summary>Swift <c>.region(r)</c>.</summary>
    public static DexBadge ForRegion(PokedexRegion region) => new(DexBadgeKind.Region, region);
    /// <summary>Swift <c>.regionShiny(r)</c>.</summary>
    public static DexBadge ForRegionShiny(PokedexRegion region) => new(DexBadgeKind.RegionShiny, region);
    public static DexBadge Unown { get; } = new(DexBadgeKind.Unown);
    public static DexBadge National { get; } = new(DexBadgeKind.National);
    public static DexBadge NationalShiny { get; } = new(DexBadgeKind.NationalShiny);

    public string NotificationKey => this switch
    {
        { Kind: DexBadgeKind.Region, Region: { } r } => $"region.{r.RawValue()}",
        { Kind: DexBadgeKind.RegionShiny, Region: { } r } => $"regionShiny.{r.RawValue()}",
        { Kind: DexBadgeKind.Unown } => "unown",
        { Kind: DexBadgeKind.National } => "national",
        { Kind: DexBadgeKind.NationalShiny } => "nationalShiny",
        _ => throw new InvalidOperationException($"invalid badge {Kind}"),
    };
}

/// <summary>
/// Pure computation of dex completion from the set of owned species. Kept store-independent so the generation math is
/// unit-testable without spinning up the whole companion store.
/// </summary>
public static class DexProgress
{
    /// <summary>Number of Unown letter forms required for the Unown badge (all 28).</summary>
    public static int UnownTotal => PokemonForm.Forms(PokemonForm.UnownSpeciesID).Count;

    /// <summary>Owned ids → whether any owned individual of that id is shiny.</summary>
    private static Dictionary<int, bool> ShinyByID(IEnumerable<(int Id, bool IsShiny)> owned)
    {
        var shinyByID = new Dictionary<int, bool>();
        foreach (var (id, isShiny) in owned)
            shinyByID[id] = shinyByID.GetValueOrDefault(id) || isShiny;
        return shinyByID;
    }

    /// <summary>Per-region owned/total (and shiny-owned) counts. Duplicate ids contribute once per form; a species
    /// counts as shiny-owned if any owned individual of it is shiny.</summary>
    public static List<DexCompletion> RegionCompletions(IEnumerable<(int Id, bool IsShiny)> owned)
    {
        var shinyByID = ShinyByID(owned);
        return PokedexRegions.AllCases.Select(region =>
        {
            var ids = shinyByID.Keys.Where(region.DexRange().Contains).ToList();
            return new DexCompletion(region, ids.Count, ids.Count(id => shinyByID[id]));
        }).ToList();
    }

    /// <summary>National (Gen 1–5) owned/total, as a plain tuple the header formats directly.</summary>
    public static (int Owned, int ShinyOwned, int Total) NationalProgress(IEnumerable<(int Id, bool IsShiny)> owned)
    {
        var shinyByID = ShinyByID(owned.Where(s => PokedexRegions.NationalRange.Contains(s.Id)));
        return (shinyByID.Count, shinyByID.Values.Count(v => v), PokedexRegions.NationalTotal);
    }

    /// <summary>Every badge the player has currently earned, in a stable order (regions, then Unown, then national).</summary>
    public static List<DexBadge> EarnedBadges(IReadOnlyCollection<(int Id, bool IsShiny)> owned, int unownFormsCollected)
    {
        var badges = new List<DexBadge>();
        foreach (var completion in RegionCompletions(owned))
        {
            if (completion.IsComplete) badges.Add(DexBadge.ForRegion(completion.Region));
            if (completion.IsShinyComplete) badges.Add(DexBadge.ForRegionShiny(completion.Region));
        }
        if (unownFormsCollected >= UnownTotal) badges.Add(DexBadge.Unown);
        var national = NationalProgress(owned);
        if (national.Owned >= national.Total) badges.Add(DexBadge.National);
        if (national.ShinyOwned >= national.Total) badges.Add(DexBadge.NationalShiny);
        return badges;
    }

    /// <summary>Badges that are earned but not yet announced — the ones a completion notification should fire for.
    /// Order is preserved so the notifications arrive in the stable badge order.</summary>
    public static List<DexBadge> NewlyEarnedBadges(IEnumerable<DexBadge> earned, IReadOnlySet<string> alreadyNotified) =>
        earned.Where(b => !alreadyNotified.Contains(b.NotificationKey)).ToList();
}
