using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PokeTokenBar.App;
using PokeTokenBar.Core;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// The Pokédex header's generation capsules and completion medals (#442): the view cases of
/// GenerationDexScreenshotTests.swift and the region case of DexBackNavigationTests.swift. Swift drives the mounted view
/// through OCR and real clicks; here the click is raised on the capsule found by its accessible name, and the grid is
/// read back from the rebuilt tree.
/// </summary>
public class DexRegionProgressTests
{
    private static DexCompletion Completion(PokedexRegion region, int owned, int shinyOwned = 0) => new(region, owned, shinyOwned);

    [Fact]
    public void TheCapsuleMarksCompletionOnlyOnceTheRegionIsComplete()
    {
        Assert.Equal(RegionTally.Mark.None, RegionTally.MarkFor(Completion(PokedexRegion.Kanto, 150)));
        Assert.Equal(RegionTally.Mark.Medal, RegionTally.MarkFor(Completion(PokedexRegion.Kanto, 151)));
        // Shiny completion outranks the plain medal; a shiny count short of the total does not.
        Assert.Equal(RegionTally.Mark.Medal, RegionTally.MarkFor(Completion(PokedexRegion.Johto, 100, 99)));
        Assert.Equal(RegionTally.Mark.Sparkles, RegionTally.MarkFor(Completion(PokedexRegion.Johto, 100, 100)));
    }

    [Fact]
    public void ARegionWithNothingOwnedCannotBeSelected()
    {
        Assert.False(RegionTally.IsEnabled(Completion(PokedexRegion.Sinnoh, 0)));
        Assert.True(RegionTally.IsEnabled(Completion(PokedexRegion.Sinnoh, 1)));
    }

    /// <summary>Regional medals already show on their capsule, so the header collects the National and Unown tiers only,
    /// and takes no space when none is earned.</summary>
    [Fact]
    public void TheHeaderCollectsOnlyTheNationalAndUnownMedals() => Sta(() =>
    {
        var l = new L(AppLanguage.En);
        DexBadge[] regional = [DexBadge.ForRegion(PokedexRegion.Kanto), DexBadge.ForRegionShiny(PokedexRegion.Kanto)];
        Assert.Empty(DexBadgeMedal.HeaderBadges(regional));
        Assert.Null(DexBadgeMedal.Strip(regional, l));

        DexBadge[] earned = [.. regional, DexBadge.Unown, DexBadge.National, DexBadge.NationalShiny];
        Assert.Equal([DexBadge.Unown, DexBadge.National, DexBadge.NationalShiny], DexBadgeMedal.HeaderBadges(earned));
        var strip = Assert.IsAssignableFrom<Panel>(DexBadgeMedal.Strip(earned, l));
        Assert.Equal([l.DexBadgeLabel(DexBadge.Unown), l.DexBadgeLabel(DexBadge.National), l.DexBadgeLabel(DexBadge.NationalShiny)],
            strip.Children.OfType<FrameworkElement>().Select(m => m.ToolTip as string));
    });

    [Fact]
    public void EveryBadgeHasItsMedalEmoji()
    {
        Assert.Equal("🏅", DexBadgeMedal.Emoji(DexBadge.ForRegion(PokedexRegion.Unova)));
        Assert.Equal("✨", DexBadgeMedal.Emoji(DexBadge.ForRegionShiny(PokedexRegion.Unova)));
        Assert.Equal("🔤", DexBadgeMedal.Emoji(DexBadge.Unown));
        Assert.Equal("🌍", DexBadgeMedal.Emoji(DexBadge.National));
        Assert.Equal("✨", DexBadgeMedal.Emoji(DexBadge.NationalShiny));
    }

    /// <summary>Port of <c>testRegionChipFiltersMountedCollectionAndTogglesBack</c>: Johto shows only Johto, a second
    /// click shows everything again.</summary>
    [Fact]
    public void TheRegionCapsuleFiltersTheGridAndTogglesBack() => Sta(() =>
    {
        var (root, l) = Collection([(1, "Maple"), (152, "Willow")]);
        Assert.Equal(["Maple", "Willow"], Names(root, "Maple", "Willow"));

        Click(Chip(root, l.DexRegionProgressLabel(PokedexRegion.Johto, 1, 100)));
        Assert.Equal(["Willow"], Names(root, "Maple", "Willow"));

        Click(Chip(root, l.DexRegionProgressLabel(PokedexRegion.Johto, 1, 100)));
        Assert.Equal(["Maple", "Willow"], Names(root, "Maple", "Willow"));

        // An empty generation is disabled: clicking it filters nothing away.
        Click(Chip(root, l.DexRegionProgressLabel(PokedexRegion.Sinnoh, 0, 107)));
        Assert.Equal(["Maple", "Willow"], Names(root, "Maple", "Willow"));
    });

    /// <summary>Selecting a generation starts its list on the first page (Swift <c>.onChange(of: selectedRegion)</c>).</summary>
    [Fact]
    public void SelectingARegionStartsOnTheFirstPage() => Sta(() =>
    {
        var species = Enumerable.Range(1, 20).Select(id => (id, $"K{id}"))
            .Concat(Enumerable.Range(152, 20).Select(id => (id, $"J{id}"))).ToList();
        var (root, l) = Collection(species);
        Click(Chip(root, l.DexPageNext));
        Assert.Equal(["J152", "K17"], Names(root, "K17", "J152", "J168"));   // page 2 of 3 of everything

        Click(Chip(root, l.DexRegionProgressLabel(PokedexRegion.Johto, 20, 100)));
        Assert.Equal(["J152"], Names(root, "K17", "J152", "J168"));          // page 1 of Johto, not page 2
    });

    // MARK: Helpers

    private static (ContentControl Root, L L) Collection(IReadOnlyList<(int Id, string Name)> species)
    {
        var state = new CompanionState { Language = AppLanguage.En };
        foreach (var (id, name) in species)
            state.Dex.Add(new DexEntry(id, id, [id], Rarity.Common, DateTimeOffset.Now,
                names: new() { [id] = new() { ["en"] = name } }));
        var path = Path.Combine(Path.GetTempPath(), $"ptb-dex-regions-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, CompanionJson.Serialize(state));
        var s = SampleData.CollectionServices(new RenderHarness.Context(AppLanguage.En, Dark: true), withForms: false, savePath: path);
        s.Nav.Tab = PopoverTab.Collection;
        return ((ContentControl)CollectionView.Build(s), s.Companion.L);
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root) =>
        new[] { root }.Concat(LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(Tree));

    /// <summary>Which of <paramref name="names"/> the grid shows (as cell names), sorted.</summary>
    private static List<string> Names(DependencyObject root, params string[] names) =>
        Tree(root).OfType<TextBlock>().Select(t => t.Text).Where(names.Contains).Distinct().Order(StringComparer.Ordinal).ToList();

    private static FrameworkElement Chip(DependencyObject root, string accessibleName) =>
        Tree(root).OfType<FrameworkElement>().Single(e => AutomationProperties.GetName(e) == accessibleName);

    private static void Click(FrameworkElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    /// <summary>WPF elements need an STA thread; the medal artwork loads back onto the UI context, as in the app.</summary>
    private static void Sta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
