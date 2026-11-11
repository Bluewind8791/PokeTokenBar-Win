using System.Reflection;
using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>UpdateChecker</c>: checks the latest GitHub release and offers it when newer. macOS
/// upgrades a Homebrew cask in place; Windows has no such channel, so applying an update is the app
/// opening <see cref="UpdateTarget"/>'s release page (no Homebrew script is ported).
/// Releases are shared with the macOS app, so a release counts for Windows only when it carries a
/// Windows asset (<see cref="IsWindowsAsset"/>); a macOS-only release is not offered.
/// Call it from one thread (the UI thread), like the macOS <c>@MainActor</c> class.
/// </summary>
public sealed class UpdateChecker
{
    public sealed record UpdateRelease(string Version, string Url);

    public enum NoticeKind { Offer, Skipped, Current }

    /// <summary>What Settings says after a check. A skipped release is not "up to date".</summary>
    public readonly record struct SettingsNotice(NoticeKind Kind, string? Version = null);

    /// <summary>Tag and release-page URL from the GitHub latest endpoint (or a test double).</summary>
    public sealed record LatestRelease(string Tag, string Url);

    public const string SkippedVersionKey = "skippedUpdateVersion";
    public const string Repo = "chattymin/PokeTokenBar";

    /// <summary>Raised after <see cref="Available"/> / <see cref="Skipped"/> change (SwiftUI observation stand-in).</summary>
    public event Action? Changed;

    public UpdateRelease? Available { get; private set; }
    /// <summary>Newer release the user chose to skip. Hidden from the banner, still shown in Settings.</summary>
    public UpdateRelease? Skipped { get; private set; }

    public string CurrentVersion { get; }
    private readonly Func<DateTimeOffset> _clock;
    private readonly AppSettings _settings;
    private readonly Func<Task<LatestRelease?>> _fetchLatest;
    private DateTimeOffset? _lastChecked;
    /// <summary>Overlapping popover opens must not stack GitHub calls now that a failed check leaves no stamp.</summary>
    private int _checkInFlight;

    public UpdateChecker(string? currentVersion = null, Func<DateTimeOffset>? clock = null, AppSettings? settings = null,
        Func<Task<LatestRelease?>>? fetchLatest = null)
    {
        CurrentVersion = currentVersion ?? EntryVersion();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _settings = settings ?? AppSettings.Current;
        _fetchLatest = fetchLatest ?? (() => LiveLatestRelease(Repo));
    }

    public SettingsNotice Notice =>
        Available != null ? new(NoticeKind.Offer, Available.Version)
        : Skipped != null ? new(NoticeKind.Skipped, Skipped.Version)
        : new(NoticeKind.Current);

    /// <summary>Release Settings can install. A skip hides the banner; it does not throw the URL away.</summary>
    public UpdateRelease? UpdateTarget => Available ?? Skipped;

    /// <summary>
    /// Looks up the latest release. The interval only applies between *successful* checks (rate-limit
    /// protection): a network failure, a bad payload, an unsafe URL or an unsupported tag starts no
    /// cooldown, so opening the popover again retries at once.
    /// </summary>
    public async Task Check(TimeSpan? minInterval = null)
    {
        if (Interlocked.Exchange(ref _checkInFlight, 1) == 1) return;
        try
        {
            if (_lastChecked is { } last && _clock() - last < (minInterval ?? TimeSpan.FromSeconds(1800))) return;
            if (await _fetchLatest() is not { } release || !IsTrustedReleaseURL(release.Url)
                || NormalizedReleaseVersion(release.Tag) is not { } version) return;
            _lastChecked = _clock();
            Consider(version, release.Url);
        }
        finally
        {
            Volatile.Write(ref _checkInFlight, 0);
        }
    }

    /// <summary>The release page is opened by the shell: https on github.com only (no scheme hijack).</summary>
    public static bool IsTrustedReleaseURL(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "github.com";

    /// <summary>
    /// Tag → comparable version, or null when it is not a plain <c>MAJOR.MINOR.PATCH</c> (optionally
    /// v-prefixed). <see cref="IsNewer"/> reads non-numeric parts as 0, so a prerelease would compare
    /// equal to its release and garbage as 0.0.0; neither may be applied or count as a successful check.
    /// </summary>
    public static string? NormalizedReleaseVersion(string tag)
    {
        var version = tag.Trim();
        if (version.StartsWith('v') || version.StartsWith('V')) version = version[1..];
        var parts = version.Split('.');
        if (parts.Length != 3 || !parts.All(p => p.Length is > 0 and <= 9 && p.All(char.IsAsciiDigit))) return null;
        return string.Join('.', parts.Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Live GitHub latest-release lookup. Null on any transport or payload failure.</summary>
    public static async Task<LatestRelease?> LiveLatestRelease(string repo, HttpClient? http = null)
    {
        if (LiveFetchGate.PublicClient(http) is not { } client) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            // GitHub rejects API requests without a User-Agent (URLSession sends one by default).
            request.Headers.TryAddWithoutValidation("User-Agent", "PokeTokenBar");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await client.SendAsync(request, timeout.Token);
            if ((int)response.StatusCode != 200) return null;
            return ParseLatestRelease(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary><c>tag_name</c> + <c>html_url</c> of a release that carries a Windows asset; null otherwise.</summary>
    public static LatestRelease? ParseLatestRelease(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("html_url", out var html) || html.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            var hasWindowsAsset = assets.EnumerateArray().Any(a =>
                a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String && IsWindowsAsset(n.GetString()!));
            return hasWindowsAsset ? new LatestRelease(tag.GetString()!, html.GetString()!) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Assumed Windows asset naming (no Windows release exists yet): a name containing <c>windows</c> or
    /// <c>-win-</c> (e.g. <c>PokeTokenBar-Windows-x64.zip</c>, <c>PokeTokenBar-2.6.0-win-x64.msi</c>), as a
    /// zip, msi, msix or exe. The macOS asset is <c>PokeTokenBar.zip</c>.
    /// </summary>
    public static bool IsWindowsAsset(string name)
    {
        var lower = name.ToLowerInvariant();
        return (lower.Contains("windows") || lower.Contains("-win-"))
            && (lower.EndsWith(".zip") || lower.EndsWith(".msi") || lower.EndsWith(".msix") || lower.EndsWith(".exe"));
    }

    /// <summary>Applies one fetched release. <paramref name="tag"/> may be a tag (<c>v2.5.4</c>) or a bare version.</summary>
    public void Consider(string tag, string url)
    {
        var latest = tag.StartsWith('v') ? tag[1..] : tag;
        var skippedVersion = _settings.GetString(SkippedVersionKey);
        if (!IsNewer(latest, CurrentVersion))
        {
            Available = null;
            Skipped = null;
        }
        else if (latest == skippedVersion)
        {
            Available = null;
            Skipped = new UpdateRelease(latest, url);
        }
        else
        {
            Available = new UpdateRelease(latest, url);
            Skipped = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Hides the banner for this version. Settings can still see and install it.</summary>
    public void SkipCurrent()
    {
        if (Available is not { } release) return;
        _settings.Set(SkippedVersionKey, release.Version);
        Skipped = release;
        Available = null;
        Changed?.Invoke();
    }

    /// <summary>Undoes a skip so the banner can show the same release again.</summary>
    public void ShowSkippedAgain()
    {
        _settings.Remove(SkippedVersionKey);
        if (Skipped is { } release)
        {
            Available = release;
            Skipped = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Numeric semver comparison ("2.0.10" > "2.0.9"); missing or non-numeric parts read as 0.</summary>
    public static bool IsNewer(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    public static string EntryVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "0";
}
