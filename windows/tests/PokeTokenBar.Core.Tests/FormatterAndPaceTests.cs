using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of TokenFormatterTests (PokeTokenBarTests.swift) and PaceTierTests.swift.</summary>
public class TokenFormatterTests
{
    [Theory]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(999_949, "999.9K")]
    [InlineData(999_950, "1M")]
    [InlineData(999_999, "1M")]
    [InlineData(1_000_000, "1M")]
    [InlineData(999_949_999, "999.9M")]
    [InlineData(999_950_000, "1B")]
    [InlineData(999_999_999, "1B")]
    [InlineData(1_000_000_000, "1B")]
    [InlineData(1_240_000_000, "1.24B")]
    public void CompactPromotesUnitsAtRoundedBoundaries(long value, string expected)
    {
        Assert.Equal(expected, TokenFormatter.Compact(value));
        Assert.Equal("-" + expected, TokenFormatter.Compact(-value));
    }

    [Fact]
    public void Compact()
    {
        Assert.Equal("0", TokenFormatter.Compact(0));
        Assert.Equal("987", TokenFormatter.Compact(987));
        Assert.Equal("12.3K", TokenFormatter.Compact(12_345));
        Assert.Equal("190.6M", TokenFormatter.Compact(190_612_940));
    }

    [Theory]
    [InlineData(9.5, "$9.5")]
    [InlineData(99.96, "$100")]
    [InlineData(311.3, "$311")]
    [InlineData(9_999.4, "$9999")]
    [InlineData(12_345, "$12.3K")]
    public void CostCompact(double usd, string expected) => Assert.Equal(expected, TokenFormatter.CostCompact(usd));

    [Theory]
    [InlineData(79.96, "80%")]
    [InlineData(88.35, "88.3%")]   // 88.35 is 88.349999… in binary: printf truncates to .3
    [InlineData(100, "100%")]
    public void Percent(double v, string expected) => Assert.Equal(expected, TokenFormatter.Percent(v));

    /// <summary>
    /// Exact binary ties, where half-to-even (printf) and half-away-from-zero would disagree.
    /// Pins the assumption that .NET "F" formatting matches Swift's String(format:).
    /// </summary>
    [Theory]
    [InlineData(0.125, 2, "0.12")]
    [InlineData(0.375, 2, "0.38")]
    [InlineData(2.5, 0, "2")]
    [InlineData(3.5, 0, "4")]
    [InlineData(0.05, 1, "0.1")]       // 0.05000000000000000277 — above the tie
    [InlineData(1e21, 2, "1000000000000000000000.00")]
    [InlineData(-0.01, 1, "-0.0")]
    public void PrintfMatchesCRounding(double v, int decimals, string expected) =>
        Assert.Equal(expected, TokenFormatter.Printf(v, decimals));

    [Fact]
    public void CostTwoDecimals() => Assert.Equal("$311.30", TokenFormatter.Cost(311.30462895));
}

public class PaceTierTests
{
    private const double Crit = 95.0;
    private static PaceTier? Tier(double utilization, double? pace = 0.5) => PaceTiers.Tier(utilization, pace, Crit);

    [Theory]
    [InlineData(0, PaceTier.WayUnder)]
    [InlineData(24, PaceTier.WayUnder)]
    [InlineData(25, PaceTier.Under)]
    [InlineData(39, PaceTier.Under)]
    [InlineData(40, PaceTier.OnPace)]
    [InlineData(54, PaceTier.OnPace)]
    [InlineData(55, PaceTier.SlightlyOver)]
    [InlineData(64, PaceTier.SlightlyOver)]
    [InlineData(65, PaceTier.Over)]
    [InlineData(79, PaceTier.Over)]
    [InlineData(80, PaceTier.WayOver)]
    public void BoundariesOnBothSides(double utilization, PaceTier expected) => Assert.Equal(expected, Tier(utilization));

    [Fact]
    public void TierFollowsRoundedDelta()
    {
        Assert.Equal(PaceTier.SlightlyOver, Tier(54.6));
        Assert.Equal(PaceTier.OnPace, Tier(54.4));
        Assert.Equal(5, PaceTiers.RoundedDelta(54.6, 0.5));
        Assert.Equal(-12, PaceTiers.RoundedDelta(38.5, 0.5));   // half away from zero, like Swift .rounded()
    }

    [Fact]
    public void CritOverridesPace()
    {
        Assert.Equal(PaceTier.WayOver, Tier(95, 0.99));
        Assert.Equal(PaceTier.OnPace, Tier(94, 0.99));
    }

    [Fact]
    public void NoPaceMeansNoTier() => Assert.Null(Tier(50, null));

    [Fact]
    public void HeldEarlyInWindow()
    {
        Assert.Null(Tier(10, 0.02));
        Assert.Null(Tier(99, PaceTiers.MinimumPace - 0.001));
        Assert.Equal(PaceTier.OnPace, Tier(10, PaceTiers.MinimumPace));
    }

    [Fact]
    public void WithoutAPaceTheThresholdsPickTheColor()
    {
        Assert.Equal(PaceTier.OnPace, PaceTiers.Gauge(10, null, 80, 95));
        Assert.Equal(PaceTier.Over, PaceTiers.Gauge(80, null, 80, 95));
        Assert.Equal(PaceTier.WayOver, PaceTiers.Gauge(95, null, 80, 95));
    }

    [Fact]
    public void CalmMeansOnPaceOrSlower()
    {
        Assert.True(PaceTier.OnPace.IsCalm());
        Assert.False(PaceTier.SlightlyOver.IsCalm());
    }
}
