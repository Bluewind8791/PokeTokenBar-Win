using System.Globalization;

namespace PokeTokenBar.Core;

/// <summary>Port of <c>TokenFormatter.swift</c>. <c>String(format:)</c> is C printf: invariant culture here.</summary>
public static class TokenFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>987 → "987", 12_345 → "12.3K", 190_612_940 → "190.6M", 1_240_000_000 → "1.24B".</summary>
    public static string Compact(long value)
    {
        var v = (double)Math.Abs(value);
        var sign = value < 0 ? "-" : "";
        if (v < 1_000) return value.ToString(Inv);
        // K and M use one decimal: promote when rounding would display 1000.0.
        if (v < 999_950) return sign + Trim(v / 1_000, 1) + "K";
        if (v < 999_950_000) return sign + Trim(v / 1_000_000, 1) + "M";
        return sign + Trim(v / 1_000_000_000, 2) + "B";
    }

    /// <summary>Thousands separator from the system region (not the app language), like macOS.</summary>
    public static string Grouped(long value, CultureInfo? culture = null) =>
        value.ToString("N0", culture ?? CultureInfo.CurrentCulture);

    public static string Cost(double usd) => "$" + Printf(usd, 2);

    /// <summary>Menu-bar cost: $9.5 / $311 / $1.2K. Bands are judged on the rounded string.</summary>
    public static string CostCompact(double usd)
    {
        var tenths = Printf(usd, 1);
        if (double.Parse(tenths, Inv) < 100) return "$" + tenths;
        var whole = Printf(usd, 0);
        if (double.Parse(whole, Inv) < 10_000) return "$" + whole;
        return "$" + Printf(usd / 1_000, 1) + "K";
    }

    /// <summary>79.96 → "80%", 88.35 → "88.3%".</summary>
    public static string Percent(double value)
    {
        var tenths = Printf(value, 1);
        return (tenths.EndsWith(".0", StringComparison.Ordinal) ? tenths[..^2] : tenths) + "%";
    }

    private static string Trim(double value, int decimals)
    {
        var s = Printf(value, decimals);
        s = s.TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }

    /// <summary>
    /// C printf <c>%.Nf</c>, as Swift's <c>String(format:)</c> renders it. .NET Core 3.0+ "F" formatting
    /// rounds the exact binary value half-to-even too; <c>PrintfMatchesCRounding</c> pins the ties.
    /// </summary>
    internal static string Printf(double value, int decimals) => value.ToString("F" + decimals, Inv);
}

/// <summary>Port of <c>PaceTier.swift</c>: six pace bands, delta = utilization − pace (%p).</summary>
public enum PaceTier
{
    WayUnder, Under, OnPace, SlightlyOver, Over, WayOver,
}

public static class PaceTiers
{
    /// <summary>The tier is held until this fraction of the window has passed.</summary>
    public const double MinimumPace = 0.1;

    /// <summary>Null means the caller falls back to the absolute threshold color.</summary>
    public static PaceTier? Tier(double utilization, double? pace, double critThreshold)
    {
        if (pace is not { } p || p < MinimumPace) return null;
        if (utilization >= critThreshold) return PaceTier.WayOver;
        var d = RoundedDelta(utilization, p);
        if (d < -25) return PaceTier.WayUnder;
        if (d < -10) return PaceTier.Under;
        if (d < 5) return PaceTier.OnPace;
        if (d < 15) return PaceTier.SlightlyOver;
        if (d < 30) return PaceTier.Over;
        return PaceTier.WayOver;
    }

    /// <summary>Swift <c>.rounded()</c> is schoolbook rounding (half away from zero).</summary>
    public static int RoundedDelta(double utilization, double pace) =>
        (int)Math.Round(utilization - pace * 100, MidpointRounding.AwayFromZero);

    public static PaceTier Gauge(double utilization, double? pace, double warnThreshold, double critThreshold)
    {
        if (Tier(utilization, pace, critThreshold) is { } tier) return tier;
        if (utilization >= critThreshold) return PaceTier.WayOver;
        if (utilization >= warnThreshold) return PaceTier.Over;
        return PaceTier.OnPace;
    }

    public static bool IsCalm(this PaceTier tier) => tier <= PaceTier.OnPace;
}
