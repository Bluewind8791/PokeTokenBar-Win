using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

// Port of RareCandyTests.swift.
// Not ported: CandyGrantEvaluationTests.testCodexWindowClassification and RareCandyGrantIntegrationTests
// (they exercise UsageStore, ported separately); CandyNotificationCopyTests' Korean wording checks
// (the L type is a temporary English-only stub in this sandbox).

file static class RC
{
    public static CandyWindow W(string key, WindowClass kind, double util, string name = "T", string? epoch = null) =>
        new(key, name, kind, util, epoch);

    /// <summary>Seeds with no windows, then raises n unique session windows to 100% (n candies).</summary>
    public static void GiveCandies(CompanionStore s, int n)
    {
        s.GrantCandies([], limitsReady: true);
        for (var i = 0; i < n; i++) s.GrantCandies([W($"test.session.{i}", WindowClass.Session, 100)], limitsReady: true);
    }
}

/// <summary>line() throws — reproduces the line-not-loaded state (offline / right after restart).</summary>
file sealed class RCLineThrows : IPokeProviding
{
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromException<EvoLine>(new HttpRequestException("offline"));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() => Task.FromResult<IReadOnlyList<BaseSpecies>>([]);
    public Task<BaseSpecies?> BaseSpecies(int id) => Task.FromResult<BaseSpecies?>(null);
}

public class CandyGrantEvaluationTests
{
    private static List<CandyGrant> Eval(Dictionary<string, int> tier, Dictionary<string, string> epochs,
        params CandyWindow[] windows) => CompanionStore.EvaluateCandyGrants(windows, tier, epochs);

    [Fact]
    public void SessionGrantsOne()
    {
        var tier = new Dictionary<string, int>();
        var grants = Eval(tier, [], RC.W("s", WindowClass.Session, 100));
        Assert.Equal([1], grants.Select(g => g.Count));
        Assert.Equal(1, tier["s"]);
    }

    [Fact]
    public void WeeklyGrantsFive()
    {
        var grants = Eval([], [], RC.W("wk", WindowClass.Weekly, 100));
        Assert.Equal([RareCandy.WeeklyGrant], grants.Select(g => g.Count));
    }

    [Fact]
    public void Below100NoGrant()
    {
        var tier = new Dictionary<string, int>();
        var grants = Eval(tier, [], RC.W("s", WindowClass.Session, 99.9));
        Assert.Empty(grants);
        Assert.False(tier.ContainsKey("s"));
    }

    /// <summary>No regrant while the same tier holds.</summary>
    [Fact]
    public void NoDoubleGrantWhileAt100()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        Eval(tier, epochs, RC.W("s", WindowClass.Session, 100, epoch: "E1"));
        var again = Eval(tier, epochs, RC.W("s", WindowClass.Session, 100, epoch: "E1"));
        Assert.Empty(again);
    }

    /// <summary>Dropping below 100% re-arms (removed from the map); filling again regrants.</summary>
    [Fact]
    public void RearmAfterDropBelow100()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        Eval(tier, epochs, RC.W("s", WindowClass.Session, 100));
        Eval(tier, epochs, RC.W("s", WindowClass.Session, 40));
        Assert.False(tier.ContainsKey("s"));
        var regrant = Eval(tier, epochs, RC.W("s", WindowClass.Session, 100));
        Assert.Equal([1], regrant.Select(g => g.Count));
    }

    /// <summary>[#326] A reset missed while asleep keeps util at 100%; the epoch change re-arms.</summary>
    [Fact]
    public void RearmWhenWindowEpochAdvancesWhileStillAt100()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        Eval(tier, epochs, RC.W("claude.fiveHour", WindowClass.Session, 100, epoch: "2026-09-17T10:00:00Z"));
        Assert.Equal(1, tier["claude.fiveHour"]);
        var grants = Eval(tier, epochs, RC.W("claude.fiveHour", WindowClass.Session, 100, epoch: "2026-09-17T15:00:00Z"));
        Assert.Equal([1], grants.Select(g => g.Count));
        Assert.Equal("2026-09-17T15:00:00Z", epochs["claude.fiveHour"]);
    }

    /// <summary>Claude API microsecond variations across polls must not regrant.</summary>
    [Fact]
    public void VolatileMicrosecondEpochDoesNotRegrant()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        var initial = Eval(tier, epochs,
            RC.W("claude.152146ba.fiveHour", WindowClass.Session, 100, epoch: "2026-09-28T17:10:00.928794+00:00"));
        Assert.Equal([1], initial.Select(g => g.Count));
        var nextPoll = Eval(tier, epochs,
            RC.W("claude.152146ba.fiveHour", WindowClass.Session, 100, epoch: "2026-09-28T17:10:00.935515+00:00"));
        Assert.Empty(nextPoll);
        Assert.Equal(1, tier["claude.152146ba.fiveHour"]);
    }

    /// <summary>Codex server-side ±1s jitter must not regrant.</summary>
    [Fact]
    public void CodexJitterEpochDoesNotRegrant()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        var initial = Eval(tier, epochs, RC.W("codex.codex.primary", WindowClass.Session, 100, epoch: "1790613843"));
        Assert.Equal([1], initial.Select(g => g.Count));
        Assert.Empty(Eval(tier, epochs, RC.W("codex.codex.primary", WindowClass.Session, 100, epoch: "1790613844")));
        Assert.Empty(Eval(tier, epochs, RC.W("codex.codex.primary", WindowClass.Session, 100, epoch: "1790613843")));
        Assert.Equal(1, tier["codex.codex.primary"]);
    }

    /// <summary>Rolling window minor drift (1–5 minutes) must not regrant.</summary>
    [Fact]
    public void RollingWindowMinorDriftDoesNotRegrant()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        Eval(tier, epochs, RC.W("antigravity.3p.5h", WindowClass.Session, 100, epoch: "2026-09-28T19:10:12Z"));
        Assert.Equal(1, tier["antigravity.3p.5h"]);
        Assert.Empty(Eval(tier, epochs, RC.W("antigravity.3p.5h", WindowClass.Session, 100, epoch: "2026-09-28T19:11:12Z")));
        Assert.Empty(Eval(tier, epochs, RC.W("antigravity.3p.5h", WindowClass.Session, 100, epoch: "2026-09-28T19:15:12Z")));
    }

    /// <summary>A full session advancement (5h) at a steady 100% regrants.</summary>
    [Fact]
    public void SessionFullResetWhileStillAt100Regrants()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        Eval(tier, epochs, RC.W("codex.codex.primary", WindowClass.Session, 100, epoch: "1790613843"));
        var nextSession = Eval(tier, epochs, RC.W("codex.codex.primary", WindowClass.Session, 100, epoch: "1790631843"));
        Assert.Equal([1], nextSession.Select(g => g.Count));
        Assert.Equal("1790631843", epochs["codex.codex.primary"]);
    }

    /// <summary>A full weekly advancement (7d) at a steady 100% regrants the weekly grant.</summary>
    [Fact]
    public void WeeklyFullResetWhileStillAt100Regrants()
    {
        var tier = new Dictionary<string, int>();
        var epochs = new Dictionary<string, string>();
        Eval(tier, epochs, RC.W("claude.sevenDay", WindowClass.Weekly, 100, epoch: "2026-09-20T00:00:00Z"));
        var nextWeek = Eval(tier, epochs, RC.W("claude.sevenDay", WindowClass.Weekly, 100, epoch: "2026-09-27T00:00:00Z"));
        Assert.Equal([RareCandy.WeeklyGrant], nextWeek.Select(g => g.Count));
        Assert.Equal("2026-09-27T00:00:00Z", epochs["claude.sevenDay"]);
    }

    /// <summary>Learning an epoch for the first time does not regrant (old save → new field).</summary>
    [Fact]
    public void LearningEpochForFirstTimeDoesNotRegrant()
    {
        var tier = new Dictionary<string, int> { ["claude.fiveHour"] = 1 };
        var epochs = new Dictionary<string, string>();
        var grants = Eval(tier, epochs, RC.W("claude.fiveHour", WindowClass.Session, 100, epoch: "E1"));
        Assert.Empty(grants);
        Assert.Equal("E1", epochs["claude.fiveHour"]);
        Assert.Equal(1, tier["claude.fiveHour"]);
    }

    /// <summary>Session + weekly + below-threshold: 1 + 5, the low window ignored.</summary>
    [Fact]
    public void MixedWindows()
    {
        var grants = Eval([], [],
            RC.W("claude.fiveHour", WindowClass.Session, 100),
            RC.W("claude.sevenDay", WindowClass.Weekly, 100),
            RC.W("codex.codex.primary", WindowClass.Session, 50));
        Assert.Equal(1 + RareCandy.WeeklyGrant, grants.Sum(g => g.Count));
    }

    /// <summary>A grant carries the window name (why it was granted).</summary>
    [Fact]
    public void GrantCarriesWindowName()
    {
        var grants = Eval([], [], RC.W("claude.fiveHour", WindowClass.Session, 100, name: "Claude 5시간 세션"));
        Assert.Equal("Claude 5시간 세션", grants.First().WindowName);
    }
}

public class InventoryDecodeTests
{
    /// <summary>Old saves (no inventory keys) load with defaults.</summary>
    [Fact]
    public void DecodesWithoutInventoryFields()
    {
        const string json = """{"installBaselineSet":true,"usedSinceInstall":5,"lastDate":"d","dex":[],"language":"ko"}""";
        var s = CompanionJson.Deserialize(json);
        Assert.Empty(s.Inventory);
        Assert.Empty(s.CandyGrantTier);
        Assert.False(s.CandyFeatureSeeded);
    }

    [Fact]
    public void InventoryRoundTrip()
    {
        var st = new CompanionState
        {
            Inventory = new() { ["rareCandy"] = 3 },
            CandyGrantTier = new() { ["claude.fiveHour"] = 1 },
            CandyFeatureSeeded = true,
        };
        var round = CompanionJson.Deserialize(CompanionJson.Serialize(st));
        Assert.Equal(new Dictionary<string, int> { ["rareCandy"] = 3 }, round.Inventory);
        Assert.Equal(new Dictionary<string, int> { ["claude.fiveHour"] = 1 }, round.CandyGrantTier);
        Assert.True(round.CandyFeatureSeeded);
    }
}

public class RareCandyStoreTests
{
    private static CompanionStore Store(EvoLine line, ulong seed = 7) => StateFiles.Store(line, seed);

    /// <summary>[#326] After seeding, a new epoch at a steady 100% regrants.</summary>
    [Fact]
    public void GrantAfterSeedWhenEpochAdvancesWithoutUtilDip() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100, epoch: "E1")], limitsReady: true); // seed
        Assert.Equal(0, s.RareCandyCount);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100, epoch: "E1")], limitsReady: true);
        Assert.Equal(0, s.RareCandyCount);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100, epoch: "E2")], limitsReady: true);
        Assert.Equal(1, s.RareCandyCount);
        Assert.Equal("E2", s.State.CandyWindowEpoch["claude.fiveHour"]);
    });

    // Grants

    /// <summary>First run: windows already at 100% are only seeded, not granted retroactively.</summary>
    [Fact]
    public void FirstRunSeedsWithoutGranting() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        Assert.Equal(0, s.RareCandyCount);
        Assert.True(s.State.CandyFeatureSeeded);
        Assert.Equal(1, s.State.CandyGrantTier["claude.fiveHour"]);
    });

    [Fact]
    public void NoGrantForAlreadySeededWindow() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        Assert.Equal(0, s.RareCandyCount);
    });

    /// <summary>Limits not loaded: not even seeding (retry next refresh).</summary>
    [Fact]
    public void NoSeedWhenLimitsNotReady() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: false);
        Assert.False(s.State.CandyFeatureSeeded);
        Assert.Equal(0, s.RareCandyCount);
    });

    [Fact]
    public void SessionGrantAfterSeed() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        s.GrantCandies([], limitsReady: true);
        s.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        Assert.Equal(1, s.RareCandyCount);
    });

    [Fact]
    public void WeeklyGrantsFiveCandies() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        s.GrantCandies([], limitsReady: true);
        s.GrantCandies([RC.W("claude.sevenDay", WindowClass.Weekly, 100)], limitsReady: true);
        Assert.Equal(RareCandy.WeeklyGrant, s.RareCandyCount);
    });

    /// <summary>[Key regression] The grant tier persists: after a restart the same 100% window is not regranted.</summary>
    [Fact]
    public void GrantTierPersistsAcrossRestartNoDoubleGrant() => MainActor.Run(() =>
    {
        var path = StateFiles.TempPath("rc-persist");
        var s1 = StateFiles.Store(Lines.Linear3, 1, path);
        s1.GrantCandies([], limitsReady: true);
        s1.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        Assert.Equal(1, s1.RareCandyCount);

        var s2 = StateFiles.Store(Lines.Linear3, 1, path);
        Assert.Equal(1, s2.RareCandyCount);
        Assert.Equal(1, s2.State.CandyGrantTier["claude.fiveHour"]);
        s2.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        Assert.Equal(1, s2.RareCandyCount);
    });

    /// <summary>[Regression] Re-arming without a grant must also be saved, or a restart loses the next grant.</summary>
    [Fact]
    public void RearmPersistsAcrossRestart() => MainActor.Run(() =>
    {
        var path = StateFiles.TempPath("rc-rearm");
        var s1 = StateFiles.Store(Lines.Linear3, 1, path);
        s1.GrantCandies([], limitsReady: true);
        s1.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        Assert.Equal(1, s1.RareCandyCount);
        s1.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 40)], limitsReady: true);
        Assert.False(s1.State.CandyGrantTier.ContainsKey("claude.fiveHour"));

        var s2 = StateFiles.Store(Lines.Linear3, 1, path);
        Assert.False(s2.State.CandyGrantTier.ContainsKey("claude.fiveHour"));
        s2.GrantCandies([RC.W("claude.fiveHour", WindowClass.Session, 100)], limitsReady: true);
        Assert.Equal(2, s2.RareCandyCount);
    });

    // Use

    [Fact]
    public void BatchCarriesXPThroughMultipleEvolutions() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        s.ApplyUsage(100_000_000);
        RC.GiveCandies(s, 10);
        var before = s.State.UsedSinceInstall;
        var preview = s.PlanRareCandyUse(3);
        Assert.NotNull(preview);
        Assert.Equal(7, s.MaxRareCandyUseCount);
        Assert.Equal(3, preview.Count);
        Assert.True(preview.Evolves);
        Assert.False(preview.Graduates);
        Assert.Equal(25_000_000, preview.CarryoverXP);
        Assert.Equal(0, preview.DiscardedXP);
        Assert.Equal(CompanionStore.CandyUseResult.Evolved, s.UseRareCandy(3));
        Assert.Equal(2, s.State.Active?.StageIndex);
        Assert.Equal(preview.CarryoverXP, s.State.Active?.UsedAtStage);
        Assert.Equal(7, s.RareCandyCount);
        Assert.Equal(300_000_000, s.CandyFeedbackAmount);
        Assert.Equal(before, s.State.UsedSinceInstall);
    });

    [Fact]
    public void BatchCapsAtGraduationAndPreservesUnusedCandies() => MainActor.Run(async () =>
    {
        foreach (var remaining in new long[] { 250_000_000, 300_000_000 })
        {
            var s = Store(Lines.NoEvo);
            await s.Hatch(20);
            s.ApplyUsage(750_000_000 - remaining);
            RC.GiveCandies(s, 5);
            Assert.Equal(3, s.MaxRareCandyUseCount);
            var preview = s.PlanRareCandyUse(5);
            Assert.NotNull(preview);
            Assert.Equal(3, preview.Count);
            Assert.True(preview.Graduates);
            Assert.Equal(300_000_000 - remaining, preview.DiscardedXP);
            Assert.Equal(CompanionStore.CandyUseResult.Graduated, s.UseRareCandy(5));
            Assert.Equal(2, s.RareCandyCount);
            Assert.Equal(0, s.State.EggUsage);
            Assert.Single(s.DexEntries);
            Assert.Equal(0, s.MaxRareCandyUseCount);
            Assert.Equal(CompanionStore.CandyUseResult.Unavailable, s.UseRareCandy(5));
        }
    });

    [Fact]
    public void BatchCapsAtInventoryAndRejectsNonpositiveAmounts() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        RC.GiveCandies(s, 1);
        foreach (var count in new[] { 0, -1, int.MinValue })
        {
            Assert.Null(s.PlanRareCandyUse(count));
            Assert.Equal(CompanionStore.CandyUseResult.Unavailable, s.UseRareCandy(count));
        }
        Assert.Equal(1, s.RareCandyCount);
        Assert.Equal(1, s.MaxRareCandyUseCount);
        var preview = s.PlanRareCandyUse(int.MaxValue);
        Assert.NotNull(preview);
        Assert.Equal(1, preview.Count);
        Assert.False(preview.Evolves);
        Assert.Equal(CompanionStore.CandyUseResult.Progressed, s.UseRareCandy(int.MaxValue));
        Assert.Equal(RareCandy.Xp, s.State.Active?.UsedAtStage);
        Assert.Equal(0, s.RareCandyCount);
    });

    [Fact]
    public void BatchPreviewMatchesGrowthWithDifficultyAndRepeatBonus() => MainActor.Run(async () =>
    {
        foreach (var difficulty in new[] { 0.1, 0.5, 1.0, 2.0 })
        {
            foreach (var boosted in new[] { false, true })
            {
                var path = StateFiles.TempPath("rc-batch");
                var settings = new AppSettings(null);
                settings.Set("growthDifficulty", difficulty);
                var s = new CompanionStore(new StubProvider(Lines.Linear3), clock: () => Lines.FixedNow,
                    filePath: path, rng: new SeededRng(7), dittoDisguiseRollingEnabled: false, settings: settings);
                await s.Hatch(1);
                if (boosted)
                {
                    s.ApplyUsage(10_000_000_000);
                    await s.Hatch(1);
                }
                s.ApplyUsage(s.Threshold - 1);
                RC.GiveCandies(s, 20);
                var preview = s.PlanRareCandyUse(1);
                Assert.NotNull(preview);
                var result = s.UseRareCandy();
                if (preview.Graduates)
                {
                    Assert.Equal(CompanionStore.CandyUseResult.Graduated, result);
                    Assert.Null(s.State.Active);
                }
                else
                {
                    Assert.Equal(CompanionStore.CandyUseResult.Evolved, result);
                    Assert.Equal(preview.CarryoverXP, s.State.Active?.UsedAtStage);
                    var finalPreview = s.PlanRareCandyUse(20);
                    Assert.NotNull(finalPreview);
                    Assert.True(finalPreview.Graduates);
                    var stock = s.RareCandyCount;
                    Assert.Equal(CompanionStore.CandyUseResult.Graduated, s.UseRareCandy(20));
                    Assert.Equal(stock - finalPreview.Count, s.RareCandyCount);
                }
                var reloaded = new CompanionStore(new StubProvider(Lines.Linear3), filePath: path, settings: settings,
                    dittoDisguiseRollingEnabled: false);
                Assert.Equal(s.RareCandyCount, reloaded.RareCandyCount);
                Assert.Null(reloaded.State.Active);
                Assert.Equal(s.State.Dex.Count, reloaded.State.Dex.Count);
            }
        }
    });

    /// <summary>Candy XP (100M) &lt; the lowest threshold (125M): partial progress, statistics unchanged.</summary>
    [Fact]
    public void UseProgressesWithoutEvolution() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        RC.GiveCandies(s, 1);
        Assert.Equal(1, s.RareCandyCount);
        var before = s.State.UsedSinceInstall;
        Assert.Equal(CompanionStore.CandyUseResult.Progressed, s.UseRareCandy());
        Assert.Equal(RareCandy.Xp, s.State.Active?.UsedAtStage);
        Assert.Equal(0, s.State.Active?.StageIndex);
        Assert.Equal(0, s.RareCandyCount);
        Assert.Equal(before, s.State.UsedSinceInstall);
    });

    /// <summary>Remaining ≤ candy XP at this stage → exactly one evolution.</summary>
    [Fact]
    public void UseEvolvesWhenCrossingThreshold() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        s.ApplyUsage(50_000_000);
        RC.GiveCandies(s, 1);
        Assert.Equal(CompanionStore.CandyUseResult.Evolved, s.UseRareCandy());
        Assert.Equal(2, s.CurrentSpeciesID);
        Assert.Equal(1, s.State.Active?.StageIndex);
    });

    /// <summary>[Invariant] One candy = at most one stage, even right before a threshold.</summary>
    [Fact]
    public void SingleCandyAdvancesAtMostOneStage() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        s.ApplyUsage(124_000_000);
        RC.GiveCandies(s, 1);
        s.UseRareCandy();
        Assert.Equal(1, s.State.Active?.StageIndex);
    });

    [Fact]
    public void SingleCandyAdvancesAtMostOneBoostedStage() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        for (var stage = 0; stage < 3; stage++)
            s.ApplyUsage(PokemonBalance.PhaseThreshold(Rarity.Common, 3, stage));
        await s.Hatch(1);
        Assert.True(s.State.Active?.HasGrowthBoost == true);

        var firstThreshold = s.Threshold;
        s.ApplyUsage(firstThreshold - 1);
        RC.GiveCandies(s, 1);

        Assert.Equal(CompanionStore.CandyUseResult.Evolved, s.UseRareCandy());
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(RareCandy.Xp - 1, s.State.Active?.UsedAtStage);
        Assert.True((s.State.Active?.UsedAtStage ?? long.MaxValue) < s.Threshold);
    });

    /// <summary>Remaining ≤ candy XP at the final stage graduates → Pokédex + new egg.</summary>
    [Fact]
    public void UseGraduatesFinalStage() => MainActor.Run(async () =>
    {
        var s = Store(Lines.NoEvo);
        await s.Hatch(20);
        s.ApplyUsage(700_000_000);
        RC.GiveCandies(s, 1);
        Assert.Equal(CompanionStore.CandyUseResult.Graduated, s.UseRareCandy());
        Assert.Null(s.State.Active);
        Assert.Single(s.DexEntries);
    });

    /// <summary>
    /// [Regression] Candy graduation must notify observers without a usage poll tick, so the menu bar sprite
    /// updates at once. Swift observed currentSpeciesID/currentIsShiny; here the store's Changed event.
    /// </summary>
    [Fact]
    public void CandyGraduationFiresSpriteIdentityObservation() => MainActor.Run(async () =>
    {
        var s = Store(Lines.NoEvo);
        await s.Hatch(20);
        s.ApplyUsage(700_000_000);
        RC.GiveCandies(s, 1);
        var fired = false;
        s.Changed += () => fired = true;
        Assert.Equal(CompanionStore.CandyUseResult.Graduated, s.UseRareCandy());
        Assert.True(fired);
        Assert.Null(s.CurrentSpeciesID);
    });

    /// <summary>An egg cannot use candy; stock is not consumed.</summary>
    [Fact]
    public void CannotUseOnEgg() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        RC.GiveCandies(s, 2);
        Assert.True(s.IsEgg);
        Assert.False(s.CanUseRareCandy);
        Assert.Equal(CompanionStore.CandyUseResult.Unavailable, s.UseRareCandy());
        Assert.Equal(2, s.RareCandyCount);
    });

    [Fact]
    public void CannotUseWithoutStock() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        Assert.Equal(0, s.RareCandyCount);
        Assert.False(s.CanUseRareCandy);
        Assert.Equal(CompanionStore.CandyUseResult.Unavailable, s.UseRareCandy());
    });

    /// <summary>[Regression guard] Line not loaded (restart/offline): no use and no consumption.</summary>
    [Fact]
    public void CannotUseWhileLineUnloaded() => MainActor.Run(() =>
    {
        var path = StateFiles.TempPath("rc-unloaded");
        const string json = """{"installBaselineSet":true,"lastDate":"d1","active":{"baseID":1,"pathIDs":[1],"stageIndex":0,"usedAtStage":0,"rarity":"common","totalForms":3},"inventory":{"rareCandy":1},"candyFeatureSeeded":true,"dex":[],"collectedFinals":[]}""";
        StateFiles.WriteText(json, path);
        var s = new CompanionStore(new RCLineThrows(), clock: () => Lines.FixedNow, filePath: path, rng: new SeededRng(1),
            dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));
        Assert.NotNull(s.State.Active);
        Assert.Null(s.CurrentLine);
        Assert.Equal(1, s.RareCandyCount);
        Assert.False(s.CanUseRareCandy);
        Assert.Equal(CompanionStore.CandyUseResult.Unavailable, s.UseRareCandy());
        Assert.Equal(1, s.RareCandyCount);
    });

    /// <summary>Use bumps the "+XP" feedback seq (even without evolving).</summary>
    [Fact]
    public void UseBumpsCandyFeedback() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        RC.GiveCandies(s, 1);
        var before = s.CandyFeedbackSeq;
        s.UseRareCandy();
        Assert.Equal(before + 1, s.CandyFeedbackSeq);
        Assert.Equal(RareCandy.Xp, s.CandyFeedbackAmount);
    });

    [Fact]
    public void OwnedItemsReflectsStock() => MainActor.Run(() =>
    {
        var s = Store(Lines.Linear3);
        Assert.Empty(s.OwnedItems);
        RC.GiveCandies(s, 3);
        Assert.Equal([ItemKind.RareCandy], s.OwnedItems.Select(i => i.Kind));
        Assert.Equal(3, s.OwnedItems.First().Count);
    });

    /// <summary>Demo scenario (3 forms, usedAtStage 100M, 3 candies): evolve → partial → evolve.</summary>
    [Fact]
    public void SequentialCandyUseMatchesDemo() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        s.ApplyUsage(100_000_000);
        RC.GiveCandies(s, 3);
        Assert.Equal(CompanionStore.CandyUseResult.Evolved, s.UseRareCandy());
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(CompanionStore.CandyUseResult.Progressed, s.UseRareCandy());
        Assert.Equal(1, s.State.Active?.StageIndex);
        Assert.Equal(CompanionStore.CandyUseResult.Evolved, s.UseRareCandy());
        Assert.Equal(2, s.State.Active?.StageIndex);
        Assert.Equal(0, s.RareCandyCount);
    });

    /// <summary>"+XP" is one-shot: amount &gt; 0 after use, 0 after consume.</summary>
    [Fact]
    public void ConsumeCandyFeedbackResets() => MainActor.Run(async () =>
    {
        var s = Store(Lines.Linear3);
        await s.Hatch(1);
        RC.GiveCandies(s, 1);
        s.UseRareCandy();
        Assert.Equal(RareCandy.Xp, s.CandyFeedbackAmount);
        s.ConsumeCandyFeedback();
        Assert.Equal(0, s.CandyFeedbackAmount);
    });
}

public class CandyNotificationCopyTests
{
    /// <summary>Every language substitutes the count and is non-empty. (The Korean wording tests are not ported:
    /// L is a temporary English-only stub in this sandbox.)</summary>
    [Fact]
    public void TitleLocalizedAllLanguages()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);
            var title = l.NotifCandyTitle(l.ItemName(ItemKind.RareCandy.RawValue()), 3);
            Assert.Contains("3", title);
            Assert.NotEmpty(title);
        }
    }
}
