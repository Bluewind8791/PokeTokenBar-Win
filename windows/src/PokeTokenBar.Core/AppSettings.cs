using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

/// <summary>
/// The Windows counterpart of <c>UserDefaults.standard</c>: a flat key → JSON value store saved to
/// <c>%LOCALAPPDATA%\PokeTokenBar\settings.json</c>. Keys are the same strings the macOS app uses
/// so a setting can be found by grepping either code base.
/// </summary>
public sealed class AppSettings
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly JsonObject _values;

    /// <summary>The installed app's store. Under tests it is an in-memory store unless replaced.</summary>
    public static AppSettings Current { get; set; } = new(null);

    public event Action<string>? Changed;

    /// <param name="path">File to load and save; null keeps everything in memory.</param>
    public AppSettings(string? path)
    {
        _path = path;
        _values = new JsonObject();
        if (path == null || !File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject loaded) _values = loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"settings: unreadable {path}: {ex.Message}");
        }
    }

    public static AppSettings ForInstalledApp() =>
        new(Path.Combine(AppEnv.StateDirectory(), "settings.json"));

    public string? GetString(string key)
    {
        lock (_gate) return _values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    public bool? GetBool(string key)
    {
        lock (_gate) return _values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    public double? GetDouble(string key)
    {
        lock (_gate) return _values[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    }

    public long? GetLong(string key)
    {
        lock (_gate) return _values[key] is JsonValue v && v.TryGetValue<long>(out var d) ? d : null;
    }

    public JsonNode? GetNode(string key)
    {
        lock (_gate) return _values[key]?.DeepClone();
    }

    public void Set(string key, string? value) => SetNode(key, value == null ? null : JsonValue.Create(value));
    public void Set(string key, bool value) => SetNode(key, JsonValue.Create(value));
    public void Set(string key, double value) => SetNode(key, JsonValue.Create(value));
    public void Set(string key, long value) => SetNode(key, JsonValue.Create(value));

    public void SetNode(string key, JsonNode? value)
    {
        lock (_gate)
        {
            if (value == null) _values.Remove(key);
            else _values[key] = value.DeepClone();
            Save();
        }
        Changed?.Invoke(key);
    }

    public void Remove(string key) => SetNode(key, null);

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"settings: save failed: {ex.Message}");
        }
    }
}
