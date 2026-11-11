namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>PokemonNameLocalization</c> (PokemonNameLocalization.swift). Shared by species names and battle
/// metadata. Keeps every language the API returns: adding an app language must not require changing a second
/// allowlist or redownloading names.
/// </summary>
public static class PokemonNameLocalization
{
    public static string LanguageCode(string raw) => raw.Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>PokéAPI <c>names[]</c> entries (language.name, name) → normalized code → trimmed name.</summary>
    public static Dictionary<string, string> Collect(IEnumerable<(string Language, string Name)> entries)
    {
        var names = new Dictionary<string, string>();
        foreach (var (language, value) in entries)
        {
            var code = LanguageCode(language);
            var name = value.Trim();
            if (code.Length > 0 && name.Length > 0) names[code] = name;
        }
        return names;
    }

    public static string? Resolve(IReadOnlyDictionary<string, string> names, IEnumerable<string> preferredCodes)
    {
        // Old saves may contain mixed-case PokéAPI language codes (for example ja-Hrkt).
        var normalized = new Dictionary<string, string>();
        foreach (var key in names.Keys.Order(StringComparer.Ordinal))
        {
            var value = names[key].Trim();
            if (value.Length > 0) normalized[LanguageCode(key)] = value;
        }
        foreach (var code in preferredCodes.Append("en"))
        {
            if (normalized.TryGetValue(LanguageCode(code), out var name)) return name;
        }
        return null;
    }

    /// <summary>Last resort while offline or before the first translation response arrives.</summary>
    public static string Identifier(string raw) =>
        string.Join(" ", raw.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part[..1].ToUpperInvariant() + part[1..]));
}

/// <summary>Port of <c>PokemonNameResource</c>: a PokéAPI resource whose multilingual names are fetched on demand.</summary>
public readonly record struct PokemonNameResource(PokemonNameResource.ResourceKind Kind, string Name)
{
    public enum ResourceKind { Type, Ability, Move, PokemonForm }

    /// <summary>PokéAPI path segment, identical to the Swift raw value.</summary>
    public static string RawValue(ResourceKind kind) => kind switch
    {
        ResourceKind.Type => "type",
        ResourceKind.Ability => "ability",
        ResourceKind.Move => "move",
        ResourceKind.PokemonForm => "pokemon-form",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public string CacheKey => RawValue(Kind) + "-" + Name;

    /// <summary>Only lowercase ASCII letters, digits and '-' — the name goes into a URL and a file name.</summary>
    public bool IsValid => Name.Length > 0 && Name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
