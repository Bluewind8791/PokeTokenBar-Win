using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Port of the "Choice — the extension point" cases of ActivityVisualizationTests.swift (#449): the default, the
/// click cycle, the names and the persisted choice. The grid and panel cases test App code and live in App.Tests.
/// </summary>
public class ActivityVisualizationTests
{
    /// <summary>The default is the existing bar chart. If it changed, an update alone would change people's popover.</summary>
    [Fact]
    public void DefaultVisualizationIsTheExistingBarChart()
    {
        Assert.Equal(ActivityVisualization.MonthlyBars, ActivityVisualizations.Default);
        Assert.Equal(ActivityVisualization.MonthlyBars, ActivityVisualizations.AllCases[0]);   // the cycle starts at the default too
    }

    /// <summary>The cycle visits every member once and comes back. No hardcoded count — adding a visualization
    /// should not need this test touched.</summary>
    [Fact]
    public void CyclingVisitsEveryVisualizationExactlyOnceAndWrapsAround()
    {
        var seen = new List<ActivityVisualization>();
        var current = ActivityVisualizations.Default;
        foreach (var _ in ActivityVisualizations.AllCases)
        {
            seen.Add(current);
            current = current.Next();
        }
        Assert.Equal(ActivityVisualizations.AllCases.ToHashSet(), seen.ToHashSet());
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(ActivityVisualizations.Default, current);
    }

    /// <summary>Every visualization has a name in every language — and an arm in the title switch, which throws otherwise.</summary>
    [Fact]
    public void EveryVisualizationHasANonEmptyTitleInEveryLanguage()
    {
        foreach (var visualization in ActivityVisualizations.AllCases)
            foreach (var lang in Enum.GetValues<AppLanguage>())
                Assert.False(string.IsNullOrWhiteSpace(visualization.Title(new L(lang))), $"{visualization} has no {lang} title");
    }

    /// <summary>Saved and restored under the macOS key. An unknown value falls back to the default — going back to a
    /// build without that visualization, or a hand-edited setting, must not leave the popover empty.</summary>
    [Fact]
    public void SelectionRoundTripsAndUnknownValuesFallBack()
    {
        var settings = new AppSettings(null);
        Assert.Equal(ActivityVisualizations.Default, Stores.Make(settings).ActivityVisualization);

        Stores.Make(settings).ActivityVisualization = ActivityVisualization.RecentHeatmap;
        Assert.Equal("recentHeatmap", settings.GetString("activityVisualization"));
        Assert.Equal(ActivityVisualization.RecentHeatmap, Stores.Make(settings).ActivityVisualization);

        settings.Set("activityVisualization", "someVisualizationFromTheFuture");
        Assert.Equal(ActivityVisualizations.Default, Stores.Make(settings).ActivityVisualization);
    }

    [Fact]
    public void RawValuesMatchTheSwiftCaseNames()
    {
        Assert.Equal("monthlyBars", ActivityVisualization.MonthlyBars.RawValue());
        Assert.Equal("recentHeatmap", ActivityVisualization.RecentHeatmap.RawValue());
        foreach (var visualization in ActivityVisualizations.AllCases)
            Assert.Equal(visualization, ActivityVisualizations.FromRawValue(visualization.RawValue()));
        Assert.Null(ActivityVisualizations.FromRawValue(null));
    }
}
