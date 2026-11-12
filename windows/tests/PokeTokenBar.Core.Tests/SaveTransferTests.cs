using System.Text;
using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of SaveTransferTests.swift.

file sealed class TransferStubException : Exception;

/// <summary>Save transfer needs no evolution line: usage crediting must work without one, so everything fails.</summary>
file sealed class OfflineProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new TransferStubException());
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new TransferStubException());
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromException<BaseSpecies?>(new TransferStubException());
}

/// <summary>Stops at the <b>species roll</b> (BaseSpeciesIndex): HatchIfNeeded's first await window.</summary>
file sealed class GatedIndexProvider(TaskCompletionSource entered, TaskCompletionSource release, EvoLine result) : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(result);
    public async Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex()
    {
        entered.TrySetResult();
        await release.Task;
        return [new BaseSpecies(1, 255)];
    }
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

/// <summary>Stops in the line fetch until released: reproduces a state replacement during hatching.</summary>
file sealed class GatedProvider(TaskCompletionSource entered, TaskCompletionSource release, EvoLine result) : IPokeProviding
{
    public async Task<EvoLine> Line(int baseSpeciesID)
    {
        entered.TrySetResult();
        await release.Task;
        return result;
    }
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new TransferStubException());
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromException<BaseSpecies?>(new TransferStubException());
}

public class SaveTransferTests
{
    private static readonly DateTimeOffset TransferNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly Dictionary<string, long> Today1 = new() { ["test"] = 1 };
    private static readonly Dictionary<string, long> Today0 = new() { ["test"] = 0 };

    private static Dictionary<string, long> Usage(long v) => new() { ["test"] = v };

    /// <summary>A dedicated directory per test: import backups are named by time, not by state file.</summary>
    private static string TempPath(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ptb-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "companion-state.json");
    }

    private static CompanionStore Store(string path, Func<DateTimeOffset>? clock = null, IPokeProviding? provider = null) =>
        new(provider ?? new OfflineProvider(), clock: clock ?? (() => TransferNow), filePath: path,
            rng: new SeededRng(7), settings: new AppSettings(null));

    /// <summary>Shaped like a state exported from the old Mac — 56.8M already credited today.</summary>
    private static CompanionState OldMacState(string today) => new()
    {
        InstallBaselineSet = true,
        UsedSinceInstall = 8_000_000_000,
        SpentTokens = 3_500_000_000,
        ClaimedTodayTokensByProvider = new() { ["test"] = 56_800_000 },
        LastDate = today,
        Inventory = new() { ["rareCandy"] = 2 },
        CollectedFinals = ["1-3"],
        Dex = [new DexEntry(1, 3, [1, 2, 3], Rarity.Common, TransferNow)],
        RepresentativeSpeciesID = 2,
    };

    private static byte[] Encode(CompanionState s, string device = "Old Mac") =>
        SaveTransfer.Encode(s, "2.5.0", device, TransferNow);

    private static void WriteState(CompanionState s, string path) => File.WriteAllBytes(path, CompanionJson.Serialize(s));

    private static SaveTransferException Throws(Action action) => Assert.Throws<SaveTransferException>(action);

    // Envelope

    [Fact]
    public void TestRoundTripPreservesProgress()
    {
        var original = OldMacState("2026-08-03");
        var envelope = SaveTransfer.Decode(Encode(original));

        Assert.Equal(SaveEnvelope.FormatID, envelope.Format);
        Assert.Equal("Old Mac", envelope.SourceDevice);
        Assert.Equal(original.UsedSinceInstall, envelope.State.UsedSinceInstall);
        Assert.Equal(original.SpentTokens, envelope.State.SpentTokens);
        Assert.Equal(original.Inventory, envelope.State.Inventory);
        Assert.Single(envelope.State.Dex);
        Assert.Equal(original.CollectedFinals, envelope.State.CollectedFinals);
        Assert.Equal(2, envelope.State.RepresentativeSpeciesID); // the representative moves with the save
    }

    /// <summary>A repeated species id in chainOrder / pathIDs (hand edit) breaks unique-key name maps; deduped on load.</summary>
    [Fact]
    public void TestDuplicateSpeciesInSavedChainsAreRemovedOnLoad() => MainActor.Run(async () =>
    {
        var path = TempPath("dup-chain");
        var s = OldMacState("2026-08-03");
        s.Dex = [new DexEntry(1, 2, [1, 1, 2], Rarity.Common, TransferNow)];
        s.Active = new MonState(4, [4, 4, 5], [4, 4, 5, 5, 6], stageIndex: 2, usedAtStage: 0, rarity: Rarity.Common, totalForms: 3);
        WriteState(s, path);

        var store = Store(path);
        Assert.Equal([1, 2], store.State.Dex[0].ChainOrder);
        Assert.Equal([4, 5], store.State.Active!.PathIDs);
        Assert.Equal([4, 5, 6], store.State.Active!.PlannedPathIDs);
        Assert.Equal(5, store.CurrentSpeciesID); // the current species survives the dedupe
        Assert.Equal(1, store.State.Active!.StageIndex);

        var names = await store.DexResolveChainNames(store.State.Dex[0]);
        Assert.Equal(new Dictionary<int, string> { [1] = "#1", [2] = "#2" }, names);
    });

    [Fact]
    public void TestRoundTripPreservesActiveRepeatGrowthBoost()
    {
        var original = new CompanionState
        {
            Active = new MonState(1, [1], [1, 2, 3], stageIndex: 0, usedAtStage: 12_000_000, rarity: Rarity.Common,
                totalForms: 3, hasGrowthBoost: true),
        };
        var envelope = SaveTransfer.Decode(Encode(original));

        Assert.True(envelope.State.Active?.HasGrowthBoost);
        Assert.Equal(12_000_000, envelope.State.Active!.UsedAtStage);
        Assert.Equal(62_500_000, envelope.State.Active!.PhaseThreshold);
    }

    /// <summary>[Core] Without the envelope, lenient decoding absorbs any JSON as an empty state.</summary>
    [Fact]
    public void TestForeignJSONIsRejectedRatherThanImportedAsEmptyState()
    {
        var foreign = Encoding.UTF8.GetBytes("""{"some":"other app","dex":123}""");
        Assert.Equal(SaveTransferErrorKind.NotASaveFile, Throws(() => SaveTransfer.Decode(foreign)).Kind);

        // A bare state (no envelope) is not a save either.
        var bare = CompanionJson.Serialize(OldMacState("2026-08-03"));
        Assert.Equal(SaveTransferErrorKind.NotASaveFile, Throws(() => SaveTransfer.Decode(bare)).Kind);
    }

    /// <summary>[Regression H4] B alone of the "decode failed || wrong format" gate: a complete envelope whose only
    /// difference is the format value.</summary>
    [Fact]
    public void TestValidEnvelopeWithWrongFormatIDIsRejected()
    {
        var data = Encode(OldMacState("2026-08-03"), "Other App");
        var json = JsonNode.Parse(data)!.AsObject();
        Assert.Equal(SaveEnvelope.FormatID, (string?)json["format"]); // premise: the original is valid
        json["format"] = "someotherapp.save";
        var patched = Encoding.UTF8.GetBytes(json.ToJsonString());

        // Premise: the structure itself is valid — restoring the format makes it decode, so only branch B is tested.
        var restored = JsonNode.Parse(patched)!.AsObject();
        restored["format"] = SaveEnvelope.FormatID;
        SaveTransfer.Decode(Encoding.UTF8.GetBytes(restored.ToJsonString()));

        Assert.Equal(SaveTransferErrorKind.NotASaveFile, Throws(() => SaveTransfer.Decode(patched)).Kind);
    }

    [Fact]
    public void TestNewerSchemaIsRejected()
    {
        var json = JsonNode.Parse(Encode(new CompanionState(), "Future Mac"))!.AsObject();
        json["schema"] = SaveEnvelope.SchemaVersion + 1;
        var data = Encoding.UTF8.GetBytes(json.ToJsonString());

        var error = Throws(() => SaveTransfer.Decode(data));
        Assert.Equal(SaveTransferErrorKind.NewerSchema, error.Kind);
        Assert.Equal(SaveEnvelope.SchemaVersion + 1, error.Found);
        Assert.Equal(SaveEnvelope.SchemaVersion, error.Supported);
    }

    // Device re-alignment (regressions)

    /// <summary>[Regression] Tokens used on the new Mac on transfer day were silently dropped. The control (no rebase)
    /// confirms the trigger branch is really exercised.</summary>
    [Fact]
    public void TestTransferDayTokensStillCountAfterRebase() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var imported = OldMacState(today);
        const long newMacTodaySoFar = 5_000_000;
        const long newMacTodayLater = 8_000_000;

        // Control: imported as-is — no delta must be credited (the defect).
        var rawPath = TempPath("raw");
        WriteState(imported, rawPath);
        var raw = Store(rawPath);
        var rawBefore = raw.State.UsedSinceInstall;
        raw.Update(Usage(newMacTodayLater), today, 0, BurnTier.Idle, false, true);
        Assert.Equal(0, raw.State.UsedSinceInstall - rawBefore);

        // Real path: the ledger is re-seeded with this device's usage at import time.
        var s = Store(TempPath("rebased"));
        var envelope = SaveTransfer.Decode(Encode(imported));
        s.ApplySave(envelope, Usage(newMacTodaySoFar), today, true);

        Assert.Equal(newMacTodaySoFar, s.State.ClaimedTodayTokensByProvider?["test"]);
        Assert.Equal(today, s.State.LastDate);
        Assert.True(s.State.InstallBaselineSet);

        var before = s.State.UsedSinceInstall;
        s.Update(Usage(newMacTodayLater), today, 0, BurnTier.Idle, false, true);
        Assert.Equal(newMacTodayLater - newMacTodaySoFar, s.State.UsedSinceInstall - before);
    });

    /// <summary>Without this device's usage at import time, the baseline goes to the new-install path.</summary>
    [Fact]
    public void TestImportWithoutUsageDataDefersBaselineInsteadOfCreditingWholeDay() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var s = Store(TempPath("nodata"));
        s.ApplySave(SaveTransfer.Decode(Encode(OldMacState(today))), Today0, today, false);

        Assert.False(s.State.InstallBaselineSet);

        var before = s.State.UsedSinceInstall;
        s.Update(Usage(40_000_000), today, 0, BurnTier.Idle, false, true);
        Assert.Equal(before, s.State.UsedSinceInstall); // no retroactive day credit
        Assert.Equal(40_000_000, s.State.ClaimedTodayTokensByProvider?["test"]);

        s.Update(Usage(41_000_000), today, 0, BurnTier.Idle, false, true);
        Assert.Equal(1_000_000, s.State.UsedSinceInstall - before);
    });

    /// <summary>[Regression] hasUsageData true with an empty today map (stale only) must not seed an empty ledger.</summary>
    [Fact]
    public void TestImportWithStaleOnlyUsageDefersBaselineInsteadOfSeedingEmptyLedger() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var s = Store(TempPath("stale-only"));
        s.ApplySave(SaveTransfer.Decode(Encode(OldMacState(today))), new Dictionary<string, long>(), today, true);

        Assert.False(s.State.InstallBaselineSet);
        Assert.Null(s.State.ClaimedTodayTokensByProvider);
        Assert.Equal("", s.State.LastDate);

        var before = s.State.UsedSinceInstall;
        s.Update(Usage(40_000_000), today, 0, BurnTier.Idle, false, true);
        Assert.Equal(before, s.State.UsedSinceInstall);
        Assert.Equal(40_000_000, s.State.ClaimedTodayTokensByProvider?["test"]);

        s.Update(Usage(41_000_000), today, 0, BurnTier.Idle, false, true);
        Assert.Equal(1_000_000, s.State.UsedSinceInstall - before);
    });

    // Progress preservation · guard rails

    [Fact]
    public void TestImportKeepsProgressAndCandyGrantLedger() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var imported = OldMacState(today);
        // Limits are account-wide: dropping the ledger would grant candies again.
        imported.CandyGrantTier = new() { ["five_hour|2026-08-03T00:00:00Z"] = 100 };
        imported.CandyFeatureSeeded = true;

        var s = Store(TempPath("progress"));
        s.ApplySave(SaveTransfer.Decode(Encode(imported)), Today1, today, true);

        Assert.Equal(8_000_000_000, s.State.UsedSinceInstall);
        Assert.Equal(3_500_000_000, s.State.SpentTokens);
        Assert.Single(s.State.Dex);
        Assert.Equal(2, s.State.Inventory["rareCandy"]);
        Assert.Equal(2, s.State.RepresentativeSpeciesID);
        Assert.Equal(100, s.State.CandyGrantTier["five_hour|2026-08-03T00:00:00Z"]);
        Assert.True(s.State.CandyFeatureSeeded);
    });

    /// <summary>The previous state is kept before overwriting — the manual way back after a wrong import.</summary>
    [Fact]
    public void TestPreviousStateIsBackedUpBeforeOverwrite() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var path = TempPath("backup");
        WriteState(new CompanionState { InstallBaselineSet = true, UsedSinceInstall = 123_456_789 }, path);

        var s = Store(path);
        Assert.Equal(123_456_789, s.State.UsedSinceInstall);

        s.ApplySave(SaveTransfer.Decode(Encode(OldMacState(today))), Today0, today, true);

        var backup = Path.Combine(Path.GetDirectoryName(path)!, SaveTransfer.BackupFileName(TransferNow));
        var restored = CompanionJson.Deserialize(File.ReadAllBytes(backup));
        Assert.Equal(123_456_789, restored.UsedSinceInstall);
        Assert.Equal(8_000_000_000, s.State.UsedSinceInstall);
    });

    /// <summary>[Regression] An import landing in a hatch's network wait was overwritten by the late hatch.</summary>
    [Fact]
    public void TestImportDuringHatchDiscardsTheHatch() => MainActor.Run(async () =>
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var evo = new EvoLine(1, new EvoNode(1), Rarity.Common,
            new Dictionary<int, Dictionary<string, string>> { [1] = new() { ["en"] = "P1", ["ko"] = "포1", ["ja"] = "ポ1" } });
        var s = Store(TempPath("hatchrace"), provider: new GatedProvider(entered, release, evo));

        var hatching = s.Hatch(1);
        await entered.Task; // the hatch reached the line fetch (network) wait

        var imported = OldMacState("2026-08-03");
        imported.Active = new MonState(403, [403], [403], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        s.ApplySave(SaveTransfer.Decode(Encode(imported)), Today1, "2026-08-03", true);

        release.SetResult();
        await hatching;

        Assert.Equal(403, s.State.Active?.BaseID); // the late hatch must not overwrite the import
        Assert.Equal(imported.Dex.Count, s.State.Dex.Count);
    });

    /// <summary>[Regression B1] An import in the hatch's <b>first</b> await (ChooseBase) window.</summary>
    [Fact]
    public void TestImportDuringSpeciesRollDiscardsTheHatch() => MainActor.Run(async () =>
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var evo = new EvoLine(1, new EvoNode(1), Rarity.Common,
            new Dictionary<int, Dictionary<string, string>> { [1] = new() { ["en"] = "P1", ["ko"] = "포1", ["ja"] = "ポ1" } });
        var path = TempPath("rollrace");

        // Egg + hatch threshold reached + no pre-roll → enters ChooseBase().
        WriteState(new CompanionState { InstallBaselineSet = true, EggUsage = PokemonBalance.EggHatchThreshold }, path);

        var s = Store(path, provider: new GatedIndexProvider(entered, release, evo));
        Assert.Null(s.State.Active);

        var hatching = s.HatchIfNeeded();
        await entered.Task; // the species roll reached the index wait

        var imported = OldMacState("2026-08-03");
        imported.Active = new MonState(403, [403], [403], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        s.ApplySave(SaveTransfer.Decode(Encode(imported)), Today1, "2026-08-03", true);

        release.SetResult();
        await hatching;

        Assert.Equal(403, s.State.Active?.BaseID);
        Assert.Equal(imported.Dex.Count, s.State.Dex.Count);
    });

    /// <summary>[Regression H3] After the hatch is discarded, the imported Pokémon's line must still load.</summary>
    [Fact]
    public void TestImportDuringHatchStillLoadsTheImportedLine() => MainActor.Run(async () =>
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var evo = new EvoLine(403, new EvoNode(403), Rarity.Common,
            new Dictionary<int, Dictionary<string, string>> { [403] = new() { ["en"] = "Shinx", ["ko"] = "꼬링크", ["ja"] = "コリンク" } });
        var s = Store(TempPath("linereload"), provider: new GatedProvider(entered, release, evo));

        var hatching = s.Hatch(1);
        await entered.Task;

        var imported = OldMacState("2026-08-03");
        imported.Active = new MonState(403, [403], [403], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        s.ApplySave(SaveTransfer.Decode(Encode(imported)), Today1, "2026-08-03", true);
        Assert.Null(s.CurrentLine); // premise: blocked by the hatch lock

        release.SetResult();
        await hatching;

        var spins = 0;
        while (s.CurrentLine == null && spins < 500)
        {
            await Task.Yield();
            spins++;
        }

        Assert.NotNull(s.CurrentLine);
        Assert.Equal(403, s.CurrentLine!.BaseID);
        Assert.NotEqual("Token Egg", s.DisplayName);
    });

    /// <summary>[Regression H1] Imported before any AI CLI use: the Pokémon must not show as an egg.</summary>
    [Fact]
    public void TestImportedCompanionIsNotShownAsEggBeforeUsageArrives() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var s = Store(TempPath("noegg"));
        var imported = OldMacState(today);
        imported.Active = new MonState(403, [403], [403], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        s.ApplySave(SaveTransfer.Decode(Encode(imported)), Today0, today, false);
        Assert.False(s.State.InstallBaselineSet);

        s.Update(Today0, today, 0, BurnTier.Idle, false, false);
        Assert.NotNull(s.State.Active);
        Assert.Equal(CompanionStateKind.Idle, s.DisplayState);

        // The egg on the same path is still an egg (the opposite direction).
        var e = Store(TempPath("stillegg"));
        e.Update(Today0, today, 0, BurnTier.Idle, false, false);
        Assert.Equal(CompanionStateKind.Egg, e.DisplayState);
    });

    // Trust-boundary normalization (H2)

    [Fact]
    public void TestExtremeValuesAreClampedAtTheTrustBoundary() => MainActor.Run(() =>
    {
        var evil = new CompanionState
        {
            InstallBaselineSet = true,
            UsedSinceInstall = long.MaxValue,
            SpentTokens = long.MinValue,
            EggUsage = long.MaxValue,
            ClaimedTodayTokensByProvider = new() { ["test"] = -42 },
            Active = new MonState(1, [1], [1], stageIndex: int.MaxValue, usedAtStage: long.MaxValue,
                rarity: Rarity.Common, totalForms: int.MaxValue),
        };

        var envelope = SaveTransfer.Decode(SaveTransfer.Encode(evil, "2.5.0", "Corrupt", TransferNow));
        var s = envelope.State;

        Assert.Equal(SaveTransfer.MaxTokenValue, s.UsedSinceInstall);
        Assert.Equal(0, s.SpentTokens);
        Assert.Equal(SaveTransfer.MaxTokenValue, s.EggUsage);
        Assert.Equal(0, s.ClaimedTodayTokensByProvider?["test"]);
        Assert.Equal(SaveTransfer.MaxTokenValue, s.Active!.UsedAtStage);
        Assert.Equal(12, s.Active.TotalForms);
        Assert.Equal(0, s.Active.StageIndex);

        // Run real arithmetic on the normalized values.
        var store = Store(TempPath("clamped"));
        store.ApplySave(envelope, Today0, "2026-08-03", true);
        Assert.True(store.AvailableTokens >= 0);
        store.Update(Usage(1_000), "2026-08-03", 0, BurnTier.Idle, false, true);
        Assert.True(store.State.UsedSinceInstall <= SaveTransfer.MaxTokenValue + 1_000);
    });

    /// <summary>[Regression H2 follow-up] Extreme values already on disk are clamped on load too.</summary>
    [Fact]
    public void TestCorruptStateOnDiskIsClampedOnLoadNotJustOnImport() => MainActor.Run(() =>
    {
        var path = TempPath("diskclamp");
        var json = "{\"installBaselineSet\":true,\"usedSinceInstall\":9223372036854775807,"
                   + "\"spentTokens\":-9223372036854775808,\"eggUsage\":9223372036854775807,"
                   + "\"claimedTodayTokens\":-1,\"lastDate\":\"2026-08-03\"}";
        File.WriteAllText(path, json);

        var s = Store(path);
        Assert.Equal(SaveTransfer.MaxTokenValue, s.State.UsedSinceInstall);
        Assert.Equal(0, s.State.SpentTokens);
        Assert.Equal(SaveTransfer.MaxTokenValue, s.State.EggUsage);
        Assert.Null(s.State.ClaimedTodayTokensByProvider); // the legacy aggregate is not split per provider

        Assert.True(s.AvailableTokens >= 0);
        s.Update(Usage(1_000), "2026-08-03", 0, BurnTier.Idle, false, true);
        Assert.True(s.State.UsedSinceInstall <= SaveTransfer.MaxTokenValue + 1_000);
    });

    // Field classes (M-c · M-e · M-g)

    /// <summary>[M-g] Pins the field list so every new CompanionState field gets a transfer classification.
    /// Checked against both the C# properties and the persisted JSON keys of a fully populated state.</summary>
    [Fact]
    public void TestEveryCompanionStateFieldIsClassifiedForTransfer()
    {
        // eggTier (egg guarantee) = progress — a purchase, it follows the save to another device.
        // eggPrayers = progress — a record on the egg being held, so it moves with the egg (#466).
        // notifiedDexCompletions / dexCompletionsSeeded = progress — derived from the dex, so they follow it and the
        // other device does not announce the same completion again (#442).
        string[] progress = ["usedSinceInstall", "spentTokens", "eggUsage", "eggTier", "pendingHatchID", "pendingForm",
            "eggPrayers", "active", "representativeSpeciesID", "representativeForm", "dex", "collectedFinals", "inventory",
            "notifiedDexCompletions", "dexCompletionsSeeded"];
        string[] deviceLedger = ["installBaselineSet", "claimedTodayTokensByProvider", "lastDate"];
        string[] accountLedger = ["candyGrantTier", "candyWindowEpoch", "candyFeatureSeeded"];
        string[] devicePreference = ["language"];
        var classified = progress.Concat(deviceLedger).Concat(accountLedger).Concat(devicePreference).ToHashSet();

        var properties = typeof(CompanionState).GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).ToHashSet();
        Assert.Equal(classified.Order(), properties.Order());

        var full = new CompanionState
        {
            EggTier = Rarity.Rare,
            PendingHatchID = 201,
            PendingForm = new PokemonForm("b"),
            ClaimedTodayTokensByProvider = [],
            Active = new MonState(1, [1]),
            RepresentativeSpeciesID = 201,
            RepresentativeForm = new PokemonForm("b"),
            EggPrayers = new PrayerRecord(1),
        };
        var keys = CompanionJson.EncodeState(full, SwiftDateEncoding.DeferredToDate).Select(kv => kv.Key).ToHashSet();
        Assert.Equal(classified.Order(), keys.Order());
    }

    /// <summary>[M-c] Language is how this device shows things, not progress.</summary>
    [Fact]
    public void TestImportKeepsThisDevicesLanguage() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var path = TempPath("lang");
        WriteState(new CompanionState { InstallBaselineSet = true, Language = AppLanguage.En }, path);
        var s = Store(path);
        Assert.Equal(AppLanguage.En, s.Language);

        var imported = OldMacState(today);
        imported.Language = AppLanguage.Ja;
        s.ApplySave(SaveTransfer.Decode(Encode(imported, "JA Mac")), Today1, today, true);

        Assert.Equal(AppLanguage.En, s.Language);
        Assert.Equal(imported.Dex.Count, s.State.Dex.Count);
    });

    /// <summary>[M-e] The candy ledger is account-wide: merged, not replaced by an older save.</summary>
    [Fact]
    public void TestCandyGrantLedgerMergesInsteadOfBeingReplacedByAnOlderSave() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var path = TempPath("candy");
        WriteState(new CompanionState
        {
            InstallBaselineSet = true,
            CandyGrantTier = new() { ["five_hour|A"] = 100, ["weekly|B"] = 80 },
        }, path);
        var s = Store(path);

        var older = OldMacState(today);
        older.CandyGrantTier = new() { ["weekly|B"] = 50, ["five_hour|C"] = 100 };
        s.ApplySave(SaveTransfer.Decode(Encode(older)), Today1, today, true);

        Assert.Equal(100, s.State.CandyGrantTier["five_hour|A"]);
        Assert.Equal(80, s.State.CandyGrantTier["weekly|B"]);
        Assert.Equal(100, s.State.CandyGrantTier["five_hour|C"]);
    });

    // Backup guard rails (M-a · M-b)

    /// <summary>[M-a] No backup → abort without overwriting.</summary>
    [Fact]
    public void TestImportAbortsWhenBackupCannotBeWritten() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var path = TempPath("nobackup");
        WriteState(new CompanionState { InstallBaselineSet = true, UsedSinceInstall = 123_456_789 }, path);
        var s = Store(path);

        // Block the backup slot with a directory so the write fails.
        var blocked = Path.Combine(Path.GetDirectoryName(path)!, SaveTransfer.BackupFileName(TransferNow));
        Directory.CreateDirectory(blocked);
        try
        {
            var envelope = SaveTransfer.Decode(Encode(OldMacState(today)));
            var error = Throws(() => s.ApplySave(envelope, Today1, today, true));
            Assert.Equal(SaveTransferErrorKind.BackupFailed, error.Kind);
            Assert.Equal(123_456_789, s.State.UsedSinceInstall);
            Assert.Empty(s.State.Dex);
        }
        finally
        {
            Directory.Delete(blocked);
        }
    });

    /// <summary>[M-b] Every import gets a new backup slot, so a second import keeps the original.</summary>
    [Fact]
    public void TestSecondImportDoesNotDestroyTheOriginalBackup() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var path = TempPath("twoimports");
        WriteState(new CompanionState { InstallBaselineSet = true, UsedSinceInstall = 111 }, path);

        var now = TransferNow;
        var s = Store(path, clock: () => now);

        var first = OldMacState(today);
        first.UsedSinceInstall = 222;
        s.ApplySave(SaveTransfer.Decode(SaveTransfer.Encode(first, "2.5.0", "A", TransferNow)), Today1, today, true);

        now = TransferNow.AddSeconds(60); // the next backup is another slot
        var second = OldMacState(today);
        second.UsedSinceInstall = 333;
        s.ApplySave(SaveTransfer.Decode(SaveTransfer.Encode(second, "2.5.0", "B", TransferNow)), Today1, today, true);

        var dir = Path.GetDirectoryName(path)!;
        var backups = Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.StartsWith(SaveTransfer.BackupFilePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, backups.Count);
        var oldest = CompanionJson.Deserialize(File.ReadAllBytes(Path.Combine(dir, backups[0])));
        Assert.Equal(111, oldest.UsedSinceInstall);
    });

    // Confirmation policy · display state (M-f · M-h)

    /// <summary>[M-f] A destructive default button would replace progress with one Return key.</summary>
    [Fact]
    public void TestCancelIsTheDefaultButtonOnTheImportConfirmation()
    {
        Assert.Equal("\r", ImportConfirmPolicy.KeyEquivalent(ImportConfirmPolicy.CancelButtonIndex));
        Assert.Equal("", ImportConfirmPolicy.KeyEquivalent(ImportConfirmPolicy.ReplaceButtonIndex));
    }

    /// <summary>[M-h] Pins ApplySave's display state.</summary>
    [Fact]
    public void TestApplySaveSetsDisplayStateFromWhetherACompanionCameIn() => MainActor.Run(() =>
    {
        const string today = "2026-08-03";
        var withMon = OldMacState(today);
        withMon.Active = new MonState(403, [403], [403], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        var a = Store(TempPath("dispA"));
        a.ApplySave(SaveTransfer.Decode(SaveTransfer.Encode(withMon, "2.5.0", "A", TransferNow)), Today1, today, true);
        Assert.Equal(CompanionStateKind.Idle, a.DisplayState);

        var eggOnly = OldMacState(today); // active == null
        var b = Store(TempPath("dispB"));
        b.ApplySave(SaveTransfer.Decode(SaveTransfer.Encode(eggOnly, "2.5.0", "B", TransferNow)), Today1, today, true);
        Assert.Equal(CompanionStateKind.Egg, b.DisplayState);
    });

    // File boundary (LOW)

    [Fact]
    public void TestOversizedFileIsRejectedBeforeParsing()
    {
        var huge = new byte[SaveTransfer.MaxFileBytes + 1];
        var error = Throws(() => SaveTransfer.Decode(huge));
        Assert.Equal(SaveTransferErrorKind.FileTooLarge, error.Kind);
        Assert.Equal(SaveTransfer.MaxFileBytes + 1, error.Bytes);
        Assert.Equal(SaveTransfer.MaxFileBytes, error.Limit);
    }

    /// <summary>A newer-schema body may not decode; the header still reports "update the app".</summary>
    [Fact]
    public void TestNewerSchemaIsReportedEvenWhenTheBodyIsUnreadable()
    {
        var json = """{"format":"poketokenbar.save","schema":99,"whatever":{"unknown":true}}""";
        var error = Throws(() => SaveTransfer.Decode(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(SaveTransferErrorKind.NewerSchema, error.Kind);
        Assert.Equal(99, error.Found);
        Assert.Equal(SaveEnvelope.SchemaVersion, error.Supported);
    }

    [Fact]
    public void TestSuggestedFileNameCarriesDate()
    {
        // 2026-08-03 12:00 UTC — noon, so no time zone crosses the date boundary (the name uses the local date).
        var name = SaveTransfer.SuggestedFileName(DateTimeOffset.FromUnixTimeSeconds(1_785_758_400));
        Assert.StartsWith("PokeTokenBar-Save-", name);
        Assert.EndsWith(".json", name);
        Assert.Contains("2026-08-03", name);
    }
}
