namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>PokemonForm.swift</c>. An individual's appearance within a species: Unown's letter,
/// Shaymin's Sky Forme, Shellos's sea.
///
/// The value belongs to the individual and is resolved per species of its line, so a Sandy Burmy becomes
/// a Sandy Wormadam while the Mothim branch keeps Mothim's single appearance. Species IDs and evolution
/// paths are unchanged; the form only selects sprites, names and collection slots.
/// Persisted as its raw string.
/// </summary>
public readonly record struct PokemonForm(string RawValue)
{
    public const int UnownSpeciesID = 201;

    public override string ToString() => RawValue;

    /// <summary>Unown shows its letter instead of a localized form name.</summary>
    public string Symbol => RawValue switch
    {
        "exclamation" => "!",
        "question" => "?",
        _ => RawValue.ToUpperInvariant(),
    };

    // Catalog

    /// <summary>
    /// <see cref="PokemonID"/> is the PokéAPI variety whose static PNG holds this form (<c>10006.png</c> for
    /// Sky Shaymin); null means the static sprite is named like the animated one (<c>412-sandy.png</c>).
    /// <see cref="FormID"/> is the PokéAPI <c>pokemon-form</c> resource carrying the localized form names.
    /// </summary>
    public sealed record Entry(PokemonForm Form, int FormID, int? PokemonID = null)
    {
        public Entry(string rawValue, int formID, int? pokemonID = null) : this(new PokemonForm(rawValue), formID, pokemonID) { }
    }

    /// <summary>
    /// Every catalogued species lists its default appearance first; that one keeps the plain
    /// <c>&lt;id&gt;.png</c>/<c>&lt;id&gt;.gif</c> sprites. Only forms with a Gen V animated sprite are listed,
    /// the same source that bounds hatchable species. Gender differences are not forms.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, IReadOnlyList<Entry>> Catalog = BuildCatalog();

    private static Dictionary<int, IReadOnlyList<Entry>> BuildCatalog()
    {
        var letters = "abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString()).Concat(["exclamation", "question"]).ToList();
        var unown = letters.Select((letter, index) => new Entry(letter, index == 0 ? UnownSpeciesID : 10000 + index)).ToList();
        return new Dictionary<int, IReadOnlyList<Entry>>
        {
            [UnownSpeciesID] = unown,
            [351] = [new("normal", 351), new("sunny", 10028, 10013), new("rainy", 10029, 10014), new("snowy", 10030, 10015)],
            [386] = [new("normal", 386), new("attack", 10031, 10001), new("defense", 10032, 10002), new("speed", 10033, 10003)],
            [412] = [new("plant", 412), new("sandy", 10034), new("trash", 10035)],
            [413] = [new("plant", 413), new("sandy", 10036, 10004), new("trash", 10037, 10005)],
            [421] = [new("overcast", 421), new("sunshine", 10038)],
            [422] = [new("west", 422), new("east", 10039)],
            [423] = [new("west", 423), new("east", 10040)],
            [479] = [new("normal", 479), new("heat", 10058, 10008), new("wash", 10059, 10009), new("frost", 10060, 10010),
                     new("fan", 10061, 10011), new("mow", 10062, 10012)],
            [487] = [new("altered", 487), new("origin", 10063, 10007)],
            [492] = [new("land", 492), new("sky", 10064, 10006)],
            [493] = [new("normal", 493), new("fighting", 10045), new("flying", 10047), new("poison", 10052),
                     new("ground", 10050), new("rock", 10054), new("bug", 10041), new("ghost", 10048),
                     new("steel", 10055), new("fire", 10046), new("water", 10056), new("grass", 10049),
                     new("electric", 10044), new("psychic", 10053), new("ice", 10051), new("dragon", 10043),
                     new("dark", 10042), new("fairy", 10085)],
            [550] = [new("red-striped", 550), new("blue-striped", 10066, 10016)],
            [555] = [new("standard", 555), new("zen", 10067, 10017)],
            [585] = [new("spring", 585), new("summer", 10068), new("autumn", 10069), new("winter", 10070)],
            [586] = [new("spring", 586), new("summer", 10071), new("autumn", 10072), new("winter", 10073)],
            [641] = [new("incarnate", 641), new("therian", 10079, 10019)],
            [642] = [new("incarnate", 642), new("therian", 10080, 10020)],
            [645] = [new("incarnate", 645), new("therian", 10081, 10021)],
            [646] = [new("normal", 646), new("black", 10082, 10022), new("white", 10083, 10023)],
            [647] = [new("ordinary", 647), new("resolute", 10084, 10024)],
            [648] = [new("aria", 648), new("pirouette", 10074, 10018)],
            [649] = [new("normal", 649), new("douse", 10075), new("shock", 10076), new("burn", 10077), new("chill", 10078)],
        };
    }

    /// <summary>Catalogued species whose line starts elsewhere. The individual's form is chosen at hatch,
    /// so Cherubi and Darumaka eggs already know which Cherrim or Darmanitan they will become.</summary>
    private static readonly Dictionary<int, int> LineBaseIDs = new() { [413] = 412, [421] = 420, [423] = 422, [555] = 554, [586] = 585 };

    public static IReadOnlyList<Entry> Entries(int speciesID) => Catalog.TryGetValue(speciesID, out var e) ? e : [];

    /// <summary>The species' forms, default first. Empty when the species has a single appearance.</summary>
    public static List<PokemonForm> Forms(int speciesID) => Entries(speciesID).Select(e => e.Form).ToList();

    public static bool HasForms(int speciesID) => Catalog.ContainsKey(speciesID);

    /// <summary>Species of the line that change appearance with the individual's form, in line order.</summary>
    public static List<int> FormSpecies(int baseID) =>
        Catalog.Keys.Where(k => (LineBaseIDs.TryGetValue(k, out var b) ? b : k) == baseID).Order().ToList();

    /// <summary>Everything an individual of this line can carry, default first.</summary>
    public static List<PokemonForm> LineForms(int baseID)
    {
        var seen = new HashSet<PokemonForm>();
        return FormSpecies(baseID).SelectMany(Forms).Where(seen.Add).ToList();
    }

    /// <summary>The appearance of an individual at one species: its own form when that species has it,
    /// otherwise the species default. Null for species without forms.</summary>
    public static PokemonForm? ResolvedForSpecies(int speciesID, PokemonForm? form)
    {
        var forms = Forms(speciesID);
        if (forms.Count == 0) return null;
        return form is { } f && forms.Contains(f) ? f : forms[0];
    }

    /// <summary>Normalizes the form an individual stores: null for lines without forms, and the line default
    /// for records written before this line had forms (older Unown saves keep their A).</summary>
    public static PokemonForm? ResolvedForLine(int baseID, PokemonForm? form)
    {
        var forms = LineForms(baseID);
        if (forms.Count == 0) return null;
        return form is { } f && forms.Contains(f) ? f : forms[0];
    }

    public static bool IsDefault(PokemonForm? form, int speciesID)
    {
        var forms = Forms(speciesID);
        PokemonForm? first = forms.Count > 0 ? forms[0] : null;
        return first == ResolvedForSpecies(speciesID, form);
    }

    public static int SortOrder(PokemonForm? form, int speciesID)
    {
        if (form is not { } f) return 0;
        var index = Forms(speciesID).IndexOf(f);
        return index < 0 ? 0 : index;
    }

    /// <summary>Chosen only after the species roll: uncollected forms weigh 2, collected forms weigh 1.</summary>
    public static PokemonForm Roll(ulong roll, IReadOnlyList<PokemonForm> among, IReadOnlySet<PokemonForm> collected)
    {
        var weights = among.Select(f => CollectionWeight.Adjusted(2, collected.Contains(f))).ToList();
        var remaining = (int)(roll % (ulong)weights.Sum());
        for (var i = 0; i < among.Count - 1; i++)
        {
            remaining -= weights[i];
            if (remaining < 0) return among[i];
        }
        return among[^1]; // Any remaining roll belongs to the last form.
    }

    // Sprites and names

    /// <summary>Animated GIFs and cache files use <c>&lt;id&gt;-&lt;form&gt;</c>; the default keeps the plain
    /// <c>&lt;id&gt;</c> names (and so the cache keys of saves written before forms existed).</summary>
    public static string AssetName(int speciesID, PokemonForm? form)
    {
        if (ResolvedForSpecies(speciesID, form) is not { } f || IsDefault(f, speciesID)) return speciesID.ToString();
        return $"{speciesID}-{f.RawValue}";
    }

    /// <summary>Static PNGs of forms that are separate PokéAPI varieties live under the variety id.</summary>
    public static string StaticAssetName(int speciesID, PokemonForm? form)
    {
        var resolved = ResolvedForSpecies(speciesID, form);
        if (Entries(speciesID).FirstOrDefault(e => e.Form == resolved)?.PokemonID is { } pokemonID)
            return pokemonID.ToString();
        return AssetName(speciesID, form);
    }

    /// <summary>PokéAPI <c>pokemon-form</c> id holding the localized form names. Null for species without forms.</summary>
    public static int? FormID(int speciesID, PokemonForm? form)
    {
        var resolved = ResolvedForSpecies(speciesID, form);
        return Entries(speciesID).FirstOrDefault(e => e.Form == resolved)?.FormID;
    }

    /// <summary>Unown always names its letter; other species name only a non-default form.</summary>
    public static string DisplayName(string name, int speciesID, PokemonForm? form, string? label)
    {
        if (ResolvedForSpecies(speciesID, form) is not { } f) return name;
        if (speciesID == UnownSpeciesID) return $"{name} [{f.Symbol}]";
        if (IsDefault(f, speciesID) || string.IsNullOrEmpty(label)) return name;
        return $"{name} [{label}]";
    }

    /// <summary>Castform, Rotom, Kyurem and Genesect have no official name for their usual look:
    /// PokéAPI only says "Forme de Motisma" or nothing at all.</summary>
    private static readonly HashSet<int> UnnamedDefaultSpecies = [351, 479, 646, 649];

    private static bool HasUnnamedDefault(int speciesID, PokemonForm? form) =>
        UnnamedDefaultSpecies.Contains(speciesID) && IsDefault(form, speciesID);

    /// <summary>PokéAPI resource with the localized name of the form this species shows.
    /// Unown and the unnamed usual looks need none.</summary>
    public static PokemonNameResource? NameResource(int speciesID, PokemonForm? form)
    {
        if (speciesID == UnownSpeciesID || HasUnnamedDefault(speciesID, form)
            || FormID(speciesID, form) is not { } id) return null;
        return new PokemonNameResource(PokemonNameResource.ResourceKind.PokemonForm, id.ToString());
    }

    /// <summary>Every form of the species, for pickers that label all of them.</summary>
    public static List<PokemonNameResource> NameResources(int speciesID) =>
        Forms(speciesID).Select(f => NameResource(speciesID, f)).OfType<PokemonNameResource>().ToList();

    /// <summary>
    /// Localized form name ("Forme Céleste"), Unown's letter, or the identifier until PokéAPI answers.
    /// Null for species without forms. <paramref name="names"/> is the loaded translation cache
    /// (the Swift app's <c>PokemonNameDisplayStore.names</c>).
    /// </summary>
    public static string? Label(int speciesID, PokemonForm? form, AppLanguage language,
        IReadOnlyDictionary<PokemonNameResource, Dictionary<string, string>>? names = null)
    {
        if (ResolvedForSpecies(speciesID, form) is not { } f) return null;
        if (speciesID == UnownSpeciesID) return f.Symbol;
        if (HasUnnamedDefault(speciesID, f)) return new L(language).NormalForm;
        Dictionary<string, string>? translations = null;
        if (NameResource(speciesID, f) is { } resource && names != null) names.TryGetValue(resource, out translations);
        return (translations == null ? null : language.ResolveName(translations))
               ?? PokemonNameLocalization.Identifier(f.RawValue);
    }

    public static string DisplayName(string name, int speciesID, PokemonForm? form, AppLanguage language,
        IReadOnlyDictionary<PokemonNameResource, Dictionary<string, string>>? names = null) =>
        DisplayName(name, speciesID, form, Label(speciesID, form, language, names));
}
