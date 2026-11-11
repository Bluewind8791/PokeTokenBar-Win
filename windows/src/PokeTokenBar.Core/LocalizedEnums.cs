namespace PokeTokenBar.Core;

// Small, pure Swift types that L takes as parameters. Each mirrors its Swift declaration; the files that own
// them on macOS are noted so their ports can extend these instead of redeclaring them.

/// <summary>
/// Port of <c>ProviderStatusIndicator</c> (ProviderStatusChecker.swift): a provider status-page incident
/// indicator, a display-only signal so stale/zero/error output during an API outage is not mistaken for an app bug.
/// </summary>
public enum ProviderStatusIndicator
{
    /// <summary>statuspage raw value "none".</summary>
    Operational,
    Minor,
    Major,
    Critical,
    Maintenance,
    Unknown,
}

public static class ProviderStatusIndicators
{
    /// <summary>Any incident (only <see cref="ProviderStatusIndicator.Operational"/> is false). Gates the icon/row.</summary>
    public static bool HasIssue(this ProviderStatusIndicator indicator) => indicator != ProviderStatusIndicator.Operational;

    /// <summary>statuspage.io <c>status.indicator</c> string → indicator (unknown values map to Unknown).</summary>
    public static ProviderStatusIndicator FromStatuspage(string raw) => raw switch
    {
        "none" => ProviderStatusIndicator.Operational,
        "minor" => ProviderStatusIndicator.Minor,
        "major" => ProviderStatusIndicator.Major,
        "critical" => ProviderStatusIndicator.Critical,
        "maintenance" => ProviderStatusIndicator.Maintenance,
        _ => ProviderStatusIndicator.Unknown,
    };

    /// <summary>statuspage.io component status → app indicator.</summary>
    public static ProviderStatusIndicator FromComponentStatus(string raw) => raw switch
    {
        "operational" => ProviderStatusIndicator.Operational,
        "degraded_performance" => ProviderStatusIndicator.Minor,
        "partial_outage" => ProviderStatusIndicator.Major,
        "major_outage" => ProviderStatusIndicator.Critical,
        "under_maintenance" => ProviderStatusIndicator.Maintenance,
        _ => ProviderStatusIndicator.Unknown,
    };
}

/// <summary>Port of <c>RecapScope</c> (UsageRecap.swift): calendar week, month or year.</summary>
public enum RecapScope { Week, Month, Year }

/// <summary>Port of <c>UsageStore.MenuLimitColorMode</c>: which menu bar limit items are drawn in their gauge color.</summary>
public enum MenuLimitColorMode
{
    /// <summary>Every item, in all six tiers (the default).</summary>
    Gauge,
    /// <summary>Only items running faster than pace; calm items keep the system text color.</summary>
    Attention,
    /// <summary>None — the system text color.</summary>
    Off,
}

/// <summary>Port of <c>CompanionStore.DexSortOption</c>.</summary>
public enum DexSortOption { NumberAsc, NumberDesc, NameAsc, NameDesc, RarityDesc }

/// <summary>Port of <c>CompanionStore.CatchLogSortOption</c>.</summary>
public enum CatchLogSortOption { RecentFirst, OldestFirst, NumberAsc, NumberDesc, NameAsc, NameDesc, RarityDesc }

/// <summary>Port of <c>PokemonGender</c> (PokemonProfile.swift).</summary>
public enum PokemonGender { Male, Female, Genderless }

/// <summary>Port of <c>PokemonMoveLearnMethod</c> (PokemonProfile.swift).</summary>
public sealed record PokemonMoveLearnMethod(string Method, int Level);
