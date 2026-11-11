using System.Globalization;

namespace PokeTokenBar.Core;

// Port of SaveSnapshots.swift.

/// <summary>A point-in-time local snapshot of CompanionState, with summary metadata for listing and rollback.</summary>
public sealed record SaveSnapshot(
    string Id,
    string FilePath,
    DateTimeOffset Date,
    int DexCount,
    long LifetimeTokens,
    int? CurrentSpeciesID,
    bool CurrentIsShiny,
    PokemonForm? CurrentForm);

public static class SaveSnapshotManager
{
    public const string SnapshotPrefix = "companion-snapshot-";
    public const int MaxSnapshotsToKeep = 10;
    /// <summary>12 hours between auto-snapshots.</summary>
    public static readonly TimeSpan MinAutoSnapshotInterval = TimeSpan.FromHours(12);

    public static string DefaultDeviceName => Environment.MachineName;

    public static string DefaultAppVersion =>
        typeof(SaveSnapshotManager).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.5.0";

    /// <summary>Directory where local snapshots are stored: <c>&lt;stateDir&gt;/.snapshots/&lt;stateFileName&gt;/</c>.</summary>
    public static string SnapshotsDirectory(string stateFilePath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stateFilePath))!, ".snapshots",
            Path.GetFileName(stateFilePath));
        try { Directory.CreateDirectory(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return dir;
    }

    /// <summary>Snapshot file name for the timestamp (local time, like Swift's DateFormatter).</summary>
    public static string SnapshotFileName(DateTimeOffset date) =>
        $"{SnapshotPrefix}{SaveTransfer.Stamp(date, "yyyy-MM-dd-HHmmss")}.json";

    private static DateTimeOffset? ParseStamp(string fileName)
    {
        if (!fileName.StartsWith(SnapshotPrefix, StringComparison.Ordinal) || !fileName.EndsWith(".json", StringComparison.Ordinal))
            return null;
        var raw = fileName[SnapshotPrefix.Length..^".json".Length];
        var dash = raw.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(raw[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            raw = raw[..dash];
        if (!DateTime.TryParseExact(raw, "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static SaveSnapshot Summary(string id, string path, DateTimeOffset date, CompanionState st) => new(
        id, path, date, st.Dex.Count, st.UsedSinceInstall, st.Active?.CurrentID, st.Active?.DisplaysShiny ?? false,
        st.Active is { } a ? PokemonForm.ResolvedForSpecies(a.CurrentID, a.Form) : null);

    private static DateTimeOffset FallbackDate(string path) =>
        ParseStamp(Path.GetFileName(path)) ?? (File.Exists(path) ? new DateTimeOffset(File.GetCreationTime(path)) : DateTimeOffset.Now);

    /// <summary>All valid snapshots, newest first.</summary>
    public static List<SaveSnapshot> ListSnapshots(string stateFilePath)
    {
        var dir = SnapshotsDirectory(stateFilePath);
        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var results = new List<SaveSnapshot>();
        foreach (var path in files)
        {
            var file = Path.GetFileName(path);
            if (!file.StartsWith(SnapshotPrefix, StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.Ordinal)) continue;
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            try
            {
                var envelope = SaveTransfer.Decode(data);
                results.Add(Summary(file, path, envelope.ExportedAt, envelope.State));
                continue;
            }
            catch (SaveTransferException) { }
            try
            {
                var st = CompanionJson.Deserialize(data);
                results.Add(Summary(file, path, FallbackDate(path), st));
            }
            catch (SwiftDecodingException) { }
        }
        return results.OrderByDescending(s => s.Date).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Loads the envelope from a snapshot file (raw legacy states are wrapped and sanitized).</summary>
    public static SaveEnvelope LoadEnvelope(string path)
    {
        var data = File.ReadAllBytes(path);
        try
        {
            return SaveTransfer.Decode(data);
        }
        catch (SaveTransferException) { }
        try
        {
            var st = CompanionJson.Deserialize(data);
            return new SaveEnvelope(SaveEnvelope.FormatID, SaveEnvelope.SchemaVersion, DefaultAppVersion,
                FallbackDate(path), DefaultDeviceName, SaveTransfer.Sanitized(st));
        }
        catch (SwiftDecodingException)
        {
            throw new SaveTransferException(SaveTransferErrorKind.NotASaveFile);
        }
    }

    /// <summary>Sanitized CompanionState from a snapshot file, or null.</summary>
    public static CompanionState? LoadState(string path)
    {
        try
        {
            return LoadEnvelope(path).State;
        }
        catch (Exception ex) when (ex is SaveTransferException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a snapshot of the state into the snapshots directory, then prunes old ones.</summary>
    public static SaveSnapshot CreateSnapshot(CompanionState state, string stateFilePath, DateTimeOffset date,
        string? appVersion = null, string? deviceName = null)
    {
        var dir = SnapshotsDirectory(stateFilePath);
        var fileName = SnapshotFileName(date);
        var target = Path.Combine(dir, fileName);
        if (File.Exists(target))
        {
            var @base = fileName[..^".json".Length];
            var counter = 1;
            while (File.Exists(Path.Combine(dir, $"{@base}-{counter}.json"))) counter++;
            target = Path.Combine(dir, $"{@base}-{counter}.json");
        }
        var data = SaveTransfer.Encode(state, appVersion ?? DefaultAppVersion, deviceName ?? DefaultDeviceName, date);
        AtomicFile.Write(target, data);
        PruneOldSnapshots(stateFilePath);
        return Summary(Path.GetFileName(target), target, date, state);
    }

    /// <summary>Keeps only the newest <see cref="MaxSnapshotsToKeep"/>.</summary>
    public static void PruneOldSnapshots(string stateFilePath)
    {
        var snapshots = ListSnapshots(stateFilePath);
        if (snapshots.Count <= MaxSnapshotsToKeep) return;
        foreach (var old in snapshots.Skip(MaxSnapshotsToKeep))
        {
            try { File.Delete(old.FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static SaveSnapshot? LatestSnapshot(string stateFilePath) => ListSnapshots(stateFilePath).FirstOrDefault();

    public static CompanionState? LoadLatestValidSnapshot(string stateFilePath) =>
        LatestSnapshot(stateFilePath) is { } latest ? LoadState(latest.FilePath) : null;
}

/// <summary>Write-to-temp-then-rename, the counterpart of <c>Data.write(to:options:.atomic)</c>.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
