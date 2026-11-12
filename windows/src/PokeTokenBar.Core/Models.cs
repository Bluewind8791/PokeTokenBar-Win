using System.Globalization;
using System.Text.RegularExpressions;

namespace PokeTokenBar.Core;

/// <summary>Port of <c>CostCoverage</c> (UsageCost.swift).</summary>
public record struct CostCoverage(bool Reported = false, bool Estimated = false, bool Unknown = false)
{
    public static readonly CostCoverage Empty = new();
    public static readonly CostCoverage Source = new(Reported: true);
    public static readonly CostCoverage Estimate = new(Estimated: true);
    public static readonly CostCoverage Unavailable = new(Unknown: true);

    public readonly bool HasKnown => Reported || Estimated;

    public void Merge(CostCoverage other)
    {
        Reported = Reported || other.Reported;
        Estimated = Estimated || other.Estimated;
        Unknown = Unknown || other.Unknown;
    }
}

/// <summary>Port of <c>UsageCost</c> (UsageCost.swift). Text rendering lives with localization in the app.</summary>
public record struct UsageCost(double Amount = 0, CostCoverage Coverage = default)
{
    public void Add(UsageCost other)
    {
        Amount += other.Amount;
        var c = Coverage;
        c.Merge(other.Coverage);
        Coverage = c;
    }

    /// <summary>True when every contributing amount was unpriced: the UI shows "Unavailable" / "$—".</summary>
    public readonly bool IsUnavailable => Coverage.Unknown && !Coverage.HasKnown;
}

/// <summary>Port of <c>DailyUsage</c> (Models.swift).</summary>
public sealed record DailyUsage(
    string Date,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens,
    double TotalCost,
    IReadOnlyDictionary<string, long>? Models = null,
    CostCoverage CostCoverage = default)
{
    public UsageCost UsageCost => new(TotalCost, CostCoverage);
}

/// <summary>Port of <c>BlockUsage</c> (Models.swift).</summary>
public sealed record BlockUsage(
    string Id,
    string StartTime,
    string EndTime,
    bool IsActive,
    long TotalTokens,
    double CostUSD,
    double? TokensPerMinute,
    CostCoverage CostCoverage = default)
{
    public UsageCost UsageCost => new(CostUSD, CostCoverage);
    public DateTimeOffset? EndDate => Iso8601Parser.Date(EndTime);
}

/// <summary>Port of <c>PeriodUsage</c> (Models.swift).</summary>
public sealed record PeriodUsage(string Period, long TotalTokens, double TotalCost, CostCoverage CostCoverage = default)
{
    public UsageCost UsageCost => new(TotalCost, CostCoverage);
}

/// <summary>Port of <c>DailyTrendWindow</c> (Models.swift): the span of the popover's daily trend.</summary>
public enum DailyTrendWindow { D7, D14, D21, Month }

public static class DailyTrendWindows
{
    public static string RawValue(this DailyTrendWindow window) => window switch
    {
        DailyTrendWindow.D7 => "7d",
        DailyTrendWindow.D14 => "14d",
        DailyTrendWindow.D21 => "21d",
        _ => "month",
    };

    public static DailyTrendWindow? FromRawValue(string? raw) => raw switch
    {
        "7d" => DailyTrendWindow.D7,
        "14d" => DailyTrendWindow.D14,
        "21d" => DailyTrendWindow.D21,
        "month" => DailyTrendWindow.Month,
        _ => null,
    };

    /// <summary>Rolling window length in days; null for the calendar month.</summary>
    public static int? DayCount(this DailyTrendWindow window) => window switch
    {
        DailyTrendWindow.D7 => 7,
        DailyTrendWindow.D14 => 14,
        DailyTrendWindow.D21 => 21,
        _ => null,
    };
}

/// <summary>
/// Port of <c>ISO8601Parser</c> (Models.swift): internet date-time with a required zone,
/// optional fractional seconds truncated to milliseconds (Foundation's fractional formatter precision).
/// </summary>
public static partial class Iso8601Parser
{
    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex Pattern();

    public static DateTimeOffset? Date(string s)
    {
        var m = Pattern().Match(s);
        if (!m.Success) return null;
        try
        {
            int I(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            var zone = m.Groups[8].Value;
            var offset = TimeSpan.Zero;
            if (zone != "Z")
            {
                var digits = zone[1..].Replace(":", "");
                offset = new TimeSpan(int.Parse(digits[..2], CultureInfo.InvariantCulture),
                    int.Parse(digits[2..], CultureInfo.InvariantCulture), 0);
                if (zone[0] == '-') offset = -offset;
            }
            var ms = 0;
            if (m.Groups[7].Success)
            {
                var frac = m.Groups[7].Value;
                ms = int.Parse(frac.Length >= 3 ? frac[..3] : frac.PadRight(3, '0'), CultureInfo.InvariantCulture);
            }
            return new DateTimeOffset(I(1), I(2), I(3), I(4), I(5), I(6), ms, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Foundation <c>ISO8601DateFormatter()</c> default output: UTC, whole seconds.</summary>
    public static string Format(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Port of <c>UsageCost.text(_:compact:)</c> / <c>explanation(_:)</c> (UsageCost.swift).</summary>
public static class UsageCostText
{
    public static string Text(this UsageCost cost, L l, bool compact = false)
    {
        if (cost.Coverage.Unknown && !cost.Coverage.HasKnown) return compact ? "$—" : l.CostUnavailable;
        return compact ? TokenFormatter.CostCompact(cost.Amount) : TokenFormatter.Cost(cost.Amount);
    }

    public static string Explanation(this UsageCost cost, L l)
    {
        if (cost.Coverage.Unknown) return cost.Coverage.HasKnown ? l.CostPartialHint : l.CostUnavailableHint;
        return cost.Coverage.Estimated ? l.CostEstimateHint : l.CostReportedHint;
    }
}
