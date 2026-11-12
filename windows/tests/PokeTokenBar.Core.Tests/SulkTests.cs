using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Port of SulkStateTests (SulkTests.swift, #466): "neglected? it turns its back" — the sulk state after three hours
/// without token usage, and the back-facing switch. The hover text and the back-sprite URL / cache tests are in the
/// App tests (they test App code).
/// </summary>
public class SulkTests
{
    private static readonly DateTimeOffset SulkNow = Lines.FixedNow;
    private static readonly TimeSpan Threshold = PokemonBalance.SulkAfterIdle;
    private DateTimeOffset _now = SulkNow;

    private static TimeSpan Hours(double h) => TimeSpan.FromHours(h);

    private CompanionStore NewStore() =>
        new(new StubProvider(Lines.NoEvo), clock: () => _now, filePath: StateFiles.TempPath(), rng: new SeededRng(5),
            dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));

    private async Task<CompanionStore> MakeStore(bool expireHatchWindow = true)
    {
        var store = NewStore();
        await store.Hatch(20);
        if (expireHatchWindow) _now = SulkNow.AddSeconds(60);
        return store;
    }

    private static void Update(CompanionStore store, TimeSpan? idle, long today = 0, bool hasData = true,
        bool limitWarning = false, string date = "d") =>
        store.Update(new Dictionary<string, long> { ["test"] = today }, date, 0, BurnTier.Idle, limitWarning, hasData, idle);

    [Fact]
    public void ThresholdBoundary() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        Update(store, Threshold - TimeSpan.FromSeconds(1));
        Assert.Equal(CompanionStateKind.Sleep, store.DisplayState);   // just under the threshold does not sulk
        Assert.False(store.FacesBack);
        Assert.Equal(TimeSpan.Zero, store.SulkIdle);   // nor does the hover text say so
        Update(store, Threshold);
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);    // exactly the threshold sulks
        Assert.True(store.FacesBack);
        Assert.Equal(Threshold, store.SulkIdle);
    });

    [Fact]
    public void ThresholdIsThreeHours() => Assert.Equal(TimeSpan.FromHours(3), Threshold);

    [Fact]
    public void FacesBackTruthTable() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        foreach (var (always, idle, expected) in new[] { (false, TimeSpan.Zero, false), (false, Threshold, true),
                     (true, TimeSpan.Zero, true), (true, Threshold, true) })
        {
            store.AlwaysFacesBack = always;
            Update(store, idle);
            Assert.Equal(expected, store.FacesBack);
            // The preference never changes the state kind, nor the sulk text input.
            Assert.Equal(idle >= Threshold ? CompanionStateKind.Sulk : CompanionStateKind.Sleep, store.DisplayState);
            Assert.Equal(idle >= Threshold ? idle : TimeSpan.Zero, store.SulkIdle);
        }
    });

    [Fact]
    public void AlwaysFacesBackDoesNotAffectAnEgg()
    {
        var store = NewStore();
        store.AlwaysFacesBack = true;
        Assert.False(store.FacesBack);
    }

    [Fact]
    public void UnknownLastUsageNeverSulks() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        Update(store, null);
        Assert.Equal(CompanionStateKind.Sleep, store.DisplayState);
        Assert.False(store.FacesBack);
        Assert.Equal(TimeSpan.Zero, store.SulkIdle);
    });

    [Fact]
    public void NoUsageDataDoesNotSulk() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        // The first update only fixes the install baseline, so run one with data before losing it.
        Update(store, TimeSpan.Zero);
        Update(store, 3 * Threshold, hasData: false);
        Assert.Equal(CompanionStateKind.Sleep, store.DisplayState);
        Assert.False(store.FacesBack);
    });

    [Fact]
    public void TiredBeatsSulk() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        Update(store, 3 * Threshold, limitWarning: true);
        Assert.Equal(CompanionStateKind.Tired, store.DisplayState);
        Assert.Equal(TimeSpan.Zero, store.SulkIdle);
    });

    [Fact]
    public void SulkBeatsSleepAndResumingFlipsBack() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        Update(store, 2 * Threshold);
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);
        // Usage resumes: today's total rises before the scan's last-usage time catches up.
        Update(store, 2 * Threshold, today: 500);
        Assert.NotEqual(CompanionStateKind.Sulk, store.DisplayState);
        Assert.False(store.FacesBack);
        Assert.Equal(TimeSpan.Zero, store.SulkIdle);
        // And once the scan catches up, the idle time is small again.
        Update(store, TimeSpan.FromSeconds(60));
        Assert.Equal(CompanionStateKind.Sleep, store.DisplayState);
    });

    [Fact]
    public void SameDayIdleSulksEvenThoughTodayHasUsage() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        // Worked 09:00-12:00, away since: today's total is positive but the last usage is 4 h old.
        Update(store, Hours(4), today: 40_000);
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);
        Assert.True(store.FacesBack);
        Assert.Equal(Hours(4), store.SulkIdle);
        Update(store, Hours(3) - TimeSpan.FromSeconds(1), today: 40_000);
        Assert.NotEqual(CompanionStateKind.Sulk, store.DisplayState);   // just under three hours
    });

    [Fact]
    public void RisingTodayTotalUnSulksBeforeTheScanCatchesUp() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        Update(store, Hours(4), today: 40_000);
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);
        Update(store, Hours(4), today: 40_500);   // the scan still reports the old last usage
        Assert.NotEqual(CompanionStateKind.Sulk, store.DisplayState);
        Assert.False(store.FacesBack);
        Assert.Equal(TimeSpan.Zero, store.SulkIdle);
        // Nothing further happens for three hours: the observed usage ages like any other.
        _now = _now.AddHours(3);
        Update(store, Hours(7), today: 40_500);
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);
    });

    [Fact]
    public void FirstSightingAfterLaunchIsNotUsage() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        Update(store, null, today: 0, hasData: false);   // launch: no data in yet
        Update(store, Hours(4), today: 40_000);          // the first total with data
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);   // a baseline, not a rise
    });

    [Fact]
    public void MidnightRolloverNeitherUnSulksNorSulksByItself() => MainActor.Run(async () =>
    {
        var store = await MakeStore();
        Update(store, Hours(4), today: 40_000, date: "2026-09-01");
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);
        // A new day starts from a different (even larger) total: a new baseline, not a rise.
        Update(store, Hours(5), today: 90_000, date: "2026-09-02");
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);
        Update(store, Hours(5), today: 0, date: "2026-09-03");
        Assert.Equal(CompanionStateKind.Sulk, store.DisplayState);
        // Only a rise within the same day counts.
        Update(store, Hours(5), today: 300, date: "2026-09-03");
        Assert.NotEqual(CompanionStateKind.Sulk, store.DisplayState);
        // And an active companion is not pushed into sulking by the rollover.
        Update(store, TimeSpan.FromSeconds(60), today: 0, date: "2026-09-04");
        Assert.NotEqual(CompanionStateKind.Sulk, store.DisplayState);
    });

    [Fact]
    public void LevelUpBeatsSulk() => MainActor.Run(async () =>
    {
        var store = await MakeStore(expireHatchWindow: false);   // the hatch event window is still open
        Update(store, 3 * Threshold);
        Assert.Equal(CompanionStateKind.LevelUp, store.DisplayState);
    });

    [Fact]
    public void EggNeverSulks()
    {
        var store = NewStore();
        Update(store, 3 * Threshold);
        Assert.Equal(CompanionStateKind.Egg, store.DisplayState);
    }

    [Fact]
    public void StatusLineCopy()
    {
        Assert.Equal("삐져서 등을 돌리고 있어요.", new L(AppLanguage.Ko).StatusSulk);
        Assert.Equal("Sulking with its back turned.", new L(AppLanguage.En).StatusSulk);
        Assert.Equal("30시간째 삐져 있어요", new L(AppLanguage.Ko).FloatingPetHoverSulkHours(30));
        Assert.Equal("3일째 삐져 있어요", new L(AppLanguage.Ko).FloatingPetHoverSulkDays(3));
    }
}
