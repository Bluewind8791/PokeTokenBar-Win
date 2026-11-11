using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptb-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(IEnumerable<string> lines, string name = "s.jsonl", string? sub = null)
    {
        var folder = sub == null ? Path : System.IO.Path.Combine(Path, sub);
        Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, name);
        File.WriteAllText(file, string.Join("\n", lines));
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

internal static class Fixtures
{
    public static string ClaudeLine(string id, string req, string model, string ts, long i, long o, long cw, long cr) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + (req) + "\",\"timestamp\":\"" + (ts) + "\",\"message\":{\"id\":\"" + (id) + "\",\"model\":\"" + (model) + "\",\"usage\":{\"input_tokens\":" + (i) + ",\"output_tokens\":" + (o) + ",\"cache_creation_input_tokens\":" + (cw) + ",\"cache_read_input_tokens\":" + cr + "}}}";

    public static UsageEntry Entry(DateTimeOffset date, long input, string model = "claude-opus-4-8", string? id = null,
        long output = 0, TimeZoneInfo? tz = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        Date = date,
        LocalDay = LocalUsageReader.LocalDay(date, tz),
        Model = model,
        Input = input,
        Output = output,
    };

    public static UsageBucket Bucket(IEnumerable<UsageEntry> entries)
    {
        var b = new UsageBucket();
        foreach (var e in entries) b.Add(e);
        return b;
    }

    /// <summary>Repository root, found by walking up to the folder holding Package.swift.</summary>
    public static string RepoRoot()
    {
        if (Environment.GetEnvironmentVariable("PTB_REPO_ROOT") is { Length: > 0 } fromEnv) return fromEnv;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Package.swift"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Package.swift not found above the test binary");
    }
}
