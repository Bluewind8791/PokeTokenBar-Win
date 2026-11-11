namespace PokeTokenBar.Core;

/// <summary>Port of <c>ModelRate</c> (ModelPricing.swift). USD per token.</summary>
public readonly record struct ModelRate(double Input, double Output, double CacheWrite, double CacheRead)
{
    public static readonly ModelRate Zero = new(0, 0, 0, 0);

    /// <summary>Declared in USD per million tokens for readability, stored per token.</summary>
    public static ModelRate PerMillion(double input, double output, double cacheWrite, double cacheRead) =>
        new(input / 1_000_000, output / 1_000_000, cacheWrite / 1_000_000, cacheRead / 1_000_000);
}

/// <summary>
/// Port of <c>ModelPricing.swift</c>. The table must stay byte-for-byte equal to the Swift table:
/// <c>ModelPricingParityTests</c> parses the Swift source and fails on any drift.
/// </summary>
public static class ModelPricing
{
    public static readonly IReadOnlyDictionary<string, ModelRate> Table = new Dictionary<string, ModelRate>
    {
        ["claude-opus-5"] = ModelRate.PerMillion(5, 25, 6.25, 0.5),
        ["claude-opus-5-5"] = ModelRate.PerMillion(4, 20, 5, 0.2),
        ["claude-sonnet-5"] = ModelRate.PerMillion(2, 10, 2.5, 0.2),
        ["claude-sonnet-5-5"] = ModelRate.PerMillion(2, 10, 2.5, 0.1),
        ["claude-opus-4-20250514"] = ModelRate.PerMillion(15, 75, 18.75, 1.5),
        ["claude-sonnet-4-20250514"] = ModelRate.PerMillion(3, 15, 3.75, 0.3),
        ["claude-sonnet-4-5-20250929"] = ModelRate.PerMillion(3, 15, 3.75, 0.3),
        ["claude-opus-4-8"] = ModelRate.PerMillion(5, 25, 6.25, 0.5),
        ["claude-opus-4-7"] = ModelRate.PerMillion(5, 25, 6.25, 0.5),
        ["claude-sonnet-4-6"] = ModelRate.PerMillion(3, 15, 3.75, 0.3),
        ["claude-haiku-4-5-20251001"] = ModelRate.PerMillion(1, 5, 1.25, 0.1),
        ["claude-fable-5"] = ModelRate.PerMillion(10, 50, 12.5, 1.0),
        ["claude-fable-5-1"] = ModelRate.PerMillion(10, 50, 12.5, 0.25),
        ["gpt-6-astra"] = ModelRate.PerMillion(10, 50, 12.5, 1),
        ["gpt-6-sol"] = ModelRate.PerMillion(2, 10, 2.5, 0.2),
        ["gpt-6.1-sol"] = ModelRate.PerMillion(2, 10, 2.5, 0.1),
        ["gpt-6-luna"] = ModelRate.PerMillion(0.1, 0.5, 0.125, 0.01),
        ["gpt-5.6-sol"] = ModelRate.PerMillion(4, 20, 5, 0.4),
        ["gpt-5.6-terra"] = ModelRate.PerMillion(2, 12, 2.5, 0.2),
        ["gpt-5.6-luna"] = ModelRate.PerMillion(0.2, 1.2, 0.25, 0.02),
        ["gpt-5"] = ModelRate.PerMillion(1.25, 10, 0, 0.125),
        ["gpt-5-codex"] = ModelRate.PerMillion(1.25, 10, 0, 0.125),
        ["gpt-5.1"] = ModelRate.PerMillion(1.25, 10, 0, 0.125),
        ["gpt-5.1-codex"] = ModelRate.PerMillion(1.25, 10, 0, 0.125),
        ["gpt-5.2"] = ModelRate.PerMillion(1.75, 14, 0, 0.175),
        ["gpt-5.2-codex"] = ModelRate.PerMillion(1.75, 14, 0, 0.175),
        ["gpt-5.3-codex"] = ModelRate.PerMillion(1.75, 14, 0, 0.175),
        ["gpt-5.4"] = ModelRate.PerMillion(2.5, 15, 0, 0.25),
        ["gpt-5.5"] = ModelRate.PerMillion(5, 30, 0, 0.5),
        ["gemini-2.5-pro"] = ModelRate.PerMillion(1.25, 10, 0, 0.125),
        ["gemini-2.5-flash"] = ModelRate.PerMillion(0.30, 2.5, 0, 0.03),
        ["gemini-2.5-flash-lite"] = ModelRate.PerMillion(0.10, 0.40, 0, 0.01),
        ["gemini-2.0-flash"] = ModelRate.PerMillion(0.10, 0.4, 0, 0.025),
    };

    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["gpt-5.6"] = "gpt-5.6-sol",
        ["claude-sonnet-4"] = "claude-sonnet-4-20250514",
        ["claude-opus-4"] = "claude-opus-4-20250514",
        ["claude-sonnet-4-5"] = "claude-sonnet-4-5-20250929",
        ["claude-haiku-4-5"] = "claude-haiku-4-5-20251001",
        ["gpt-5-2025-08-07"] = "gpt-5",
        ["gpt-5.1-2025-11-13"] = "gpt-5.1",
        ["gpt-5.2-2025-12-11"] = "gpt-5.2",
        ["gpt-5.4-2026-03-05"] = "gpt-5.4",
        ["gpt-5.5-2026-04-23"] = "gpt-5.5",
    };

    private static readonly HashSet<string> LongContextGpt =
    [
        "gpt-6-astra", "gpt-6-sol", "gpt-6.1-sol", "gpt-6-luna",
        "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4",
    ];

    private static string ModelKey(string model)
    {
        var key = model.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "openai/", "anthropic/", "google/", "models/" })
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                key = key[prefix.Length..];
                break;
            }
        }
        return Aliases.TryGetValue(key, out var alias) ? alias : key;
    }

    public static ModelRate Rate(string model) =>
        Table.TryGetValue(ModelKey(model), out var r) ? r : ModelRate.Zero;

    /// <summary>One request's disjoint token buckets. Null when the model is unpriced or a bucket is invalid.</summary>
    public static double? EstimatedCost(string model, long input, long output, long cacheWrite, long cacheRead)
    {
        var key = ModelKey(model);
        if (!Table.TryGetValue(key, out var r) || input < 0 || output < 0 || cacheWrite < 0 || cacheRead < 0)
            return null;
        // Zero in this column means no supported separate write rate, not free write tokens.
        if (!(cacheWrite == 0 || r.CacheWrite > 0)) return null;
        var prompt = (double)input + (double)cacheRead + (double)cacheWrite;
        var longContext = (LongContextGpt.Contains(key) && prompt > 272_000)
            || (key == "gemini-2.5-pro" && prompt > 200_000);
        var inputMultiplier = longContext ? 2.0 : 1.0;
        var outputMultiplier = longContext ? 1.5 : 1.0;
        return ((double)input * r.Input + (double)cacheWrite * r.CacheWrite
                + (double)cacheRead * r.CacheRead) * inputMultiplier
            + (double)output * r.Output * outputMultiplier;
    }

    private static readonly HashSet<string> UnpricedSeen = new();
    private static readonly object UnpricedLock = new();

    public static void NoteUnpriced(string model)
    {
        if (!FirstUnpricedSighting(model)) return;
        AppLog.Write($"Unpriced model: {model} — its cost shows as Unavailable until ModelPricing.table has a row");
    }

    public static bool FirstUnpricedSighting(string model)
    {
        var key = ModelKey(model);
        lock (UnpricedLock) return UnpricedSeen.Add(key);
    }

    public static double Cost(string model, long input, long output, long cacheWrite, long cacheRead) =>
        EstimatedCost(model, input, output, cacheWrite, cacheRead) ?? 0;
}
