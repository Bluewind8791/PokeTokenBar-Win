using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of the Claude Code half of <c>LocalUsageReader.swift</c>: reads
/// <c>~/.claude/projects/**/*.jsonl</c> <c>type:"assistant"</c> lines (four token buckets,
/// <c>message.model</c>, <c>message.id</c>+<c>requestId</c>, <c>timestamp</c>) and folds them
/// into daily / period / block totals. Same method names as Swift so the two can be read side by side.
/// </summary>
public static class LocalUsageReader
{
    /// <summary>5-hour rolling window shared by the active block (burn rate) and the enrichment scan floor.</summary>
    public static readonly TimeSpan BlockWindow = TimeSpan.FromHours(5);
    /// <summary>A provider used within this window keeps its tab on idle days (#336).</summary>
    public static readonly TimeSpan RecentUseWindow = TimeSpan.FromDays(7);

    // MARK: Paths

    public const string DefaultRelativeProjectsPath = ".claude/projects";
    public const string ConfigRelativeProjectsPath = ".config/claude/projects";

    /// <summary>Directories never descended into while looking for Claude Desktop embedded roots.</summary>
    private static readonly HashSet<string> RootScanSkippedDirectories = ["node_modules", ".git", "venv", ".venv"];

    /// <summary>Every projects root that can hold Claude usage logs. Cached for 300 s like Swift's RootsCache.</summary>
    public static IReadOnlyList<string> ClaudeProjectRoots => RootsCache.Roots();

    public static void InvalidateProjectRootsCache() => RootsCache.Invalidate();

    /// <summary>
    /// Same order as Swift: <c>CLAUDE_CONFIG_DIR</c> entries, other accounts, <c>~/.config/claude</c>,
    /// <c>~/.claude</c>, Claude Desktop embedded sessions, then user-added folders.
    /// Claude Desktop on Windows keeps its data under <c>%APPDATA%\Claude</c>.
    /// </summary>
    public static List<string> ComputeClaudeProjectRoots(
        string? configDirValue,
        string? customRootsValue = null,
        IEnumerable<string>? accountRoots = null,
        string? home = null,
        string? desktopDataDir = null)
    {
        home ??= AppEnv.HomeDirectory;
        var roots = new List<string>();
        if (configDirValue != null)
        {
            foreach (var part in configDirValue.Split(','))
            {
                var path = part.Trim();
                if (path.Length == 0) continue;
                roots.Add(Path.Combine(ExpandTilde(path, home), "projects"));
            }
        }
        roots.AddRange((accountRoots ?? []).Select(r => Path.Combine(r, "projects")));
        roots.Add(Path.Combine(home, ".config", "claude", "projects"));
        roots.Add(Path.Combine(home, ".claude", "projects"));

        desktopDataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        foreach (var store in new[] { "local-agent-mode-sessions", "claude-code-sessions" })
            roots.AddRange(EmbeddedClaudeProjectRoots(Path.Combine(desktopDataDir, store)));

        return CustomScanRoots.Union(roots, customRootsValue);
    }

    internal static string ExpandTilde(string path, string home)
    {
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, path[2..]);
        return path;
    }

    private static class RootsCache
    {
        private static readonly object Gate = new();
        private static List<string>? _cached;
        private static DateTimeOffset _computedAt;
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(300);

        public static IReadOnlyList<string> Roots()
        {
            lock (Gate)
            {
                if (_cached != null && DateTimeOffset.UtcNow - _computedAt < Ttl) return _cached;
            }
            // Computed outside the lock: idempotent, and a filesystem walk must not block parallel callers.
            var fresh = ComputeClaudeProjectRoots(
                UsageEnvironment.Value("CLAUDE_CONFIG_DIR"),
                CustomScanRoots.StoredValue("claude_code"),
                ClaudeAccountRoots.InstalledAccountRoots());
            lock (Gate)
            {
                _cached = fresh;
                _computedAt = DateTimeOffset.UtcNow;
            }
            return fresh;
        }

        public static void Invalidate()
        {
            lock (Gate) _cached = null;
        }
    }

    /// <summary>
    /// Finds <c>.claude/projects</c> directories inside a Claude Desktop session store
    /// (<c>&lt;store&gt;/&lt;uuid&gt;/&lt;uuid&gt;/local_&lt;uuid&gt;/.claude/projects</c>). Hidden
    /// directories are visited on purpose — <c>.claude</c> is one. Depth 7 is the measured minimum
    /// that still reaches a repo under a session work directory.
    /// </summary>
    public static List<string> EmbeddedClaudeProjectRoots(string baseDir, int maxDepth = 7)
    {
        var output = new List<string>();
        if (!Directory.Exists(baseDir)) return output;
        var prunedByDepth = false;

        void Walk(string dir, int level)
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name == "projects" && Path.GetFileName(Path.GetDirectoryName(child)) == ".claude")
                {
                    output.Add(child);
                    continue;   // project logs below — not a root search target
                }
                if (level >= maxDepth) { prunedByDepth = true; continue; }
                if (RootScanSkippedDirectories.Contains(name)) continue;
                Walk(child, level + 1);
            }
        }

        Walk(baseDir, 1);
        if (prunedByDepth)
        {
            AppLog.WriteIfChanged($"claude-desktop-scan-{Path.GetFileName(baseDir)}",
                $"claude desktop scan: depth {maxDepth} reached under {Path.GetFileName(baseDir)}, found {output.Count} root(s) — deeper roots may be missed");
        }
        return output;
    }

    /// <summary>
    /// Drops duplicate and nested roots (links resolved, case-insensitive like NTFS), keeping the
    /// original order so priority is preserved.
    /// </summary>
    public static List<string> NormalizedRoots(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>();
        foreach (var root in roots)
        {
            var path = ResolvePath(root);
            if (seen.Add(path)) unique.Add(path);
        }
        var kept = new List<string>();
        foreach (var path in unique.OrderBy(p => p.Length))
        {
            var nested = kept.Any(k =>
                string.Equals(path, k, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(k.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (!nested) kept.Add(path);
        }
        return unique.Where(kept.Contains).ToList();
    }

    /// <summary>Full path with symbolic links and junctions resolved, no trailing separator.</summary>
    internal static string ResolvePath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        try
        {
            var info = new DirectoryInfo(full);
            if (info.Exists && info.LinkTarget != null && info.ResolveLinkTarget(true) is { } target)
                full = target.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    // MARK: Scan

    /// <summary>
    /// Recursive walk that skips dot-prefixed names below the root, matching macOS
    /// <c>.skipsHiddenFiles</c> on these trees.
    /// </summary>
    internal static IEnumerable<string> EnumerateFilesSkippingHidden(string root, Func<string, bool> include)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
                if (!Path.GetFileName(f).StartsWith('.') && include(f)) yield return f;
            foreach (var d in dirs)
                if (!Path.GetFileName(d).StartsWith('.')) stack.Push(d);
        }
    }

    public static List<string> JsonlFiles(string root, DateTimeOffset modifiedSince) =>
        EnumerateFilesSkippingHidden(root, f => f.EndsWith(".jsonl", StringComparison.Ordinal))
            .Where(f => File.GetLastWriteTimeUtc(f) >= modifiedSince.UtcDateTime)
            .ToList();

    // MARK: Claude parsing

    /// <summary>
    /// Streaming/resume logs the same <c>(message.id, requestId)</c> several times with growing
    /// output; keep the largest total, but the earliest timestamp (a fork may replay a turn later).
    /// </summary>
    public static List<UsageEntry> DedupKeepMax(IEnumerable<UsageEntry> entries)
    {
        var byID = new Dictionary<string, UsageEntry>();
        foreach (var e in entries)
        {
            if (!byID.TryGetValue(e.Id, out var existing))
            {
                byID[e.Id] = e;
                continue;
            }
            var kept = (e.Total > existing.Total ? e : existing) with { };
            var earliest = e.Date < existing.Date ? e : existing;
            kept.Date = earliest.Date;
            kept.LocalDay = earliest.LocalDay;
            var sessions = existing.ClaudeSessionIDs.Concat(e.ClaudeSessionIDs)
                .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            kept.SessionID = sessions.FirstOrDefault();
            kept.SessionIDs = sessions.Count > 1 ? sessions : null;
            byID[e.Id] = kept;
        }
        return byID.Values.ToList();
    }

    /// <summary>Claude Code's own cumulative cost ledger (<c>type:"cost-state"</c>).</summary>
    public sealed record ClaudeCostState
    {
        public Dictionary<string, double> CostByModel { get; init; } = new();
        public bool HasUnknownModelCost { get; init; }
    }

    /// <summary><c>claude-opus-5[1m]</c> → <c>claude-opus-5</c>: attribution matches the base id.</summary>
    public static string ClaudeCostModelKey(string model)
    {
        var cut = model.IndexOf('[');
        return cut < 0 ? model : model[..cut];
    }

    public static ClaudeCostState? ParseClaudeCostStateLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object
                || !obj.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "cost-state"
                || !obj.TryGetProperty("modelUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return null;
            var state = new ClaudeCostState
            {
                HasUnknownModelCost = obj.TryGetProperty("hasUnknownModelCost", out var unk) && BoolValue(unk),
            };
            foreach (var p in usage.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object
                    || !p.Value.TryGetProperty("costUSD", out var c)
                    || NumberValue(c) is not { } cost || !double.IsFinite(cost) || cost < 0) continue;
                var key = ClaudeCostModelKey(p.Name);
                state.CostByModel[key] = state.CostByModel.GetValueOrDefault(key) + cost;
            }
            return state.CostByModel.Count == 0 ? null : state;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Spreads each model's reported session total across that model's entries in proportion to
    /// tokens; the last entry absorbs the rounding remainder so the session sum is exact.
    /// </summary>
    public static List<UsageEntry> ApplyReportedCost(IReadOnlyList<UsageEntry> entries, ClaudeCostState state)
    {
        if (state.CostByModel.Count == 0) return entries.ToList();
        var indicesByModel = new Dictionary<string, List<int>>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Total <= 0) continue;
            var key = ClaudeCostModelKey(entries[i].Model);
            if (!indicesByModel.TryGetValue(key, out var list)) indicesByModel[key] = list = [];
            list.Add(i);
        }
        var output = entries.Select(e => e with { }).ToList();
        foreach (var (model, cost) in state.CostByModel)
        {
            if (!indicesByModel.TryGetValue(model, out var idx) || idx.Count == 0) continue;
            var totalTokens = idx.Sum(i => output[i].Total);
            if (totalTokens <= 0) continue;
            var remaining = cost;
            for (var n = 0; n < idx.Count; n++)
            {
                var i = idx[n];
                var share = n == idx.Count - 1
                    ? remaining
                    : cost * (double)output[i].Total / (double)totalTokens;
                output[i].ExplicitCost = Math.Max(0, share);
                output[i].CostIsEstimate = false;
                remaining -= share;
            }
        }
        return output;
    }

    public sealed record ParsedClaudeFile(List<UsageEntry> Entries, long NewOffset, ClaudeCostState? CostState);

    /// <summary>Parses one Claude file (in-file dedup, reported cost applied).</summary>
    public static List<UsageEntry> ParseClaudeFile(string path, TimeZoneInfo? timeZone = null)
    {
        var parsed = ParseClaudeFile(path, 0, timeZone);
        if (parsed == null) return [];
        return parsed.CostState == null ? parsed.Entries : ApplyReportedCost(parsed.Entries, parsed.CostState);
    }

    /// <summary>
    /// Parses only the bytes after <paramref name="fromOffset"/>. The file is opened with full
    /// sharing because Claude Code keeps appending to it while we read. Null when unreadable.
    /// </summary>
    public static ParsedClaudeFile? ParseClaudeFile(string path, long fromOffset, TimeZoneInfo? timeZone = null)
    {
        byte[] data;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fromOffset > fs.Length) return null;
            fs.Seek(fromOffset, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var (entries, consumed, costState) = ParseClaudeData(data, timeZone);
        var session = ClaudeSessionID(path);
        foreach (var e in entries) e.SessionID = session;
        return new ParsedClaudeFile(entries, fromOffset + consumed, costState);
    }

    private static readonly byte[] UsageNeedle = "\"usage\""u8.ToArray();
    private static readonly byte[] AssistantNeedle = "\"assistant\""u8.ToArray();
    private static readonly byte[] CostStateNeedle = "\"cost-state\""u8.ToArray();

    /// <summary>
    /// Byte-level line scan. <c>consumed</c> stops after the last newline; a trailing line without
    /// one is parsed now but re-read next time (dedup by id makes the repeat harmless).
    /// </summary>
    public static (List<UsageEntry> Entries, long Consumed, ClaudeCostState? CostState) ParseClaudeData(
        ReadOnlySpan<byte> data, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var output = new List<UsageEntry>();
        ClaudeCostState? costState = null;
        long consumed = 0;
        var lineStart = 0;

        void Parse(ReadOnlySpan<byte> line)
        {
            if (line.IndexOf(CostStateNeedle) >= 0)
            {
                if (ParseClaudeCostStateLine(Encoding.UTF8.GetString(line)) is { } state) costState = state;
                return;
            }
            if (line.IsEmpty || line.IndexOf(UsageNeedle) < 0 || line.IndexOf(AssistantNeedle) < 0) return;
            if (ParseClaudeLine(line, tz) is { } e) output.Add(e);
        }

        while (lineStart < data.Length)
        {
            var nl = data[lineStart..].IndexOf((byte)'\n');
            if (nl < 0) break;
            Parse(data.Slice(lineStart, nl));
            consumed = lineStart + nl + 1;
            lineStart += nl + 1;
        }
        if (lineStart < data.Length) Parse(data[lineStart..]);
        return (DedupKeepMax(output), consumed, costState);
    }

    /// <summary><c>&lt;project&gt;/&lt;session&gt;.jsonl</c>, or <c>&lt;project&gt;/&lt;session&gt;/subagents/&lt;agent&gt;.jsonl</c>.</summary>
    public static string? ClaudeSessionID(string transcriptPath)
    {
        var parent = Path.GetDirectoryName(transcriptPath) ?? "";
        var name = Path.GetFileName(parent) == "subagents"
            ? Path.GetFileName(Path.GetDirectoryName(parent) ?? "")
            : Path.GetFileNameWithoutExtension(transcriptPath);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>Uncached scan (tests, diagnostics). Global dedup makes overlapping roots harmless.</summary>
    public static List<UsageEntry> ClaudeEntries(DateTimeOffset modifiedSince, IEnumerable<string>? roots = null,
        TimeZoneInfo? timeZone = null)
    {
        var all = new List<UsageEntry>();
        foreach (var root in roots ?? ClaudeProjectRoots)
            foreach (var file in JsonlFiles(root, modifiedSince))
                all.AddRange(ParseClaudeFile(file, timeZone));
        return DedupKeepMax(all);
    }

    private static UsageEntry? ParseClaudeLine(ReadOnlySpan<byte> line, TimeZoneInfo tz)
    {
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (StringValue(obj, "type") != "assistant") return null;
            if (!obj.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
            if (StringValue(obj, "timestamp") is not { } ts || Iso8601Parser.Date(ts) is not { } date) return null;
            var model = StringValue(msg, "model") ?? "unknown";
            var id = (StringValue(msg, "id") ?? "") + "|" + (StringValue(obj, "requestId") ?? "");
            return new UsageEntry
            {
                Id = id,
                Date = date,
                LocalDay = LocalDay(date, tz),
                Model = model,
                Input = IntValue(usage, "input_tokens"),
                Output = IntValue(usage, "output_tokens"),
                CacheWrite = IntValue(usage, "cache_creation_input_tokens"),
                CacheRead = IntValue(usage, "cache_read_input_tokens"),
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;   // not JSON, or not valid UTF-8 — Foundation skips the line too
        }
    }

    // MARK: Aggregation

    /// <summary>Total for one local day, or null when that day has no tokens.</summary>
    public static DailyUsage? Daily(IEnumerable<UsageEntry> entries, string localDay, bool includeModels = false)
    {
        var b = new UsageBucket();
        Dictionary<string, long>? models = includeModels ? new() : null;
        foreach (var e in entries)
        {
            if (e.LocalDay != localDay) continue;
            b.Add(e);
            if (models != null) models[e.Model] = models.GetValueOrDefault(e.Model) + e.Total;
        }
        if (b.Total <= 0) return null;
        return new DailyUsage(localDay, b.Input, b.Output, b.CacheWrite, b.CacheRead, b.Total, b.Cost,
            models, b.CostCoverage);
    }

    /// <summary>Total for local days [fromDay, toDay] inclusive.</summary>
    public static PeriodUsage Period(IEnumerable<UsageEntry> entries, string periodKey, string fromDay, string toDay)
    {
        var b = new UsageBucket();
        foreach (var e in entries)
            if (string.CompareOrdinal(e.LocalDay, fromDay) >= 0 && string.CompareOrdinal(e.LocalDay, toDay) <= 0)
                b.Add(e);
        return new PeriodUsage(periodKey, b.Total, b.Cost, b.CostCoverage);
    }

    /// <summary>
    /// Day-by-day totals for the local days [startDay, endDay]. The axis is built from the range (entries
    /// outside it are dropped) and empty days are explicit zeros.
    /// </summary>
    public static List<DailyUsage> DailySeries(IEnumerable<UsageEntry> entries, DateTimeOffset startDay,
        DateTimeOffset endDay, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var days = new List<string>();
        var lastDay = TimeZoneInfo.ConvertTime(endDay, tz).Date;
        for (var cursor = TimeZoneInfo.ConvertTime(startDay, tz).Date; cursor <= lastDay; cursor = cursor.AddDays(1))
            days.Add(cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var inRange = days.ToHashSet();
        var buckets = new Dictionary<string, UsageBucket>();
        foreach (var e in entries)
        {
            if (!inRange.Contains(e.LocalDay)) continue;
            var b = buckets.GetValueOrDefault(e.LocalDay);
            b.Add(e);
            buckets[e.LocalDay] = b;
        }
        return days.Select(day =>
        {
            var b = buckets.GetValueOrDefault(day);
            return new DailyUsage(day, b.Input, b.Output, b.CacheWrite, b.CacheRead, b.Total, b.Cost,
                null, b.CostCoverage);
        }).ToList();
    }

    /// <summary>
    /// Day-by-day totals for the current month, month start through now. Cross-month sessions are
    /// truncated and empty days are explicit zeros.
    /// </summary>
    public static List<DailyUsage> MonthDailySeries(IEnumerable<UsageEntry> entries, DateTimeOffset now,
        TimeZoneInfo? timeZone = null) =>
        DailySeries(entries, StartOfMonth(now, timeZone), now, timeZone);

    /// <summary>Rolling window daily series (e.g. 7, 14, 21 days up to today).</summary>
    public static List<DailyUsage> RollingDailySeries(IEnumerable<UsageEntry> entries, int days, DateTimeOffset now,
        TimeZoneInfo? timeZone = null) =>
        DailySeries(entries, StartOfRollingWindow(days, now, timeZone), now, timeZone);

    /// <summary>Active block over the last 5 hours (burn-rate estimate). Zero-token records are ignored.</summary>
    public static BlockUsage? ActiveBlock(IEnumerable<UsageEntry> entries, DateTimeOffset now)
    {
        var windowStart = now - BlockWindow;
        var recent = entries.Where(e => e.Date >= windowStart && e.Total > 0).OrderBy(e => e.Date).ToList();
        if (recent.Count == 0) return null;
        var first = recent[0];
        var b = new UsageBucket();
        foreach (var e in recent) b.Add(e);
        var minutes = Math.Max(1, (now - first.Date).TotalSeconds / 60);
        var tpm = b.Total / minutes;
        return new BlockUsage(
            $"block-{first.Date.ToUnixTimeSeconds()}",
            Iso8601Parser.Format(first.Date),
            Iso8601Parser.Format(first.Date + BlockWindow),
            true, b.Total, b.Cost, tpm, b.CostCoverage);
    }

    // MARK: Calendar utilities (local time zone, culture's first weekday — same as Calendar.current)

    public static DateTimeOffset StartOfDay(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        return LocalMidnight(TimeZoneInfo.ConvertTime(date, tz).Date, tz);
    }

    public static DateTimeOffset StartOfMonth(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(date, tz);
        return LocalMidnight(new DateTime(local.Year, local.Month, 1), tz);
    }

    public static DateTimeOffset StartOfWeek(DateTimeOffset date, TimeZoneInfo? timeZone = null,
        DayOfWeek? firstDayOfWeek = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var first = firstDayOfWeek ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var localDate = TimeZoneInfo.ConvertTime(date, tz).Date;
        var diff = ((int)localDate.DayOfWeek - (int)first + 7) % 7;
        return LocalMidnight(localDate.AddDays(-diff), tz);
    }

    /// <summary>Midnight of a local calendar date; if a DST jump skips midnight, the first valid instant.</summary>
    private static DateTimeOffset LocalMidnight(DateTime localDate, TimeZoneInfo tz)
    {
        var dt = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(dt)) dt = dt.AddMinutes(30);
        return new DateTimeOffset(dt, tz.GetUtcOffset(dt));
    }

    public const int MaxDailyTrendWindowDays = 21;

    /// <summary>
    /// Midnight of the first visible day of an N-day rolling trend ending on <paramref name="date"/>. Calendar-day
    /// arithmetic rather than <c>days × 24 h</c>, so the start stays at 00:00 of the first chart day when the
    /// window crosses a 25-hour DST fall-back day.
    /// </summary>
    public static DateTimeOffset StartOfRollingWindow(int days, DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(date, tz).Date;
        return LocalMidnight(today.AddDays(-(Math.Max(1, days) - 1)), tz);
    }

    /// <summary>
    /// The earliest of month start, week start, now−5h, now−7d and the 21-day trend start: the mtime floor that
    /// covers every window. A fixed <c>now − 21 × 24 h</c> would clip the first chart day's early hours across a
    /// DST fall-back day.
    /// </summary>
    public static DateTimeOffset EnrichmentScanStart(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var candidates = new[]
        {
            StartOfMonth(now, timeZone), StartOfWeek(now, timeZone), now - BlockWindow, now - RecentUseWindow,
            StartOfRollingWindow(MaxDailyTrendWindowDays, now, timeZone),
        };
        return candidates.Min();
    }

    public static string MonthKey(DateTimeOffset date, TimeZoneInfo? timeZone = null) =>
        TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string LocalDay(DateTimeOffset date, TimeZoneInfo? timeZone = null) =>
        TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string TodayKey() => LocalDay(DateTimeOffset.Now);

    /// <summary>Whole calendar days between two "yyyy-MM-dd" keys; positive when <paramref name="to"/> is later. Null when either does not parse.</summary>
    public static int? DayDifference(string from, string to)
    {
        static DateTime? Parse(string key) =>
            DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return Parse(from) is { } d1 && Parse(to) is { } d2 ? (d2 - d1).Days : null;
    }

    // MARK: JSON value helpers (same leniency as Foundation's JSONSerialization + NSNumber)

    /// <summary>Parse ceiling: 100,000× real usage, and still safe to add several times inside Int64.</summary>
    public const long MaxParsedTokenValue = 1_000_000_000_000_000;

    private static string? StringValue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>JSON booleans are NSNumbers in Foundation, so they count as 1/0 like there.</summary>
    internal static double? NumberValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    internal static long? IntOrNil(JsonElement v)
    {
        if (NumberValue(v) is not { } d) return null;
        if (d <= 0) return 0;   // there are no negative tokens
        return d >= MaxParsedTokenValue ? MaxParsedTokenValue : (long)d;
    }

    private static long IntValue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? IntOrNil(v) ?? 0 : 0;

    private static bool BoolValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
        _ => false,
    };
}
