using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

// Port of SaveTransfer.swift: device-transfer saves (export here, import on another machine).

/// <summary>
/// The exported save. <see cref="CompanionState"/> is wrapped instead of written raw because its decoding is
/// deliberately lenient: <b>any JSON decodes "successfully"</b> with every field defaulted. Without the
/// envelope, picking someone else's JSON would import an empty Pokédex and look like the app erased the
/// user's progress. <c>format</c>/<c>schema</c> have no defaults and stop that mistake first.
/// Dates are ISO 8601 (Swift <c>.iso8601</c>), keys sorted and pretty-printed.
/// </summary>
public sealed record SaveEnvelope(string Format, int Schema, string AppVersion, DateTimeOffset ExportedAt,
    string SourceDevice, CompanionState State)
{
    public const string FormatID = "poketokenbar.save";
    /// <summary>v2 adds persistent generated Pokémon profiles (IVs, gender, ability, level and moves).</summary>
    public const int SchemaVersion = 2;

    public CompanionState State { get; set; } = State;
}

/// <summary>Summary for the overwrite confirmation: shows in numbers what will be replaced.</summary>
public readonly record struct SaveSummary(int DexCount, long LifetimeTokens)
{
    public SaveSummary(CompanionState state) : this(state.Dex.Count, state.UsedSinceInstall) { }
}

public enum SaveTransferErrorKind
{
    /// <summary>Not an envelope, or another app's JSON.</summary>
    NotASaveFile,
    /// <summary>Written by a newer build (higher schema).</summary>
    NewerSchema,
    /// <summary>Too large to be a save; parsing would hold the UI thread.</summary>
    FileTooLarge,
    /// <summary>The pre-import backup could not be written; the confirmation promised one, so import stops.</summary>
    BackupFailed,
}

public sealed class SaveTransferException(SaveTransferErrorKind kind, int found = 0, int supported = 0,
    int bytes = 0, int limit = 0) : Exception(kind.ToString())
{
    public SaveTransferErrorKind Kind { get; } = kind;
    /// <summary>NewerSchema: the file's schema and the highest this build reads.</summary>
    public int Found { get; } = found;
    public int Supported { get; } = supported;
    /// <summary>FileTooLarge: the file size and the limit.</summary>
    public int Bytes { get; } = bytes;
    public int Limit { get; } = limit;
}

/// <summary>
/// Button layout rule of the import confirmation, kept out of the UI because it is directly tied to data
/// loss: swapped buttons would let one Return key replace this machine's progress.
/// </summary>
public static class ImportConfirmPolicy
{
    public const int ReplaceButtonIndex = 0;
    public const int CancelButtonIndex = 1;

    /// <summary>The default button (Return) must be <b>Cancel</b>. A destructive action is never the default.</summary>
    public static string KeyEquivalent(int forButtonAt) => forButtonAt == CancelButtonIndex ? "\r" : "";
}

public static class SaveTransfer
{
    /// <summary>Save size limit. Real saves are a few KB, a full Pokédex a few hundred KB. Without a limit a huge
    /// JSON holds the UI thread (measured: 39MB ≈ 1.8 s freeze).</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>Upper bound for numbers in a save: 100,000× real use (billions), yet sums and differences of such
    /// values stay inside Int64.</summary>
    public const long MaxTokenValue = 1_000_000_000_000_000;

    /// <summary>Export file name; the date keeps repeated exports from overwriting each other.</summary>
    public static string SuggestedFileName(DateTimeOffset date) => $"PokeTokenBar-Save-{Stamp(date, "yyyy-MM-dd")}.json";

    /// <summary>A new backup slot per import. A single slot would let a second import overwrite the <b>original</b>,
    /// losing the thing to roll back to exactly when "that import was wrong".</summary>
    public static string BackupFileName(DateTimeOffset date) => $"{BackupFilePrefix}{Stamp(date, "yyyy-MM-dd-HHmmss")}.json";
    public const string BackupFilePrefix = "companion-state.pre-import-";
    /// <summary>Backups to keep; the oldest are removed.</summary>
    public const int BackupsToKeep = 5;

    /// <summary>Swift's DateFormatter uses the local time zone and the POSIX locale.</summary>
    internal static string Stamp(DateTimeOffset date, string format) =>
        TimeZoneInfo.ConvertTime(date, TimeZoneInfo.Local).ToString(format, CultureInfo.InvariantCulture);

    public static byte[] Encode(CompanionState state, string appVersion, string deviceName, DateTimeOffset now)
    {
        var envelope = new JsonObject
        {
            ["format"] = SaveEnvelope.FormatID,
            ["schema"] = SaveEnvelope.SchemaVersion,
            ["appVersion"] = appVersion,
            ["exportedAt"] = CompanionJson.EncodeDate(now, SwiftDateEncoding.Iso8601),
            ["sourceDevice"] = deviceName,
            ["state"] = CompanionJson.EncodeState(state, SwiftDateEncoding.Iso8601),
        };
        // Readable when opened by a person (what is being moved can be checked); size is irrelevant at ~4KB.
        return Encoding.UTF8.GetBytes(CompanionJson.ToJsonString(envelope, prettySortedKeys: true));
    }

    public static SaveEnvelope Decode(byte[] data)
    {
        if (data.Length > MaxFileBytes)
            throw new SaveTransferException(SaveTransferErrorKind.FileTooLarge, bytes: data.Length, limit: MaxFileBytes);
        JsonDocument doc;
        try
        {
            doc = CompanionJson.Parse(data);
        }
        catch (SwiftDecodingException)
        {
            throw new SaveTransferException(SaveTransferErrorKind.NotASaveFile);
        }
        using (doc)
        {
            var root = doc.RootElement;
            // Read the header first, so a body from a newer schema is reported as "newer save", not "not a save".
            string format;
            int schema;
            try
            {
                CompanionJson.RequireObject(root);
                format = CompanionJson.ReqString(root, "format");
                schema = CompanionJson.ReqInt(root, "schema");
            }
            catch (SwiftDecodingException)
            {
                throw new SaveTransferException(SaveTransferErrorKind.NotASaveFile);
            }
            if (format != SaveEnvelope.FormatID) throw new SaveTransferException(SaveTransferErrorKind.NotASaveFile);
            if (schema > SaveEnvelope.SchemaVersion)
                throw new SaveTransferException(SaveTransferErrorKind.NewerSchema, found: schema,
                    supported: SaveEnvelope.SchemaVersion);
            try
            {
                var envelope = new SaveEnvelope(format, schema,
                    CompanionJson.ReqString(root, "appVersion"),
                    CompanionJson.DecodeDate(CompanionJson.Req(root, "exportedAt"), SwiftDateEncoding.Iso8601),
                    CompanionJson.ReqString(root, "sourceDevice"),
                    CompanionJson.DecodeState(CompanionJson.Req(root, "state"), SwiftDateEncoding.Iso8601));
                envelope.State = Sanitized(envelope.State);
                return envelope;
            }
            catch (SwiftDecodingException)
            {
                throw new SaveTransferException(SaveTransferErrorKind.NotASaveFile); // same schema, unreadable = damaged
            }
        }
    }

    /// <summary>
    /// Trust-boundary normalization: saves come from <b>outside the app</b> (hand edits, transfer damage, other builds).
    /// Decoding is deliberately lenient, so nonsensical values pass; stored as-is they would break later arithmetic
    /// on every launch (Swift traps; here they would wrap). Normalizing at the single entry boundary avoids guarding
    /// every downstream site. Only fields used in arithmetic are touched — Pokédex and inventory are never trimmed.
    /// Returns a normalized copy; the input is not modified.
    /// </summary>
    public static CompanionState Sanitized(CompanionState state)
    {
        static long ClampToken(long v) => Math.Min(Math.Max(0, v), MaxTokenValue);
        var s = state.Clone();
        s.UsedSinceInstall = ClampToken(s.UsedSinceInstall);
        s.SpentTokens = ClampToken(s.SpentTokens);
        s.EggUsage = ClampToken(s.EggUsage);
        s.ClaimedTodayTokensByProvider = s.ClaimedTodayTokensByProvider?.ToDictionary(kv => kv.Key, kv => ClampToken(kv.Value));
        // An egg guarantee belongs to the egg being held and cannot coexist with an active Pokémon. If both come in
        // (hand edit, old build), the guarantee would leak into the next egg as a permanent premium, so drop it —
        // and the species pre-rolled for it, or the next free egg would hatch a premium result nobody paid for.
        if (s.Active != null)
        {
            s.EggTier = null;
            s.PendingHatchID = null;
        }
        s.PendingForm = PokemonForm.ResolvedForLine(s.PendingHatchID ?? 0, s.PendingForm);
        // An unsatisfiable guarantee would keep the egg from ever hatching: legendary cannot be expressed by
        // capture_rate, so both roll paths find zero candidates and nothing ever consumes the guarantee.
        if (s.EggTier is { } tier && tier.CaptureRateCeiling() == null) s.EggTier = null;
        // A species repeated in a chain (hand edit) breaks the unique-key name maps; keep the first occurrence.
        static List<int> Deduped(IEnumerable<int> ids)
        {
            var seen = new HashSet<int>();
            return ids.Where(seen.Add).ToList();
        }
        if (s.Active is { } active)
        {
            active.UsedAtStage = ClampToken(active.UsedAtStage);
            // totalForms is used as kk * (kk + 1) (PokemonBalance.PhaseThreshold); a large value is itself a trap.
            active.TotalForms = Math.Min(Math.Max(1, active.TotalForms), 12);
            active.StageIndex = Math.Min(Math.Max(0, active.StageIndex), Math.Max(0, active.PathIDs.Count - 1));
            var currentID = active.CurrentID;
            active.PathIDs = Deduped(active.PathIDs);
            active.PlannedPathIDs = Deduped(active.PlannedPathIDs);
            var index = active.PathIDs.IndexOf(currentID);
            active.StageIndex = index >= 0 ? index : active.StageIndex;
            active.StageIndex = Math.Min(active.StageIndex, Math.Max(0, active.PathIDs.Count - 1));
            active.Profile?.Sanitize();
        }
        foreach (var entry in s.Dex)
        {
            entry.ChainOrder = Deduped(entry.ChainOrder);
            entry.Profile?.Sanitize();
        }
        s.ReconcileRepresentativeSelection();
        return s;
    }

    /// <summary>
    /// Re-aligns a state from another device <b>to this device</b>.
    /// <list type="bullet">
    /// <item><b>Progress</b> (true on any device: lifetime tokens, dex, inventory, active, egg usage, egg tier,
    /// pending hatch/form, representative) → kept. The egg guarantee is a purchase, not this device's ledger.</item>
    /// <item><b>Local ledger</b> (how far <i>that</i> device credited: claimedTodayTokensByProvider, lastDate,
    /// installBaselineSet) → re-seeded here; importing it would make the old device's daily total the threshold and
    /// silently drop this device's usage until midnight.</item>
    /// <item><b>Device preference</b> (language) → this device keeps its own value.</item>
    /// </list>
    /// Account-wide candy ledgers are <b>merged per key</b>, not replaced: limit window keys are per account, and
    /// replacing with an older save would forget already-granted windows and grant them again.
    /// </summary>
    public static CompanionState RebasedForThisDevice(CompanionState imported, CompanionState current,
        IReadOnlyDictionary<string, long> todayTokensByProvider, string todayDate, bool hasUsageData)
    {
        var state = imported.Clone();
        state.Language = current.Language;
        state.CandyGrantTier = MergedGrantTier(imported.CandyGrantTier, current.CandyGrantTier);
        state.CandyWindowEpoch = MergedWindowEpochs(imported.CandyWindowEpoch, imported.CandyGrantTier,
            current.CandyWindowEpoch, current.CandyGrantTier);
        state.CandyFeatureSeeded = imported.CandyFeatureSeeded || current.CandyFeatureSeeded;
        var hasCurrentProviderData = hasUsageData && todayTokensByProvider.Count > 0;
        if (hasCurrentProviderData)
        {
            // Same rule as a new install: this device's usage before the import is not credited retroactively.
            state.InstallBaselineSet = true;
            state.ClaimedTodayTokensByProvider = new Dictionary<string, long>(todayTokensByProvider);
            state.LastDate = todayDate;
        }
        else
        {
            // Today's usage on this device is unknown yet. Saving an empty map as a seeded ledger would treat the
            // first real snapshot as a "new provider" and silently drop that day → hand the baseline to the
            // new-install path instead.
            state.InstallBaselineSet = false;
            state.ClaimedTodayTokensByProvider = null;
            state.LastDate = "";
        }
        return state;
    }

    /// <summary>Keeps the higher tier per window key: granted on either side means granted.</summary>
    public static Dictionary<string, int> MergedGrantTier(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        var result = new Dictionary<string, int>(a);
        foreach (var (key, value) in b) result[key] = result.TryGetValue(key, out var existing) ? Math.Max(existing, value) : value;
        return result;
    }

    /// <summary>
    /// Epoch merge: windows granted on both sides keep the later epoch (no re-grant in the current window);
    /// granted on one side only uses that side's epoch (a window the other device never saw can re-arm later).
    /// </summary>
    public static Dictionary<string, string> MergedWindowEpochs(
        IReadOnlyDictionary<string, string> epochsA, IReadOnlyDictionary<string, int> tiersA,
        IReadOnlyDictionary<string, string> epochsB, IReadOnlyDictionary<string, int> tiersB)
    {
        var keys = epochsA.Keys.Union(epochsB.Keys).Union(tiersA.Keys).Union(tiersB.Keys);
        var result = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            var tierA = tiersA.TryGetValue(key, out var ta) ? ta : 0;
            var tierB = tiersB.TryGetValue(key, out var tb) ? tb : 0;
            epochsA.TryGetValue(key, out var ea);
            epochsB.TryGetValue(key, out var eb);
            string? chosen;
            if (tierA > 0 && tierB > 0) chosen = Max(ea, eb);
            else if (tierA > 0) chosen = ea;
            else if (tierB > 0) chosen = eb;
            else chosen = Max(ea, eb);
            if (chosen != null) result[key] = chosen;
        }
        return result;
    }

    /// <summary>Swift <c>[String?].compactMap{$0}.max()</c>: String comparison by Unicode scalars ≈ ordinal.</summary>
    private static string? Max(string? a, string? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return string.CompareOrdinal(a, b) >= 0 ? a : b;
    }
}
