using System.IO.Compression;
using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of the Claude half of <c>LocalUsageCache.swift</c>: per-file incremental cache keyed by path,
/// reused while <c>(mtime, size)</c> is unchanged and tail-parsed when an append-only log grows.
/// Persisted (zlib-compressed JSON) so the cold full parse happens once, not on every launch.
/// </summary>
public sealed class LocalUsageCache
{
    public static readonly LocalUsageCache Shared = new();

    /// <summary>Bump when the entry → cost mapping changes, so blobs from the old rule are re-parsed.</summary>
    private const int ClaudeParserVersion = 1;

    private sealed class Blob
    {
        public required long MtimeTicks { get; init; }
        public required long Size { get; init; }
        public required List<UsageEntry> Entries { get; init; }
        public long? Offset { get; init; }
        public LocalUsageReader.ClaudeCostState? ClaudeCostState { get; init; }

        public List<UsageEntry> AttributedEntries() =>
            ClaudeCostState == null ? Entries : LocalUsageReader.ApplyReportedCost(Entries, ClaudeCostState);
    }

    private sealed class Snapshot
    {
        public Dictionary<string, Blob> Claude { get; init; } = new();
        public int ClaudeParserVersion { get; init; }
    }

    private readonly object _gate = new();
    private Dictionary<string, Blob> _claudeCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private bool _dirty;
    private DateTimeOffset? _lastSave;

    private readonly IReadOnlyList<string>? _claudeRoots;
    private readonly string _filePath;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeZoneInfo? _timeZone;

    /// <summary>Whether this cache reads and writes its file. False for tests that inject nothing.</summary>
    public bool PersistsToDisk { get; }

    public LocalUsageCache(IReadOnlyList<string>? claudeRoots = null, string? filePath = null,
        Func<DateTimeOffset>? now = null, TimeZoneInfo? timeZone = null)
    {
        _claudeRoots = claudeRoots;
        _filePath = filePath ?? Path.Combine(AppEnv.StateDirectory(), "usage-cache.json");
        PersistsToDisk = AppEnv.PersistsToUserLocation(filePath);
        _now = now ?? (() => DateTimeOffset.Now);
        _timeZone = timeZone;
    }

    public List<UsageEntry> ClaudeEntries(DateTimeOffset modifiedSince)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var all = new List<UsageEntry>();
            foreach (var root in _claudeRoots ?? LocalUsageReader.ClaudeProjectRoots)
                all.AddRange(Collect(root, modifiedSince));
            SaveIfNeeded();
            return LocalUsageReader.DedupKeepMax(all);
        }
    }

    private List<UsageEntry> Collect(string root, DateTimeOffset since)
    {
        var result = new List<UsageEntry>();
        foreach (var path in LocalUsageReader.EnumerateFilesSkippingHidden(root,
                     f => f.EndsWith(".jsonl", StringComparison.Ordinal)))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var mtime = info.LastWriteTimeUtc;
            if (mtime < since.UtcDateTime) continue;
            var size = info.Length;
            _claudeCache.TryGetValue(path, out var prior);

            List<UsageEntry> fileEntries;
            if (prior != null && prior.MtimeTicks == mtime.Ticks && prior.Size == size)
            {
                fileEntries = prior.AttributedEntries();   // unchanged → no re-parse
            }
            else
            {
                // Tail-parse only when the file strictly grew; a same-size rewrite or a truncation
                // is re-read from the start.
                var from = prior != null && size > prior.Size ? prior.Offset ?? 0 : 0;
                var tail = LocalUsageReader.ParseClaudeFile(path, from, _timeZone);
                if (tail != null)
                {
                    var entries = from > 0
                        ? LocalUsageReader.DedupKeepMax(prior!.Entries.Concat(tail.Entries))
                        : tail.Entries;
                    var blob = new Blob
                    {
                        MtimeTicks = mtime.Ticks, Size = size, Entries = entries, Offset = tail.NewOffset,
                        ClaudeCostState = tail.CostState ?? (from > 0 ? prior!.ClaudeCostState : null),
                    };
                    _claudeCache[path] = blob;
                    _dirty = true;
                    fileEntries = blob.AttributedEntries();
                }
                else if (prior != null)
                {
                    // A transient read failure is not frozen into the current signature.
                    fileEntries = prior.AttributedEntries();
                }
                else
                {
                    continue;
                }
            }
            // Blobs cached before SessionID existed get it from their path, without a re-parse.
            if (fileEntries.Any(e => e.SessionID == null))
            {
                var session = LocalUsageReader.ClaudeSessionID(path);
                fileEntries = fileEntries.Select(e => e.SessionID == null ? e with { SessionID = session } : e).ToList();
            }
            result.AddRange(fileEntries);
        }
        return result;
    }

    // MARK: Persistence

    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = false };

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!PersistsToDisk || !File.Exists(_filePath)) return;
        try
        {
            using var file = File.OpenRead(_filePath);
            using var z = new ZLibStream(file, CompressionMode.Decompress);
            var snap = JsonSerializer.Deserialize<Snapshot>(z, JsonOptions);
            if (snap == null) return;
            if (snap.ClaudeParserVersion != ClaudeParserVersion)
            {
                _dirty = true;
                return;
            }
            _claudeCache = new Dictionary<string, Blob>(snap.Claude, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Write($"usage cache: discarded unreadable {_filePath}: {ex.Message}");
        }
    }

    /// <summary>Blobs older than 40 days cannot fall in any window (today/week/month): drop them.</summary>
    private void Prune()
    {
        var cutoff = (_now() - TimeSpan.FromDays(40)).UtcDateTime.Ticks;
        foreach (var key in _claudeCache.Where(kv => kv.Value.MtimeTicks < cutoff).Select(kv => kv.Key).ToList())
            _claudeCache.Remove(key);
    }

    /// <summary>Writes at most once a minute.</summary>
    private void SaveIfNeeded()
    {
        if (!_dirty) return;
        if (!PersistsToDisk) { _dirty = false; return; }
        if (_lastSave is { } last && _now() - last < TimeSpan.FromSeconds(60)) return;
        Prune();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            using (var file = File.Create(tmp))
            using (var z = new ZLibStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(z, new Snapshot { Claude = _claudeCache, ClaudeParserVersion = ClaudeParserVersion }, JsonOptions);
            }
            File.Move(tmp, _filePath, overwrite: true);
            _dirty = false;
            _lastSave = _now();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"usage cache: save failed: {ex.Message}");
        }
    }
}
