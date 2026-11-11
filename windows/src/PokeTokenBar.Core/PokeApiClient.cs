using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeTokenBar.Core;

// Port of PokeAPIClient.swift.

/// <summary>Hatch candidate: the first species of an evolution line (base) and its official capture rate.</summary>
/// <param name="CaptureRate">3 (Mewtwo class) … 255 (Caterpie class): the official rarity signal.</param>
public sealed record BaseSpecies(int Id, int CaptureRate);

/// <summary>Pokémon line data (injectable; tests use stubs).</summary>
public interface IPokeProviding
{
    Task<EvoLine> Line(int baseSpeciesID);
    /// <summary>All Gen 1–5 bases (one GraphQL query, disk cached).</summary>
    Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex();
    /// <summary>
    /// The species as a hatch candidate when it is a base (start of a line), otherwise null. The REST fallback
    /// for when the GraphQL index endpoint is down. Default: look it up in <see cref="BaseSpeciesIndex"/>
    /// (the Swift tests' shared protocol extension), which only the real client overrides.
    /// </summary>
    async Task<BaseSpecies?> BaseSpecies(int id) => (await BaseSpeciesIndex()).FirstOrDefault(b => b.Id == id);
}

/// <summary>Separate from <see cref="IPokeProviding"/> so evolution-only test doubles stay small.</summary>
public interface IPokemonDetailProviding
{
    Task<PokemonDetails> PokemonDetails(int speciesID);
}

/// <summary>
/// PokéAPI client: fetches and parses species/evolution chains at runtime; no Pokémon data is bundled.
/// Species responses are cached in memory (multilingual names reused), lines too (prefetch → zero network
/// at hatch), and combat details / the base index on disk under <see cref="AppEnv.StateDirectory"/>.
/// </summary>
public sealed class PokeApiClient : IPokeProviding, IPokemonDetailProviding
{
    private static readonly Lazy<PokeApiClient> SharedInstance = new(() => new PokeApiClient());
    public static PokeApiClient Shared => SharedInstance.Value;

    private const string BaseUrl = "https://pokeapi.co/api/v2/";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly HttpClient _http;
    private readonly string? _cacheDirectory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Dictionary<int, SpeciesDto> _speciesCache = new();
    private readonly Dictionary<int, EvoLine> _lineCache = new();
    private readonly Dictionary<int, PokemonDetails> _detailsCache = new();
    private List<BaseSpecies>? _baseIndexCache;
    private bool _restBuildInFlight;
    private bool _restBuildTried; // once per session (REST index build when GraphQL is down)

    public static IEnumerable<string> LangCodes => Enum.GetValues<AppLanguage>().SelectMany(l => l.ApiCodes());

    /// <param name="cacheDirectory">Where the detail cache and base index live. Null = the state directory when
    /// running as the installed app, otherwise no disk cache (tests never touch the user's files).</param>
    public PokeApiClient(HttpClient? http = null, string? cacheDirectory = null, Func<DateTimeOffset>? now = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _cacheDirectory = cacheDirectory ?? (AppEnv.IsInstalledApp ? AppEnv.StateDirectory() : null);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    private string? DetailsFile(int speciesID) =>
        _cacheDirectory == null ? null : Path.Combine(_cacheDirectory, "pokemon-details-v1", $"{speciesID}.json");

    private string? BaseIndexFile => _cacheDirectory == null ? null : Path.Combine(_cacheDirectory, "base-index.json");

    private sealed record DetailSnapshot(DateTimeOffset FetchedAt, PokemonDetails Details);
    private sealed record BaseIndexSnapshot(DateTimeOffset FetchedAt, List<BaseSpecies> Entries);

    private static readonly JsonSerializerOptions CacheJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static T? ReadCache<T>(string? path) where T : class
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), CacheJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteCache<T>(string? path, T value)
    {
        if (path == null) return;
        try
        {
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, CacheJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"pokeapi cache write failed {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Default-form battle metadata. Memory → 30-day disk cache → REST, with stale disk fallback offline.</summary>
    public async Task<PokemonDetails> PokemonDetails(int speciesID)
    {
        lock (_gate)
            if (_detailsCache.TryGetValue(speciesID, out var cached)) return cached;
        var file = DetailsFile(speciesID);
        var disk = ReadCache<DetailSnapshot>(file);
        if (disk != null && _now() - disk.FetchedAt < CacheTtl)
        {
            lock (_gate) _detailsCache[speciesID] = disk.Details;
            return disk.Details;
        }
        try
        {
            var dto = await Get<PokemonDto>($"pokemon/{speciesID}").ConfigureAwait(false);
            var speciesDto = await Species(speciesID).ConfigureAwait(false);
            var baseStats = new Dictionary<string, int>();
            foreach (var s in dto.Stats) baseStats[s.Stat.Name] = s.BaseStat; // uniqueKeysWithValues in Swift
            var details = new PokemonDetails(
                speciesID,
                dto.Name,
                dto.Height,
                dto.Weight,
                dto.BaseExperience,
                speciesDto.GenderRate ?? -1,
                dto.Types.OrderBy(t => t.Slot).Select(t => t.Type.Name).ToList(),
                baseStats,
                dto.Abilities.Select(a => new PokemonAbilityOption(a.Ability.Name, a.Slot, a.IsHidden))
                    .OrderBy(a => a.Slot).ToList(),
                NormalizedMoves(dto.Moves));
            lock (_gate) _detailsCache[speciesID] = details;
            WriteCache(file, new DetailSnapshot(_now(), details));
            return details;
        }
        catch (Exception) when (disk != null)
        {
            lock (_gate) _detailsCache[speciesID] = disk.Details;
            return disk.Details;
        }
    }

    /// <summary>Reduces PokéAPI's cross-generation move history at the trust boundary, before it reaches memory or
    /// disk. Moves absent from the supported version group are omitted entirely.</summary>
    internal static List<PokemonMoveOption> NormalizedMoves(IEnumerable<PokemonMoveDto> moves) =>
        moves.Select(move =>
            {
                var methods = move.VersionGroupDetails
                    .Where(row => row.VersionGroup.Name == Core.PokemonDetails.PreferredVersionGroup)
                    .Select(row => new PokemonMoveLearnMethod(row.MoveLearnMethod.Name, row.LevelLearnedAt))
                    .ToList();
                return methods.Count == 0 ? null : new PokemonMoveOption(move.Move.Name, methods);
            })
            .OfType<PokemonMoveOption>()
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

    public async Task<EvoLine> Line(int baseSpeciesID)
    {
        lock (_gate)
            if (_lineCache.TryGetValue(baseSpeciesID, out var cached)) return cached;
        var baseSpecies = await Species(baseSpeciesID).ConfigureAwait(false);
        // A malformed/empty URL from the response throws instead of crashing (the app keeps the egg).
        var chainUrl = ValidatedChainUrl(baseSpecies.EvolutionChain.Url)
                       ?? throw new HttpRequestException("bad evolution_chain url");
        var chain = await Get<ChainDto>(chainUrl.ToString()).ConfigureAwait(false);
        var tree = Node(chain.Chain);
        var rarity = RarityExtensions.From(baseSpecies.CaptureRate, baseSpecies.IsLegendary, baseSpecies.IsMythical);
        // Keep all API languages so later app-language additions can reuse persisted names.
        var names = new Dictionary<int, Dictionary<string, string>>();
        foreach (var id in AllIDs(tree))
        {
            var sp = await Species(id).ConfigureAwait(false);
            names[id] = CollectNames(sp.Names);
        }
        var line = new EvoLine(baseSpeciesID, tree, rarity, names);
        lock (_gate) _lineCache[baseSpeciesID] = line;
        return line;
    }

    /// <summary><c>PokemonNameLocalization.collect</c>: every API language, normalized code, blank names dropped.</summary>
    private static Dictionary<string, string> CollectNames(IEnumerable<NameDto> entries)
    {
        var names = new Dictionary<string, string>();
        foreach (var entry in entries)
        {
            var code = PokemonNameLocalization.LanguageCode(entry.Language.Name);
            var name = entry.Name.Trim();
            if (code.Length > 0 && name.Length > 0) names[code] = name;
        }
        return names;
    }

    // Base index (hatch candidates)

    private sealed record GraphQlBaseResponse(GraphQlData Data);
    private sealed record GraphQlData(List<GraphQlRow> Pokemonspecies);
    private sealed record GraphQlRow(int Id, [property: JsonPropertyName("capture_rate")] int CaptureRate);

    /// <summary>
    /// Every Gen 1–5 base species. Order: memory → disk (30-day TTL) → GraphQL (refreshes disk) → stale disk
    /// (offline fallback). Throws when everything fails (egg kept, retried next tick).
    /// </summary>
    public async Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex()
    {
        lock (_gate)
            if (_baseIndexCache != null) return _baseIndexCache;
        var disk = ReadCache<BaseIndexSnapshot>(BaseIndexFile);
        if (disk != null && _now() - disk.FetchedAt < CacheTtl && disk.Entries.Count > 0)
        {
            lock (_gate) _baseIndexCache = disk.Entries;
            return disk.Entries;
        }
        try
        {
            var entries = await FetchBaseIndex().ConfigureAwait(false);
            lock (_gate) _baseIndexCache = entries;
            WriteCache(BaseIndexFile, new BaseIndexSnapshot(_now(), entries));
            return entries;
        }
        catch (Exception ex)
        {
            if (disk is { Entries.Count: > 0 }) // offline: an old index beats none
            {
                lock (_gate) _baseIndexCache = disk.Entries;
                return disk.Entries;
            }
            // GraphQL down and no cache → build the index over REST in the background (once per session).
            // This hatch is handled by the per-hatch REST fallback; once built, the disk cache restores weighted,
            // collection-aware, offline-capable selection.
            var startBuild = false;
            lock (_gate)
            {
                if (!_restBuildTried)
                {
                    _restBuildTried = true;
                    startBuild = true;
                }
            }
            if (startBuild) _ = Task.Run(BuildBaseIndexViaRest);
            AppLog.Write($"base index (GraphQL) failed, no cache — REST build triggered; per-hatch fallback handles now: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Builds and persists the base index over REST (pokemon-species/{id}) when the GraphQL endpoint is down.
    /// A self-healing cache so hatching is never tied to one endpoint's survival. Small concurrency out of
    /// courtesy to PokéAPI.
    /// </summary>
    public async Task BuildBaseIndexViaRest()
    {
        lock (_gate)
        {
            if (_baseIndexCache != null || _restBuildInFlight) return;
            _restBuildInFlight = true;
        }
        try
        {
            AppLog.Write("base index: building via REST (GraphQL unavailable)…");
            var bases = new List<BaseSpecies>();
            const int batchSize = 6;
            for (var start = PokemonAssets.AnimatedSpeciesMin; start <= PokemonAssets.AnimatedSpeciesMax; start += batchSize)
            {
                var end = Math.Min(start + batchSize - 1, PokemonAssets.AnimatedSpeciesMax);
                var found = await Task.WhenAll(Enumerable.Range(start, end - start + 1).Select(async id =>
                {
                    try { return await BaseSpecies(id).ConfigureAwait(false); }
                    catch (Exception) { return null; }
                })).ConfigureAwait(false);
                bases.AddRange(found.OfType<BaseSpecies>());
            }
            // Mostly failed (unstable network) → do not persist a thin index; retry next session.
            if (bases.Count < 150)
            {
                AppLog.Write($"base index: REST build incomplete ({bases.Count}) — not cached, will retry next session");
                return;
            }
            bases.Sort((a, b) => a.Id.CompareTo(b.Id));
            lock (_gate) _baseIndexCache = bases;
            WriteCache(BaseIndexFile, new BaseIndexSnapshot(_now(), bases));
            AppLog.Write($"base index: REST build done — {bases.Count} bases persisted (offline-capable now)");
        }
        finally
        {
            lock (_gate) _restBuildInFlight = false;
        }
    }

    private async Task<List<BaseSpecies>> FetchBaseIndex()
    {
        // Official GraphQL: evolves_from IS NULL (= base) and id ≤ 649 (Gen-V animated sprite bound).
        // Ditto (#132) is reveal-only and excluded from the normal hatch pool (_neq).
        var query = "{ pokemonspecies(where: {evolves_from_species_id: {_is_null: true}, id: {_lte: "
                    + PokemonAssets.AnimatedSpeciesMax + ", _neq: " + PokemonOdds.DittoSpeciesID
                    + "}}, order_by: {id: asc}) { id capture_rate } }";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.pokeapi.co/v1beta2")
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["query"] = query }),
                Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if ((int)response.StatusCode != 200) throw new HttpRequestException($"status {(int)response.StatusCode}");
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var decoded = JsonSerializer.Deserialize<GraphQlBaseResponse>(bytes, CacheJson)
                      ?? throw new JsonException("empty response");
        var entries = decoded.Data.Pokemonspecies.Select(r => new BaseSpecies(r.Id, r.CaptureRate)).ToList();
        if (entries.Count == 0) throw new JsonException("no base species");
        return entries;
    }

    private async Task<SpeciesDto> Species(int id)
    {
        lock (_gate)
            if (_speciesCache.TryGetValue(id, out var cached)) return cached;
        var dto = await Get<SpeciesDto>($"pokemon-species/{id}").ConfigureAwait(false);
        lock (_gate) _speciesCache[id] = dto;
        return dto;
    }

    /// <summary>REST fallback: one species (pokemon-species/{id}) decides base-ness and capture_rate.
    /// The REST API is a separate endpoint, so it works while the GraphQL index is down.</summary>
    public async Task<BaseSpecies?> BaseSpecies(int id)
    {
        if (id == PokemonOdds.DittoSpeciesID) return null; // Ditto is reveal-only, never a normal hatch
        var dto = await Species(id).ConfigureAwait(false);
        if (dto.EvolvesFromSpecies != null) return null;   // a middle evolution is not a hatch candidate
        return new BaseSpecies(id, dto.CaptureRate);
    }

    private async Task<T> Get<T>(string pathOrUrl)
    {
        var url = pathOrUrl.StartsWith("https://", StringComparison.Ordinal) ? pathOrUrl : BaseUrl + pathOrUrl;
        var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new JsonException("empty response");
    }

    private static EvoNode Node(ChainLink link) =>
        new(Id(link.Species.Url ?? ""), link.EvolvesTo.Select(Node).ToList());

    private static IEnumerable<int> AllIDs(EvoNode n) => new[] { n.SpeciesID }.Concat(n.Children.SelectMany(AllIDs));

    /// <summary>".../pokemon-species/{id}/" → id (0 when unparsable).</summary>
    public static int Id(string speciesUrl)
    {
        var parts = speciesUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[^1], out var id) ? id : 0;
    }

    /// <summary>
    /// Validates the PokéAPI evolution_chain URL (SSRF guard): a server-controlled string, so it is pinned to
    /// https + pokeapi.co and a tampered response cannot make the app fetch an arbitrary host.
    /// </summary>
    public static Uri? ValidatedChainUrl(string raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "pokeapi.co" ? url : null;

    // DTOs (partial decode of PokéAPI responses)

    private sealed record SpeciesDto(
        [property: JsonPropertyName("capture_rate")] int CaptureRate,
        [property: JsonPropertyName("is_legendary")] bool IsLegendary,
        [property: JsonPropertyName("is_mythical")] bool IsMythical,
        [property: JsonPropertyName("names")] List<NameDto> Names,
        [property: JsonPropertyName("evolution_chain")] UrlRef EvolutionChain,
        [property: JsonPropertyName("evolves_from_species")] NamedRef? EvolvesFromSpecies, // null = base
        [property: JsonPropertyName("gender_rate")] int? GenderRate);

    private sealed record NameDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("language")] NamedRef Language);

    internal sealed record NamedRef(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record UrlRef([property: JsonPropertyName("url")] string Url);

    private sealed record ChainDto([property: JsonPropertyName("chain")] ChainLink Chain);

    private sealed record ChainLink(
        [property: JsonPropertyName("species")] NamedRef Species,
        [property: JsonPropertyName("evolves_to")] List<ChainLink> EvolvesTo);

    private sealed record PokemonDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("weight")] int Weight,
        [property: JsonPropertyName("base_experience")] int? BaseExperience,
        [property: JsonPropertyName("types")] List<PokemonTypeDto> Types,
        [property: JsonPropertyName("abilities")] List<PokemonAbilityDto> Abilities,
        [property: JsonPropertyName("stats")] List<PokemonStatDto> Stats,
        [property: JsonPropertyName("moves")] List<PokemonMoveDto> Moves);

    private sealed record PokemonTypeDto(
        [property: JsonPropertyName("slot")] int Slot,
        [property: JsonPropertyName("type")] NamedRef Type);

    private sealed record PokemonAbilityDto(
        [property: JsonPropertyName("is_hidden")] bool IsHidden,
        [property: JsonPropertyName("slot")] int Slot,
        [property: JsonPropertyName("ability")] NamedRef Ability);

    private sealed record PokemonStatDto(
        [property: JsonPropertyName("base_stat")] int BaseStat,
        [property: JsonPropertyName("stat")] NamedRef Stat);

    internal sealed record PokemonMoveDto(
        [property: JsonPropertyName("move")] NamedRef Move,
        [property: JsonPropertyName("version_group_details")] List<PokemonMoveVersionDto> VersionGroupDetails);

    internal sealed record PokemonMoveVersionDto(
        [property: JsonPropertyName("level_learned_at")] int LevelLearnedAt,
        [property: JsonPropertyName("move_learn_method")] NamedRef MoveLearnMethod,
        [property: JsonPropertyName("version_group")] NamedRef VersionGroup);
}
