using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Companion display state (DisplayState) transitions for combinations of Update() inputs.</summary>
public class CompanionDisplayStateTests
{
    private static readonly DateTimeOffset DNow = Lines.FixedNow;

    private static EvoNode N(int id, params EvoNode[] children) => new(id, children);

    private static EvoLine DLine(int @base, Rarity rarity = Rarity.Common) =>
        new(@base, N(@base, N(@base + 1, N(@base + 2))), rarity, new Dictionary<int, Dictionary<string, string>>());

    /// <summary>Mutable clock to move the event window forward.</summary>
    private sealed class ClockBox(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }

    private static Dictionary<string, long> Today(long tokens) => new() { ["test"] = tokens };

    private static async Task<(CompanionStore, ClockBox)> HatchedStore(Rarity rarity = Rarity.Common)
    {
        var clock = new ClockBox(DNow);
        var s = new CompanionStore(new StubProvider(DLine(1, rarity)), clock: () => clock.Now,
            filePath: StateFiles.TempPath("poke-disp"), rng: new SeededRng(5), dittoDisguiseRollingEnabled: false,
            settings: new AppSettings(null));
        await s.Hatch(1);
        return (s, clock);
    }

    private static CompanionStore EggStore() =>
        new(new StubProvider(DLine(1)), clock: () => DNow, filePath: StateFiles.TempPath("poke-disp"),
            rng: new SeededRng(1), dittoDisguiseRollingEnabled: false, settings: new AppSettings(null));

    [Fact]
    public void TestEggWhenNoUsageData() => MainActor.Run(() =>
    {
        var s = EggStore();
        s.Update(Today(0), "d", 0, BurnTier.Idle, false, false);
        Assert.Equal(CompanionStateKind.Egg, s.DisplayState);
    });

    [Fact]
    public void TestLevelUpDuringEventWindow() => MainActor.Run(async () =>
    {
        var (s, _) = await HatchedStore();
        // While the event window is alive (clock not advanced) → stays levelUp.
        s.Update(Today(100), "d", 0, BurnTier.Idle, false, true);
        Assert.Equal(CompanionStateKind.LevelUp, s.DisplayState);
    });

    [Fact]
    public void TestWorkingAfterEventExpires() => MainActor.Run(async () =>
    {
        var (s, clock) = await HatchedStore();
        clock.Now = DNow.AddSeconds(60); // event expired
        s.Update(Today(100), "d", 0, BurnTier.Normal, false, true);
        Assert.Equal(CompanionStateKind.Working, s.DisplayState);
    });

    [Fact]
    public void TestFocusOnHighBurn() => MainActor.Run(async () =>
    {
        var (s, clock) = await HatchedStore();
        clock.Now = DNow.AddSeconds(60);
        s.Update(Today(100), "d", 0, BurnTier.Blazing, false, true);
        Assert.Equal(CompanionStateKind.Focus, s.DisplayState);
    });

    [Fact]
    public void TestTiredWhenLimitWarning() => MainActor.Run(async () =>
    {
        var (s, clock) = await HatchedStore();
        clock.Now = DNow.AddSeconds(60);
        s.Update(Today(100), "d", 0, BurnTier.Normal, true, true);
        Assert.Equal(CompanionStateKind.Tired, s.DisplayState);
    });

    [Fact]
    public void TestSleepWhenZeroUsageToday() => MainActor.Run(async () =>
    {
        var (s, clock) = await HatchedStore();
        clock.Now = DNow.AddSeconds(60);
        s.Update(Today(0), "d", 0, BurnTier.Idle, false, true);
        Assert.Equal(CompanionStateKind.Sleep, s.DisplayState);
    });

    /// <summary>
    /// Regression (#4): the evolution message (JustEvolvedTo) must survive the whole .levelUp window the evolution
    /// opened. It used to be cleared at the start of every update(), so a tick inside the window reverted the text.
    /// </summary>
    [Fact]
    public void TestEvolveStatusSurvivesUpdateWithinEventWindow() => MainActor.Run(async () =>
    {
        var (s, clock) = await HatchedStore();
        clock.Now = DNow.AddSeconds(60); // hatch window expired — only the evolution event remains afterwards
        // Baseline (the first update only sets the baseline, no delta).
        s.Update(Today(100), "d", 0, BurnTier.Normal, false, true);
        // Stage-0 threshold → exactly one evolution (1→2). Sets JustEvolvedTo and renews the event window.
        s.ApplyUsage(PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0));
        Assert.Equal(1, s.State.Active?.StageIndex);
        var evolvedName = s.JustEvolvedTo;
        Assert.NotNull(evolvedName);
        // Another update (delta 0) inside the window keeps the evolution message and levelUp.
        s.Update(Today(100), "d", 0, BurnTier.Normal, false, true);
        Assert.Equal(evolvedName, s.JustEvolvedTo);
        Assert.Equal(CompanionStateKind.LevelUp, s.DisplayState);
        // After the window: message cleared, normal state back.
        clock.Now = DNow.AddSeconds(70);
        s.Update(Today(100), "d", 0, BurnTier.Normal, false, true);
        Assert.Null(s.JustEvolvedTo);
        Assert.Equal(CompanionStateKind.Working, s.DisplayState);
    });

    // Egg incubation derived values

    [Fact]
    public void TestEggProgressAndTokensToHatch() => MainActor.Run(() =>
    {
        var s = EggStore();
        Assert.True(s.IsEgg);
        Assert.Equal(0, s.EggProgress);
        Assert.Equal(PokemonBalance.EggHatchThreshold, s.EggTokensToHatch);

        // 40% of the threshold used.
        s.Update(Today(0), "d", 0, BurnTier.Idle, false, true);
        var part = PokemonBalance.EggHatchThreshold * 2 / 5;
        s.Update(Today(part), "d", 0, BurnTier.Idle, false, true);
        Assert.Equal(0.4, s.EggProgress, 3);
        Assert.Equal(PokemonBalance.EggHatchThreshold - part, s.EggTokensToHatch);
        Assert.True(s.EggStarted);
    });
}
