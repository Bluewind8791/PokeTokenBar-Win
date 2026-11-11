using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of SaveSnapshotTests.swift.

file sealed class SnapshotStubException : Exception;

file sealed class OfflineProvider : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new SnapshotStubException());
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromException<IReadOnlyList<BaseSpecies>>(new SnapshotStubException());
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromException<BaseSpecies?>(new SnapshotStubException());
}

public class SaveSnapshotTests
{
    private static readonly DateTimeOffset BaseNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static string TempPath(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ptb-snapshot-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "companion-state.json");
    }

    private static CompanionStore Store(string path, Func<DateTimeOffset> clock) =>
        new(new OfflineProvider(), clock: clock, filePath: path, rng: new SeededRng(7), settings: new AppSettings(null));

    private static CompanionState SampleState(long tokens, int dexCount)
    {
        var s = new CompanionState { InstallBaselineSet = true, UsedSinceInstall = tokens, SpentTokens = 1_000 };
        for (var i = 1; i <= Math.Max(0, dexCount); i++)
            s.Dex.Add(new DexEntry(i, i, [i], Rarity.Common, BaseNow));
        s.Active = new MonState(1, [1], [1], stageIndex: 0, usedAtStage: 500, rarity: Rarity.Common, totalForms: 1);
        return s;
    }

    private static void WriteState(CompanionState s, string path) => File.WriteAllBytes(path, CompanionJson.Serialize(s));

    private static string LegacyPath(string stateFile) =>
        Path.Combine(SaveSnapshotManager.SnapshotsDirectory(stateFile), $"{SaveSnapshotManager.SnapshotPrefix}2020-01-01-000000.json");

    [Fact]
    public void TestSnapshotCreationAndListing() => MainActor.Run(() =>
    {
        var path = TempPath("create");
        WriteState(SampleState(12_500, 3), path);

        var store = Store(path, () => BaseNow);
        Assert.Empty(store.AvailableSnapshots);

        var snapshot = store.CreateManualSnapshot();
        Assert.Equal(3, snapshot.DexCount);
        Assert.Equal(12_500, snapshot.LifetimeTokens);
        Assert.Equal(1, snapshot.CurrentSpeciesID);
        Assert.False(snapshot.CurrentIsShiny);
        Assert.Null(snapshot.CurrentForm);
        Assert.True(File.Exists(snapshot.FilePath));

        Assert.Single(store.AvailableSnapshots);
        Assert.Equal(snapshot.Id, store.AvailableSnapshots[0].Id);
    });

    [Fact]
    public void TestSnapshotKeepsNonDefaultFormInSummaryAndRestoredState()
    {
        var path = TempPath("form");
        var sky = new PokemonForm("sky");
        var state = SampleState(1_000, 1);
        state.Active = new MonState(492, [492], stageIndex: 0, usedAtStage: 500, rarity: Rarity.Legendary,
            totalForms: 1, isShiny: true, form: sky);

        var created = SaveSnapshotManager.CreateSnapshot(state, path, BaseNow);
        var listed = SaveSnapshotManager.ListSnapshots(path).First();
        foreach (var snapshot in new[] { created, listed })
        {
            Assert.Equal(492, snapshot.CurrentSpeciesID);
            Assert.True(snapshot.CurrentIsShiny);
            Assert.Equal(sky, snapshot.CurrentForm);
            // The Swift test checks SpriteStore.spriteURL(...) == "10006.png"; the logic part is the asset name.
            Assert.Equal("10006", PokemonForm.StaticAssetName(492, snapshot.CurrentForm));
        }
        var restored = SaveSnapshotManager.LoadEnvelope(created.FilePath).State;
        Assert.Equal(sky, restored.Active?.Form);
        Assert.True(restored.Active?.IsShiny);
    }

    [Fact]
    public void TestSnapshotSummariesResolveCurrentAppearanceAcrossLegacyAndEnvelopeFiles()
    {
        (string Name, int SpeciesID, string? StoredForm, string Key, PokemonForm? Expected)[] cases =
        [
            ("sky", 492, "sky", "form", new PokemonForm("sky")),
            ("missing", 492, null, "form", new PokemonForm("land")),
            ("invalid", 492, "future", "form", new PokemonForm("land")),
            ("unown", 201, "b", "unownForm", new PokemonForm("b")),
            ("precursor", 420, "sunshine", "form", null),
            ("single", 25, "future", "form", null),
        ];
        foreach (var fixture in cases)
        {
            var path = TempPath("form-" + fixture.Name);
            var active = new JsonObject
            {
                ["baseID"] = fixture.SpeciesID, ["pathIDs"] = new JsonArray(fixture.SpeciesID), ["stageIndex"] = 0,
                ["usedAtStage"] = 0, ["rarity"] = "common", ["totalForms"] = 1,
            };
            if (fixture.StoredForm is { } form) active[fixture.Key] = form;
            var json = new JsonObject { ["active"] = active }.ToJsonString();
            File.WriteAllText(LegacyPath(path), json);
            var state = CompanionJson.Deserialize(json);
            // Creation also resolves raw in-memory values, rather than relying on the decoder.
            state.Active!.Form = fixture.StoredForm is { } raw ? new PokemonForm(raw) : null;
            var created = SaveSnapshotManager.CreateSnapshot(state, path, BaseNow);
            var listed = SaveSnapshotManager.ListSnapshots(path);
            Assert.Equal(2, listed.Count);
            foreach (var snapshot in listed.Prepend(created))
            {
                Assert.Equal(fixture.SpeciesID, snapshot.CurrentSpeciesID);
                Assert.Equal(fixture.Expected, snapshot.CurrentForm);
            }
        }

        var eggPath = TempPath("form-egg");
        var egg = SaveSnapshotManager.CreateSnapshot(new CompanionState(), eggPath, BaseNow);
        Assert.Null(egg.CurrentSpeciesID);
        Assert.Null(egg.CurrentForm);
        Assert.Null(SaveSnapshotManager.ListSnapshots(eggPath).First().CurrentForm);
    }

    /// <summary>A disguised Ditto hides its shiny state until the reveal; the snapshot row must not spoil it.</summary>
    [Fact]
    public void TestSnapshotDoesNotRevealShinyOfDisguisedDitto()
    {
        var path = TempPath("ditto");
        var state = SampleState(1_000, 1);
        var trash = new PokemonForm("trash");
        state.Active = new MonState(412, [412], stageIndex: 0, usedAtStage: 500, rarity: Rarity.Common, totalForms: 2,
            isShiny: true, dittoDisguise: 412, form: trash);
        var disguisedState = state.Clone();

        var created = SaveSnapshotManager.CreateSnapshot(state, path, BaseNow);
        Assert.False(created.CurrentIsShiny);
        Assert.Equal(trash, created.CurrentForm);
        Assert.Equal([false], SaveSnapshotManager.ListSnapshots(path).Select(s => s.CurrentIsShiny));
        Assert.Equal([trash], SaveSnapshotManager.ListSnapshots(path).Select(s => s.CurrentForm));

        state.Active = new MonState(132, [132], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Rare, totalForms: 1,
            isShiny: true, dittoDisguise: 412, dittoRevealed: true);
        var revealed = SaveSnapshotManager.CreateSnapshot(state, path, BaseNow.AddSeconds(60));
        Assert.True(revealed.CurrentIsShiny);
        Assert.Null(revealed.CurrentForm);
        Assert.Equal([true, false], SaveSnapshotManager.ListSnapshots(path).Select(s => s.CurrentIsShiny));

        WriteState(disguisedState, LegacyPath(path));
        Assert.False(SaveSnapshotManager.ListSnapshots(path).Last().CurrentIsShiny);
        Assert.Equal(trash, SaveSnapshotManager.ListSnapshots(path).Last().CurrentForm);
    }

    [Fact]
    public void TestSnapshotPruningKeepsAtMostTen()
    {
        var path = TempPath("pruning");
        var state = SampleState(20_000, 2);
        WriteState(state, path);

        for (var i = 1; i <= 15; i++)
            SaveSnapshotManager.CreateSnapshot(state, path, BaseNow.AddSeconds(i * 3600));

        var snapshots = SaveSnapshotManager.ListSnapshots(path);
        Assert.Equal(10, snapshots.Count);
        Assert.Equal(BaseNow.AddSeconds(15 * 3600), snapshots[0].Date);
        Assert.Equal(BaseNow.AddSeconds(6 * 3600), snapshots[^1].Date);
    }

    [Fact]
    public void TestAutoSnapshotIntervalGuard() => MainActor.Run(() =>
    {
        var path = TempPath("autointerval");
        var now = BaseNow;
        WriteState(SampleState(5_000, 1), path);

        var store = Store(path, () => now);
        store.CreateManualSnapshot();
        Assert.Single(store.AvailableSnapshots);

        now = BaseNow.AddSeconds(3600); // 1 hour later: no snapshot
        store.AutoSnapshotIfNeeded();
        Assert.Single(store.AvailableSnapshots);

        now = BaseNow.AddSeconds(13 * 3600); // 13 hours later: snapshot
        store.AutoSnapshotIfNeeded();
        Assert.Equal(2, store.AvailableSnapshots.Count);
    });

    [Fact]
    public void TestAutoCorruptionRecoveryFromLatestSnapshot() => MainActor.Run(() =>
    {
        var path = TempPath("recovery");
        WriteState(SampleState(77_777, 5), path);

        var store = Store(path, () => BaseNow);
        store.CreateManualSnapshot();
        Assert.Single(store.AvailableSnapshots);

        // Corrupt the main state file.
        const string garbage = "{ corrupted state json missing brackets...";
        File.WriteAllText(path, garbage);

        var recovered = Store(path, () => BaseNow);
        Assert.Equal(77_777, recovered.State.UsedSinceInstall);
        Assert.Equal(5, recovered.State.Dex.Count);
        Assert.Equal(1, recovered.State.Active?.BaseID);

        var corruptBackup = path + ".corrupt";
        Assert.True(File.Exists(corruptBackup));
        Assert.Equal(garbage, File.ReadAllText(corruptBackup));
    });

    [Fact]
    public void TestRestoreSnapshotRollsBackProgressAndPreservesCurrentAsSnapshot() => MainActor.Run(() =>
    {
        var path = TempPath("restore");
        var now = BaseNow;

        WriteState(SampleState(10_000, 1), path);
        var store = Store(path, () => now);
        var snapshot1 = store.CreateManualSnapshot();

        now = BaseNow.AddSeconds(3600);
        WriteState(SampleState(90_000, 4), path);
        var store2 = Store(path, () => now);
        Assert.Equal(90_000, store2.State.UsedSinceInstall);
        Assert.Equal(4, store2.State.Dex.Count);

        now = BaseNow.AddSeconds(7200);
        store2.RestoreSnapshot(snapshot1);

        Assert.Equal(10_000, store2.State.UsedSinceInstall);
        Assert.Single(store2.State.Dex);
        Assert.Contains(store2.AvailableSnapshots, s => s.LifetimeTokens == 90_000 && s.DexCount == 4);
    });

    [Fact]
    public void TestRestoreOldestSnapshotAtRetentionLimit() => MainActor.Run(() =>
    {
        var path = TempPath("restore-oldest-at-limit");
        for (var i = 0; i < SaveSnapshotManager.MaxSnapshotsToKeep; i++)
            SaveSnapshotManager.CreateSnapshot(SampleState(10_000 + i * 1_000, i + 1), path, BaseNow.AddSeconds(i * 3600));

        WriteState(SampleState(99_000, 12), path);
        var now = BaseNow.AddSeconds(SaveSnapshotManager.MaxSnapshotsToKeep * 3600);
        var store = Store(path, () => now);
        Assert.Equal(SaveSnapshotManager.MaxSnapshotsToKeep, store.AvailableSnapshots.Count);
        var oldest = store.AvailableSnapshots[^1];
        Assert.Equal(10_000, oldest.LifetimeTokens);

        // The pre-restore safety snapshot is the 11th file and prunes the selected one.
        store.RestoreSnapshot(oldest);

        Assert.Equal(10_000, store.State.UsedSinceInstall);
        Assert.Single(store.State.Dex);
        Assert.Equal(SaveSnapshotManager.MaxSnapshotsToKeep, store.AvailableSnapshots.Count);
        Assert.Contains(store.AvailableSnapshots, s => s.LifetimeTokens == 99_000 && s.DexCount == 12);
    });

    [Fact]
    public void TestRestoreUnreadableSnapshotLeavesStateAndSnapshotsUntouched() => MainActor.Run(() =>
    {
        var path = TempPath("restore-unreadable");
        for (var i = 0; i < SaveSnapshotManager.MaxSnapshotsToKeep; i++)
            SaveSnapshotManager.CreateSnapshot(SampleState(10_000 + i * 1_000, i + 1), path, BaseNow.AddSeconds(i * 3600));

        WriteState(SampleState(99_000, 12), path);
        var now = BaseNow.AddSeconds(SaveSnapshotManager.MaxSnapshotsToKeep * 3600);
        var store = Store(path, () => now);
        var selected = store.AvailableSnapshots[0];
        var before = store.AvailableSnapshots.Select(s => s.Id).ToList();

        // The listed file goes bad before the user confirms the restore.
        File.WriteAllText(selected.FilePath, "corrupted invalid json");

        Assert.ThrowsAny<Exception>(() => store.RestoreSnapshot(selected));
        Assert.Equal(99_000, store.State.UsedSinceInstall);
        Assert.Equal(12, store.State.Dex.Count);
        // No safety snapshot was written, so nothing valid was pruned for a restore that never happened.
        var after = SaveSnapshotManager.ListSnapshots(path).Select(s => s.Id).ToHashSet();
        Assert.Equal(before.Where(id => id != selected.Id).ToHashSet(), after);
    });

    [Fact]
    public void TestCorruptSnapshotFileIsSkippedGracefully()
    {
        var path = TempPath("corruptsnapshot");
        var dir = SaveSnapshotManager.SnapshotsDirectory(path);
        File.WriteAllText(Path.Combine(dir, "companion-snapshot-2026-09-18-120000.json"), "corrupted invalid json");

        Assert.Empty(SaveSnapshotManager.ListSnapshots(path));
    }
}
