using System.IO;
using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Sample data for the Collection renders: the 18-species Pokédex / 18-individual catch log of
/// assets/screenshot-collection-*.png, and (for the detail pages) a Rotom with Heat and Wash forms caught
/// — assets/screenshot-pokemon-forms.png. Saves go to temp files; names/sprites/details come from PokéAPI.
/// </summary>
public static partial class SampleData
{
    /// <summary>The whole popover over the Collection sample save, after applying a navigation route.</summary>
    public static FrameworkElement CollectionPopover(RenderHarness.Context c, Action<PopoverNavigation> route, bool withForms = false,
        int? preloadDetailsFor = null)
    {
        var s = CollectionServices(c, withForms);
        if (preloadDetailsFor is { } id) PreloadDetail(s.Companion, id);
        route(s.Nav);
        return PopoverRoot.Build(s);
    }

    public static AppServices CollectionServices(RenderHarness.Context c, bool withForms)
    {
        var baseline = Services(c);
        var settings = new AppSettings(null);
        var companion = new CompanionStore(filePath: WriteCollectionSave(c.Language, withForms), settings: settings,
            formNameLoader: DexNames.Fetch);
        companion.SetLanguage(c.Language);
        return new AppServices { Store = baseline.Store, Companion = companion, Updater = baseline.Updater, Nav = new PopoverNavigation() };
    }

    /// <summary>Loads what the detail page fetches on open, so a fixed-time capture shows it settled.</summary>
    private static void PreloadDetail(CompanionStore store, int speciesID)
    {
        Pump(store.LoadPokemonDetails(speciesID));
        var resources = PokemonForm.NameResources(speciesID);
        Pump(store.LoadFormNames(resources));
        if (!store.PokemonDetailsByID.TryGetValue(speciesID, out var details)) return;
        resources.AddRange(details.Types.Select(t => new PokemonNameResource(PokemonNameResource.ResourceKind.Type, t)));
        resources.AddRange(details.Abilities.Select(a => new PokemonNameResource(PokemonNameResource.ResourceKind.Ability, a.Name)));
        foreach (var entry in store.PokemonIndividuals(speciesID, null).Concat(store.DexEntries.Where(e => e.FinalID == speciesID)))
            if (entry.Profile is { } p)
            {
                resources.AddRange(p.Moves.Select(m => new PokemonNameResource(PokemonNameResource.ResourceKind.Move, m.Name)));
                if (p.AbilityName is { } ability) resources.Add(new PokemonNameResource(PokemonNameResource.ResourceKind.Ability, ability));
            }
        Pump(Task.WhenAll(resources.Distinct().Select(DexNames.Load)));
    }

    private static readonly Dictionary<int, (string En, string Ko, string Ja)> SpeciesNames = new()
    {
        [13] = ("Weedle", "뿔충이", "ビードル"),
        [14] = ("Kakuna", "딱충이", "コクーン"),
        [15] = ("Beedrill", "독침붕", "スピアー"),
        [114] = ("Tangela", "덩쿠리", "モンジャラ"),
        [129] = ("Magikarp", "잉어킹", "コイキング"),
        [130] = ("Gyarados", "갸라도스", "ギャラドス"),
        [131] = ("Lapras", "라프라스", "ラプラス"),
        [140] = ("Kabuto", "투구", "カブト"),
        [141] = ("Kabutops", "투구푸스", "カブトプス"),
        [165] = ("Ledyba", "레디바", "レディバ"),
        [166] = ("Ledian", "레디안", "レディアン"),
        [169] = ("Crobat", "크로뱃", "クロバット"),
        [177] = ("Natu", "네이티", "ネイティ"),
        [178] = ("Xatu", "네이티오", "ネイティオ"),
        [265] = ("Wurmple", "개무소", "ケムッソ"),
        [266] = ("Silcoon", "실쿤", "カラサリス"),
        [267] = ("Beautifly", "뷰티플라이", "アゲハント"),
        [287] = ("Slakoth", "게을로", "ナマケロ"),
        [479] = ("Rotom", "로토무", "ロトム"),
    };

    private static Dictionary<int, Dictionary<string, string>> Names(IEnumerable<int> chain) =>
        chain.ToDictionary(id => id, id => new Dictionary<string, string>
        {
            ["en"] = SpeciesNames[id].En, ["ko"] = SpeciesNames[id].Ko, ["ja"] = SpeciesNames[id].Ja,
        });

    private static string WriteCollectionSave(AppLanguage lang, bool withForms)
    {
        var state = new CompanionState
        {
            InstallBaselineSet = true,
            UsedSinceInstall = 9_000_000_000,
            SpentTokens = 1_000_000_000,
            LastDate = LocalUsageReader.LocalDay(Now),
            Language = lang,
        };
        // Insertion order decides each species' rarity (first sight); catch dates decide the log order.
        void Add(int[] chain, Rarity rarity, double daysAgo, PokemonNature nature = PokemonNature.Adamant, bool shiny = false)
        {
            var entry = new DexEntry(chain[0], chain[^1], chain, rarity, Now.AddDays(-daysAgo), isShiny: shiny, nature: nature,
                names: Names(chain));
            state.Dex.Add(entry);
            state.CollectedFinals.Add($"{chain[0]}:{chain[^1]}");
        }
        Add([13], Rarity.Common, 1080, PokemonNature.Hardy);
        Add([13, 14, 15], Rarity.Uncommon, 1050);
        Add([287], Rarity.Common, 1051);
        Add([267], Rarity.Legendary, 1052);
        Add([114], Rarity.Rare, 1060, PokemonNature.Calm);
        Add([129], Rarity.Legendary, 1061, PokemonNature.Bold);
        Add([130], Rarity.Common, 1062, PokemonNature.Timid);
        Add([131], Rarity.Uncommon, 1063, PokemonNature.Modest);
        Add([131], Rarity.Uncommon, 1064, PokemonNature.Jolly);
        Add([140], Rarity.Rare, 1065, PokemonNature.Impish);
        Add([141], Rarity.Legendary, 1066, PokemonNature.Brave);
        Add([165], Rarity.Common, 1067, PokemonNature.Docile);
        Add([166], Rarity.Uncommon, 1068, PokemonNature.Naive);
        Add([169], Rarity.Rare, 1069, PokemonNature.Hasty);
        Add([177], Rarity.Legendary, 1070, PokemonNature.Quiet);
        Add([178], Rarity.Common, 1071, PokemonNature.Sassy);
        Add([265], Rarity.Uncommon, 1072, PokemonNature.Lax);
        Add([266], Rarity.Rare, 1073, PokemonNature.Gentle);
        if (withForms)
        {
            foreach (var (form, days, seed) in new[] { ("heat", 20.0, "heat"), ("heat", 35.0, "heat-2"), ("wash", 22.0, "wash") })
            {
                var id = Guid.NewGuid().ToString().ToUpperInvariant();
                state.Dex.Add(new DexEntry(479, 479, [479], Rarity.Rare, Now.AddDays(-days), nature: PokemonNature.Hardy,
                    profile: PokemonProfile.Generate(PokemonProfileMigration.Seed($"sample:{seed}"), 0, id),
                    names: Names([479]), form: new PokemonForm(form), id: id));
                state.CollectedFinals.Add("479:479");
            }
            state.RepresentativeSpeciesID = 479;
            state.RepresentativeForm = new PokemonForm("heat");
        }
        var path = Path.Combine(Path.GetTempPath(), $"ptb-sample-collection-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, CompanionJson.Serialize(state));
        return path;
    }
}
