using PokeTokenBar.Core;
using static PokeTokenBar.Core.Tests.Fixtures;

namespace PokeTokenBar.Core.Tests;

/// <summary>Port of the Claude cases in LocalUsageReaderTests.swift.</summary>
public class LocalUsageReaderTests
{
    [Fact]
    public void ClaudeDedupKeepsMaxOutput()
    {
        using var dir = new TempDir();
        const string ts = "2026-06-30T10:00:00.000Z";
        dir.Write([
            ClaudeLine("A", "R1", "claude-opus-4-8", ts, 100, 5, 0, 1000),
            ClaudeLine("A", "R1", "claude-opus-4-8", ts, 100, 200, 0, 1000),
            ClaudeLine("B", "R2", "claude-sonnet-4-6", ts, 50, 10, 0, 0),
        ], sub: Path.Combine("proj", "sub"));
        var entries = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path]);
        Assert.Equal(2, entries.Count);
        var a = entries.Single(e => e.Id.StartsWith("A|", StringComparison.Ordinal));
        Assert.Equal(200, a.Output);
        Assert.Equal(1000, a.CacheRead);
    }

    [Fact]
    public void ClaudeDailyAndCost()
    {
        using var dir = new TempDir();
        const string ts = "2026-06-30T10:00:00.000Z";
        var day = LocalUsageReader.LocalDay(Iso8601Parser.Date(ts)!.Value);
        dir.Write([ClaudeLine("A", "R1", "claude-opus-4-8", ts, 1_000_000, 0, 0, 0)], sub: "p");
        var entries = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path]);
        var d = LocalUsageReader.Daily(entries, day);
        Assert.Equal(1_000_000, d!.TotalTokens);
        Assert.Equal(5.0, d.TotalCost, 6);
        Assert.Null(LocalUsageReader.Daily(entries, "2000-01-01"));
    }

    [Fact]
    public void HiddenFilesAndDirectoriesAreSkippedLikeMacOS()
    {
        using var dir = new TempDir();
        const string ts = "2026-06-30T10:00:00.000Z";
        dir.Write([ClaudeLine("A", "R1", "claude-opus-4-8", ts, 1, 0, 0, 0)], sub: "p");
        dir.Write([ClaudeLine("B", "R2", "claude-opus-4-8", ts, 1, 0, 0, 0)], sub: ".hidden");
        dir.Write([ClaudeLine("C", "R3", "claude-opus-4-8", ts, 1, 0, 0, 0)], name: ".x.jsonl", sub: "p");
        dir.Write([ClaudeLine("D", "R4", "claude-opus-4-8", ts, 1, 0, 0, 0)], name: "meta.json", sub: "p");
        var entries = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path]);
        Assert.Equal(["A|R1"], entries.Select(e => e.Id));
    }

    [Fact]
    public void FileHeldOpenForWritingByClaudeCodeIsStillRead()
    {
        // Windows-specific: Claude Code keeps the active transcript open. A reader that does not
        // share write access fails with a sharing violation and silently drops today's usage.
        using var dir = new TempDir();
        var file = dir.Write([ClaudeLine("A", "R1", "claude-opus-4-8", "2026-06-30T10:00:00.000Z", 7, 0, 0, 0) + "\n"]);
        using var writer = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var entries = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path]);
        Assert.Equal(7, entries.Single().Input);
    }

    [Fact]
    public void CrlfLineEndingsParse()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "s.jsonl"),
            ClaudeLine("A", "R1", "claude-opus-4-8", "2026-06-30T10:00:00.000Z", 3, 0, 0, 0) + "\r\n"
            + ClaudeLine("B", "R2", "claude-opus-4-8", "2026-06-30T10:00:00.000Z", 4, 0, 0, 0) + "\r\n");
        Assert.Equal(7, LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path]).Sum(e => e.Total));
    }

    [Fact]
    public void EmbeddedRootsFindHiddenClaudeProjectsDirs()
    {
        using var dir = new TempDir();
        var session = Path.Combine(dir.Path, "2eb6d133", "a3a236da", "local_35a9f8a7");
        var projects = Path.Combine(session, ".claude", "projects");
        Directory.CreateDirectory(projects);
        Directory.CreateDirectory(Path.Combine(session, "uploads"));
        Assert.Equal([projects], LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path));
    }

    [Fact]
    public void EmbeddedRootsIgnoreMissingBaseAndDepthLimit()
    {
        Assert.Empty(LocalUsageReader.EmbeddedClaudeProjectRoots(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid())));
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "a", "b", "c", "d", "e", "f", "g", ".claude", "projects"));
        Assert.Empty(LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path, maxDepth: 4));
    }

    [Fact]
    public void EmbeddedRootsDepthBoundaryMatchesRealLayoutWithHeadroom()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "2eb6d133", "a3a236da", "local_35a9f8a7", ".claude", "projects"));
        Assert.Empty(LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path, maxDepth: 4));
        Assert.Single(LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path, maxDepth: 5));
        Assert.Single(LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path));

        Directory.CreateDirectory(Path.Combine(dir.Path, "2eb6d133", "a3a236da", "local_35a9f8a7", "outputs", "myrepo", ".claude", "projects"));
        Assert.Equal(2, LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path).Count);
        Assert.Single(LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path, maxDepth: 6));
    }

    [Fact]
    public void EmbeddedRootsDoNotDescendIntoBulkDirectories()
    {
        using var dir = new TempDir();
        var session = Path.Combine(dir.Path, "s1", "s2", "local_x");
        Directory.CreateDirectory(Path.Combine(session, ".claude", "projects"));
        Directory.CreateDirectory(Path.Combine(session, "node_modules", "pkg", ".claude", "projects"));
        var found = LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path);
        Assert.Single(found);
        Assert.DoesNotContain("node_modules", found[0]);
    }

    [Fact]
    public void EmbeddedRootsFindRootsUnderWorkDirectoryNames()
    {
        using var dir = new TempDir();
        var session = Path.Combine(dir.Path, "u1", "u2", "local_x");
        foreach (var work in new[] { Path.Combine("outputs", "myrepo"), Path.Combine("uploads", "repo2"), "build", "target" })
            Directory.CreateDirectory(Path.Combine(session, work, ".claude", "projects"));
        var found = LocalUsageReader.EmbeddedClaudeProjectRoots(dir.Path);
        foreach (var work in new[] { "outputs", "uploads", "build", "target" })
            Assert.Contains(found, f => f.Contains(Path.DirectorySeparatorChar + work + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ConfigDirParsingHandlesCommasWhitespaceAndTilde()
    {
        const string home = @"C:\Users\testhome";
        var roots = LocalUsageReader.ComputeClaudeProjectRoots(@" C:\a\one , ,~/two ", home: home, desktopDataDir: @"C:\nonexistent-desktop");
        Assert.Contains(@"C:\a\one\projects", roots);
        Assert.Contains(@"C:\Users\testhome\two\projects", roots);
        Assert.DoesNotContain(roots, r => r.TrimEnd('\\').EndsWith(":\\projects", StringComparison.Ordinal) || r == @"\projects");
        Assert.Contains(@"C:\Users\testhome\.claude\projects", roots);
        Assert.Contains(@"C:\Users\testhome\.config\claude\projects", roots);
        var none = LocalUsageReader.ComputeClaudeProjectRoots(null, home: home, desktopDataDir: @"C:\nonexistent-desktop");
        Assert.DoesNotContain(none, r => r.StartsWith(@"C:\a\one", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizedRootsDropsDuplicatesAndNestedRootsKeepingOrder()
    {
        string[] roots =
        [
            @"C:\Users\x\.claude\projects",
            @"C:\Users\x\.config\claude\projects",
            @"C:\Users\x\.claude\projects",
            @"C:\Users\x\.claude\projects\sub",
            @"C:\Users\x\.claude\projects-other",
            @"C:\Users\x\.CLAUDE\Projects",   // NTFS is case-insensitive
        ];
        Assert.Equal(
            [@"C:\Users\x\.claude\projects", @"C:\Users\x\.config\claude\projects", @"C:\Users\x\.claude\projects-other"],
            LocalUsageReader.NormalizedRoots(roots));
    }

    [Fact]
    public void NormalizedRootsFoldJunctionDuplicates()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real");
        Directory.CreateDirectory(Path.Combine(real, "projects"));
        var link = Path.Combine(dir.Path, "linked");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (IOException) { return; }                    // symlink privilege not held: nothing to assert
        catch (UnauthorizedAccessException) { return; }
        var folded = LocalUsageReader.NormalizedRoots([real, link]);
        Assert.Single(folded);
    }

    [Fact]
    public void MultipleRootsSumButShareGlobalDedup()
    {
        using var cli = new TempDir();
        using var desktop = new TempDir();
        const string ts = "2026-06-30T10:00:00.000Z";
        cli.Write([ClaudeLine("A", "R1", "claude-opus-4-8", ts, 100, 10, 0, 0)], sub: "p");
        desktop.Write([
            ClaudeLine("A", "R1", "claude-opus-4-8", ts, 100, 10, 0, 0),
            ClaudeLine("B", "R2", "claude-opus-4-8", ts, 7, 3, 0, 0),
        ], sub: "p");
        var entries = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [cli.Path, desktop.Path]);
        Assert.Equal(2, entries.Count);
        Assert.Equal(120, entries.Sum(e => e.Total));
        Assert.Single(LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [cli.Path]));
    }

    [Fact]
    public void DefaultRootsContainCLIPathAndAreUnique()
    {
        const string home = @"C:\Users\testhome";
        var roots = LocalUsageReader.ComputeClaudeProjectRoots(null, home: home, desktopDataDir: @"C:\nonexistent-desktop");
        Assert.Contains(Path.Combine(home, ".claude", "projects"), roots);
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PeriodAndActiveBlock()
    {
        var today = DateTimeOffset.Now;
        var now = new DateTimeOffset(today.Year, today.Month, today.Day, 12, 0, 0, today.Offset);
        var recent = now.AddMinutes(-30);
        var old = now.AddHours(-10);
        var entries = new[] { Entry(recent, 600_000), Entry(old, 999) };
        var block = LocalUsageReader.ActiveBlock(entries, now);
        Assert.Equal(600_000, block!.TotalTokens);
        Assert.True(block.IsActive);
        Assert.True(block.TokensPerMinute > 0);
        var day = LocalUsageReader.LocalDay(now);
        Assert.True(LocalUsageReader.Period(entries, "w", day, day).TotalTokens >= 600_000);
    }

    [Fact]
    public void ActiveBlockIgnoresZeroTokenSyntheticEntries()
    {
        var now = DateTimeOffset.Now;
        var synthetic = Entry(now.AddSeconds(-120), 0, "<synthetic>", "synthetic");
        Assert.Null(LocalUsageReader.ActiveBlock([synthetic], now));
        var actual = Entry(now.AddSeconds(-60), 10, "claude-opus", "actual", output: 5);
        var block = LocalUsageReader.ActiveBlock([synthetic, actual], now);
        Assert.Equal(15, block!.TotalTokens);
        Assert.Equal($"block-{actual.Date.ToUnixTimeSeconds()}", block.Id);
    }

    [Fact]
    public void EnrichmentScanStartCoversAllWindows()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        DateTimeOffset? straddle = null;
        for (var m = 1; m <= 12; m++)
        {
            var candidate = new DateTimeOffset(2026, m, 1, 2, 0, 0, TimeSpan.FromHours(9));
            if (LocalUsageReader.StartOfWeek(candidate, tz) < LocalUsageReader.StartOfMonth(candidate, tz)) { straddle = candidate; break; }
        }
        var now = straddle!.Value;
        var scan = LocalUsageReader.EnrichmentScanStart(now, tz);
        Assert.True(scan <= LocalUsageReader.StartOfMonth(now, tz));
        Assert.True(scan <= LocalUsageReader.StartOfWeek(now, tz));
        Assert.True(scan <= now - LocalUsageReader.BlockWindow);
        Assert.True(scan <= now - LocalUsageReader.RecentUseWindow);
        // The 21-day rolling trend must not be clipped by the mtime filter either.
        Assert.True(scan <= LocalUsageReader.StartOfRollingWindow(LocalUsageReader.MaxDailyTrendWindowDays, now, tz));
        Assert.True(scan < LocalUsageReader.StartOfMonth(now, tz));

        // Late in the month (28th) the month start is earlier than the 21-day start: the floor is the month start.
        var late = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal(LocalUsageReader.StartOfMonth(late, tz), LocalUsageReader.EnrichmentScanStart(late, tz));

        // Mid-month (20th): the 21-day window starts on 6/30, before the month start (7/1).
        var mid = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal(LocalUsageReader.StartOfRollingWindow(LocalUsageReader.MaxDailyTrendWindowDays, mid, tz),
            LocalUsageReader.EnrichmentScanStart(mid, tz));
    }

    [Fact]
    public void ClaudeParsingClampsAbsurdTokenCountsInsteadOfTrapping()
    {
        using var dir = new TempDir();
        dir.Write(["""{"type":"assistant","requestId":"r1","timestamp":"2026-06-30T10:00:00.000Z","message":{"id":"m1","model":"claude","usage":{"input_tokens":1e30,"output_tokens":1e30,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}}"""], "a.jsonl");
        var e = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path]).Single();
        Assert.Equal(LocalUsageReader.MaxParsedTokenValue, e.Input);
        Assert.Equal(LocalUsageReader.MaxParsedTokenValue, e.Output);
    }

    [Fact]
    public void ParsingStillFoldsMissingAndNegativeToZero()
    {
        using var dir = new TempDir();
        dir.Write(["""{"type":"assistant","requestId":"r2","timestamp":"2026-06-30T10:00:00.000Z","message":{"id":"m2","model":"claude","usage":{"input_tokens":-5,"output_tokens":null,"cache_read_input_tokens":"nope"}}}"""], "b.jsonl");
        var e = LocalUsageReader.ClaudeEntries(DateTimeOffset.MinValue, [dir.Path]).Single();
        Assert.Equal(0, e.Input);
        Assert.Equal(0, e.Output);
        Assert.Equal(0, e.CacheRead);
    }

    [Fact]
    public void SubagentTranscriptCountsWithItsParentSession()
    {
        Assert.Equal("sess", LocalUsageReader.ClaudeSessionID(Path.Combine("p", "sess", "subagents", "agent-1.jsonl")));
        Assert.Equal("sess", LocalUsageReader.ClaudeSessionID(Path.Combine("p", "sess.jsonl")));
    }

    [Fact]
    public void DayDifferenceCalculatesCalendarDaysCorrectly()
    {
        Assert.Equal(3, LocalUsageReader.DayDifference("2026-10-01", "2026-10-04"));
        Assert.Equal(-3, LocalUsageReader.DayDifference("2026-10-04", "2026-10-01"));
        Assert.Equal(0, LocalUsageReader.DayDifference("2026-10-01", "2026-10-01"));
        Assert.Equal(7, LocalUsageReader.DayDifference("2026-09-25", "2026-10-02"));
        Assert.Null(LocalUsageReader.DayDifference("invalid", "2026-10-01"));
    }

    [Fact]
    public void RollingDailySeriesReturnsExactWindowCountAndFillsMissingDaysWithZero()
    {
        static DateTimeOffset Midnight(int y, int m, int d)
        {
            var local = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        static UsageEntry E(string id, DateTimeOffset at, long input, long output) => new()
        {
            Id = id, Date = at, LocalDay = LocalUsageReader.LocalDay(at), Model = "test", Input = input, Output = output,
        };
        var now = Midnight(2026, 10, 15);
        var entries = new[]
        {
            E("e1", Midnight(2026, 10, 14), 100, 50),
            E("e2", Midnight(2026, 10, 15), 200, 80),
            E("e0", Midnight(2026, 9, 1), 500, 50),
        };

        var series7 = LocalUsageReader.RollingDailySeries(entries, 7, now);
        Assert.Equal(7, series7.Count);
        Assert.Equal("2026-10-09", series7[0].Date);
        Assert.Equal("2026-10-15", series7[^1].Date);
        Assert.Equal(280, series7[^1].TotalTokens);
        Assert.Equal("2026-10-14", series7[5].Date);
        Assert.Equal(150, series7[5].TotalTokens);
        Assert.Equal(0, series7[0].TotalTokens);   // missing days in the window are explicit zeros

        var series14 = LocalUsageReader.RollingDailySeries(entries, 14, now);
        Assert.Equal(14, series14.Count);
        Assert.Equal("2026-10-02", series14[0].Date);
        Assert.Equal("2026-10-15", series14[^1].Date);

        var series21 = LocalUsageReader.RollingDailySeries(entries, 21, now);
        Assert.Equal(21, series21.Count);
        Assert.Equal("2026-09-25", series21[0].Date);
        Assert.Equal("2026-10-15", series21[^1].Date);
    }
}
