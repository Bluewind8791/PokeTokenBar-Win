using System.Net.NetworkInformation;
using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>One provider's status. <c>Description</c> is the component name or the statuspage text.</summary>
public sealed record ProviderStatus(ProviderStatusIndicator Indicator, string Description);

/// <summary>providerID → status. A provider whose lookup failed is left out (the caller keeps the previous one).</summary>
public interface IProviderStatusProviding
{
    Task<Dictionary<string, ProviderStatus>> Fetch();
}

/// <summary>
/// Port of <c>StatuspageStatusProvider</c>: reads statuspage.io <c>summary.json</c>. The company-wide
/// status includes unrelated products, so only the component the app depends on is read. The Windows
/// port follows Claude Code only.
/// </summary>
public sealed class StatuspageStatusProvider(HttpClient? http = null) : IProviderStatusProviding
{
    public sealed record Endpoint(string Id, Uri Url, string ComponentName);

    public static readonly IReadOnlyList<Endpoint> Endpoints =
    [
        new("claude_code", new Uri("https://status.claude.com/api/v2/summary.json"), "Claude Code"),
    ];

    public async Task<Dictionary<string, ProviderStatus>> Fetch()
    {
        var results = await Task.WhenAll(Endpoints.Select(async e => (e.Id, Status: await FetchOne(e.Url, e.ComponentName))));
        return results.Where(r => r.Status != null).ToDictionary(r => r.Id, r => r.Status!);
    }

    private async Task<ProviderStatus?> FetchOne(Uri url, string componentName)
    {
        // Public data, but still a network call: only from the installed app, or with a test client.
        if (LiveFetchGate.PublicClient(http) is not { } client) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "PokeTokenBar");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(request, timeout.Token);
            if ((int)response.StatusCode != 200) return null;
            return ParseComponent(await response.Content.ReadAsByteArrayAsync(timeout.Token), componentName);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>statuspage.io status.json: <c>{ "status": { "indicator", "description" } }</c>.</summary>
    public static ProviderStatus? Parse(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object
                || !status.TryGetProperty("indicator", out var indicator) || indicator.ValueKind != JsonValueKind.String)
                return null;
            var description = status.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            return new ProviderStatus(ProviderStatusIndicators.FromStatuspage(indicator.GetString()!), description);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Only the named component of summary.json. When the global indicator reports an incident but the
    /// component is operational, the result is operational: an unrelated outage must not warn here.
    /// </summary>
    public static ProviderStatus? ParseComponent(byte[] data, string componentName)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Array) return null;
            // Every component must decode, like the Swift Decodable array.
            var rows = new List<(string Name, string Status)>();
            foreach (var c in components.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object
                    || !c.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String
                    || !c.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String) return null;
                rows.Add((n.GetString()!, s.GetString()!));
            }
            var match = rows.FirstOrDefault(r => string.Equals(r.Name, componentName, StringComparison.OrdinalIgnoreCase));
            return match.Name == null ? null
                : new ProviderStatus(ProviderStatusIndicators.FromComponentStatus(match.Status), match.Name);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Port of <c>NetworkReachabilityMonitor</c>: notices the offline → online moment (Wi-Fi reconnect,
/// network back after sleep) and triggers a refresh without waiting for the 1–5 minute timer.
/// Windows reports availability through <see cref="NetworkChange.NetworkAvailabilityChanged"/>, which
/// has no initial event: <see cref="Start"/> seeds the current state, so a launch never counts as a reconnect.
/// </summary>
public sealed class NetworkReachabilityMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _clock;
    private bool _started;
    private bool? _lastStatus;
    private DateTimeOffset? _lastTriggeredAt;

    public Action? OnReconnected { get; set; }

    public NetworkReachabilityMonitor(TimeSpan? cooldown = null, Func<DateTimeOffset>? clock = null)
    {
        _cooldown = cooldown ?? TimeSpan.FromSeconds(5);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _lastStatus = NetworkInterface.GetIsNetworkAvailable();
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            _started = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            _started = false;
            _lastStatus = null;
        }
    }

    public void Dispose() => Stop();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => HandleStatusUpdate(e.IsAvailable);

    internal void HandleStatusUpdate(bool isAvailable)
    {
        bool trigger;
        Action? callback;
        lock (_gate)
        {
            var previous = _lastStatus;
            _lastStatus = isAvailable;
            var now = _clock();
            trigger = ShouldTriggerReconnect(previous, isAvailable, _lastTriggeredAt, now, _cooldown);
            if (trigger) _lastTriggeredAt = now;
            callback = OnReconnected;
        }
        if (!trigger) return;
        AppLog.Write("network reconnected — triggering refresh");
        callback?.Invoke();
    }

    /// <summary>
    /// True only for offline → online. A first event (no previous state) is a cold start, not a
    /// reconnect, and a cooldown keeps a flapping interface from firing repeatedly.
    /// </summary>
    public static bool ShouldTriggerReconnect(bool? previous, bool current, DateTimeOffset? lastTriggeredAt = null,
        DateTimeOffset? now = null, TimeSpan? cooldown = null)
    {
        if (previous is not { } prev) return false;
        if (prev || !current) return false;
        if (lastTriggeredAt is { } last && (now ?? DateTimeOffset.UtcNow) - last < (cooldown ?? TimeSpan.FromSeconds(5)))
            return false;
        return true;
    }
}
