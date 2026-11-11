using System.Globalization;
using System.Text.RegularExpressions;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of ModelPricingTests.swift plus a source-level parity check against the Swift table.</summary>
public class ModelPricingTests
{
    [Fact]
    public void CurrentClaudeStandardRates()
    {
        Assert.Equal(ModelRate.PerMillion(5, 25, 6.25, 0.5), ModelPricing.Rate("claude-opus-5"));
        Assert.Equal(ModelRate.PerMillion(4, 20, 5, 0.2), ModelPricing.Rate("claude-opus-5-5"));
        Assert.Equal(ModelRate.PerMillion(2, 10, 2.5, 0.2), ModelPricing.Rate("claude-sonnet-5"));
        Assert.Equal(ModelRate.PerMillion(2, 10, 2.5, 0.1), ModelPricing.Rate("claude-sonnet-5-5"));
    }

    [Fact]
    public void ModelsReadFromProviderLogsAreAllPriced()
    {
        foreach (var model in new[] { "claude-opus-5", "claude-opus-5-5", "claude-sonnet-5", "claude-sonnet-5-5",
                     "claude-opus-4-8", "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5",
                     "claude-fable-5", "claude-fable-5-1" })
        {
            var cost = ModelPricing.EstimatedCost(model, 2, 175, 26_939, 42_676);
            Assert.True(cost > 0, $"{model} resolves to no price");
        }
    }

    [Fact]
    public void PricingExactAndFallbackAndZero()
    {
        Assert.Equal(5.0, ModelPricing.Cost("claude-opus-4-8", 1_000_000, 0, 0, 0), 6);
        Assert.Equal(25.0, ModelPricing.Cost("claude-opus-4-8", 0, 1_000_000, 0, 0), 6);
        Assert.Equal(1.0, ModelPricing.Cost("claude-haiku-4-5-20251001", 1_000_000, 0, 0, 0), 6);
        Assert.Equal(73.5, ModelPricing.Cost("claude-fable-5", 1_000_000, 1_000_000, 1_000_000, 1_000_000), 6);
        Assert.Equal(ModelRate.PerMillion(10, 50, 12.5, 0.25), ModelPricing.Rate("claude-fable-5-1"));
        Assert.Equal(0.25, ModelPricing.Cost("claude-fable-5-1", 0, 0, 0, 1_000_000), 6);
        Assert.Equal(0, ModelPricing.Cost("claude-opus-4-99", 1_000_000, 0, 0, 0));
        Assert.Equal(0, ModelPricing.Cost("claude-fable-6", 1_000_000, 0, 0, 0));
        Assert.Equal(ModelRate.Zero, ModelPricing.Rate("claude-opus-6"));
        Assert.Equal(0, ModelPricing.Cost("totally-unknown", 1_000_000, 0, 0, 0));
    }

    [Fact]
    public void UnpricedModelIsReportedOnlyOnFirstSighting()
    {
        var model = "claude-unpriced-" + Guid.NewGuid().ToString().ToLowerInvariant();
        Assert.True(ModelPricing.FirstUnpricedSighting(model));
        Assert.False(ModelPricing.FirstUnpricedSighting(model));
        Assert.False(ModelPricing.FirstUnpricedSighting("ANTHROPIC/" + model.ToUpperInvariant()));
    }

    [Fact]
    public void AggregatingAnUnpricedModelReportsItButNotASourceUnavailableRow()
    {
        UsageEntry E(string model, bool? unavailable = null) => new()
        {
            Id = Guid.NewGuid().ToString(), Date = DateTimeOffset.Now, LocalDay = "2026-09-25", Model = model,
            Input = 10, Output = 5, CostUnavailable = unavailable,
        };
        var unpriced = "claude-unpriced-" + Guid.NewGuid().ToString().ToLowerInvariant();
        var sourceUnavailable = "claude-unpriced-" + Guid.NewGuid().ToString().ToLowerInvariant();
        var bucket = new UsageBucket();
        bucket.Add(E(unpriced));
        bucket.Add(E(sourceUnavailable, true));
        Assert.False(ModelPricing.FirstUnpricedSighting(unpriced));
        Assert.True(ModelPricing.FirstUnpricedSighting(sourceUnavailable));
    }

    [Fact]
    public void UnknownNamesNeverBorrowFamilyPrices()
    {
        foreach (var model in new[] { "gpt-5.3-codex-spark", "gpt-99", "codex", "codex-auto-review", "o3", "o4",
                     "grok-codex-next", "claude-opus-4-99", "claude-fable-6", "gemini-99-pro", "custom/claude-opus-4-8",
                     "antigravity/claude-opus-4-8", "gpt-5.5-pro", "gpt-5.5-2026-99-99" })
        {
            Assert.Null(ModelPricing.EstimatedCost(model, 100, 20, 0, 40));
            Assert.Equal(0, ModelPricing.Cost(model, 100, 20, 0, 40));
        }
    }

    [Fact]
    public void ExplicitAliasesAndProviderPrefixes()
    {
        Assert.Equal(ModelPricing.Rate("gpt-5.4"), ModelPricing.Rate("openai/gpt-5.4-2026-03-05"));
        Assert.Equal(ModelRate.PerMillion(10, 50, 12.5, 0.25), ModelPricing.Rate(" ANTHROPIC/CLAUDE-FABLE-5-1 "));
        Assert.Equal(ModelPricing.Rate("gemini-2.5-pro"), ModelPricing.Rate("models/gemini-2.5-pro"));
    }

    [Fact]
    public void RequestContextThresholdIncludesCacheRead()
    {
        Assert.Equal(0.175, ModelPricing.EstimatedCost("gpt-5.5", 2_000, 1_000, 0, 270_000)!.Value, 12);
        Assert.Equal(0.33501, ModelPricing.EstimatedCost("gpt-5.5", 2_001, 1_000, 0, 270_000)!.Value, 12);
        Assert.Equal(0.0551502, ModelPricing.EstimatedCost("gpt-6-luna", 272_001, 1_000, 0, 0)!.Value, 12);
        Assert.Equal(1.103004, ModelPricing.EstimatedCost("gpt-6-sol", 272_001, 1_000, 0, 0)!.Value, 12);
        Assert.Equal(0.525, ModelPricing.EstimatedCost("gpt-5.3-codex", 300_000, 0, 0, 0)!.Value, 12);
        Assert.Equal(0.2900025, ModelPricing.EstimatedCost("gemini-2.5-pro", 100_001, 1_000, 0, 100_000)!.Value, 12);
    }

    [Fact]
    public void UnsupportedCacheWriteRateIsUnavailableInsteadOfFree()
    {
        foreach (var model in new[] { "gpt-5.5", "gpt-5.3-codex", "gemini-2.5-pro" })
            Assert.Null(ModelPricing.EstimatedCost(model, 0, 0, 100_000, 0));
        Assert.Equal(1.25, ModelPricing.EstimatedCost("gpt-6-astra", 0, 0, 100_000, 0)!.Value, 12);
        Assert.Null(ModelPricing.EstimatedCost("gpt-5.5", -1, 0, 0, 0));
        Assert.Equal(0, ModelPricing.EstimatedCost("gpt-5.5", 0, 0, 0, 0));
    }

    /// <summary>
    /// Parity guard: the C# table and aliases must equal the Swift source exactly. A price added on
    /// macOS without the Windows port (or the reverse) fails here instead of silently diverging.
    /// </summary>
    [Fact]
    public void TableMatchesSwiftSource()
    {
        var swift = File.ReadAllText(Path.Combine(Fixtures.RepoRoot(), "Sources", "PokeTokenBar", "Core", "ModelPricing.swift"));
        var rows = Regex.Matches(swift, @"""([^""]+)"":\s*\.perMillion\(([^)]*)\)");
        var swiftTable = rows.ToDictionary(m => m.Groups[1].Value, m =>
        {
            var n = m.Groups[2].Value.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            return ModelRate.PerMillion(n[0], n[1], n[2], n[3]);
        });
        Assert.NotEmpty(swiftTable);
        Assert.Equal(swiftTable.OrderBy(kv => kv.Key), ModelPricing.Table.OrderBy(kv => kv.Key));

        const string aliasHeader = "aliases: [String: String] = [";
        var aliasBlock = swift[(swift.IndexOf(aliasHeader, StringComparison.Ordinal) + aliasHeader.Length)..];
        aliasBlock = aliasBlock[..aliasBlock.IndexOf(']')];
        var swiftAliases = Regex.Matches(aliasBlock, @"""([^""]+)"":\s*""([^""]+)""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.Equal(swiftAliases.OrderBy(kv => kv.Key), ModelPricing.Aliases.OrderBy(kv => kv.Key));
    }
}
