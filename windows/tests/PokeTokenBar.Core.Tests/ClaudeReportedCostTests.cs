using System.Globalization;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of ClaudeReportedCostTests.swift.</summary>
public class ClaudeReportedCostTests
{
    private static string Assistant(string id, string model, string ts, long input = 0, long output = 0) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + (ts) + "\",\"requestId\":\"r-" + (id) + "\",\"message\":{\"id\":\"" + (id) + "\",\"model\":\"" + (model) + "\",\"usage\":{\"input_tokens\":" + (input) + ",\"output_tokens\":" + (output) + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    private static string CostState(Dictionary<string, double> models, bool unknown = false)
    {
        var usage = string.Join(",", models.Select(kv =>
            $"\"{kv.Key}\":{{\"costUSD\":{kv.Value.ToString(CultureInfo.InvariantCulture)}}}"));
        var total = models.Values.Sum().ToString(CultureInfo.InvariantCulture);
        return $"{{\"type\":\"cost-state\",\"totalCostUSD\":{total},\"modelUsage\":{{{usage}}},\"hasUnknownModelCost\":{(unknown ? "true" : "false")}}}";
    }

    private static List<UsageEntry> Parse(params string[] lines)
    {
        using var dir = new TempDir();
        return LocalUsageReader.ParseClaudeFile(dir.Write(lines));
    }

    private const string Unpriced = "claude-future-unpriced-model";

    [Fact]
    public async Task IncrementalAppendReattributesTheWholeSessionToLatestLedger()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "session.jsonl");
        var prefix = Assistant("a", Unpriced, "2026-09-15T10:00:00.000Z", 10) + "\n" + CostState(new() { [Unpriced] = 1 }) + "\n";
        File.WriteAllText(file, prefix);
        var cache = new LocalUsageCache([dir.Path], Path.Combine(dir.Path, "cache.json"));
        Assert.Equal(1, Fixtures.Bucket(cache.ClaudeEntries(DateTimeOffset.MinValue)).Cost, 9);

        File.WriteAllText(file, prefix + Assistant("b", Unpriced, "2026-09-16T10:00:00.000Z", 10) + "\n"
            + CostState(new() { [Unpriced] = 9 }) + "\n");
        var appended = cache.ClaudeEntries(DateTimeOffset.MinValue);
        Assert.Equal(2, appended.Count);
        Assert.Equal(9, Fixtures.Bucket(appended).Cost, 9);
        Assert.All(appended, e => { Assert.Equal(4.5, e.ExplicitCost); Assert.Equal("session", e.SessionID); });

        var unchanged = cache.ClaudeEntries(DateTimeOffset.MinValue);
        Assert.Equal(9, Fixtures.Bucket(unchanged).Cost, 9);
        Assert.All(unchanged, e => Assert.Equal(4.5, e.ExplicitCost));
        await Task.CompletedTask;
    }

    [Fact]
    public void PersistedLedgerIsRetainedWhenTheAppendedTailHasNoLedger()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "session.jsonl");
        var cacheFile = Path.Combine(dir.Path, "cache.json");
        var prefix = Assistant("a", Unpriced, "2026-09-15T10:00:00.000Z", 10) + "\n" + CostState(new() { [Unpriced] = 4 }) + "\n";
        File.WriteAllText(file, prefix);
        new LocalUsageCache([dir.Path], cacheFile).ClaudeEntries(DateTimeOffset.MinValue);

        var reloaded = new LocalUsageCache([dir.Path], cacheFile);
        File.WriteAllText(file, prefix + Assistant("b", Unpriced, "2026-09-16T10:00:00.000Z", 10) + "\n");
        var entries = reloaded.ClaudeEntries(DateTimeOffset.MinValue);
        Assert.Equal(2, entries.Count);
        Assert.Equal(4, Fixtures.Bucket(entries).Cost, 9);
        Assert.All(entries, e => Assert.Equal(2, e.ExplicitCost));
    }

    [Fact]
    public void TruncationDiscardsTheCachedLedger()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "session.jsonl");
        var line = Assistant("a", Unpriced, "2026-09-15T10:00:00.000Z", 10);
        File.WriteAllText(file, line + "\n" + CostState(new() { [Unpriced] = 9 }) + "\n");
        var cache = new LocalUsageCache([dir.Path], Path.Combine(dir.Path, "cache.json"));
        cache.ClaudeEntries(DateTimeOffset.MinValue);

        File.WriteAllText(file, line + "\n");
        var entries = cache.ClaudeEntries(DateTimeOffset.MinValue);
        Assert.Single(entries);
        Assert.Null(entries[0].ExplicitCost);
        Assert.True(Fixtures.Bucket(entries).CostCoverage.Unknown);
    }

    [Fact]
    public void HalfWrittenCostStateIsRetriedOnAppend()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "session.jsonl");
        var prefix = Assistant("a", Unpriced, "2026-09-15T10:00:00.000Z", 10) + "\n";
        var ledger = CostState(new() { [Unpriced] = 9 });
        File.WriteAllText(file, prefix + ledger[..(ledger.Length / 2)]);
        var cache = new LocalUsageCache([dir.Path], Path.Combine(dir.Path, "cache.json"));
        Assert.Null(cache.ClaudeEntries(DateTimeOffset.MinValue)[0].ExplicitCost);

        File.WriteAllText(file, prefix + ledger + "\n");
        var completed = cache.ClaudeEntries(DateTimeOffset.MinValue);
        Assert.Equal(9, Fixtures.Bucket(completed).Cost, 9);
        Assert.Single(completed);
    }

    [Fact]
    public void UnpricedModelUsesReportedCost()
    {
        Assert.Null(ModelPricing.EstimatedCost(Unpriced, 10, 10, 0, 0));
        var b = Fixtures.Bucket(Parse(
            Assistant("a", Unpriced, "2026-09-15T10:00:00.000Z", 100, 50),
            CostState(new() { [Unpriced + "[1m]"] = 12.5 })));
        Assert.Equal(12.5, b.Cost, 9);
        Assert.True(b.CostCoverage.Reported);
        Assert.False(b.CostCoverage.Unknown);
    }

    [Fact]
    public void ContextWindowVariantMatchesBaseModel()
    {
        var entries = Parse(
            Assistant("a", "claude-opus-5", "2026-09-15T10:00:00.000Z", 10, 10),
            CostState(new() { ["claude-opus-5[1m]"] = 4, ["claude-opus-5"] = 1 }));
        Assert.Equal(5, Fixtures.Bucket(entries).Cost, 9);
    }

    [Fact]
    public void ReportedCostOverridesTableEstimate()
    {
        const string model = "claude-haiku-4-5-20251001";
        Assert.Equal(1.0, ModelPricing.EstimatedCost(model, 1_000_000, 0, 0, 0)!.Value, 9);
        var entries = Parse(Assistant("a", model, "2026-09-15T10:00:00.000Z", 1_000_000), CostState(new() { [model] = 7.25 }));
        Assert.Equal(7.25, Fixtures.Bucket(entries).Cost, 9);
    }

    [Fact]
    public void LastCostStateWins()
    {
        var entries = Parse(
            Assistant("a", "claude-opus-5", "2026-09-15T10:00:00.000Z", 10),
            CostState(new() { ["claude-opus-5"] = 1 }),
            Assistant("b", "claude-opus-5", "2026-09-15T11:00:00.000Z", 10),
            CostState(new() { ["claude-opus-5"] = 9 }));
        Assert.Equal(9, Fixtures.Bucket(entries).Cost, 9);
    }

    [Fact]
    public void CostSplitsAcrossDaysAndSumsExactly()
    {
        var entries = Parse(
            Assistant("a", "claude-opus-5", "2026-09-15T12:00:00.000Z", 300),
            Assistant("b", "claude-opus-5", "2026-09-16T12:00:00.000Z", 100),
            CostState(new() { ["claude-opus-5"] = 10 }));
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries.Select(e => e.LocalDay).Distinct().Count());
        Assert.Equal(10, entries.Sum(e => e.ExplicitCost ?? 0), 9);
        var sorted = entries.OrderBy(e => e.Date).ToList();
        Assert.Equal(7.5, sorted[0].ExplicitCost!.Value, 9);
        Assert.Equal(2.5, sorted[1].ExplicitCost!.Value, 9);
    }

    [Fact]
    public void UnattributableModelCostIsNotReassigned()
    {
        var entries = Parse(
            Assistant("a", "claude-opus-5", "2026-09-15T10:00:00.000Z", 100),
            CostState(new() { ["claude-opus-5"] = 2, ["claude-haiku-4-5-20251001"] = 98 }));
        Assert.Equal(2, Fixtures.Bucket(entries).Cost, 9);
    }

    [Fact]
    public void NoCostStateFallsBackToTable()
    {
        var pb = Fixtures.Bucket(Parse(Assistant("a", "claude-haiku-4-5-20251001", "2026-09-15T10:00:00.000Z", 1_000_000)));
        Assert.Equal(1.0, pb.Cost, 9);
        Assert.True(pb.CostCoverage.Estimated);

        var ub = Fixtures.Bucket(Parse(Assistant("a", Unpriced, "2026-09-15T10:00:00.000Z", 1_000_000)));
        Assert.Equal(0, ub.Cost);
        Assert.True(ub.CostCoverage.Unknown);
    }

    [Fact]
    public void ZeroTokenEntriesDoNotAbsorbCost()
    {
        var entries = Parse(
            Assistant("a", "claude-opus-5", "2026-09-15T10:00:00.000Z"),
            Assistant("b", "claude-opus-5", "2026-09-15T10:05:00.000Z", 500),
            CostState(new() { ["claude-opus-5"] = 6 }));
        Assert.Equal(6, Fixtures.Bucket(entries).Cost, 9);
    }
}
