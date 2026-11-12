using System.Globalization;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>CompanionStore.swift</c> (non-UI logic). The source of truth for the game: usage since install
/// evolves the Pokémon; at the final form plus one more threshold it graduates into the Pokédex (whole line)
/// and a new egg arrives. Evolution trees, rarity and names come from an injected <see cref="IPokeProviding"/>.
///
/// Threading: like the Swift <c>@MainActor</c> class, every member must be used from one thread (the UI
/// thread). Background work that Swift starts with <c>Task { … }</c> is posted to the current
/// <see cref="SynchronizationContext"/> (so it runs after the current call returns, on the same thread), or
/// runs inline when there is none. <see cref="Changed"/> stands in for <c>@Observable</c>.
/// </summary>
public sealed class CompanionStore
{
    // MARK: Observable state

    private CompanionState _state = new();
    /// <summary>The live state. Treat as read-only; every mutation goes through the store's operations.</summary>
    public CompanionState State => _state;
    public CompanionStateKind DisplayState { get; private set; } = CompanionStateKind.Egg;
    /// <summary>Time since the last token usage while <see cref="DisplayState"/> is <c>Sulk</c>, else 0.</summary>
    public TimeSpan SulkIdle { get; private set; }
    /// <summary>Mirror of <c>UsageStore.AlwaysFacesBack</c> (set by <c>UsageStore.UpdateCompanion</c>): a display preference.</summary>
    public bool AlwaysFacesBack { get; set; }
    /// <summary>The companion shows its back sprite on every surface: while sulking, or always when asked to.
    /// An egg has no back to show.</summary>
    public bool FacesBack => _state.Active != null && (AlwaysFacesBack || DisplayState == CompanionStateKind.Sulk);
    public EvoLine? CurrentLine { get; private set; }
    public RepresentativeSubjectInfo RepresentativeSubject { get; private set; } = new(null, false);
    public bool IsHatching { get; private set; }
    private bool _isRevealingDitto;   // de-duplicates the async Ditto reveal (IsHatching's sibling)
    /// <summary>Name shown by the evolution message.</summary>
    public string? JustEvolvedTo { get; private set; }
    public string? JustGraduated { get; private set; }
    private DateTimeOffset? _eventUntil;

    /// <summary>Raised after state changes, in place of Swift's <c>@Observable</c> tracking.</summary>
    public event Action? Changed;

    /// <summary>A companion event to show as a system notification (title, body). Raised only while the
    /// <c>companionNotifications</c> setting is on (default on).</summary>
    public event Action<string, string>? CompanionNotification;

    /// <summary>Hatch/evolution celebration trigger: the UI watches the seq; it plays once even if the popover was closed.</summary>
    public enum CelebrationKind { Hatch, Evolve, DittoReveal }

    public readonly record struct Celebration(CelebrationKind Kind, bool Shiny = false)
    {
        public static Celebration Hatched(bool shiny) => new(CelebrationKind.Hatch, shiny);
        public static readonly Celebration Evolved = new(CelebrationKind.Evolve);
        public static Celebration DittoRevealed(bool shiny) => new(CelebrationKind.DittoReveal, shiny);
    }

    public Celebration? CurrentCelebration { get; private set; }
    public int CelebrationSeq { get; private set; }

    private void FireCelebration(Celebration c)
    {
        CurrentCelebration = c;
        CelebrationSeq += 1;
    }

    /// <summary>Called by the UI after playing the celebration (play once).</summary>
    public void ConsumeCelebration()
    {
        CurrentCelebration = null;
        NotifyChanged();
    }

    /// <summary>"+XP" flash on candy use, also for partial progress without evolving.</summary>
    public int CandyFeedbackSeq { get; private set; }
    public long CandyFeedbackAmount { get; private set; }
    /// <summary>The header calls this after showing "+XP". Without consuming, re-mounting the header (switching tabs)
    /// would show the same value again (regression).</summary>
    public void ConsumeCandyFeedback()
    {
        CandyFeedbackAmount = 0;
        NotifyChanged();
    }

    /// <summary>"Nature changed to X" flash on mint use; same one-shot pattern (seq + consume).</summary>
    public int MintFeedbackSeq { get; private set; }
    public PokemonNature? MintFeedbackNature { get; private set; }
    public void ConsumeMintFeedback()
    {
        MintFeedbackNature = null;
        NotifyChanged();
    }

    private readonly IPokeProviding _provider;
    private readonly Dictionary<int, Task<EvoLine>> _dexNameRequests = new();
    private readonly IPokemonDetailProviding? _detailProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _filePath;
    private readonly bool _persists;
    private readonly IRandomNumberGenerator _rng;
    private readonly bool _dittoDisguiseRollingEnabled;
    private readonly Dictionary<int, PokemonDetails> _pokemonDetailsByID = new();
    public IReadOnlyDictionary<int, PokemonDetails> PokemonDetailsByID => _pokemonDetailsByID;
    public HashSet<int> LoadingPokemonDetailIDs { get; } = [];
    public HashSet<int> FailedPokemonDetailIDs { get; } = [];
    private readonly AppSettings _settings;
    private readonly Func<PokemonNameResource, Task<Dictionary<string, string>?>>? _formNameLoader;
    /// <summary>Detects a replaced active Pokémon within the session, so a result arriving after an await never
    /// overwrites the new one.</summary>
    private int _activeGeneration;
    private DateTimeOffset? _lastAutoSnapshotDate;
    public List<SaveSnapshot> AvailableSnapshots { get; private set; } = [];

    /// <summary>
    /// Loaded localized form names (the Swift app's <c>PokemonNameDisplayStore.names</c>), used for form labels in
    /// display names and notifications. Filled by <see cref="LoadFormNames(IEnumerable{PokemonNameResource})"/>.
    /// </summary>
    public Dictionary<PokemonNameResource, Dictionary<string, string>> FormNames { get; } = new();

    /// <summary>
    /// Optional hook the app sets to warm the next hatch's sprites (static, animated, shiny animated) while still an
    /// egg. Called only for the installed app, like the Swift <c>SpriteStore</c> warm-up.
    /// </summary>
    public Func<int, PokemonForm?, Task>? WarmEggSprites { get; set; }

    // MARK: Difficulty multipliers (settings, not the save)
    //
    // Stored in AppSettings, not CompanionState: a preference, not progress. Inside the save, importing someone
    // else's save would silently change this user's difficulty.

    /// <summary>Growth multiplier: applied to the egg hatch threshold and evolution/graduation thresholds.
    /// Candy XP is not scaled (scaling both would cancel out).</summary>
    public double GrowthDifficulty { get; private set; }
    /// <summary>Shop multiplier: applied to item and egg prices.</summary>
    public double ShopDifficulty { get; private set; }

    public const string GrowthDifficultyKey = "growthDifficulty";
    public const string ShopDifficultyKey = "shopDifficulty";
    public const string CompanionNotificationsKey = "companionNotifications";

    /// <param name="filePath">State file; null = <c>companion-state.json</c> in the state directory, read and written
    /// only by the installed app.</param>
    /// <param name="formNameLoader">Fetches localized names of a form resource (the Swift <c>PokemonNameClient</c>);
    /// null disables form-name loading.</param>
    public CompanionStore(IPokeProviding? provider = null, IPokemonDetailProviding? detailProvider = null,
        Func<DateTimeOffset>? clock = null, string? filePath = null, IRandomNumberGenerator? rng = null,
        bool? dittoDisguiseRollingEnabled = null, AppSettings? settings = null,
        Func<PokemonNameResource, Task<Dictionary<string, string>?>>? formNameLoader = null)
    {
        _provider = provider ?? PokeApiClient.Shared;
        _detailProvider = detailProvider ?? _provider as IPokemonDetailProviding;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _persists = AppEnv.PersistsToUserLocation(filePath);
        _filePath = filePath ?? DefaultPath();
        _rng = rng ?? new SystemRandomNumberGenerator();
        _dittoDisguiseRollingEnabled = dittoDisguiseRollingEnabled ?? AppEnv.IsInstalledApp;
        _settings = settings ?? AppSettings.Current;
        _formNameLoader = formNameLoader;
        var storedGrowth = _settings.GetDouble(GrowthDifficultyKey) ?? PokemonBalance.DefaultDifficulty;
        // Previous releases accepted 0.01%–2000%. Reprice their banked progress before persisting the narrower
        // range, otherwise an upgrade silently changes completion.
        var previousGrowth = double.IsFinite(storedGrowth) ? Math.Min(20, Math.Max(0.0001, storedGrowth)) : 1;
        GrowthDifficulty = PokemonBalance.ClampDifficulty(storedGrowth);
        ShopDifficulty = PokemonBalance.ClampDifficulty(_settings.GetDouble(ShopDifficultyKey) ?? PokemonBalance.DefaultDifficulty);
        Load();
        if (previousGrowth != GrowthDifficulty)
        {
            RescaleBankedGrowth(previousGrowth, GrowthDifficulty);
            Save();
        }
        _settings.Set(GrowthDifficultyKey, GrowthDifficulty);
        _settings.Set(ShopDifficultyKey, ShopDifficulty);
        MigratePokemonProfilesIfNeeded();
        SeedDexCompletionsIfNeeded();
        RefreshRepresentativeSubject();
        RefreshSnapshots();
        if (_state.Active != null) DisplayState = CompanionStateKind.Idle;
    }

    /// <summary>State file location; <c>PTB_STATE_DIR</c> overrides the directory (dev/QA isolation).</summary>
    public static string DefaultPath() => Path.Combine(AppEnv.StateDirectory(), "companion-state.json");

    private void NotifyChanged() => Changed?.Invoke();

    /// <summary>Swift <c>Task { … }</c> on the main actor: runs after the current call, on the same context.</summary>
    private static void Spawn(Func<Task> work)
    {
        if (SynchronizationContext.Current is { } context)
            context.Post(_ => _ = work(), null);
        else
            _ = work();
    }

    // MARK: Derived values (UI)

    public AppLanguage Language => _state.Language;

    public void SetLanguage(AppLanguage lang)
    {
        _state.Language = lang;
        Save();
    }

    // MARK: Difficulty — applied only when saved

    /// <summary>Keeps the earned fraction of this egg/stage. These are progression credits, not usage: lifetime
    /// tokens, provider ledgers and wallet never change here. Never evolves, graduates or hatches by itself.</summary>
    public void SetGrowthDifficulty(double value)
    {
        var clamped = PokemonBalance.ClampDifficulty(value);
        if (clamped == GrowthDifficulty) return;
        RescaleBankedGrowth(GrowthDifficulty, clamped);
        GrowthDifficulty = clamped;
        _settings.Set(GrowthDifficultyKey, clamped);
        Save();
    }

    private void RescaleBankedGrowth(double old, double @new)
    {
        long Rescaled(long credits, long @base)
        {
            // Calculate the old threshold without the new range clamp during migration.
            var oldThreshold = Math.Max(1, (long)Math.Round(@base * old, MidpointRounding.AwayFromZero));
            var newThreshold = Math.Max(1, (long)Math.Round(@base * @new, MidpointRounding.AwayFromZero));
            var value = credits / (double)oldThreshold * newThreshold;
            var rounded = (long)Math.Min(SaveTransfer.MaxTokenValue, Math.Max(0, Math.Floor(value)));
            // Rounding must never turn an incomplete stage into a completed one.
            return credits < oldThreshold ? Math.Min(newThreshold - 1, rounded) : rounded;
        }
        if (_state.Active is { } active)
            active.UsedAtStage = Rescaled(active.UsedAtStage, active.PhaseThreshold);
        else
            _state.EggUsage = Rescaled(_state.EggUsage, PokemonBalance.EggHatchThreshold);
    }

    /// <summary>Prices are pure reads, so there is no state to re-evaluate.</summary>
    public void SetShopDifficulty(double value)
    {
        var clamped = PokemonBalance.ClampDifficulty(value);
        if (clamped == ShopDifficulty) return;
        ShopDifficulty = clamped;
        _settings.Set(ShopDifficultyKey, clamped);
        NotifyChanged();
    }

    /// <summary>The difficulty-adjusted hatch threshold (the first-run hint shows it too).</summary>
    public long EggHatchThreshold => PokemonBalance.Scaled(PokemonBalance.EggHatchThreshold, GrowthDifficulty);

    /// <summary>The difficulty-adjusted stage threshold. Never call <see cref="PokemonBalance.PhaseThreshold"/>
    /// directly: a call site that forgets the multiplier silently falls back to the default difficulty.</summary>
    private long StageThreshold(MonState mon) => PokemonBalance.Scaled(mon.PhaseThreshold, GrowthDifficulty);

    /// <summary>Displayed and charged price: base × shop difficulty. Null when not sold.</summary>
    public long? Price(ItemKind kind) => kind.ShopPrice() is { } p ? PokemonBalance.Scaled(p, ShopDifficulty) : null;

    /// <summary>Actual price of a shop row, eggs included.</summary>
    public long Price(ShopEntry entry) => PokemonBalance.Scaled(entry.Price, ShopDifficulty);

    /// <summary>All UI strings in the current language.</summary>
    public L L => new(Language);

    public bool HasActive => _state.Active != null;
    public Rarity? Rarity => _state.Active?.Rarity;
    /// <summary>Hidden while disguised (revealed with the Ditto reveal).</summary>
    public bool CurrentIsShiny => _state.Active?.DisplaysShiny ?? false;

    /// <summary>
    /// Whether buying a new egg needs the two-step confirmation: only shiny or legendary. Rare gets the normal
    /// confirmation to avoid alert fatigue on repeated premium rerolls. A disguised Ditto follows the
    /// <see cref="CurrentIsShiny"/>/<see cref="Rarity"/> invariants and leaks nothing before the reveal.
    /// </summary>
    public bool IsHighValueCompanion => CurrentIsShiny || Rarity == Core.Rarity.Legendary;
    public PokemonNature? CurrentNature => _state.Active?.Nature;
    public int? GrowthMultiplier => _state.Active?.HasGrowthBoost == true ? PokemonBalance.RepeatGrowthMultiplier : null;

    /// <summary>What the menu bar and floating pet draw. A stored value, so always-on rendering never re-folds the
    /// whole Pokédex.</summary>
    public readonly record struct RepresentativeSubjectInfo(int? SpeciesID, bool IsShiny, PokemonForm? Form = null);

    public int? RepresentativeSpeciesID => _state.RepresentativeSpeciesID;
    public PokemonForm? RepresentativeForm => _state.RepresentativeForm;

    /// <summary>Updated only at save boundaries; checks one pinned species' shininess without building DexSpecies.</summary>
    private void RefreshRepresentativeSubject()
    {
        RepresentativeSubjectInfo next;
        if (_state.RepresentativeSpeciesID is { } selected)
            next = new RepresentativeSubjectInfo(selected,
                _state.OwnsShinySpecies(selected, _state.RepresentativeForm),
                PokemonForm.ResolvedForSpecies(selected, _state.RepresentativeForm));
        else
            next = new RepresentativeSubjectInfo(CurrentSpeciesID, CurrentIsShiny, CurrentForm);
        if (RepresentativeSubject != next) RepresentativeSubject = next;
    }

    /// <summary>Null tracks the current Pokémon automatically. Ids outside the Pokédex are not stored; a failed
    /// request returns false and never silently clears the existing choice.</summary>
    public bool SetRepresentativeSpeciesID(int? id, PokemonForm? form = null)
    {
        if (id is { } i && !_state.OwnsSpecies(i, form)) return false;
        _state.RepresentativeSpeciesID = id;
        _state.RepresentativeForm = PokemonForm.ResolvedForSpecies(id ?? 0, form);
        Save();
        return true;
    }

    public bool IsRepresentative(DexSpecies species) =>
        _state.RepresentativeSpeciesID == species.Id
        && (species.Form == null || _state.RepresentativeForm == species.Form);

    /// <summary>Settings describe the selected form, even though the main Pokédex aggregates the species.</summary>
    public DexSpecies? RepresentativeDexSpecies
    {
        get
        {
            if (RepresentativeSpeciesID is not { } selected) return null;
            var candidates = PokemonForm.HasForms(selected) ? FormSpeciesList(selected) : DexSpeciesList;
            return candidates.FirstOrDefault(IsRepresentative);
        }
    }

    // Egg incubation (no active Pokémon)
    public bool IsEgg => _state.Active == null;
    public bool EggStarted => _state.EggUsage > 0;
    public double EggProgress => Math.Min(1, Math.Max(0, _state.EggUsage / (double)EggHatchThreshold));
    public long EggTokensToHatch => Math.Max(0, EggHatchThreshold - _state.EggUsage);
    /// <summary>The egg is ready (100%) but the PokéAPI request or candidate pick failed; waiting for the next update.</summary>
    public bool IsHatchRetryDelayed { get; private set; }

    public string DisplayName
    {
        get
        {
            if (_state.Active is not { } a || CurrentLine is not { } line) return "Token Egg";
            return FormDisplayName(line.LocalizedName(a.CurrentID, _state.Language), a.CurrentID, CurrentForm);
        }
    }

    public int? CurrentSpeciesID => _state.Active?.CurrentID;
    public PokemonForm? CurrentForm => PokemonForm.ResolvedForSpecies(CurrentSpeciesID ?? 0, _state.Active?.Form);

    public bool IsFinalStage
    {
        get
        {
            if (_state.Active is not { } a || CurrentLine is not { } line) return false;
            return line.Tree.Node(a.CurrentID) is not { } node || node.Children.Count == 0;
        }
    }

    public string StageText
    {
        get
        {
            if (_state.Active is not { } a) return "";
            return IsFinalStage ? L.FinalForm : L.Stage(a.StageIndex + 1, a.TotalForms);
        }
    }

    public long Threshold => _state.Active is { } a ? StageThreshold(a) : 1;

    public double Progress
    {
        get
        {
            if (_state.Active is not { } a || Threshold <= 0) return 0;
            return Math.Min(1, Math.Max(0, a.UsedAtStage / (double)Threshold));
        }
    }

    /// <summary>Follow the companion being raised, even when a Pokédex representative is pinned.</summary>
    public string MenuGrowthText
    {
        get
        {
            var total = IsEgg ? EggHatchThreshold : Threshold;
            var credits = IsEgg ? _state.EggUsage : _state.Active?.UsedAtStage ?? 0;
            var used = Math.Min(Math.Max(0, credits), total);
            // Match Home's integer flooring without losing exact boundaries to double rounding.
            var percent = total > 0 ? used * 100 / total : 0;
            return L.MenuGrowthProgress((int)percent, IsEgg);
        }
    }

    public long TokensToNext => _state.Active is { } a ? Math.Max(0, Threshold - a.UsedAtStage) : 0;

    /// <summary>
    /// Evolution line for display: the realized path plus a preview of what follows. When a branch follows the
    /// single-child stages, the guaranteed prefix is shown with one mystery slot. Branch choices stay hidden as a
    /// mystery slot until the evolution actually happens, even though they were planned at hatch.
    /// </summary>
    public List<EvoLineItem> LineNodes
    {
        get
        {
            if (_state.Active is not { } a || CurrentLine is not { } line) return [];
            var output = RealizedLineItems(a.PathIDs, a.StageIndex);
            if (line.Tree.Node(a.CurrentID) is { } current)
            {
                var node = current;
                var guaranteedPrefix = new List<EvoNode>();
                while (node.Children.Count == 1)
                {
                    var child = node.Children[0];
                    guaranteedPrefix.Add(child);
                    node = child;
                }
                output.AddRange(guaranteedPrefix.Select(n =>
                    new EvoLineItem(EvoLineItemContent.Species(n.SpeciesID), EvoLineItemState.Future)));
                if (node.Children.Count > 1)
                    output.Add(new EvoLineItem(EvoLineItemContent.Mystery, EvoLineItemState.Future));
            }
            return output;
        }
    }

    public static List<EvoLineItem> RealizedLineItems(IReadOnlyList<int> pathIDs, int stageIndex) =>
        pathIDs.Select((id, i) => new EvoLineItem(EvoLineItemContent.Species(id),
            i == stageIndex ? EvoLineItemState.Current : EvoLineItemState.Done)).ToList();

    private Dictionary<int, Dictionary<string, string>>? NamesFromLine(IEnumerable<int> ids)
    {
        if (CurrentLine is not { } line) return null;
        var result = new Dictionary<int, Dictionary<string, string>>();
        foreach (var id in ids)
            if (line.Names.TryGetValue(id, out var n)) result.Add(id, new Dictionary<string, string>(n));
        return result;
    }

    /// <summary>
    /// The Pokédex shows permanently kept graduations plus the Pokémon being raised. The current one is not stored
    /// in the persistent dex; it is synthesized for the screen. At graduation the active Pokémon disappears and its
    /// permanent DexEntry is added, so the count stays the same.
    /// </summary>
    private DexEntry? ActiveDexEntry
    {
        get
        {
            if (_state.Active is not { } active) return null;
            return new DexEntry(
                id: $"active-{active.BaseID}-{active.CurrentID}",
                baseID: active.BaseID,
                finalID: active.CurrentID,
                chainOrder: active.PathIDs,
                rarity: active.Rarity,
                caughtAt: null,
                isShiny: CurrentIsShiny, // a disguised Ditto hides its shininess until revealed (single rule)
                nature: active.Nature,
                profile: active.Profile,
                names: NamesFromLine(active.PathIDs),
                form: active.Form,
                prayers: active.Prayers);
        }
    }

    /// <summary>
    /// The permanent record of a released Pokémon, created when a new egg is bought instead of finishing.
    /// <b>Only reached forms</b> (<c>PathIDs.Take(StageIndex + 1)</c>), the same range the Pokédex showed while
    /// raising — using the planned path would count never-reached evolutions as owned. Shininess uses
    /// <see cref="CurrentIsShiny"/> (a disguised Ditto stays hidden). <c>CaughtAt</c> is the release time, so the
    /// catch log orders it where the record was made.
    /// </summary>
    private DexEntry ReleasedDexEntry(MonState a)
    {
        // Keep at least one form even for a negative / out-of-range stageIndex (corrupt state defence).
        var reached = a.PathIDs.Take(Math.Max(1, a.StageIndex + 1)).ToList();
        var chain = reached.Count == 0 ? [a.BaseID] : reached;
        var now = _clock();
        return new DexEntry(
            id: a.Profile?.InstanceID ?? Guid.NewGuid().ToString().ToUpperInvariant(),
            baseID: a.BaseID,
            finalID: chain[^1],
            chainOrder: chain,
            rarity: a.Rarity,
            caughtAt: now,
            isShiny: CurrentIsShiny,
            nature: a.Nature,
            profile: a.Profile?.Clone(),
            names: NamesFromLine(chain),
            releasedAt: now,
            form: a.Form,
            prayers: a.Prayers);
    }

    public List<DexEntry> DexEntries
    {
        get
        {
            var entries = _state.Dex.ToList();
            if (ActiveDexEntry is { } active) entries.Add(active);
            return entries;
        }
    }

    /// <summary>Whether this is the synthesized current-Pokémon entry (not an old graduation without CaughtAt).</summary>
    public bool IsActiveDexEntry(DexEntry entry) => entry.Id == ActiveDexEntry?.Id;

    /// <summary>
    /// Catch log order: the current Pokémon pinned first, graduations <b>newest first</b>. Old entries without
    /// CaughtAt sort as the distant past (last).
    /// </summary>
    public List<DexEntry> DexEntriesSorted
    {
        get
        {
            var graduated = _state.Dex.OrderByDescending(e => e.CaughtAt ?? DateTimeOffset.MinValue).ToList();
            if (ActiveDexEntry is not { } active) return graduated;
            return [active, .. graduated];
        }
    }

    /// <summary>Catch-log count per rarity (individuals). The species Pokédex uses <see cref="DexSpeciesList"/>.</summary>
    public int DexCount(Rarity rarity) => DexEntries.Count(e => e.Rarity == rarity);

    /// <summary>
    /// One Pokédex cell: per species in the main list, per form in the detail list of species with forms.
    /// <b>Species information only</b> — nature and catch counts belong to individuals (the catch log).
    /// </summary>
    /// <param name="Id">speciesID = Pokédex number (sort key).</param>
    /// <param name="IsShiny">Ever owned shiny.</param>
    /// <param name="IsRaising">The <b>current form</b> of the Pokémon being raised; earlier stages are not marked.</param>
    public sealed record DexSpecies(int Id, string Name, Rarity Rarity, bool IsShiny, bool IsRaising,
        PokemonForm? Form = null, Dictionary<string, string>? Names = null, bool HasNormal = false)
    {
        /// <summary>Species IDs remain Pokédex numbers; selection also includes the form.</summary>
        public string CollectionID => Form is { } f ? $"{Id}-{f.RawValue}" : Id.ToString(CultureInfo.InvariantCulture);
    }

    private readonly record struct DexKey(int SpeciesID, PokemonForm? Form)
    {
        public static DexKey Make(int speciesID, PokemonForm? form, bool groupForms) =>
            new(speciesID, groupForms ? PokemonForm.ResolvedForSpecies(speciesID, form) : null);
    }

    /// <summary>What one species collects, accumulate-only. One struct instead of parallel dictionaries whose key
    /// sets could drift apart.</summary>
    private sealed class DexAccumulator(Rarity rarity)
    {
        /// <summary>Fixed at first sight: a species always comes from the same base line.</summary>
        public Rarity Rarity { get; } = rarity;
        public Dictionary<string, string>? Names;
        public bool IsShiny;
        public bool HasNormal;
    }

    /// <summary>
    /// The Pokédex list: owned species only, by number. Included = graduated <c>ChainOrder</c> ∪ the current
    /// Pokémon's <b>reached</b> <c>PathIDs[0…StageIndex]</c>. <c>PlannedPathIDs</c> includes unreached stages and
    /// is never used.
    /// </summary>
    public List<DexSpecies> DexSpeciesList => CollectedDexSpecies(groupForms: false);

    /// <summary>
    /// Sprite → Pokédex detail link targets (species number → dex cell <c>CollectionID</c>). Only species in the
    /// dex have a key, so eggs, unreached evolutions and uncaught species are not clickable.
    /// </summary>
    public Dictionary<int, string> DexLinkTargets =>
        DexSpeciesList.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Last().CollectionID);

    /// <summary>Collected form summaries for the detail picker; missing forms stay visible but disabled.</summary>
    public List<DexSpecies> FormSpeciesList(int speciesID)
    {
        if (!PokemonForm.HasForms(speciesID)) return [];
        return CollectedDexSpecies(groupForms: true).Where(s => s.Id == speciesID).ToList();
    }

    // MARK: Generation (region) dex completion tracking

    /// <summary>Owned species reduced to the (dex number, shiny) pairs the generation math needs.</summary>
    private List<(int Id, bool IsShiny)> OwnedDexForProgress => DexSpeciesList.Select(s => (s.Id, s.IsShiny)).ToList();

    /// <summary>Per-region owned/total progress, in National Dex order. Drives the Pokédex generation chips.</summary>
    public List<DexCompletion> DexCompletions => DexProgress.RegionCompletions(OwnedDexForProgress);

    /// <summary>National (Gen 1–5) progress for the Pokédex header.</summary>
    public (int Owned, int ShinyOwned, int Total) NationalDexProgress => DexProgress.NationalProgress(OwnedDexForProgress);

    /// <summary>Every completion badge currently earned — derived, never persisted (ownership only grows).</summary>
    public List<DexBadge> EarnedDexBadges => DexProgress.EarnedBadges(OwnedDexForProgress,
        _state.CollectedForms(PokemonForm.UnownSpeciesID).Count);

    /// <summary>
    /// Fires a one-time completion notification for every badge earned since the last check. Idempotent: a badge
    /// already in <c>NotifiedDexCompletions</c> never re-fires. Does not persist on its own — <see cref="Save"/> calls
    /// it just before encoding, so the recorded keys are written in the same pass. Guarded on seeding so pre-feature
    /// completions are recorded by <see cref="SeedDexCompletionsIfNeeded"/>, not announced here.
    /// </summary>
    public void CheckDexCompletions()
    {
        if (!_state.DexCompletionsSeeded) return;
        foreach (var badge in DexProgress.NewlyEarnedBadges(EarnedDexBadges, _state.NotifiedDexCompletions))
        {
            var (title, body) = DexCompletionNotification(badge);
            NotifyCompanionEvent(title, body);
            _state.NotifiedDexCompletions.Add(badge.NotificationKey);
        }
    }

    /// <summary>
    /// On the first run after this feature ships (and after importing a save that predates it), records already-earned
    /// badges as announced <b>without</b> firing — a player who finished a region months ago should not get a
    /// notification burst.
    /// </summary>
    private void SeedDexCompletionsIfNeeded()
    {
        if (_state.DexCompletionsSeeded) return;
        _state.DexCompletionsSeeded = true;
        var keys = EarnedDexBadges.Select(b => b.NotificationKey).ToList();
        // A fresh or just-reset save owns nothing — don't rewrite the state file just to seed an empty set (that would
        // clobber the "corrupt original moved aside, not recreated" invariant). The seeded flag then persists on the
        // next natural save; re-seeding meanwhile is idempotent.
        if (keys.Count == 0) return;
        _state.NotifiedDexCompletions.UnionWith(keys);
        Save();
    }

    private (string Title, string Body) DexCompletionNotification(DexBadge badge) => badge switch
    {
        { Kind: DexBadgeKind.Region, Region: { } r } =>
            (L.NotifDexRegionCompleteTitle(L.RegionName(r)), L.NotifDexRegionCompleteBody(L.RegionName(r))),
        { Kind: DexBadgeKind.RegionShiny, Region: { } r } =>
            (L.NotifDexRegionShinyCompleteTitle(L.RegionName(r)), L.NotifDexRegionShinyCompleteBody(L.RegionName(r))),
        { Kind: DexBadgeKind.Unown } => (L.NotifDexUnownCompleteTitle, L.NotifDexUnownCompleteBody),
        { Kind: DexBadgeKind.National } => (L.NotifDexNationalCompleteTitle, L.NotifDexNationalCompleteBody),
        { Kind: DexBadgeKind.NationalShiny } => (L.NotifDexNationalShinyCompleteTitle, L.NotifDexNationalShinyCompleteBody),
        _ => throw new ArgumentOutOfRangeException(nameof(badge), badge, null),
    };

    private List<DexSpecies> CollectedDexSpecies(bool groupForms)
    {
        var acc = new Dictionary<DexKey, DexAccumulator>();
        foreach (var entry in _state.Dex)
        {
            foreach (var id in entry.ChainOrder)
            {
                var key = DexKey.Make(id, entry.Form, groupForms);
                if (!acc.TryGetValue(key, out var a)) acc[key] = a = new DexAccumulator(entry.Rarity);
                if (entry.Names != null && entry.Names.TryGetValue(id, out var n)) a.Names = n; // nameless old entries never overwrite
                if (entry.IsShiny) a.IsShiny = true;
                else a.HasNormal = true;
            }
        }
        if (_state.Active is { } active)
        {
            // Reached stages only; StageIndex within PathIDs is guaranteed by both entrances (decode clamp and
            // the import sanitizer).
            foreach (var id in active.PathIDs.Take(active.StageIndex + 1))
            {
                var key = DexKey.Make(id, active.Form, groupForms);
                if (!acc.TryGetValue(key, out var a)) acc[key] = a = new DexAccumulator(active.Rarity);
                if (CurrentLine is { } line && line.Names.TryGetValue(id, out var n)) a.Names = n;
                if (CurrentIsShiny) a.IsShiny = true; // reuses the disguise-hiding rule
                else a.HasNormal = true;
            }
        }
        var currentForm = CurrentForm;
        return acc.OrderBy(kv => kv.Key.SpeciesID)
            .ThenBy(kv => PokemonForm.SortOrder(kv.Key.Form, kv.Key.SpeciesID))
            .Select(kv =>
            {
                var (key, a) = (kv.Key, kv.Value);
                var name = (a.Names == null ? null : _state.Language.ResolveName(a.Names)) ?? $"#{key.SpeciesID}";
                return new DexSpecies(
                    key.SpeciesID,
                    groupForms ? FormDisplayName(name, key.SpeciesID, key.Form) : name,
                    a.Rarity,
                    a.IsShiny,
                    key.SpeciesID == _state.Active?.CurrentID && (!groupForms || key.Form == currentForm),
                    key.Form,
                    a.Names,
                    a.HasNormal);
            }).ToList();
    }

    /// <summary>The primary localized name of an individual's final species, for sorting and display.</summary>
    public string DexEntryName(DexEntry entry)
    {
        var targetID = entry.FinalID;
        if (entry.Names != null && entry.Names.TryGetValue(targetID, out var stored)
            && _state.Language.ResolveName(stored) is { } localized)
            return localized;
        foreach (var id in Enumerable.Reverse(entry.ChainOrder))
            if (entry.Names != null && entry.Names.TryGetValue(id, out var s) && _state.Language.ResolveName(s) is { } l)
                return l;
        return $"#{targetID}";
    }

    /// <summary>
    /// The one search rule shared by the Pokédex grid and the Catch Log, so the two screens cannot drift apart.
    /// A query matches a Pokédex number (<c>25</c> or <c>#25</c>) or part of any stored name in any language,
    /// ignoring case and diacritics ("flabebe" finds Flabébé).
    /// </summary>
    public sealed class DexSearchMatcher
    {
        private readonly string _query;
        private readonly long? _number;

        private DexSearchMatcher(string query, long? number)
        {
            _query = query;
            _number = number;
        }

        /// <summary>Null for a blank query, which filters nothing out.</summary>
        public static DexSearchMatcher? Create(string rawQuery)
        {
            var trimmed = rawQuery.Trim();
            if (trimmed.Length == 0) return null;
            var digits = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
            return new DexSearchMatcher(trimmed,
                long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null);
        }

        public bool Matches(DexSpecies species) =>
            Matches([species.Id], new[] { species.Name }.Concat(species.Names?.Values ?? Enumerable.Empty<string>()));

        /// <summary>An individual matches through any species of its chain, like its evolution line in the log row.</summary>
        public bool Matches(DexEntry entry) =>
            Matches(new[] { entry.BaseID, entry.FinalID }.Concat(entry.ChainOrder),
                entry.Names?.Values.SelectMany(n => n.Values) ?? []);

        private bool Matches(IEnumerable<int> ids, IEnumerable<string> names)
        {
            var idList = ids.ToList();
            if (_number is { } number && idList.Any(id => id == number)) return true;
            // Partial numbers keep working: "25" also finds #125, "#25" finds #25 and #250.
            if (idList.Any(id => $"#{id}".Contains(_query, StringComparison.Ordinal))) return true;
            var compare = CultureInfo.InvariantCulture.CompareInfo;
            return names.Any(n => compare.IndexOf(n, _query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
        }
    }

    /// <summary>Filters and sorts DexSpecies for the Pokédex grid.</summary>
    public List<DexSpecies> FilteredDexSpecies(string query = "", Rarity? rarity = null, bool shinyOnly = false,
        PokedexRegion? region = null, DexSortOption sort = DexSortOption.NumberAsc)
    {
        IEnumerable<DexSpecies> result = DexSpeciesList;
        if (rarity is { } r) result = result.Where(s => s.Rarity == r);
        if (region is { } g) result = result.Where(s => g.DexRange().Contains(s.Id));
        if (shinyOnly) result = result.Where(s => s.IsShiny);
        if (DexSearchMatcher.Create(query) is { } matcher) result = result.Where(matcher.Matches);
        return SortDexSpecies(result.ToList(), sort);
    }

    private static int LocalizedCompare(string a, string b) => string.Compare(a, b, StringComparison.CurrentCulture);

    private static List<T> StableSorted<T>(IEnumerable<T> items, Comparison<T> comparison) =>
        items.OrderBy(x => x, Comparer<T>.Create(comparison)).ToList();

    /// <summary>Sorts DexSpecies by the given option.</summary>
    public List<DexSpecies> SortDexSpecies(IEnumerable<DexSpecies> species, DexSortOption sort) => sort switch
    {
        DexSortOption.NumberAsc => StableSorted(species, (a, b) => a.Id.CompareTo(b.Id)),
        DexSortOption.NumberDesc => StableSorted(species, (a, b) => b.Id.CompareTo(a.Id)),
        DexSortOption.NameAsc => StableSorted(species, (a, b) =>
        {
            var cmp = LocalizedCompare(a.Name, b.Name);
            return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
        }),
        DexSortOption.NameDesc => StableSorted(species, (a, b) =>
        {
            var cmp = LocalizedCompare(b.Name, a.Name);
            return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
        }),
        _ => StableSorted(species, (a, b) =>
            a.Rarity.SortRank() != b.Rarity.SortRank()
                ? b.Rarity.SortRank().CompareTo(a.Rarity.SortRank())
                : a.Id.CompareTo(b.Id)),
    };

    /// <summary>Filters and sorts DexEntry records for the Catch Log.</summary>
    public List<DexEntry> FilteredDexEntries(string query = "", Rarity? rarity = null, bool shinyOnly = false,
        CatchLogSortOption sort = CatchLogSortOption.RecentFirst)
    {
        IEnumerable<DexEntry> result = DexEntries;
        if (rarity is { } r) result = result.Where(e => e.Rarity == r);
        if (shinyOnly) result = result.Where(e => e.IsShiny);
        if (DexSearchMatcher.Create(query) is { } matcher) result = result.Where(matcher.Matches);
        return SortDexEntries(result.ToList(), sort);
    }

    private static DateTimeOffset CaughtOrPast(DexEntry e) => e.CaughtAt ?? DateTimeOffset.MinValue;

    /// <summary>Newest first, then id: the shared tie-break of the catch-log orders.</summary>
    private static int RecentThenId(DexEntry a, DexEntry b)
    {
        var d0 = CaughtOrPast(a);
        var d1 = CaughtOrPast(b);
        if (d0 != d1) return d1.CompareTo(d0);
        return string.CompareOrdinal(a.Id, b.Id);
    }

    /// <summary>Sorts DexEntry records by the given option.</summary>
    public List<DexEntry> SortDexEntries(IEnumerable<DexEntry> entries, CatchLogSortOption sort)
    {
        var list = entries.ToList();
        switch (sort)
        {
            case CatchLogSortOption.RecentFirst:
            {
                var active = list.FirstOrDefault(IsActiveDexEntry);
                var graduated = StableSorted(list.Where(e => !IsActiveDexEntry(e)), RecentThenId);
                return active == null ? graduated : [active, .. graduated];
            }
            case CatchLogSortOption.OldestFirst:
            {
                var active = list.FirstOrDefault(IsActiveDexEntry);
                var graduated = StableSorted(list.Where(e => !IsActiveDexEntry(e)), (a, b) =>
                {
                    var d0 = CaughtOrPast(a);
                    var d1 = CaughtOrPast(b);
                    if (d0 != d1) return d0.CompareTo(d1);
                    return string.CompareOrdinal(a.Id, b.Id);
                });
                return active == null ? graduated : [.. graduated, active];
            }
            case CatchLogSortOption.NumberAsc:
                return StableSorted(list, (a, b) => a.FinalID != b.FinalID ? a.FinalID.CompareTo(b.FinalID) : RecentThenId(a, b));
            case CatchLogSortOption.NumberDesc:
                return StableSorted(list, (a, b) => a.FinalID != b.FinalID ? b.FinalID.CompareTo(a.FinalID) : RecentThenId(a, b));
            case CatchLogSortOption.NameAsc:
                return StableSorted(list, (a, b) =>
                {
                    var cmp = LocalizedCompare(DexEntryName(a), DexEntryName(b));
                    return cmp != 0 ? cmp : RecentThenId(a, b);
                });
            case CatchLogSortOption.NameDesc:
                return StableSorted(list, (a, b) =>
                {
                    var cmp = LocalizedCompare(DexEntryName(b), DexEntryName(a));
                    return cmp != 0 ? cmp : RecentThenId(a, b);
                });
            default:
                return StableSorted(list, (a, b) =>
                    a.Rarity.SortRank() != b.Rarity.SortRank()
                        ? b.Rarity.SortRank().CompareTo(a.Rarity.SortRank())
                        : RecentThenId(a, b));
        }
    }

    /// <summary>
    /// Refreshes legacy names once, including saves that kept only app-supported languages. The grid reads stored
    /// names only, so without this backfill cells stay as numbers (<c>#41</c>). Lines are cached per base by the
    /// provider, so one network call per line. Offline, <see cref="DexResolveChainNames"/> stores nothing and the
    /// next visit retries.
    /// </summary>
    public async Task BackfillMissingDexNames()
    {
        foreach (var entry in _state.Dex.Where(e => e.NeedsNamesRefresh).ToList())
            await DexResolveChainNames(entry); // stores successful results into the dex itself
    }

    /// <summary>Stored chain names (speciesID → current-language name) instantly, or null (the view falls back to
    /// an async lookup).</summary>
    public Dictionary<int, string>? DexStoredChainNames(DexEntry entry)
    {
        if (entry.Names is not { Count: > 0 } names) return null;
        var result = new Dictionary<int, string>();
        foreach (var (id, byLang) in names)
            if (_state.Language.ResolveName(byLang) is { } n) result[id] = n;
        return result;
    }

    private async Task<EvoLine> DexNameLine(int baseID)
    {
        if (_dexNameRequests.TryGetValue(baseID, out var pending)) return await pending;
        var request = _provider.Line(baseID);
        _dexNameRequests[baseID] = request;
        try
        {
            return await request;
        }
        finally
        {
            _dexNameRequests.Remove(baseID);
        }
    }

    /// <summary>
    /// Refreshes missing/legacy multilingual names; current versions need no lookup. Offline, saved names are kept
    /// and species numbers fill only the gaps. Returns every <c>ChainOrder</c> species → current-language name.
    /// </summary>
    public async Task<Dictionary<int, string>> DexResolveChainNames(DexEntry entry)
    {
        // Another row of this evolution line may already have refreshed the stored entry.
        entry = _state.Dex.FirstOrDefault(e => e.Id == entry.Id) ?? entry;
        if (!entry.NeedsNamesRefresh && DexStoredChainNames(entry) is { } stored) return stored;
        var oldNames = DexStoredChainNames(entry) ?? [];
        EvoLine line;
        try
        {
            line = await DexNameLine(entry.BaseID);
        }
        catch (Exception)
        {
            return entry.ChainOrder.Distinct().ToDictionary(id => id, id => oldNames.TryGetValue(id, out var n) ? n : $"#{id}");
        }
        // Keep usable older names when a response is partial. Only a complete chain gets the new version, so
        // partial/offline responses stay eligible for retry.
        DexEntry Refreshed(DexEntry original)
        {
            var result = original.Clone();
            var merged = DexEntry.CloneNames(original.Names) ?? [];
            foreach (var id in original.ChainOrder)
            {
                if (line.Names.TryGetValue(id, out var incoming) && incoming.Count > 0)
                {
                    if (!merged.TryGetValue(id, out var existing)) merged[id] = existing = [];
                    foreach (var (lang, name) in incoming) existing[lang] = name;
                }
            }
            result.Names = merged.Count == 0 ? null : merged;
            if (original.ChainOrder.All(id => line.Names.TryGetValue(id, out var n) && n.Count > 0))
                result.NamesVersion = DexEntry.CurrentNamesVersion;
            return result;
        }
        // One successful lookup also refreshes duplicate catches of the same evolution line.
        for (var i = 0; i < _state.Dex.Count; i++)
            if (_state.Dex[i].BaseID == entry.BaseID && _state.Dex[i].NeedsNamesRefresh)
                _state.Dex[i] = Refreshed(_state.Dex[i]);
        Save();
        var chainNames = Refreshed(entry).Names ?? [];
        return entry.ChainOrder.Distinct().ToDictionary(id => id, id =>
            (chainNames.TryGetValue(id, out var n) ? _state.Language.ResolveName(n) : null) ?? $"#{id}");
    }

    // MARK: Update (the app calls this with UsageStore values)

    /// <summary>
    /// Feeds usage into the companion. <paramref name="todayTokensByProvider"/> holds only providers whose today
    /// value is confirmed (keyed by <c>UsageProvider.id</c>); <paramref name="hasUsageData"/> is whether any
    /// snapshot exists for display. <paramref name="timeSinceLastUsage"/>: null when unknown (see
    /// <c>UsageStore.TimeSinceLastUsage</c>); unknown never sulks. It comes from the slower enrichment scan, so a
    /// rise in today's total seen here counts as usage at once.
    /// </summary>
    public void Update(IReadOnlyDictionary<string, long> todayTokensByProvider, string todayDate, long monthTotal,
        BurnTier burnTier, bool limitWarning, bool hasUsageData, TimeSpan? timeSinceLastUsage = null)
    {
        var todayTokens = todayTokensByProvider.Values.Sum();
        // hasUsageData only says a snapshot exists for display; this map holds providers whose today date is
        // confirmed. A stale snapshot or a today == nil carrier is not an observation that may move the ledger.
        var hasCurrentProviderData = hasUsageData && todayTokensByProvider.Count > 0;
        var idle = EffectiveIdle(timeSinceLastUsage, todayDate, todayTokens, hasCurrentProviderData);
        if (!_state.InstallBaselineSet)
        {
            // Install baseline: today's value at the moment real data arrives (earlier usage not counted).
            // Not set before data arrives (the empty refresh right after launch).
            if (!hasCurrentProviderData)
            {
                // A save import may have handed the baseline decision here (SaveTransfer.RebasedForThisDevice). The
                // Pokémon is already in, so it must not show as an egg and its line must keep loading — a new
                // machine has no usage data until the first AI CLI run, which would otherwise mean an egg all day.
                DisplayState = _state.Active == null ? CompanionStateKind.Egg : CompanionStateKind.Idle;
                KickLineLoadIfNeeded();
                NotifyChanged();
                return;
            }
            _state.InstallBaselineSet = true;
            _state.ClaimedTodayTokensByProvider = new Dictionary<string, long>(todayTokensByProvider);
            _state.LastDate = todayDate;
            Save();
        }
        else if (hasCurrentProviderData)
        {
            // A refresh with only today == nil carriers or failed parsing can have an empty map. Moving the date or
            // ledger on such an observation would treat the next real snapshot as a whole day of new usage, so only
            // refreshes with valid usage update the ledger.
            var dateChanged = todayDate != _state.LastDate;
            if (_state.ClaimedTodayTokensByProvider == null)
            {
                // Old saves only had an aggregate high-water mark that cannot be split per provider. The first valid
                // observation only seeds the new ledger; past usage is not credited retroactively.
                _state.ClaimedTodayTokensByProvider = new Dictionary<string, long>(todayTokensByProvider);
                _state.LastDate = todayDate;
                AppLog.Write($"companion provider ledger seeded date={todayDate} providers={string.Join(",", todayTokensByProvider.Keys.Order(StringComparer.Ordinal))}");
            }
            else if (dateChanged)
            {
                // Daily snapshots are not comparable across dates: on a new date the whole cumulative value counts
                // as the new date's usage. A provider known yesterday may be missing from the first refresh (no data
                // today, stale, transient failure); dropping it would later seed its recovered value as "already
                // credited" and lose usage, so known providers open the new date at 0.
                _state.LastDate = todayDate;
                var newLedger = _state.ClaimedTodayTokensByProvider.Keys.ToDictionary(k => k, _ => 0L);
                foreach (var (providerID, current) in todayTokensByProvider) newLedger[providerID] = current;
                _state.ClaimedTodayTokensByProvider = newLedger;
                var delta = todayTokensByProvider.Values.Sum();
                if (delta > 0) Credit(delta);
            }
            else
            {
                var ledger = _state.ClaimedTodayTokensByProvider;
                long delta = 0;
                foreach (var (providerID, current) in todayTokensByProvider)
                {
                    if (!ledger.TryGetValue(providerID, out var previous))
                    {
                        // A newly observed provider's past logs are not credited retroactively; seed its value so
                        // later refreshes track its increase.
                        ledger[providerID] = current;
                        continue;
                    }
                    if (current < previous)
                    {
                        // Rebase only this provider's line, not the total. A provider that did not report this
                        // refresh has no line in the map, so its baseline stays untouched.
                        ledger[providerID] = current;
                        AppLog.Write($"companion usage regression provider={providerID} date={todayDate} previous={previous} current={current} drop={previous - current} — rebased provider ledger");
                        continue;
                    }
                    delta += current - previous;
                    ledger[providerID] = current;
                }
                if (delta > 0) Credit(delta);
            }
        }
        // Event window (evolution/graduation/hatch) expiry clears the message flags with the .levelUp window.
        // JustEvolvedTo is cleared only here: clearing it at the start of every update reverted "evolved into …"
        // to "grew" when an update tick fell inside the 4-second window (regression #4).
        if (_eventUntil is { } until && _clock() > until)
        {
            JustGraduated = null;
            JustEvolvedTo = null;
            _eventUntil = null;
        }
        if (_state.Active == null && _state.InstallBaselineSet && !IsHatching)
        {
            if (_state.EggUsage >= EggHatchThreshold)
                // A ready egg hatches first. Racing a prefetch in a separate task on the same tick could make the
                // hatch see the prefetch lock, return, and miss the failure state.
                Spawn(HatchIfNeeded);
            else
                // Before the threshold: pre-roll the species and warm line/sprites (no delay at hatch).
                Spawn(EnsureEggPrefetch);
        }
        // Active but line not loaded (app restart) → load.
        if (_state.Active != null && CurrentLine == null && !IsHatching) Spawn(LoadCurrentLine);
        // A disguised Ditto reached its first evolution threshold → reveal (backup trigger when the applyUsage kick
        // was missed, e.g. after a restart).
        if (_state.Active is { } a && a.DittoDisguise != null && !a.DittoRevealed && CurrentLine != null
            && !IsHatching && !_isRevealingDitto && a.UsedAtStage >= StageThreshold(a))
            Spawn(RevealDitto);
        DisplayState = ComputeState(burnTier, limitWarning, hasUsageData, todayTokens, idle);
        SulkIdle = DisplayState == CompanionStateKind.Sulk ? idle ?? TimeSpan.Zero : TimeSpan.Zero;
        Save();
        AutoSnapshotIfNeeded();
    }

    private void Credit(long delta)
    {
        _state.UsedSinceInstall += delta;
        if (_state.Active == null) _state.EggUsage += delta; // egg incubation
        else ApplyUsage(delta);
    }

    /// <summary>
    /// Applies a token delta to the current Pokémon — evolves/graduates at thresholds. Usage is always credited
    /// even while the line is not loaded (restart, offline): dropping it here would lose it for good because the
    /// provider ledger already advanced. Only the evolution decision waits for the line.
    /// </summary>
    public void ApplyUsage(long delta)
    {
        if (_state.Active == null) return;
        _state.Active.UsedAtStage += delta;
        ReconcileActiveProfileGrowth();
        if (CurrentLine is not { } line)
        {
            Save();
            return;
        }
        var guardCount = 0;
        while (_state.Active != null && guardCount < 50)
        {
            guardCount += 1;
            var a = _state.Active;
            var thr = StageThreshold(a);
            if (a.UsedAtStage < thr) break;
            if (line.Tree.Node(a.CurrentID) is not { } node) break;
            // The disguise is multi-form at hatch but can become a leaf after asset normalization/migration, so
            // reveal before terminal graduation or the disguise species would graduate into the Pokédex by mistake.
            if (a.DittoDisguise != null && !a.DittoRevealed)
            {
                if (!_isRevealingDitto) Spawn(RevealDitto);
                break;
            }
            if (node.Children.Count == 0)
            {
                Graduate();
                break;
            }
            var nextIndex = a.StageIndex + 1;
            EvoNode next;
            if (nextIndex < a.PlannedPathIDs.Count
                && node.Children.FirstOrDefault(c => c.SpeciesID == a.PlannedPathIDs[nextIndex]) is { } planned)
            {
                next = planned;
            }
            else
            {
                next = PickPlannedChild(node, a.BaseID);
                var fallbackRoute = new List<int> { node.SpeciesID };
                fallbackRoute.AddRange(MakeEvolutionPlan(next, a.BaseID));
                var repaired = RepairedPlan(a.PathIDs, a.StageIndex, fallbackRoute);
                a.PlannedPathIDs = repaired;
                a.TotalForms = repaired.Count;
                AppLog.Write($"evolve: repaired invalid planned path for base {a.BaseID}");
            }
            var form = a.Form;
            var oldName = FormDisplayName(line.LocalizedName(a.CurrentID, _state.Language), a.CurrentID, form);
            var usedAtStage = a.UsedAtStage;
            a.PathIDs = a.PathIDs.Take(a.StageIndex + 1).Append(next.SpeciesID).ToList();
            a.StageIndex += 1;
            a.UsedAtStage = usedAtStage - thr; // overflow carries over
            if (_pokemonDetailsByID.TryGetValue(next.SpeciesID, out var details))
                a.Profile?.Enrich(details);
            else if (_detailProvider != null)
            {
                var speciesID = next.SpeciesID;
                Spawn(() => LoadPokemonDetails(speciesID));
            }
            var newName = FormDisplayName(line.LocalizedName(next.SpeciesID, _state.Language), next.SpeciesID, form);
            JustEvolvedTo = newName;
            FireCelebration(Celebration.Evolved);
            // Short levelUp window so "evolved into …" shows at the moment of evolution (same pattern as
            // hatch/graduate); without it ComputeState never returned .levelUp and the message was dead code.
            _eventUntil = _clock().AddSeconds(4);
            NotifyCompanionEvent(L.NotifEvolveTitle, L.NotifEvolveBody(oldName, newName));
        }
        ReconcileActiveProfileGrowth();
        Save();
    }

    private EvoNode PickPlannedChild(EvoNode node, int baseID)
    {
        var fresh = node.Children.Where(ch =>
            ch.FinalIDs.Any(f => !_state.CollectedFinals.Contains($"{baseID}:{f}"))).ToList();
        var pool = fresh.Count == 0 ? node.Children.ToList() : fresh;
        return pool[(int)(_rng.Next() % (ulong)pool.Count)];
    }

    private List<int> MakeEvolutionPlan(EvoNode root, int baseID)
    {
        var plan = new List<int> { root.SpeciesID };
        var node = root;
        while (node.Children.Count > 0)
        {
            var next = PickPlannedChild(node, baseID);
            plan.Add(next.SpeciesID);
            node = next;
        }
        return plan;
    }

    public static List<int> RepairedPlan(IReadOnlyList<int> realizedPath, int stageIndex, IReadOnlyList<int> fallbackRoute)
    {
        if (realizedPath.Count == 0) return fallbackRoute.ToList();
        var currentIndex = Math.Min(stageIndex, realizedPath.Count - 1);
        var prefix = realizedPath.Take(currentIndex + 1).ToList();
        if (fallbackRoute.Count == 0 || fallbackRoute[0] != prefix[^1]) return prefix;
        return prefix.Concat(fallbackRoute.Skip(1)).ToList();
    }

    /// <summary>The longest id path that actually continues from the root, and its last valid node. A first id that
    /// differs from the root recovers to the root.</summary>
    private static (List<int> Path, EvoNode LastNode) LongestValidPath(IReadOnlyList<int> ids, EvoNode root)
    {
        var path = new List<int> { root.SpeciesID };
        var node = root;
        if (ids.Count == 0 || ids[0] != root.SpeciesID) return (path, node);
        foreach (var id in ids.Skip(1))
        {
            if (node.Children.FirstOrDefault(c => c.SpeciesID == id) is not { } child) break;
            path.Add(id);
            node = child;
        }
        return (path, node);
    }

    /// <summary>Fits the saved path and plan to the current asset tree. Only a complete plan is reused, so a
    /// restart does not consume RNG.</summary>
    private MonState NormalizedEvolutionState(MonState saved, EvoNode root)
    {
        var normalized = saved.Clone();
        var realized = LongestValidPath(saved.PathIDs, root);
        var candidate = LongestValidPath(saved.PlannedPathIDs, root);
        var canReusePlan = candidate.Path.SequenceEqual(saved.PlannedPathIDs)
                           && candidate.Path.Take(realized.Path.Count).SequenceEqual(realized.Path)
                           && candidate.Path.Count >= realized.Path.Count
                           && candidate.LastNode.Children.Count == 0;
        List<int> plan;
        if (canReusePlan)
            plan = candidate.Path;
        else
        {
            var suffix = MakeEvolutionPlan(realized.LastNode, saved.BaseID);
            plan = realized.Path.Concat(suffix.Skip(1)).ToList();
        }
        normalized.PathIDs = realized.Path;
        normalized.PlannedPathIDs = plan;
        normalized.StageIndex = realized.Path.Count - 1;
        normalized.TotalForms = plan.Count;
        return normalized;
    }

    private void Graduate()
    {
        if (_state.Active is not { } a) return;
        a.Profile?.AdvanceGrowth(PokemonBalance.GraduationTotal(a.Rarity), a.Rarity);
        if (_pokemonDetailsByID.TryGetValue(a.CurrentID, out var details)) a.Profile?.Enrich(details);
        var finalID = a.CurrentID;
        _state.CollectedFinals.Add($"{a.BaseID}:{finalID}");
        _state.Dex.Add(new DexEntry(
            id: a.Profile?.InstanceID ?? Guid.NewGuid().ToString().ToUpperInvariant(),
            baseID: a.BaseID, finalID: finalID,
            chainOrder: a.PathIDs, rarity: a.Rarity, caughtAt: _clock(),
            isShiny: a.IsShiny, nature: a.Nature,
            profile: a.Profile?.Clone(),
            names: NamesFromLine(a.PathIDs), // every chain species' multilingual names (instant display)
            form: a.Form,
            prayers: a.Prayers));
        var name = FormDisplayName(CurrentLine?.LocalizedName(finalID, _state.Language) ?? "", finalID, a.Form);
        JustGraduated = name;
        NotifyCompanionEvent(L.NotifGraduateTitle, L.NotifGraduateBody(name));
        _eventUntil = _clock().AddSeconds(6);
        _state.Active = null;
        _state.ReconcileRepresentativeSelection(); // the chain moved into the dex, so the choice stays valid
        _activeGeneration += 1;
        CurrentLine = null;
        _state.EggUsage = 0; // the new egg incubates from scratch
        _state.ResetEggPrayers();
        IsHatchRetryDelayed = false;
        // EggTier is untouched: an active Pokémon existed, so the guarantee is already null (hatch consumed it;
        // disk/import normalize via Sanitized). HatchCore stays the single consumer.
        // Start prefetching the moment the egg arrives: species, line and sprites of the next hatch.
        Spawn(EnsureEggPrefetch);
    }

    // MARK: Inventory / Rare Candy

    public int RareCandyCount => ItemCount(ItemKind.RareCandy);
    public int ItemCount(ItemKind kind) => _state.Inventory.TryGetValue(kind.RawValue(), out var c) ? c : 0;
    /// <summary>Passive, so count &gt; 0 = owned (lowers the hatch shiny denominator).</summary>
    public bool OwnsShinyCharm => ItemCount(ItemKind.ShinyCharm) > 0;

    /// <summary>Owned items (count &gt; 0) for the bag, in <see cref="ItemKindExtensions.AllCases"/> order.</summary>
    public List<(ItemKind Kind, int Count)> OwnedItems =>
        ItemKindExtensions.AllCases.Select(k => (k, ItemCount(k))).Where(x => x.Item2 > 0).ToList();

    /// <summary>Usable with an active Pokémon, a loaded line and stock &gt; 0. Disabled while the line is not loaded
    /// (restart, offline) so a candy cannot bank XP without evolving.</summary>
    public bool CanUseRareCandy => MaxRareCandyUseCount > 0;

    /// <summary>Same rounded, difficulty-adjusted thresholds as actual growth. Previews the apparent evolution path
    /// so hidden identities cannot change the picker.</summary>
    private List<long> RareCandyStageCosts
    {
        get
        {
            if (_state.Active is not { } active || CurrentLine == null) return [];
            var mon = active.Clone();
            if (mon.DittoDisguise != null && !mon.DittoRevealed && mon.UsedAtStage >= StageThreshold(mon))
                return []; // The accepted batch is already saved. Wait for the reveal before accepting another.
            var costs = new List<long>();
            for (var stage = mon.StageIndex; stage < mon.TotalForms; stage++)
            {
                mon.StageIndex = stage;
                costs.Add(StageThreshold(mon));
            }
            return costs;
        }
    }

    public int MaxRareCandyUseCount
    {
        get
        {
            var remaining = Math.Max(0, RareCandyStageCosts.Sum() - (_state.Active?.UsedAtStage ?? 0));
            var needed = remaining / RareCandy.Xp + (remaining % RareCandy.Xp == 0 ? 0 : 1);
            return (int)Math.Min(RareCandyCount, needed);
        }
    }

    public sealed record RareCandyUsePlan(int Count, bool Evolves, bool Graduates, long CarryoverXP, long DiscardedXP)
    {
        public long Xp => Count * RareCandy.Xp;
    }

    public RareCandyUsePlan? PlanRareCandyUse(int count)
    {
        var used = Math.Min(Math.Max(0, count), MaxRareCandyUseCount);
        if (used <= 0 || _state.Active is not { } mon) return null;
        var costs = RareCandyStageCosts;
        var remaining = mon.UsedAtStage + used * RareCandy.Xp;
        var evolves = false;
        for (var index = 0; index < costs.Count; index++)
        {
            var cost = costs[index];
            if (remaining < cost) break;
            remaining -= cost;
            if (index == costs.Count - 1)
                return new RareCandyUsePlan(used, evolves, Graduates: true, CarryoverXP: 0, DiscardedXP: remaining);
            evolves = true;
        }
        return new RareCandyUsePlan(used, evolves, Graduates: false, CarryoverXP: evolves ? remaining : 0, DiscardedXP: 0);
    }

    /// <summary>Candy use result, for UI feedback branching.</summary>
    public enum CandyUseResult { Evolved, Graduated, Progressed, Unavailable }

    /// <summary>
    /// Spends only the candies the current growth plan needs, keeping unused inventory. Candy XP goes into growth
    /// (UsedAtStage) only — never into lifetime/today usage statistics.
    /// </summary>
    public CandyUseResult UseRareCandy(int count = 1)
    {
        if (PlanRareCandyUse(count) is not { } preview) return CandyUseResult.Unavailable;
        var consumed = preview.Count;
        if (_state.Active is { } mon && mon.DittoDisguise != null && !mon.DittoRevealed)
        {
            // Keep the picker indistinguishable from the apparent Pokémon, but preserve whole candies beyond the
            // surprise reveal. Only the last consumed candy's XP carries over.
            var remaining = Math.Max(0, StageThreshold(mon) - mon.UsedAtStage);
            var needed = remaining / RareCandy.Xp + (remaining % RareCandy.Xp == 0 ? 0 : 1);
            consumed = (int)Math.Min(consumed, needed);
        }
        var xp = consumed * RareCandy.Xp;
        _state.Inventory[ItemKind.RareCandy.RawValue()] = RareCandyCount - consumed;
        var beforeStage = _state.Active?.StageIndex ?? 0;
        // Immediate "+XP" feedback even without evolving; the header shows it apart from the celebration.
        CandyFeedbackAmount = xp;
        CandyFeedbackSeq += 1;
        ApplyUsage(xp); // saves inventory and growth together, with the usual evolution effects
        if (_state.Active == null) return CandyUseResult.Graduated;
        if (_state.Active.StageIndex > beforeStage) return CandyUseResult.Evolved;
        return CandyUseResult.Progressed;
    }

    // MARK: Mint (random nature reset)

    /// <summary>Usable with an active Pokémon and stock &gt; 0. Nature lives only in MonState, so unlike candy it does
    /// not need the line (works right after a restart or offline).</summary>
    public bool CanUseMint => HasActive && ItemCount(ItemKind.Mint) > 0;

    /// <summary>
    /// Uses one mint: replaces the nature with a random <b>different</b> one (always changes). Purely cosmetic.
    /// Returns the new nature, or null when unusable (nothing consumed).
    /// </summary>
    public PokemonNature? UseMint()
    {
        if (!CanUseMint || _state.Active is not { } active) return null;
        var cur = active.Nature;
        var pool = PokemonNatureExtensions.AllCases.Where(n => n != cur).ToList(); // cur = null (old save) → all 25
        var @new = pool[(int)(_rng.Next() % (ulong)pool.Count)];
        active.Nature = @new;
        _state.Inventory[ItemKind.Mint.RawValue()] = ItemCount(ItemKind.Mint) - 1;
        MintFeedbackNature = @new;
        MintFeedbackSeq += 1;
        Save();
        return @new;
    }

    // MARK: Shop (currency = used tokens)

    /// <summary>Spendable tokens = lifetime usage − shop spending. Purchases only raise the spending ledger
    /// (growth, today/week/month statistics untouched).</summary>
    public long AvailableTokens => Math.Max(0, _state.UsedSinceInstall - _state.SpentTokens);

    /// <summary>Items for sale, cheapest first; passive items already bought sink to the bottom.</summary>
    public List<ItemKind> PurchasableItems =>
        StableSorted(ItemKindExtensions.AllCases.Where(k => k.ShopPrice() != null), (a, b) =>
        {
            // Passive items already bought go last: they cannot be bought again.
            var aDone = a.IsPassive() && ItemCount(a) > 0;
            var bDone = b.IsPassive() && ItemCount(b) > 0;
            if (aDone != bDone) return aDone ? 1 : -1;
            return (a.ShopPrice() ?? 0).CompareTo(b.ShopPrice() ?? 0); // the rest by price
        });

    /// <summary>
    /// Shop order: items for sale plus the three eggs in one ascending price list; bought passive items last.
    /// Eggs stay listed while there is no active Pokémon (egg state) — <see cref="CanBuyEgg"/>'s HasActive gate
    /// blocks the purchase and the card shows why. Grouping eggs as a tier ladder was rejected: plain price order is
    /// the rule that fixed eggs being appended below the pricier charm (see ShopTests).
    /// </summary>
    public List<ShopEntry> ShopEntries
    {
        get
        {
            var entries = PurchasableItems.Select(ShopEntry.ForItem).ToList();
            entries.AddRange(FreshEgg.ShopTiers.Select(ShopEntry.ForEgg));
            return StableSorted(entries, (a, b) =>
            {
                var aDone = IsPurchasedPassive(a);
                var bDone = IsPurchasedPassive(b);
                if (aDone != bDone) return aDone ? 1 : -1;
                return Price(a).CompareTo(Price(b));
            });
        }
    }

    /// <summary>A bought passive item (Shiny Charm, …), sorted last. Eggs are instant actions with no ownership.</summary>
    private bool IsPurchasedPassive(ShopEntry entry) =>
        entry.Item is { } kind && kind.IsPassive() && ItemCount(kind) > 0;

    /// <summary>Buyable when the balance covers the price (false when not sold). Independent of active/egg —
    /// stock can be bought ahead.</summary>
    public bool CanBuy(ItemKind kind)
    {
        if (Price(kind) is not { } price) return false;
        if (kind.IsPassive() && ItemCount(kind) > 0) return false; // passive items: once only
        return AvailableTokens >= price;
    }

    /// <summary>Max count purchasable at once: balance ÷ price. Passive items cap at 1; 0 when owned, unsold or broke.</summary>
    public int MaxBuyCount(ItemKind kind)
    {
        if (!CanBuy(kind) || Price(kind) is not { } price || price <= 0) return 0;
        return kind.IsPassive() ? 1 : (int)Math.Min(int.MaxValue, AvailableTokens / price);
    }

    /// <summary>Buys <paramref name="count"/> items: price × count from the wallet, inventory +count. Growth and
    /// statistics untouched. Out-of-range counts, unsold items or low balance are a no-op (false), never partial.</summary>
    public bool Buy(ItemKind kind, int count = 1)
    {
        if (count < 1 || count > MaxBuyCount(kind) || Price(kind) is not { } price) return false;
        _state.SpentTokens += price * count;
        _state.Inventory[kind.RawValue()] = ItemCount(kind) + count;
        Save();
        return true;
    }

    // Candy-only wrappers, kept for existing callers/tests.
    public bool CanBuyRareCandy => CanBuy(ItemKind.RareCandy);
    public bool BuyRareCandy() => Buy(ItemKind.RareCandy);

    // MARK: Eggs (reroll — discard the current Pokémon; Pokédex and odds unaffected)

    /// <summary>The rarity floor the current egg guarantees (UI). Null while a Pokémon is active (no egg).</summary>
    public Rarity? EggGuarantee => _state.Active == null ? _state.EggTier : null;

    /// <summary>
    /// Buyable only with an active Pokémon to release and a wallet covering that tier. Buying in the egg state was
    /// rejected to keep one meaning: "release the current one and roll again". The row itself stays listed.
    /// </summary>
    public bool CanBuyEgg(Rarity? tier)
    {
        // Check the tier is sold first: an unsatisfiable guarantee (legendary — not expressible by capture_rate)
        // would leave zero candidates on both roll paths, never hatch, never release the guarantee, and HasActive
        // would block any way out. Enforcing the sale list here protects every caller.
        if (!FreshEgg.ShopTiers.Contains(tier)) return false;
        return HasActive && AvailableTokens >= Price(ShopEntry.ForEgg(tier));
    }

    /// <summary>
    /// Buys an egg: releases the current Pokémon and starts incubating a new egg, charging the tier price. Mirrors
    /// Graduate()'s egg reset, but the released Pokémon <b>stays in the Pokédex</b> (<see cref="ReleasedDexEntry"/>).
    /// CollectedFinals is untouched (it was not raised to the end); growth is lost. The species is not rolled here
    /// (rolling needs the network; offline the tokens would just vanish) — only the guarantee is recorded and the
    /// prefetch/hatch path rolls with it.
    /// </summary>
    public bool BuyEgg(Rarity? tier)
    {
        if (!CanBuyEgg(tier)) return false;
        _state.SpentTokens += Price(ShopEntry.ForEgg(tier));
        if (_state.Active is { } a) _state.Dex.Add(ReleasedDexEntry(a)); // release record: species stay in the Pokédex
        _state.Active = null; // released (not graduated — CollectedFinals unchanged)
        // The released species is in the dex now, so the representative stays; only a truly lost one (corrupt
        // state) reverts to automatic tracking.
        _state.ReconcileRepresentativeSelection();
        _activeGeneration += 1;
        CurrentLine = null;
        _state.EggUsage = 0; // the new egg incubates from scratch (5M to hatch again)
        _state.ResetEggPrayers();
        IsHatchRetryDelayed = false;
        _state.EggTier = tier; // rarity guarantee (null = none)
        _state.PendingHatchID = null; // roll from scratch with the new guarantee
        _state.PendingForm = null;
        _prefetchedLineID = null;
        JustGraduated = null;
        JustEvolvedTo = null;
        _eventUntil = null;
        AppLog.Write($"egg purchased: discarded active, tier={tier?.RawValue() ?? "none"}");
        Spawn(EnsureEggPrefetch); // warm the next hatch
        Save();
        return true;
    }

    /// <summary>Pray over the egg. A placebo: it only counts, and never touches an RNG or a roll — the species is
    /// usually already in <c>PendingHatchID</c> (prefetched at purchase) long before anyone prays.</summary>
    public void Pray()
    {
        if (_state.Active != null) return;
        _state.EggPrayers = new PrayerRecord((_state.EggPrayers?.Count ?? 0) + 1);
        Save();
    }

    // Plain (no guarantee) egg wrappers, kept for existing callers/tests.
    public bool CanBuyFreshEgg => CanBuyEgg(null);
    public bool BuyFreshEgg() => BuyEgg(null);

    /// <summary>Parses an epoch string: Unix timestamp (seconds or ms) or ISO 8601.</summary>
    public static DateTimeOffset? ParseEpochDate(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
        {
            if (ts > 1_000_000_000_000) return DateTimeOffset.UnixEpoch.AddTicks((long)(ts / 1000.0 * TimeSpan.TicksPerSecond));
            if (ts > 100_000_000) return DateTimeOffset.UnixEpoch.AddTicks((long)(ts * TimeSpan.TicksPerSecond));
        }
        return Iso8601Parser.Date(value);
    }

    /// <summary>
    /// Whether a new epoch is a real window reset rather than jitter (Codex ±1s) or fractional-second noise
    /// (Claude microseconds). Session windows (≈5h) must advance ≥30 minutes, weekly ones ≥24 hours. Non-date
    /// strings (synthetic unit tests) fall back to string inequality.
    /// </summary>
    public static bool IsNewEpoch(string previous, string current, WindowClass kind)
    {
        if (previous == current) return false;
        if (ParseEpochDate(previous) is not { } dPrev || ParseEpochDate(current) is not { } dCurr) return true;
        var delta = dCurr - dPrev;
        var minAdvance = kind == WindowClass.Weekly ? TimeSpan.FromSeconds(86400) : TimeSpan.FromSeconds(1800);
        return delta >= minAdvance;
    }

    /// <summary>
    /// Grant decision (pure, edge-triggered): grants only the moment a limit window newly crosses 100%.
    /// Below 100% clears the window (re-arms). The same key with a new epoch counts as a new window and re-arms,
    /// so missing the &lt;100% sample across sleep/quit cannot lose a grant (#326). Already granted (tier ≥ 1) is not
    /// granted again. Session = 1 candy, weekly = <see cref="RareCandy.WeeklyGrant"/>.
    /// </summary>
    public static List<CandyGrant> EvaluateCandyGrants(IEnumerable<CandyWindow> windows,
        Dictionary<string, int> grantTier, Dictionary<string, string> windowEpoch)
    {
        var grants = new List<CandyGrant>();
        foreach (var w in windows)
        {
            if (w.Epoch is { } epoch)
            {
                if (windowEpoch.TryGetValue(w.Key, out var previousEpoch))
                {
                    if (IsNewEpoch(previousEpoch, epoch, w.Kind))
                    {
                        grantTier.Remove(w.Key);
                        windowEpoch[w.Key] = epoch;
                    }
                }
                else
                {
                    windowEpoch[w.Key] = epoch;
                }
            }
            if (!(w.Utilization >= 100))
            {
                grantTier.Remove(w.Key);
                if (w.Epoch is { } e) windowEpoch[w.Key] = e;
                continue;
            }
            var previous = grantTier.TryGetValue(w.Key, out var p) ? p : 0;
            if (previous >= 1) continue;
            grantTier[w.Key] = 1;
            if (w.Epoch is { } ep) windowEpoch[w.Key] = ep;
            var count = w.Kind == WindowClass.Weekly ? RareCandy.WeeklyGrant : 1;
            grants.Add(new CandyGrant(w.Key, w.Name, count));
        }
        return grants;
    }

    /// <summary>
    /// Candy grants from limit windows (edge-triggered, persisted). The app calls this after every refresh once
    /// limits are loaded. First run: windows already at 100% are only seeded (no retroactive grant).
    /// <paramref name="limitsReady"/> false (limits not loaded) waits for the next refresh.
    /// </summary>
    public void GrantCandies(IReadOnlyList<CandyWindow> windows, bool limitsReady)
    {
        if (!limitsReady) return;
        if (!_state.CandyFeatureSeeded)
        {
            // Accepted limit: if only one provider's limits load on the first refresh, only its windows are seeded,
            // and another provider already at 100% may grant once later. Small and single-user, so YAGNI.
            foreach (var w in windows.Where(w => w.Utilization >= 100))
            {
                _state.CandyGrantTier[w.Key] = 1;
                if (w.Epoch is { } epoch) _state.CandyWindowEpoch[w.Key] = epoch;
            }
            _state.CandyFeatureSeeded = true;
            Save();
            return;
        }
        var beforeTier = new Dictionary<string, int>(_state.CandyGrantTier);
        var beforeEpoch = new Dictionary<string, string>(_state.CandyWindowEpoch);
        var grants = EvaluateCandyGrants(windows, _state.CandyGrantTier, _state.CandyWindowEpoch);
        foreach (var g in grants)
        {
            _state.Inventory[ItemKind.RareCandy.RawValue()] = ItemCount(ItemKind.RareCandy) + g.Count;
            // The grant itself is a state change regardless of notifications; the notification says why.
            NotifyCompanionEvent(L.NotifCandyTitle(L.ItemName(ItemKind.RareCandy.RawValue()), g.Count), L.NotifCandyBody(g.WindowName),
                showsOnPet: false);
        }
        // Re-arming (util dip / new epoch) must persist even without grants: otherwise a restart sees a stale
        // tier=1 and the next 100% is mistaken for "already granted" (regression).
        if (grants.Count > 0 || !SameMap(_state.CandyGrantTier, beforeTier) || !SameMap(_state.CandyWindowEpoch, beforeEpoch))
            Save();
    }

    private static bool SameMap<TValue>(IReadOnlyDictionary<string, TValue> a, IReadOnlyDictionary<string, TValue> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, kv.Value));

    /// <summary>A hatch / evolution / graduation / Ditto line the floating pet pops once. <c>Seq</c> tells a new event
    /// from one already shown, even when two share a clock instant.</summary>
    public sealed record CompanionEvent(int Seq, string Text, DateTimeOffset At);

    /// <summary>The newest event line. Recorded before the notification setting is checked, so the pet shows it
    /// with notifications off too.</summary>
    public CompanionEvent? LatestEvent { get; private set; }
    private int _eventSeq;

    /// <summary>Companion event notification (only while the toggle is on). Independent of limit alerts.</summary>
    private void NotifyCompanionEvent(string title, string body, bool showsOnPet = true)
    {
        if (showsOnPet)
        {
            _eventSeq += 1;
            LatestEvent = new CompanionEvent(_eventSeq, body, _clock());
        }
        if (!(_settings.GetBool(CompanionNotificationsKey) ?? true)) return;
        CompanionNotification?.Invoke(title, body);
    }

    // MARK: Hatching

    public async Task HatchIfNeeded()
    {
        if (_state.Active != null || IsHatching || _state.EggUsage < EggHatchThreshold) return;
        // Wait only while the prefetch is rolling the species (pending not fixed) — no double RNG use. Warming
        // (line/sprites) after the pending species is fixed may run concurrently.
        if (_state.PendingHatchID == null && _prefetchInFlight) return;
        // IsHatching locks roll through hatch. It used to drop between ChooseBase and Hatch, letting another update
        // tick roll a second species in that await window. HatchCore does not re-check the lock, so this one lock
        // protects roll + hatch atomically.
        var generation = _activeGeneration;
        IsHatching = true;
        try
        {
            // A prefetched species is used as-is (line/sprites warm → ~0 delay); otherwise roll now.
            var @base = _state.PendingHatchID ?? await ChooseBase();
            if (!IsCurrentReadyEgg(generation))
            {
                AppLog.Write("hatch: discarded before result handling — subject replaced during species roll");
                KickLineLoadIfNeeded();
                return;
            }
            if (@base is not { } baseID)
            {
                IsHatchRetryDelayed = true;
                return;
            }
            var pendingForm = _state.PendingHatchID == baseID ? _state.PendingForm : null;
            await HatchCore(baseID, generation, pendingForm);
        }
        finally
        {
            IsHatching = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Reloads the line of the Pokémon left after a discarded hatch (usually a freshly imported one).
    /// LoadCurrentLine requires !IsHatching, so a load during the hatch silently fails; without a retry the name
    /// stays "Token Egg" until the next update tick. The spawned work runs after the lock is released.
    /// </summary>
    private void KickLineLoadIfNeeded()
    {
        if (_state.Active == null || CurrentLine != null) return;
        Spawn(LoadCurrentLine);
    }

    // MARK: Egg prefetch

    private bool _prefetchInFlight;
    private int? _prefetchedLineID; // species whose line/sprites are warm (session memory)

    private bool IsCurrentEgg(int generation) => _activeGeneration == generation && _state.Active == null;

    private bool IsCurrentReadyEgg(int generation) => IsCurrentEgg(generation) && _state.EggUsage >= EggHatchThreshold;

    private void MarkHatchRetryDelayedIfReady(int generation)
    {
        if (!IsCurrentReadyEgg(generation)) return;
        IsHatchRetryDelayed = true;
    }

    private void FinishEggPrefetch(int generation, bool shouldHatch)
    {
        _prefetchInFlight = false;
        if (!shouldHatch || !IsCurrentReadyEgg(generation)) return;
        Spawn(HatchIfNeeded);
    }

    /// <summary>
    /// Prepares the hatch while still an egg: ① species pre-roll (PendingHatchID, persisted) ② evolution line fetch
    /// (provider cache) ③ sprite warm-up. When everything succeeds the hatch needs no network; failures resume from
    /// that point on the next update tick.
    /// </summary>
    private async Task EnsureEggPrefetch()
    {
        if (_state.Active != null || IsHatching || _prefetchInFlight) return;
        var generation = _activeGeneration;
        var shouldHatch = false;
        _prefetchInFlight = true;
        try
        {
            if (_state.PendingHatchID == null)
            {
                var selected = await ChooseBase();
                // The hatch finished meanwhile (active != null) or the state was replaced (save import): drop this
                // roll, or the imported egg's pre-roll would be overwritten.
                if (!IsCurrentEgg(generation)) return;
                if (selected is not { } picked)
                {
                    MarkHatchRetryDelayedIfReady(generation);
                    return;
                }
                _state.PendingHatchID = picked;
                _state.PendingForm = RollLineForm(picked);
                Save();
            }
            if (_state.PendingHatchID is not { } id) return;
            if (_prefetchedLineID == id)
            {
                IsHatchRetryDelayed = false;
                shouldHatch = true;
                return;
            }
            var form = _state.PendingForm;
            EvoLine line;
            try
            {
                line = await _provider.Line(id);
            }
            catch (Exception)
            {
                MarkHatchRetryDelayedIfReady(generation);
                return;
            }
            if (!IsCurrentEgg(generation) || _state.PendingHatchID != id) return;
            // Sprite warm-up for what shows right after hatching — installed app only (tests never hit the network).
            if (AppEnv.IsInstalledApp)
            {
                if (WarmEggSprites is { } warm) await warm(line.BaseID, form);
                await LoadFormNames(line.BaseID, form);
            }
            if (!IsCurrentEgg(generation) || _state.PendingHatchID != id) return;
            _prefetchedLineID = id;
            IsHatchRetryDelayed = false;
            shouldHatch = true;
        }
        finally
        {
            FinishEggPrefetch(generation, shouldHatch);
        }
    }

    /// <summary>Draws only for lines with forms, so other species keep their random sequence.</summary>
    private PokemonForm? RollLineForm(int baseID)
    {
        var forms = PokemonForm.LineForms(baseID);
        if (forms.Count == 0) return null;
        return PokemonForm.Roll(_rng.Next(), forms, _state.CollectedLineForms(baseID));
    }

    /// <summary>Warms the localized names the hatch, evolution and graduation messages of this line show.</summary>
    private Task LoadFormNames(int baseID, PokemonForm? form) =>
        LoadFormNames(PokemonForm.FormSpecies(baseID).Select(id => PokemonForm.NameResource(id, form))
            .OfType<PokemonNameResource>());

    /// <summary>The raised Pokémon's form names, for its title and its evolution and graduation messages.</summary>
    public async Task LoadActiveFormNames()
    {
        if (_state.Active is not { } active) return;
        await LoadFormNames(active.BaseID, active.Form);
    }

    public async Task LoadFormNames(IEnumerable<PokemonNameResource> resources)
    {
        if (_formNameLoader is not { } loader) return;
        foreach (var resource in resources.Distinct().ToList())
        {
            try
            {
                if (await loader(resource) is { } names) FormNames[resource] = names;
            }
            catch (Exception)
            {
                // Labels fall back to the identifier until a later load succeeds.
            }
        }
        NotifyChanged();
    }

    public string FormDisplayName(string name, int speciesID, PokemonForm? form) =>
        PokemonForm.DisplayName(name, speciesID, form, _state.Language, FormNames);

    public async Task Hatch(int baseID)
    {
        if (IsHatching) return;
        var generation = _activeGeneration;
        IsHatching = true;
        try
        {
            await HatchCore(baseID, generation);
        }
        finally
        {
            IsHatching = false;
            NotifyChanged();
        }
    }

    // MARK: Ditto disguise / reveal

    /// <summary>Ditto disguise roll (pure): common, ≥2 forms, pre-drawn roll at 1/128.</summary>
    public static bool DittoDisguiseHit(Rarity rarity, int totalForms, ulong roll) =>
        rarity == Core.Rarity.Common && totalForms >= 2 && roll % PokemonOdds.DittoDisguiseDenominator == 0;

    /// <summary>Shiny hatch denominator: 48 with the charm, else 64. Single source for the roll and the notification text.</summary>
    public static ulong ShinyDenominatorFor(bool charmOwned) =>
        charmOwned ? ShinyCharm.ShinyDenominator : PokemonOdds.ShinyDenominator;

    /// <summary>The denominator a hatch right now would use.</summary>
    public ulong ShinyDenominator => ShinyDenominatorFor(OwnsShinyCharm);

    /// <summary>Shiny hatch roll (pure): pre-drawn roll % denominator == 0.</summary>
    public static bool RollsShiny(ulong roll, bool charmOwned) => roll % ShinyDenominatorFor(charmOwned) == 0;

    /// <summary>The actual hatch. The caller (Hatch / HatchIfNeeded) owns and releases the IsHatching lock.</summary>
    private async Task HatchCore(int baseID, int generation, PokemonForm? pendingForm = null)
    {
        EvoLine line;
        try
        {
            line = await _provider.Line(baseID);
        }
        catch (Exception)
        {
            MarkHatchRetryDelayedIfReady(generation);
            AppLog.Write($"hatch: line fetch failed for base {baseID} — egg kept, retry next tick");
            return;
        }
        // The active subject was replaced during the line fetch (a save import landed in that window): discard this
        // hatch or it would overwrite the imported Pokémon. Same generation guard as LoadCurrentLine/RevealDitto.
        if (_activeGeneration != generation)
        {
            AppLog.Write("hatch: discarded — active subject replaced during line fetch");
            KickLineLoadIfNeeded();
            return;
        }
        // The last gate protecting a bought guarantee — only here is the real rarity known (the candidate index has
        // capture_rate, not is_legendary). If the filter drifted (stale index), keep the egg and drop only the
        // pre-roll so the next tick rolls again; the user keeps the guarantee they paid for.
        if (_state.EggTier is { } tier && line.Rarity.SortRank() < tier.SortRank())
        {
            AppLog.Write($"hatch: rolled {line.Rarity.RawValue()} below guaranteed {tier.RawValue()} — discarded, re-roll next tick");
            _state.PendingHatchID = null;
            _state.PendingForm = null;
            _prefetchedLineID = null;
            MarkHatchRetryDelayedIfReady(generation);
            Save();
            return;
        }
        _state.PendingHatchID = null;
        _state.PendingForm = null;
        // A later egg can roll the same species with another form and must warm its own sprite.
        _prefetchedLineID = null;
        CurrentLine = line;
        IsHatchRetryDelayed = false;
        // Tokens over the hatch threshold carry into the hatchling's growth (nothing wasted).
        var overflow = Math.Max(0, _state.EggUsage - EggHatchThreshold);
        _state.EggUsage = 0;
        _state.EggTier = null; // the guarantee is consumed by this hatch (the next egg has none)
        var prayers = _state.EggPrayers; // moves onto the hatchling; the egg side is cleared
        _state.ResetEggPrayers();
        // Individual rolls — shiny (1/64, charm 1/48) and nature (25) fixed at hatch, kept through evolution.
        var shinyOdds = ShinyDenominator;
        var isShiny = RollsShiny(_rng.Next(), OwnsShinyCharm);
        var nature = PokemonNatureExtensions.AllCases[(int)(_rng.Next() % (ulong)PokemonNatureExtensions.AllCases.Length)];
        // Ditto disguise roll: common, ≥2 forms, 1/128. Gated (short-circuit → no RNG use when disabled, so test
        // RNG sequences are unaffected). Disguise/reveal logic is tested state-based.
        int? dittoDisguise = null;
        if (_dittoDisguiseRollingEnabled && DittoDisguiseHit(line.Rarity, line.TotalForms, _rng.Next()))
            dittoDisguise = line.BaseID;
        var evolutionPlan = MakeEvolutionPlan(line.Tree, line.BaseID);
        var profile = PokemonProfile.Generate(_rng.Next());
        if (_pokemonDetailsByID.TryGetValue(line.BaseID, out var details)) profile.Enrich(details);
        var hasGrowthBoost = _state.HasCollectedFinal(line.BaseID);
        var form = pendingForm ?? RollLineForm(line.BaseID);
        // Shininess stays hidden while disguised — hatch notification and celebration show a normal one.
        var showShiny = isShiny && dittoDisguise == null;
        _activeGeneration += 1;
        _state.Active = new MonState(line.BaseID, [line.BaseID], evolutionPlan, stageIndex: 0, usedAtStage: 0,
            rarity: line.Rarity, totalForms: evolutionPlan.Count, isShiny: isShiny, nature: nature, profile: profile,
            hasGrowthBoost: hasGrowthBoost, dittoDisguise: dittoDisguise, form: form, prayers: prayers);
        AppLog.Write($"hatch: base={line.BaseID} rarity={line.Rarity.RawValue()} shiny={isShiny} forms={evolutionPlan.Count} boost={hasGrowthBoost} ditto={dittoDisguise != null}");
        var name = FormDisplayName(line.LocalizedName(line.BaseID, _state.Language), line.BaseID, form);
        var hatchBody = prayers is { Count: > 0 }
            ? L.NotifPrayerHatchBody(name, prayers.Count, showShiny, line.Rarity)
            : showShiny ? L.NotifShinyHatchBody(name, shinyOdds) : L.NotifHatchBody(name);
        NotifyCompanionEvent(showShiny ? L.NotifShinyHatchTitle : L.NotifHatchTitle, hatchBody);
        JustEvolvedTo = null; // a new hatch says "grew", not the previous evolution's name
        DisplayState = CompanionStateKind.LevelUp;
        _eventUntil = _clock().AddSeconds(4);
        if (overflow > 0) ApplyUsage(overflow); // apply the carry-over now (evolve/reveal if needed)
        // Fire the celebration after carry-over evolutions so an evolve cannot cover the shiny hatch burst. Skipped
        // in the extreme case where the carry-over graduated it immediately (already in the Pokédex).
        if (_state.Active != null) FireCelebration(Celebration.Hatched(showShiny));
        Save();
        if (_detailProvider != null)
        {
            var speciesID = line.BaseID;
            Spawn(() => LoadPokemonDetails(speciesID));
        }
    }

    /// <summary>
    /// Disguise → reveal: the Ditto that cannot evolve shows its identity at the first evolution threshold instead.
    /// After loading Ditto's line: rare, single form, carry-over kept, IsShiny/Nature kept; celebration + notification.
    /// </summary>
    private async Task RevealDitto()
    {
        if (_state.Active is not { } a || a.DittoDisguise == null || a.DittoRevealed || _isRevealingDitto) return;
        var generation = _activeGeneration;
        var firstEvoThr = StageThreshold(a);
        if (a.UsedAtStage < firstEvoThr) return; // below the threshold
        _isRevealingDitto = true;
        try
        {
            EvoLine dittoLine;
            try
            {
                dittoLine = await _provider.Line(PokemonOdds.DittoSpeciesID);
            }
            catch (Exception)
            {
                AppLog.Write("ditto reveal: line fetch failed — retry next tick");
                return;
            }
            if (_activeGeneration != generation || _state.Active is not { } current
                || current.DittoDisguise == null || current.DittoRevealed) return;
            var m = current.Clone();
            var latestFirstEvoThr = StageThreshold(m);
            if (m.UsedAtStage < latestFirstEvoThr) return;
            var disguiseName = CurrentLine?.LocalizedName(m.BaseID, _state.Language) ?? $"#{m.BaseID}";
            var carryOver = Math.Max(0, m.UsedAtStage - latestFirstEvoThr); // disguise's first-evolution overflow → Ditto's growth
            // Become Ditto: rarity/forms from the loaded line; IsShiny/Nature/DittoDisguise kept.
            var previousRarity = m.Rarity;
            m.BaseID = dittoLine.BaseID;
            var evolutionPlan = MakeEvolutionPlan(dittoLine.Tree, dittoLine.BaseID);
            m.PathIDs = [dittoLine.BaseID];
            m.PlannedPathIDs = evolutionPlan;
            m.StageIndex = 0;
            m.Rarity = dittoLine.Rarity;
            m.TotalForms = evolutionPlan.Count;
            m.UsedAtStage = carryOver;
            m.DittoRevealed = true;
            m.Form = null;
            // The hatch boost came from the disguise's line; from now on only a repeat Ditto earns it.
            m.HasGrowthBoost = _state.HasCollectedFinal(dittoLine.BaseID);
            m.Profile?.RebaseForSpeciesIdentity(previousRarity, dittoLine.Rarity);
            if (_pokemonDetailsByID.TryGetValue(dittoLine.BaseID, out var details)) m.Profile?.Enrich(details);
            var shiny = m.IsShiny;
            _state.Active = m;
            _state.ReconcileRepresentativeSelection(); // a choice backed only by the disguise species goes with the reveal
            CurrentLine = dittoLine;
            AppLog.Write($"ditto reveal: disguise={m.DittoDisguise ?? -1} → ditto rarity={dittoLine.Rarity.RawValue()} shiny={shiny}");
            FireCelebration(Celebration.DittoRevealed(shiny));
            DisplayState = CompanionStateKind.LevelUp;
            _eventUntil = _clock().AddSeconds(5);
            NotifyCompanionEvent(shiny ? L.NotifShinyDittoRevealTitle : L.NotifDittoRevealTitle,
                shiny ? L.NotifShinyDittoRevealBody(disguiseName) : L.NotifDittoRevealBody(disguiseName));
            Save();
            ApplyUsage(0); // re-evaluate Ditto's graduation with the carry-over (rare 3B, rarely immediate)
            _isRevealingDitto = false; // details are enrichment, not part of the reveal transaction
            if (_detailProvider != null) await LoadPokemonDetails(PokemonOdds.DittoSpeciesID);
        }
        finally
        {
            _isRevealingDitto = false;
        }
    }

    private async Task LoadCurrentLine()
    {
        if (_state.Active is not { } a || CurrentLine != null || IsHatching) return;
        var generation = _activeGeneration;
        var baseID = a.BaseID;
        IsHatching = true;
        try
        {
            EvoLine line;
            try
            {
                line = await _provider.Line(baseID);
            }
            catch (Exception)
            {
                return;
            }
            // Usage, mints etc. may change the active state during the await: never write back the request-time
            // snapshot; normalize the latest state only if the same individual is still active.
            if (_activeGeneration != generation || _state.Active is not { } latest || latest.BaseID != baseID
                || CurrentLine != null) return;
            _state.Active = NormalizedEvolutionState(latest, line.Tree);
            _state.ReconcileRepresentativeSelection(); // stages removed by path normalization cannot stay representative
            CurrentLine = line;
            Save(); // persist the migration choice before re-evaluating usage, so restarts never re-roll it
            ApplyUsage(0); // usage banked while the line was missing may cross a threshold now
            // Path normalization can change CurrentID without entering the regular evolution branch.
            IsHatching = false; // do not hold the line-load lock across detail HTTP requests
            // After a relaunch only the popover loads form names; evolution and graduation notifications need them
            // before it is opened.
            if (AppEnv.IsInstalledApp) await LoadActiveFormNames();
            if (_state.Active?.CurrentID is { } speciesID && _detailProvider != null)
                await LoadPokemonDetails(speciesID);
        }
        finally
        {
            IsHatching = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Hatch species choice — no hard-coded pool: weighted over every Gen 1–5 base from PokéAPI.
    /// ① base index (id + capture_rate) from one GraphQL query (30-day disk cache → usually zero calls)
    /// ② weight = the official capture_rate (Caterpie 255 vs Mewtwo 3 = 85:1), halved for already-collected bases
    /// ③ exactly one roll over cumulative weights — no loops/rerolls.
    /// Index failure (offline, no cache) → REST fallback; null keeps the egg for the next tick.
    /// </summary>
    private async Task<int?> ChooseBase()
    {
        var tier = _state.EggTier;
        IReadOnlyList<BaseSpecies>? full = null;
        try
        {
            full = await _provider.BaseSpeciesIndex();
        }
        catch (Exception)
        {
            // Falls through to the REST fallback.
        }
        if (full is { Count: > 0 })
        {
            // Guaranteed eggs narrow the candidates first: the capture_rate ceiling is the rarity floor, so
            // legendaries are naturally included ("rare or better"). An empty result cannot honour the guarantee,
            // so keep the egg instead of falling back to the full pool (retry next tick).
            var index = tier is { } t ? full.Where(e => t.Includes(e.CaptureRate)).ToList() : full.ToList();
            if (index.Count == 0)
            {
                AppLog.Write($"hatch: no candidate for guaranteed {tier?.RawValue() ?? "none"} — egg kept, retry next tick");
                return null;
            }
            var weights = index.Select(e => CollectionWeight.Adjusted(e.CaptureRate, _state.HasCollectedFinal(e.Id))).ToList();
            var total = weights.Sum(w => (long)w);
            var r = (long)(_rng.Next() % (ulong)total);
            for (var i = 0; i < weights.Count; i++)
            {
                r -= weights[i];
                if (r < 0) return index[i].Id;
            }
            return index[^1].Id; // unreachable (defensive)
        }
        // GraphQL base index endpoint down → REST fallback, so hatching is not tied to one endpoint.
        AppLog.Write("hatch: base index unavailable — REST fallback");
        return await ChooseBaseViaRest();
    }

    /// <summary>
    /// REST fallback: draws random ids within the animated-asset range and checks base-ness (rejection sampling).
    /// Weighting is skipped — the hatched line computes its real rarity from capture_rate, so the result is exact.
    /// </summary>
    private async Task<int?> ChooseBaseViaRest()
    {
        var tier = _state.EggTier;
        const int count = PokemonAssets.AnimatedSpeciesMax - PokemonAssets.AnimatedSpeciesMin + 1;
        for (var attempt = 1; attempt <= 16; attempt++)
        {
            var id = (int)(_rng.Next() % count) + PokemonAssets.AnimatedSpeciesMin;
            try
            {
                if (await _provider.BaseSpecies(id) is { } bs)
                {
                    // The guarantee must use the same rule as the weighted path, or a GraphQL outage would silently
                    // break it. Not found → keep the egg (never let a purchase vanish).
                    if (tier is { } t && !t.Includes(bs.CaptureRate)) continue;
                    AppLog.Write($"hatch: REST fallback picked base {id} (cap {bs.CaptureRate}, {attempt} tries)");
                    return id;
                }
                // null = not a base (middle evolution) → next attempt
            }
            catch (Exception ex)
            {
                AppLog.Write($"hatch: REST fallback network error — retry next tick: {ex.Message}");
                return null; // REST is down too → keep the egg, retry next update tick
            }
        }
        AppLog.Write("hatch: REST fallback exhausted 16 tries");
        return null;
    }

    /// <summary>Today's total at the previous refresh, and when a rise in it was last seen. The enrichment scan's
    /// <c>LastUsage</c> lags a refresh behind, so without this the companion would keep its back turned for a while
    /// after usage resumed. Not persisted: after a launch there is nothing to compare with.</summary>
    private (string Date, long Total)? _lastSeenToday;
    private DateTimeOffset? _lastObservedUsage;

    /// <summary>
    /// <paramref name="timeSinceLastUsage"/>, capped by how long ago this store itself saw today's total rise. A new
    /// day or a first sighting only sets the baseline — it is not usage, so a midnight rollover neither un-sulks nor
    /// sulks by itself. Null stays null: unknown is never idle.
    /// </summary>
    private TimeSpan? EffectiveIdle(TimeSpan? timeSinceLastUsage, string todayDate, long todayTokens, bool hasCurrentProviderData)
    {
        if (hasCurrentProviderData)
        {
            if (_lastSeenToday is { } seen && seen.Date == todayDate && todayTokens > seen.Total)
                _lastObservedUsage = _clock();
            _lastSeenToday = (todayDate, todayTokens);
        }
        if (timeSinceLastUsage is not { } reported) return null;
        if (_lastObservedUsage is not { } observed) return reported;
        var sinceObserved = _clock() - observed;
        if (sinceObserved < TimeSpan.Zero) sinceObserved = TimeSpan.Zero;
        return reported < sinceObserved ? reported : sinceObserved;
    }

    private CompanionStateKind ComputeState(BurnTier burnTier, bool limitWarning, bool hasUsageData, long today,
        TimeSpan? timeSinceLastUsage = null)
    {
        if (_state.Active == null) return CompanionStateKind.Egg;
        if (JustGraduated != null || (_eventUntil is { } until && _clock() < until)) return CompanionStateKind.LevelUp;
        if (limitWarning) return CompanionStateKind.Tired;
        if (hasUsageData && timeSinceLastUsage is { } idle && idle >= PokemonBalance.SulkAfterIdle) return CompanionStateKind.Sulk;
        if (!hasUsageData || today == 0) return CompanionStateKind.Sleep;
        return burnTier switch
        {
            BurnTier.Idle => CompanionStateKind.Idle,
            BurnTier.Normal => CompanionStateKind.Working,
            _ => CompanionStateKind.Focus,
        };
    }

    // MARK: Save transfer (device change)

    /// <summary>"This device's current progress" summary for the overwrite confirmation.</summary>
    public SaveSummary TransferSummary => new(_state);

    /// <summary>Default export file name, from the <b>same clock</b> as the envelope's exportedAt (a separate
    /// DateTime.Now could split file name and content dates at midnight).</summary>
    public string SuggestedExportFileName => SaveTransfer.SuggestedFileName(_clock());

    /// <summary>Export payload. The caller (UI) writes it where the user chose.</summary>
    public byte[] ExportedSaveData(string appVersion, string deviceName) =>
        SaveTransfer.Encode(_state, appVersion, deviceName, _clock());

    /// <summary>
    /// Applies a validated save to this device: back up the current state → re-align to this device → save →
    /// reload the line. If the backup cannot be written, nothing is applied and this throws — the confirmation
    /// promised "the previous state is kept", and overwriting without it could lose progress with no way back.
    /// </summary>
    public void ApplySave(SaveEnvelope envelope, IReadOnlyDictionary<string, long> todayTokensByProvider,
        string todayDate, bool hasUsageData)
    {
        BackupStateBeforeImport();
        _state = SaveTransfer.RebasedForThisDevice(envelope.State, _state, todayTokensByProvider, todayDate, hasUsageData);
        // Invalidate every async task and effect of the previous subject. Without bumping the generation, a line
        // load still in flight would overwrite the imported Pokémon when it completes.
        _activeGeneration += 1;
        CurrentLine = null;
        _prefetchedLineID = null;
        JustEvolvedTo = null;
        JustGraduated = null;
        _eventUntil = null;
        CurrentCelebration = null;
        IsHatchRetryDelayed = false;
        // One-shot feedback about the previous Pokémon goes too, or its "+XP" would float over the imported one.
        CandyFeedbackAmount = 0;
        MintFeedbackNature = null;
        DisplayState = _state.Active != null ? CompanionStateKind.Idle : CompanionStateKind.Egg;
        MigratePokemonProfilesIfNeeded();
        SeedDexCompletionsIfNeeded();
        Save();
        if (_state.Active != null) Spawn(LoadCurrentLine);
        if (_detailProvider != null) Spawn(PreparePokemonProfiles);
        AppLog.Write($"save imported from {envelope.SourceDevice}: dex={_state.Dex.Count} lifetime={_state.UsedSinceInstall}");
    }

    /// <summary>
    /// Keeps the current state next to the file before overwriting: the way back after a wrong import. A new slot per
    /// import (oldest pruned) so a second import cannot overwrite the original.
    /// </summary>
    private string? BackupStateBeforeImport()
    {
        if (!_persists) return null; // nothing on disk to protect
        byte[] data;
        try
        {
            data = CompanionJson.Serialize(_state);
        }
        catch (Exception)
        {
            throw new SaveTransferException(SaveTransferErrorKind.BackupFailed);
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
        var backup = Path.Combine(dir, SaveTransfer.BackupFileName(_clock()));
        try
        {
            AtomicFile.Write(backup, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"save import aborted — backup write failed: {ex.Message}");
            throw new SaveTransferException(SaveTransferErrorKind.BackupFailed);
        }
        PruneImportBackups(dir);
        return backup;
    }

    /// <summary>Keeps the newest N backups. Names are <c>yyyy-MM-dd-HHmmss</c>, so lexical order = time order.</summary>
    private static void PruneImportBackups(string dir)
    {
        string[] names;
        try
        {
            names = Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var backups = names.Where(n => n.StartsWith(SaveTransfer.BackupFilePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        if (backups.Count <= SaveTransfer.BackupsToKeep) return;
        foreach (var stale in backups.Take(backups.Count - SaveTransfer.BackupsToKeep))
        {
            try { File.Delete(Path.Combine(dir, stale)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // MARK: Snapshots (local automatic backups & restore)

    public void RefreshSnapshots()
    {
        AvailableSnapshots = _persists ? SaveSnapshotManager.ListSnapshots(_filePath) : [];
    }

    public SaveSnapshot CreateManualSnapshot(DateTimeOffset? now = null)
    {
        if (!_persists) throw new InvalidOperationException("snapshots need a state file");
        var timestamp = now ?? _clock();
        var snapshot = SaveSnapshotManager.CreateSnapshot(_state, _filePath, timestamp,
            SaveSnapshotManager.DefaultAppVersion, SaveSnapshotManager.DefaultDeviceName);
        _lastAutoSnapshotDate = timestamp;
        RefreshSnapshots();
        NotifyChanged();
        return snapshot;
    }

    public void AutoSnapshotIfNeeded(DateTimeOffset? now = null)
    {
        if (!_persists) return;
        var timestamp = now ?? _clock();
        var newest = AvailableSnapshots.FirstOrDefault();
        if (newest != null && timestamp - newest.Date < SaveSnapshotManager.MinAutoSnapshotInterval) return;
        if (!(_state.UsedSinceInstall > 0 || _state.Dex.Count > 0 || _state.Active != null)) return;
        try
        {
            CreateManualSnapshot(timestamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"auto snapshot failed: {ex.Message}");
        }
    }

    public void RestoreSnapshot(SaveSnapshot snapshot, IReadOnlyDictionary<string, long>? todayTokensByProvider = null,
        string todayDate = "", bool hasUsageData = false)
    {
        // Read and validate the chosen snapshot before taking the safety snapshot: at the retention limit that extra
        // file prunes the oldest one, which may be the snapshot being restored. A snapshot that no longer decodes
        // also fails here, before anything is written or pruned.
        var envelope = SaveSnapshotManager.LoadEnvelope(snapshot.FilePath);
        try
        {
            CreateManualSnapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write($"safety snapshot before restore failed: {ex.Message}");
        }
        ApplySave(envelope, todayTokensByProvider ?? new Dictionary<string, long>(), todayDate, hasUsageData);
        RefreshSnapshots();
        AppLog.Write($"snapshot restored: {snapshot.Id}");
    }

    // MARK: Pokémon combat profiles / details

    /// <summary>Exact current/final individuals for a Pokédex species. Earlier evolution stages remain species
    /// reference pages; the same evolved individual is not duplicated as a second creature.</summary>
    public List<DexEntry> PokemonIndividuals(int speciesID, PokemonForm? form = null)
    {
        var resolved = PokemonForm.ResolvedForSpecies(speciesID, form);
        return DexEntriesSorted.Where(e => e.FinalID == speciesID && e.Profile != null
                                           && PokemonForm.ResolvedForSpecies(speciesID, e.Form) == resolved).ToList();
    }

    /// <summary>Loads immutable PokéAPI metadata and persists any deferred profile fields exactly once.</summary>
    public async Task LoadPokemonDetails(int speciesID)
    {
        if (_pokemonDetailsByID.TryGetValue(speciesID, out var known))
        {
            EnrichProfiles(speciesID, known);
            return;
        }
        if (_detailProvider is not { } detailProvider || LoadingPokemonDetailIDs.Contains(speciesID)) return;
        LoadingPokemonDetailIDs.Add(speciesID);
        FailedPokemonDetailIDs.Remove(speciesID);
        try
        {
            var details = await detailProvider.PokemonDetails(speciesID);
            _pokemonDetailsByID[speciesID] = details;
            EnrichProfiles(speciesID, details);
        }
        catch (Exception ex)
        {
            FailedPokemonDetailIDs.Add(speciesID);
            AppLog.Write($"pokemon details fetch failed id={speciesID}: {ex.Message}");
        }
        finally
        {
            LoadingPokemonDetailIDs.Remove(speciesID);
            NotifyChanged();
        }
    }

    /// <summary>Startup/background warm-up for the only profile needed before a detail page is opened.</summary>
    public async Task PreparePokemonProfiles()
    {
        if (_state.Active?.CurrentID is not { } speciesID) return;
        await LoadPokemonDetails(speciesID);
    }

    private void EnrichProfiles(int speciesID, PokemonDetails details)
    {
        var changed = false;
        if (_state.Active is { } active && active.CurrentID == speciesID && active.Profile is { } profile)
        {
            var updated = profile.Clone();
            updated.Enrich(details);
            if (updated != profile)
            {
                active.Profile = updated;
                changed = true;
            }
        }
        foreach (var entry in _state.Dex.Where(e => e.FinalID == speciesID))
        {
            if (entry.Profile is not { } entryProfile) continue;
            var updated = entryProfile.Clone();
            updated.Enrich(details);
            if (updated != entryProfile)
            {
                entry.Profile = updated;
                changed = true;
            }
        }
        if (changed) Save();
    }

    /// <summary>Additive migration for pre-profile saves. Never needs the network, so it cannot block launch.
    /// Deferred fields (gender/ability/moves) are filled after the cached/detail fetch succeeds.</summary>
    private void MigratePokemonProfilesIfNeeded()
    {
        var changed = false;
        if (_state.Active is { Profile: null } active)
        {
            var key = $"active:{active.BaseID}:{string.Join(",", active.PathIDs)}:{_state.LastDate}";
            active.Profile = PokemonProfile.Generate(PokemonProfileMigration.Seed(key));
            changed = true;
        }
        foreach (var entry in _state.Dex)
        {
            if (entry.Profile != null) continue;
            var graduated = !entry.IsReleased;
            // Released entries only persisted their reached ChainOrder, not the planned form count. Treating that
            // count as totalForms is the best recoverable estimate, but an upper bound when released before the
            // planned final.
            var growth = graduated
                ? PokemonBalance.GraduationTotal(entry.Rarity)
                : ReconstructedGrowthTokens(entry.Rarity, Math.Max(1, entry.ChainOrder.Count),
                    Math.Max(0, entry.ChainOrder.Count - 1), 0);
            var profile = PokemonProfile.Generate(PokemonProfileMigration.Seed($"dex:{entry.Id}:{entry.FinalID}"),
                growth, entry.Id);
            profile.ApplyGrowth(0, entry.Rarity);
            entry.Profile = profile;
            changed = true;
        }
        var before = _state.Active?.Profile?.Clone();
        ReconcileActiveProfileGrowth();
        if (!changed)
        {
            if (before != _state.Active?.Profile) Save();
            return;
        }

        // One recoverable snapshot before the first format expansion. Never overwrite it.
        if (_persists)
        {
            var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_filePath))!, "companion-state.pre-profiles-v1.json");
            if (File.Exists(_filePath) && !File.Exists(backup))
            {
                try { File.Copy(_filePath, backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        Save();
        AppLog.Write("pokemon profile migration complete — previous state kept as companion-state.pre-profiles-v1.json");
    }

    /// <summary>Completed phases keep their earned credit. Only the current raw phase is repriced by difficulty /
    /// repeat boost; the profile's high-water mark prevents level loss.</summary>
    private void ReconcileActiveProfileGrowth()
    {
        if (_state.Active is not { } active || active.Profile is not { } profile) return;
        var completed = ReconstructedGrowthTokens(active.Rarity, active.TotalForms, active.StageIndex, 0);
        var standardPhase = PokemonBalance.PhaseThreshold(active.Rarity, active.TotalForms, active.StageIndex);
        var fraction = Math.Min(1, Math.Max(0, active.UsedAtStage / (double)Math.Max(1, StageThreshold(active))));
        var candidate = Math.Min(PokemonBalance.GraduationTotal(active.Rarity),
            completed + (long)Math.Floor(standardPhase * fraction));
        profile.AdvanceGrowth(candidate, active.Rarity);
        if (_pokemonDetailsByID.TryGetValue(active.CurrentID, out var details)) profile.Enrich(details);
    }

    public static long ReconstructedGrowthTokens(Rarity rarity, int totalForms, int completedStages, long currentStageUsage)
    {
        var forms = Math.Max(1, totalForms);
        var completed = Math.Min(Math.Max(0, completedStages), forms);
        long completedGrowth = 0;
        for (var stage = 0; stage < completed; stage++)
            completedGrowth += PokemonBalance.PhaseThreshold(rarity, forms, stage);
        return Math.Min(SaveTransfer.MaxTokenValue,
            completedGrowth + Math.Min(SaveTransfer.MaxTokenValue, Math.Max(0, currentStageUsage)));
    }

    // MARK: Persistence

    private void Load()
    {
        if (!_persists) return;
        byte[] data;
        try
        {
            data = File.ReadAllBytes(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // no file = new install
        }
        CompanionState decoded;
        try
        {
            decoded = CompanionJson.Deserialize(data);
        }
        catch (SwiftDecodingException)
        {
            // Decode failure (total corruption / future schema) → back the original up as .corrupt, then try to
            // recover from a valid snapshot.
            var backup = _filePath + ".corrupt";
            try
            {
                File.Delete(backup);
                File.Move(_filePath, backup);
                AppLog.Write($"companion state decode failed — original backed up to {Path.GetFileName(backup)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"failed to move corrupt state file to {Path.GetFileName(backup)}: {ex.Message}");
            }
            if (SaveSnapshotManager.LoadLatestValidSnapshot(_filePath) is { } recovered)
            {
                _state = SaveTransfer.Sanitized(recovered);
                AppLog.Write("automatically recovered companion state from snapshot");
                Save();
            }
            else
            {
                _state = new CompanionState();
                AppLog.Write("no snapshot available, starting fresh");
            }
            return;
        }
        // The same normalization as the import boundary applies on disk reads: otherwise extreme values already
        // saved would stay and break arithmetic on every launch (decoding succeeds, so the .corrupt recovery above
        // never runs). Applying it here self-heals.
        _state = SaveTransfer.Sanitized(decoded);
    }

    private void Save()
    {
        RefreshRepresentativeSubject();
        CheckDexCompletions();   // announce each generation completion once, wherever owned growth is persisted
        if (_persists)
        {
            try
            {
                AtomicFile.Write(_filePath, CompanionJson.Serialize(_state)); // atomic: no partial-write corruption
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"companion state save failed: {ex.Message}");
            }
        }
        NotifyChanged();
    }
}
