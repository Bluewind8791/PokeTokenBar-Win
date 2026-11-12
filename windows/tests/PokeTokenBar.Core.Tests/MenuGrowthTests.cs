using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Port of MenuGrowthTests.swift (#388): the companion's growth in the menu-bar lines, which are the tray tooltip on
/// Windows. Not ported: <c>testNativeRendererPreservesGrowthAndLimitColorsAcrossLineChanges</c> (NSStatusBarButton
/// title colors; a tooltip has no colors). The observation case becomes "the store raises Changed": the app rebuilds
/// the tooltip on every Changed of either store.
/// </summary>
public sealed class MenuGrowthTests
{
    private static async Task Fixture(Func<UsageStore, CompanionStore, Task> body)
    {
        var settings = new AppSettings(null);
        var state = new CompanionState { Language = AppLanguage.En };
        state.Inventory[ItemKind.RareCandy.RawValue()] = 1;
        var path = StateFiles.Write(state);
        try
        {
            var companion = new CompanionStore(new StubProvider(Lines.Linear3), filePath: path, rng: new SeededRng(7),
                dittoDisguiseRollingEnabled: false, settings: settings);
            var usage = Stores.Make(settings);
            usage.ShowTokensInMenu = false;
            usage.ShowCostInMenu = false;
            usage.ShowLimitInMenu = false;
            usage.ShowGrowthInMenu = true;
            await body(usage, companion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Feed(CompanionStore companion, long credits) =>
        companion.Update(new Dictionary<string, long> { ["test"] = credits }, "d", 0, BurnTier.Idle, false, true);

    [Fact]
    public void EggEvolutionAndGraduationUseCurrentStageProgress() => MainActor.Run(() => Fixture(async (usage, companion) =>
    {
        Assert.Equal(["Hatch 0%"], MenuBarContent.Lines(usage, companion));
        Feed(companion, 0);
        Feed(companion, companion.EggHatchThreshold * 2 / 5);
        Assert.Equal("Hatch 40%", companion.MenuGrowthText);
        await companion.Hatch(1);
        Assert.Equal("Growth 0%", companion.MenuGrowthText);
        companion.ApplyUsage(companion.Threshold / 2);
        Assert.Equal("Growth 50%", companion.MenuGrowthText);
        companion.ApplyUsage(companion.TokensToNext);
        Assert.Equal(1, companion.State.Active?.StageIndex);
        Assert.Equal("Growth 0%", companion.MenuGrowthText);
        companion.ApplyUsage(companion.TokensToNext);
        Assert.True(companion.IsFinalStage);
        companion.ApplyUsage(companion.Threshold / 2);
        Assert.Equal("Growth 50%", companion.MenuGrowthText);
        companion.ApplyUsage(companion.TokensToNext);
        Assert.True(companion.IsEgg);
        Assert.Equal("Hatch 0%", companion.MenuGrowthText);
    }));

    [Fact]
    public void EggPercentageMatchesHomeAtExactTwentyNinePercentBoundary() => MainActor.Run(() => Fixture((usage, companion) =>
    {
        companion.SetLanguage(AppLanguage.Ko);
        Assert.Equal(5_000_000, companion.EggHatchThreshold);
        Feed(companion, 0);
        // A double conversion turns exactly 29% into 28.999999999999996. Also protect flooring just below a
        // boundary and clamping while hatching is pending (5_000_001 floors to 100% even unclamped; 5_100_000 does not).
        foreach (var (credits, percent) in new (long, int)[] { (0, 0), (1_449_999, 28), (1_450_000, 29),
                     (1_499_999, 29), (1_500_000, 30), (4_999_999, 99), (5_000_000, 100), (5_000_001, 100),
                     (5_100_000, 100) })
        {
            Feed(companion, credits);
            Assert.True(companion.IsEgg);
            Assert.Equal(credits, companion.State.EggUsage);
            Assert.Equal([$"부화 {percent}%"], MenuBarContent.Lines(usage, companion));
            // Home and the tooltip must agree.
            Assert.EndsWith($" · {percent}%", LimitPace.ProgressDetail(credits, companion.EggHatchThreshold));
        }
        return Task.CompletedTask;
    }));

    [Fact]
    public void ActivePercentageMatchesHomeAtExactTwentyNinePercentBoundary() => MainActor.Run(() => Fixture(async (usage, companion) =>
    {
        await companion.Hatch(1);
        companion.SetLanguage(AppLanguage.Ko);
        var total = companion.Threshold;
        Assert.Equal(0, total % 100);   // the fixture must have an exact integer 29% boundary
        var twentyNine = total * 29 / 100;
        var thirty = total * 30 / 100;
        foreach (var (credits, percent) in new (long, int)[] { (twentyNine - 1, 28), (twentyNine, 29), (thirty - 1, 29), (thirty, 30) })
        {
            companion.ApplyUsage(credits - (companion.State.Active?.UsedAtStage ?? 0));
            Assert.Equal(credits, companion.State.Active?.UsedAtStage);
            Assert.Equal([$"성장 {percent}%"], MenuBarContent.Lines(usage, companion));
            Assert.EndsWith($" · {percent}%", LimitPace.ProgressDetail(credits, total));
        }
    }));

    /// <summary>Candy, difficulty, language and the toggle change the lines without a usage refresh, and each raises
    /// its store's Changed, which the app answers by rebuilding the tooltip.</summary>
    [Fact]
    public void CandyDifficultyLanguageAndToggleChangeTheLinesWithoutUsageRefresh() => MainActor.Run(() => Fixture(async (usage, companion) =>
    {
        await companion.Hatch(1);
        var companionChanged = false;
        companion.Changed += () => companionChanged = true;

        Assert.Equal(CompanionStore.CandyUseResult.Progressed, companion.UseRareCandy());
        Assert.True(companionChanged);
        Assert.Equal("Growth 80%", companion.MenuGrowthText);

        companionChanged = false;
        companion.SetGrowthDifficulty(0.5);
        Assert.True(companionChanged);
        Assert.Equal("Growth 80%", companion.MenuGrowthText);   // difficulty preserves the earned fraction

        companionChanged = false;
        companion.SetLanguage(AppLanguage.Ko);
        Assert.True(companionChanged);
        Assert.Equal("성장 80%", companion.MenuGrowthText);
        companion.SetLanguage(AppLanguage.Ru);
        Assert.Equal(["Рост 80%"], MenuBarContent.Lines(usage, companion));

        var usageChanged = false;
        usage.Changed += () => usageChanged = true;
        usage.ShowGrowthInMenu = false;
        Assert.True(usageChanged);
        Assert.Equal(["—"], MenuBarContent.Lines(usage, companion));
    }));

    [Fact]
    public void PinnedRepresentativeDoesNotReplaceRaisedCompanionProgress() => MainActor.Run(() => Fixture(async (usage, companion) =>
    {
        await companion.Hatch(1);
        Assert.True(companion.SetRepresentativeSpeciesID(1));
        companion.ApplyUsage(companion.TokensToNext);
        Assert.Equal(1, companion.RepresentativeSubject.SpeciesID);
        Assert.Equal(2, companion.CurrentSpeciesID);
        companion.ApplyUsage(companion.Threshold / 2);
        Assert.Equal(["Growth 50%"], MenuBarContent.Lines(usage, companion));
    }));

    [Fact]
    public void AllLanguagesIncludePercentageInBothStates()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var l = new L(language);
            Assert.NotEmpty(l.PokemonGrowthPercent);
            Assert.NotEmpty(l.PokemonGrowthPercentHint);
            Assert.Contains("42%", l.MenuGrowthProgress(42, isEgg: true));
            Assert.Contains("42%", l.MenuGrowthProgress(42, isEgg: false));
            Assert.NotEqual(l.MenuGrowthProgress(42, isEgg: true), l.MenuGrowthProgress(42, isEgg: false));
        }
    }
}
