using System.Text.Json;
using System.Text.RegularExpressions;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>CustomScanRoots.swift</c>: per-provider extra scan folders from Settings (#177).
/// Custom roots only add to the curated defaults; an ancestor of a default is dropped so it cannot
/// fold a hidden default away (#162-B).
/// </summary>
public static class CustomScanRoots
{
    public static string DefaultsKey(string providerID) => $"customScanRoots.{providerID}";

    public static string? StoredValue(string providerID, AppSettings? settings = null)
    {
        var raw = (settings ?? AppSettings.Current).GetString(DefaultsKey(providerID));
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>
    /// Comma/newline split, <c>~</c> expansion, per-segment <c>*</c>/<c>?</c>/<c>[]</c> wildcards.
    /// Only existing directories are returned; relative paths are ignored.
    /// </summary>
    public static List<string> Expand(string raw, string? home = null)
    {
        home ??= AppEnv.HomeDirectory;
        var output = new List<string>();
        foreach (var part in raw.Split([',', '\n', '\r']))
        {
            var pattern = part.Trim();
            if (pattern.Length == 0) continue;
            var absolute = LocalUsageReader.ExpandTilde(pattern, home);
            if (!Path.IsPathFullyQualified(absolute)) continue;

            var root = Path.GetPathRoot(absolute)!;
            var segments = absolute[root.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var paths = new List<string> { root };
            foreach (var segment in segments)
            {
                if (segment.IndexOfAny(['*', '?', '[']) >= 0)
                {
                    var regex = GlobToRegex(segment);
                    var next = new List<string>();
                    foreach (var b in paths)
                    {
                        string[] names;
                        try { names = Directory.GetFileSystemEntries(b).Select(Path.GetFileName).ToArray()!; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                        next.AddRange(names.Order(StringComparer.Ordinal).Where(n => regex.IsMatch(n))
                            .Select(n => Path.Combine(b, n)));
                    }
                    paths = next;
                }
                else
                {
                    paths = paths.Select(p => Path.Combine(p, segment)).ToList();
                }
            }
            output.AddRange(paths.Where(Directory.Exists));
        }
        return output;
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0) { sb.Append(@"\["); break; }
                    var set = glob[(i + 1)..close];
                    if (set.StartsWith('!')) set = "^" + set[1..];
                    sb.Append('[').Append(set.Replace(@"\", @"\\")).Append(']');
                    i = close;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        // fnmatch without FNM_CASEFOLD is case-sensitive; NTFS names are not, so match case-insensitively.
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Curated defaults first; extras that would evict a default are dropped; then fold.</summary>
    public static List<string> Union(IEnumerable<string> defaults, string? extraRaw)
    {
        var protectedRoots = LocalUsageReader.NormalizedRoots(defaults);
        if (extraRaw == null) return protectedRoots;
        var added = Expand(extraRaw).Where(extra => !WouldEvict(extra, protectedRoots));
        return LocalUsageReader.NormalizedRoots(protectedRoots.Concat(added));
    }

    /// <summary>Folded extras that are not one of the curated defaults — the number Settings shows.</summary>
    public static int SurvivingExtraCount(IEnumerable<string> defaults, string extraRaw)
    {
        var list = defaults.ToList();
        var protectedSet = LocalUsageReader.NormalizedRoots(list).Select(NormalizedPath).ToHashSet();
        return Union(list, extraRaw).Count(r => !protectedSet.Contains(NormalizedPath(r)));
    }

    private static bool WouldEvict(string extra, IEnumerable<string> protectedRoots)
    {
        var extraPath = NormalizedPath(extra);
        var sep = Path.DirectorySeparatorChar;
        return protectedRoots.Any(def =>
        {
            var d = NormalizedPath(def);
            return d != extraPath && d.StartsWith(extraPath.TrimEnd(sep) + sep, StringComparison.Ordinal);
        });
    }

    private static string NormalizedPath(string path) => LocalUsageReader.ResolvePath(path).ToLowerInvariant();
}

/// <summary>
/// Port of the root-discovery half of <c>ClaudeAccountRoots.swift</c>: other Claude accounts are
/// separate config directories (<c>~/.claude-work</c>, <c>CLAUDE_CONFIG_DIR</c> entries, or the
/// <c>additionalClaudeConfigDirs</c> setting), each with its own <c>projects/</c>.
/// </summary>
public static partial class ClaudeAccountRoots
{
    public const string DefaultsKey = "additionalClaudeConfigDirs";

    public static List<string> Roots(string? raw, string? home = null)
    {
        home ??= AppEnv.HomeDirectory;
        if (raw == null) return [];
        var excluded = DefaultRootPaths(home);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        foreach (var part in raw.Split([',', '\n', '\r']))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var expanded = LocalUsageReader.ExpandTilde(trimmed, home);
            if (!Path.IsPathFullyQualified(expanded)) continue;
            var path = Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(path) || excluded.Contains(path) || !seen.Add(path)) continue;
            output.Add(path);
        }
        return output;
    }

    public static List<string> Discovered(string home, string? configDirValue)
    {
        var excluded = DefaultRootPaths(home);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = Roots(configDirValue, home).Where(seen.Add).ToList();
        IEnumerable<string> names;
        try { names = Directory.GetDirectories(home).Select(d => Path.GetFileName(d)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { names = []; }
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(".claude-", StringComparison.Ordinal) && !name.StartsWith(".claude_", StringComparison.Ordinal))
                continue;
            var path = Path.Combine(home, name);
            if (excluded.Contains(path) || !HasLogin(path) || !seen.Add(path)) continue;
            output.Add(path);
        }
        return output;
    }

    /// <summary>A config directory counts as an account only when its <c>.claude.json</c> carries <c>oauthAccount</c>.</summary>
    public static bool HasLogin(string root)
    {
        try
        {
            var file = Path.Combine(root, ".claude.json");
            if (!File.Exists(file)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("oauthAccount", out var a) && a.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static List<string> InstalledAccountRoots(AppSettings? settings = null)
    {
        var detected = AppEnv.IsInstalledApp
            ? Discovered(AppEnv.HomeDirectory, UsageEnvironment.Value("CLAUDE_CONFIG_DIR"))
            : [];
        return Merged(detected, (settings ?? AppSettings.Current).GetString(DefaultsKey));
    }

    public static List<string> Merged(IEnumerable<string> detected, string? setting, string? home = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return detected.Concat(Roots(setting, home)).Where(seen.Add).ToList();
    }

    private static HashSet<string> DefaultRootPaths(string home) => new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine(home, ".claude"),
        Path.Combine(home, ".config", "claude"),
    };
}
