using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of PokedexRegionTests.swift: generation (region) ranges are derived purely from the National Dex
/// number — no PokéAPI call.</summary>
public class PokedexRegionTests
{
    private static List<(int Id, bool IsShiny)> Owned(DexNumberRange range, bool shiny = false) =>
        range.Numbers.Select(id => (id, shiny)).ToList();

    // MARK: Region(dexNumber)

    [Fact]
    public void TestRegionBoundariesMapToExpectedRegion()
    {
        Assert.Equal(PokedexRegion.Kanto, PokedexRegions.Region(1));
        Assert.Equal(PokedexRegion.Kanto, PokedexRegions.Region(151));
        Assert.Equal(PokedexRegion.Johto, PokedexRegions.Region(152));
        Assert.Equal(PokedexRegion.Johto, PokedexRegions.Region(251));
        Assert.Equal(PokedexRegion.Hoenn, PokedexRegions.Region(252));
        Assert.Equal(PokedexRegion.Hoenn, PokedexRegions.Region(386));
        Assert.Equal(PokedexRegion.Sinnoh, PokedexRegions.Region(387));
        Assert.Equal(PokedexRegion.Sinnoh, PokedexRegions.Region(493));
        Assert.Equal(PokedexRegion.Unova, PokedexRegions.Region(494));
        Assert.Equal(PokedexRegion.Unova, PokedexRegions.Region(649));
    }

    [Fact]
    public void TestRegionIsNilOutsideGen1Through5()
    {
        Assert.Null(PokedexRegions.Region(0));
        Assert.Null(PokedexRegions.Region(-5));
        Assert.Null(PokedexRegions.Region(650));
        Assert.Null(PokedexRegions.Region(10_000));
    }

    [Fact]
    public void TestRegionTotalsMatchGenerationCounts()
    {
        Assert.Equal(151, PokedexRegion.Kanto.Total());
        Assert.Equal(100, PokedexRegion.Johto.Total());
        Assert.Equal(135, PokedexRegion.Hoenn.Total());
        Assert.Equal(107, PokedexRegion.Sinnoh.Total());
        Assert.Equal(156, PokedexRegion.Unova.Total());
    }

    [Fact]
    public void TestIdentifiableIdsAreStable()
    {
        Assert.Equal("hoenn", PokedexRegion.Hoenn.Id());
        Assert.Equal("sinnoh", new DexCompletion(PokedexRegion.Sinnoh, 3, 0).Id);
    }

    [Fact]
    public void TestNationalTotalIsSumOfAllRegions()
    {
        Assert.Equal(PokedexRegions.NationalTotal, PokedexRegions.AllCases.Sum(r => r.Total()));
        Assert.Equal(649, PokedexRegions.NationalTotal);
    }

    [Fact]
    public void TestRegionRangesAreContiguousAndNonOverlapping()
    {
        var ordered = PokedexRegions.AllCases;
        foreach (var (lower, upper) in ordered.Zip(ordered.Skip(1)))
            Assert.Equal(lower.DexRange().UpperBound + 1, upper.DexRange().LowerBound);
        Assert.Equal(1, ordered[0].DexRange().LowerBound);
        Assert.Equal(PokedexRegions.NationalTotal, ordered[^1].DexRange().UpperBound);
    }

    // MARK: RegionCompletions

    [Fact]
    public void TestPartialRegionCompletionCountsOwnedInRange()
    {
        (int, bool)[] owned =
        [
            (1, false), (4, false), (7, false), // Kanto
            (152, false),                       // Johto
            (999, false),                       // out of range, ignored
        ];
        var completions = DexProgress.RegionCompletions(owned);
        var kanto = completions.Single(c => c.Region == PokedexRegion.Kanto);
        Assert.Equal(3, kanto.Owned);
        Assert.Equal(151, kanto.Total);
        Assert.False(kanto.IsComplete);
        Assert.Equal(1, completions.Single(c => c.Region == PokedexRegion.Johto).Owned);
    }

    [Fact]
    public void TestDuplicateOwnedIdsAreNotDoubleCounted()
    {
        (int, bool)[] owned = [(25, false), (25, true), (26, false)];
        Assert.Equal(2, DexProgress.RegionCompletions(owned).Single(c => c.Region == PokedexRegion.Kanto).Owned);
    }

    /// <summary>A species is shiny-owned when any individual is, whichever comes first: both branches of
    /// "already shiny || this one shiny" alone.</summary>
    [Fact]
    public void TestAnyShinyIndividualMakesTheSpeciesShinyOwned()
    {
        int ShinyOwned((int, bool)[] owned) =>
            DexProgress.RegionCompletions(owned).Single(c => c.Region == PokedexRegion.Kanto).ShinyOwned;
        Assert.Equal(1, ShinyOwned([(25, false), (25, true)]));   // only the later one is shiny
        Assert.Equal(1, ShinyOwned([(25, true), (25, false)]));   // only the earlier one is shiny
        Assert.Equal(0, ShinyOwned([(25, false), (25, false)]));
        Assert.Equal(1, DexProgress.NationalProgress([(25, true), (25, false)]).ShinyOwned);
    }

    [Fact]
    public void TestCompleteRegionReportsIsComplete()
    {
        var johto = DexProgress.RegionCompletions(Owned(PokedexRegion.Johto.DexRange()))
            .Single(c => c.Region == PokedexRegion.Johto);
        Assert.Equal(100, johto.Owned);
        Assert.True(johto.IsComplete);
        Assert.False(johto.IsShinyComplete);
    }

    [Fact]
    public void TestShinyCompleteRequiresEverySpeciesShiny()
    {
        var owned = Owned(PokedexRegion.Johto.DexRange(), shiny: true);
        owned[0] = (owned[0].Id, false);
        var johto = DexProgress.RegionCompletions(owned).Single(c => c.Region == PokedexRegion.Johto);
        Assert.True(johto.IsComplete);
        Assert.False(johto.IsShinyComplete);
        Assert.Equal(99, johto.ShinyOwned);
    }

    /// <summary>One species short of the range is not complete: the boundary of <c>IsComplete</c>.</summary>
    [Fact]
    public void TestRegionOneShortOfItsLastSpeciesIsIncomplete()
    {
        var owned = Owned(PokedexRegion.Kanto.DexRange());
        owned.RemoveAt(owned.Count - 1);
        Assert.Contains(DexProgress.RegionCompletions(owned), c => c.Region == PokedexRegion.Kanto && c is { Owned: 150, IsComplete: false });
        Assert.DoesNotContain(DexBadge.ForRegion(PokedexRegion.Kanto), DexProgress.EarnedBadges(owned, 0));
    }

    // MARK: Badges

    [Fact]
    public void TestNoBadgesWhenNothingComplete()
    {
        (int, bool)[] owned = [(1, false), (2, false)];
        Assert.Empty(DexProgress.EarnedBadges(owned, unownFormsCollected: 0));
    }

    [Fact]
    public void TestCompletingARegionEarnsItsBadgeOnly()
    {
        var badges = DexProgress.EarnedBadges(Owned(PokedexRegion.Kanto.DexRange()), 0);
        Assert.Contains(DexBadge.ForRegion(PokedexRegion.Kanto), badges);
        Assert.DoesNotContain(DexBadge.ForRegionShiny(PokedexRegion.Kanto), badges);
        Assert.DoesNotContain(DexBadge.National, badges);
    }

    [Fact]
    public void TestShinyRegionEarnsBothRegionAndShinyBadges()
    {
        var badges = DexProgress.EarnedBadges(Owned(PokedexRegion.Kanto.DexRange(), shiny: true), 0);
        Assert.Contains(DexBadge.ForRegion(PokedexRegion.Kanto), badges);
        Assert.Contains(DexBadge.ForRegionShiny(PokedexRegion.Kanto), badges);
    }

    [Fact]
    public void TestUnownBadgeEarnedAtTwentyEightForms()
    {
        var total = PokemonForm.Forms(PokemonForm.UnownSpeciesID).Count;
        Assert.Equal(28, total);
        Assert.DoesNotContain(DexBadge.Unown, DexProgress.EarnedBadges([], unownFormsCollected: total - 1));
        Assert.Contains(DexBadge.Unown, DexProgress.EarnedBadges([], unownFormsCollected: total));
    }

    [Fact]
    public void TestNationalBadgeRequiresEverySpecies()
    {
        var badges = DexProgress.EarnedBadges(Owned(PokedexRegions.NationalRange), 0);
        Assert.Contains(DexBadge.National, badges);
        Assert.DoesNotContain(DexBadge.NationalShiny, badges);
        Assert.All(PokedexRegions.AllCases, r => Assert.Contains(DexBadge.ForRegion(r), badges));
    }

    /// <summary>One species short of 649 earns every other region but not the National Dex (nor its shiny tier).</summary>
    [Fact]
    public void TestNationalOneShortOfTheLastSpeciesEarnsNoNationalBadge()
    {
        var owned = Owned(PokedexRegions.NationalRange, shiny: true);
        owned.RemoveAt(owned.Count - 1);
        var badges = DexProgress.EarnedBadges(owned, 0);
        Assert.Equal(648, DexProgress.NationalProgress(owned).Owned);
        Assert.DoesNotContain(DexBadge.National, badges);
        Assert.DoesNotContain(DexBadge.NationalShiny, badges);
        Assert.Contains(DexBadge.ForRegion(PokedexRegion.Sinnoh), badges);
    }

    [Fact]
    public void TestNationalShinyBadgeRequiresEverySpeciesShiny()
    {
        var badges = DexProgress.EarnedBadges(Owned(PokedexRegions.NationalRange, shiny: true), 0);
        Assert.Contains(DexBadge.National, badges);
        Assert.Contains(DexBadge.NationalShiny, badges);
    }

    /// <summary>Region complete but national incomplete — the B-only branch the defect protocol asks for.</summary>
    [Fact]
    public void TestRegionCompleteDoesNotImplyNational()
    {
        var badges = DexProgress.EarnedBadges(Owned(PokedexRegion.Kanto.DexRange()), 0);
        Assert.Contains(DexBadge.ForRegion(PokedexRegion.Kanto), badges);
        Assert.DoesNotContain(DexBadge.National, badges);
    }

    // MARK: NewlyEarnedBadges — notify-once semantics

    [Fact]
    public void TestNewlyEarnedReturnsEveryBadgeWhenNothingNotified()
    {
        DexBadge[] earned = [DexBadge.ForRegion(PokedexRegion.Kanto), DexBadge.National];
        Assert.Equal(earned, DexProgress.NewlyEarnedBadges(earned, new HashSet<string>()));
    }

    [Fact]
    public void TestNewlyEarnedExcludesAlreadyNotifiedBadges()
    {
        DexBadge[] earned = [DexBadge.ForRegion(PokedexRegion.Kanto), DexBadge.National];
        Assert.Equal([DexBadge.National], DexProgress.NewlyEarnedBadges(earned, new HashSet<string> { "region.kanto" }));
    }

    [Fact]
    public void TestNewlyEarnedIsEmptyWhenAllNotified()
    {
        DexBadge[] earned = [DexBadge.ForRegion(PokedexRegion.Kanto), DexBadge.National];
        Assert.Empty(DexProgress.NewlyEarnedBadges(earned, earned.Select(b => b.NotificationKey).ToHashSet()));
    }

    [Fact]
    public void TestBadgeNotificationKeysAreStableAndUnique()
    {
        DexBadge[] badges =
        [
            DexBadge.ForRegion(PokedexRegion.Kanto), DexBadge.ForRegionShiny(PokedexRegion.Kanto),
            DexBadge.ForRegion(PokedexRegion.Unova), DexBadge.Unown, DexBadge.National, DexBadge.NationalShiny,
        ];
        var keys = badges.Select(b => b.NotificationKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        // Keys are persisted, so they must not drift.
        Assert.Equal("region.kanto", DexBadge.ForRegion(PokedexRegion.Kanto).NotificationKey);
        Assert.Equal("regionShiny.kanto", DexBadge.ForRegionShiny(PokedexRegion.Kanto).NotificationKey);
        Assert.Equal("unown", DexBadge.Unown.NotificationKey);
        Assert.Equal("national", DexBadge.National.NotificationKey);
        Assert.Equal("nationalShiny", DexBadge.NationalShiny.NotificationKey);
    }

    /// <summary>The persisted keys also name every region the same way on both platforms (Swift raw values).</summary>
    [Fact]
    public void TestEveryRegionHasItsSwiftRawValue()
    {
        Assert.Equal(["kanto", "johto", "hoenn", "sinnoh", "unova"], PokedexRegions.AllCases.Select(r => r.RawValue()));
    }

    /// <summary>Every badge kind has a label and both notification strings in every language (the switches throw on
    /// a kind they miss).</summary>
    [Fact]
    public void TestEveryBadgeHasALabelInEveryLanguage()
    {
        var all = PokedexRegions.AllCases.SelectMany(r => new[] { DexBadge.ForRegion(r), DexBadge.ForRegionShiny(r) })
            .Concat([DexBadge.Unown, DexBadge.National, DexBadge.NationalShiny]).ToList();
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);
            Assert.All(all, b => Assert.False(string.IsNullOrWhiteSpace(l.DexBadgeLabel(b))));
            Assert.All(PokedexRegions.AllCases, r => Assert.Contains(l.RegionName(r), l.DexBadgeLabel(DexBadge.ForRegion(r))));
        }
        Assert.Equal("Kanto 87/151", new L(AppLanguage.En).DexRegionProgressLabel(PokedexRegion.Kanto, 87, 151));
        Assert.Equal("Teselia", new L(AppLanguage.Es).RegionName(PokedexRegion.Unova));
    }
}
