namespace PokeTokenBar.Core;

/// <summary>Port of <c>LocalUsageReader.Entry</c>: one normalized request record.</summary>
public sealed record UsageEntry
{
    public required string Id { get; init; }
    public required DateTimeOffset Date { get; set; }
    public required string LocalDay { get; set; }
    public required string Model { get; init; }
    public long Input { get; init; }
    public long Output { get; init; }
    public long CacheWrite { get; init; }
    public long CacheRead { get; init; }
    /// <summary>Prefer a valid source-recorded amount (including zero) to model-table estimates.</summary>
    public double? ExplicitCost { get; set; }
    public bool? CostIsEstimate { get; set; }
    /// <summary>The source cannot reconstruct token buckets for a price-table estimate.</summary>
    public bool? CostUnavailable { get; set; }
    /// <summary>Claude only: the session the turn belongs to, from the transcript path.</summary>
    public string? SessionID { get; set; }
    /// <summary>Claude only: every transcript session that contained this turn.</summary>
    public List<string>? SessionIDs { get; set; }

    public List<string> ClaudeSessionIDs
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (SessionIDs != null) set.UnionWith(SessionIDs);
            if (SessionID != null) set.Add(SessionID);
            return set.ToList();
        }
    }

    public long Total => Input + Output + CacheWrite + CacheRead;
}

/// <summary>Port of <c>LocalUsageReader.Bucket</c>.</summary>
public struct UsageBucket
{
    public long Input, Output, CacheWrite, CacheRead;
    public double Cost;
    public CostCoverage CostCoverage;

    public readonly long Total => Input + Output + CacheWrite + CacheRead;

    public void Add(UsageEntry e)
    {
        Input += e.Input; Output += e.Output; CacheWrite += e.CacheWrite; CacheRead += e.CacheRead;
        // Zero-usage/replay records must not make an unknown-only total look partially priced.
        if (e.Total <= 0) return;
        if (e.ExplicitCost is { } reported && double.IsFinite(reported) && reported >= 0)
        {
            Cost += reported;
            CostCoverage.Merge(e.CostIsEstimate == true ? CostCoverage.Estimate : CostCoverage.Source);
        }
        else if (e.CostUnavailable != true
                 && ModelPricing.EstimatedCost(e.Model, e.Input, e.Output, e.CacheWrite, e.CacheRead) is { } estimate)
        {
            Cost += estimate;
            CostCoverage.Merge(CostCoverage.Estimate);
        }
        else
        {
            if (e.CostUnavailable != true) ModelPricing.NoteUnpriced(e.Model);
            CostCoverage.Merge(CostCoverage.Unavailable);
        }
    }
}
