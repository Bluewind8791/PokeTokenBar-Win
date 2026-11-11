using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeTokenBar.Core;

// Port of SessionKeyLimitsProvider.swift: official limits through the claude.ai session cookie
// (`sessionKey`). On macOS this path exists because the OAuth path reads the Keychain, which automatic
// polls must not do. Windows has no such prompt, but the path is kept: it is the only one that does not
// depend on Claude Code having refreshed its token recently, and it lets extra accounts be followed
// without their Claude Code login. The response schema equals api.anthropic.com/api/oauth/usage, so
// `LimitStatus` is reused.
//
// Verification trap (macOS notes): the endpoint cannot be checked with curl. Cloudflare challenges curl
// with a 403 (`cf-mitigated: challenge`); whether .NET's HttpClient passes like URLSession is unverified.

/// <summary>One claude.ai GET response.</summary>
public sealed record SessionKeyHTTPResponse(int Status, byte[] Data, TimeSpan? RetryAfter);

/// <summary>One claude.ai GET. Tests swap this layer for fixtures (live calls are impossible in CI).</summary>
public interface ISessionKeyHTTPClient
{
    Task<SessionKeyHTTPResponse> Get(Uri url, string sessionKey);
}

/// <summary>Port of <c>URLSessionSessionKeyClient</c>.</summary>
public sealed class HttpClientSessionKeyClient(HttpClient? http = null) : ISessionKeyHTTPClient
{
    /// <summary>claude.ai expects browser requests: without Origin/Referer/UA it may refuse.</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>
    /// No cookie container: the cookie goes in the header by hand, and a shared store could add or
    /// replace cookies from earlier responses and swap the account.
    /// </summary>
    private static readonly HttpClient Live = new(new SocketsHttpHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public async Task<SessionKeyHTTPResponse> Get(Uri url, string sessionKey)
    {
        // Tests inject their own client (or their own ISessionKeyHTTPClient), so this only stops real calls.
        if (http == null && !LiveFetchGate.AllowsLiveLimitsFetch) throw LimitsError.Of(LimitsErrorKind.LiveFetchNotPermitted);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionKey={sessionKey}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://claude.ai");
        request.Headers.TryAddWithoutValidation("Origin", "https://claude.ai");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await (http ?? Live).SendAsync(request, timeout.Token);
        return new SessionKeyHTTPResponse(
            (int)response.StatusCode,
            await response.Content.ReadAsByteArrayAsync(timeout.Token),
            LiveFetchGate.RetryAfterSeconds(response));
    }
}

/// <summary>Port of <c>SessionKeyCredential</c>.</summary>
/// <param name="OrganizationID">The organization last picked. When set, the organization list is skipped.</param>
public sealed record SessionKeyCredential(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("organizationID")] string? OrganizationID = null);

/// <summary>
/// Port of <c>SessionKeyStore</c>. macOS keeps the key as owner-only plain JSON because an app-owned
/// Keychain item prompts after every re-sign. Windows has no such prompt: the JSON is encrypted with
/// DPAPI (CurrentUser) so only this Windows user can read it, in a file under the state directory.
/// </summary>
public sealed class SessionKeyStore
{
    /// <summary>Ties the blob to this use, so another app's DPAPI call cannot read it by accident.</summary>
    private static readonly byte[] Entropy = "PokeTokenBar.session-key"u8.ToArray();

    public string FilePath { get; }

    /// <param name="filePath">Null for the default key file. The default follows <c>PTB_STATE_DIR</c>
    /// like the rest of the app state, so QA and demo runs never touch the real key.</param>
    public SessionKeyStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(AppEnv.StateDirectory(), "session-key.dat");
    }

    /// <summary>
    /// An additional Claude account's own key, next to the default one. Named by the folder's path hash,
    /// like its alert and candy keys, so no path ends up in a file name.
    /// </summary>
    public static SessionKeyStore ForConfigRoot(string root, string? directory = null) =>
        new(Path.Combine(directory ?? AppEnv.StateDirectory(), $"session-key-{ClaudeAccountRoots.PathKey(root)}.dat"));

    public SessionKeyCredential? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var credential = JsonSerializer.Deserialize<SessionKeyCredential>(plain);
            return string.IsNullOrEmpty(credential?.Key) ? null : credential;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Save(SessionKeyCredential credential)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is Windows-only");
        var blob = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credential), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Clear()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Cleans and checks a pasted value; copying from a browser often brings spaces or line breaks.
    /// Only the <c>sk-ant-</c> prefix is checked: both <c>sid01</c> and <c>sid02</c> keys exist.
    /// </summary>
    public static string Normalize(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("sk-ant-", StringComparison.Ordinal) || trimmed.Length < 40 || trimmed.Length > 2_048
            || trimmed.Any(char.IsControl))
            throw LimitsError.Of(LimitsErrorKind.SessionKeyMalformed);
        return trimmed;
    }
}

/// <summary>A candidate organization. Automatic selection and the Settings picker share it.</summary>
public sealed record SessionKeyOrganization(string Id, string Name, bool HasUsageData, LimitStatus Limits)
{
    /// <summary>Port of <c>UsageStore.accountOrganization</c>: the folder's own organization first,
    /// then the default key's rule (one with usage data, else the first).</summary>
    public static SessionKeyOrganization? AccountOrganization(IReadOnlyList<SessionKeyOrganization> organizations,
        string? loggedIn) =>
        organizations.FirstOrDefault(o => o.Id == loggedIn)
        ?? organizations.FirstOrDefault(o => o.HasUsageData) ?? organizations.FirstOrDefault();
}

/// <summary>Session key management as Settings sees it (tests substitute this interface).</summary>
public interface ISessionKeyManaging
{
    SessionKeyCredential? Credential();
    Task<List<SessionKeyOrganization>> Organizations(string sessionKey);
    void Save(string key, string? organizationID);
    void Clear();
}

/// <summary>Port of <c>SessionKeyLimitsProvider</c>.</summary>
public sealed class SessionKeyLimitsProvider : IClaudeLimitsProviding, ISessionKeyManaging
{
    private static readonly Lazy<SessionKeyLimitsProvider> LazyShared = new(() => new SessionKeyLimitsProvider());
    /// <summary>The limits chain and Settings must see the same file and organization cache.</summary>
    public static SessionKeyLimitsProvider Shared => LazyShared.Value;

    private const string Base = "https://claude.ai/api/organizations";

    private readonly SessionKeyStore _store;
    private readonly ISessionKeyHTTPClient _http;

    public SessionKeyLimitsProvider(SessionKeyStore? store = null, ISessionKeyHTTPClient? http = null)
    {
        _store = store ?? new SessionKeyStore();
        _http = http ?? new HttpClientSessionKeyClient();
    }

    /// <summary><paramref name="allowKeychainPrompt"/> is unused: this path never needs a prompt.</summary>
    public async Task<LimitStatus> Fetch(bool allowKeychainPrompt)
    {
        if (_store.Load() is not { } credential) throw LimitsError.Of(LimitsErrorKind.SessionKeyMissing);

        if (credential.OrganizationID is { } orgID)
        {
            try
            {
                return await Usage(orgID, credential.Key);
            }
            catch (LimitsError error) when (error.Kind == LimitsErrorKind.HttpStatus && error.Status == 403)
            {
                // 403 means "no access to this organization" (left it, access revoked); the key may be
                // alive, so pick again. A 401 (dead key) is not caught: rediscovery would get it too.
                AppLog.Write($"session key: cached org {orgID} forbidden, rediscovering");
            }
        }

        List<SessionKeyOrganization> candidates;
        try
        {
            candidates = await Organizations(credential.Key);
        }
        catch (LimitsError error) when (error.Kind == LimitsErrorKind.HttpStatus && error.Status == 403)
        {
            // `/api/organizations` is not organization-scoped: a 403 here means this key sees nothing,
            // i.e. it is dead (expired, browser logout). An organization permission issue only fails that
            // organization's usage and is handled by the rediscovery above. Measured 2026-08-30: a key with
            // one changed character got 403 on both usage and organizations, never 401.
            throw LimitsError.Of(LimitsErrorKind.SessionKeyInvalid);
        }
        // Prefer the organization with values: "the first one" can be a login-only organization that
        // would show 0% forever.
        var picked = candidates.FirstOrDefault(o => o.HasUsageData) ?? candidates.FirstOrDefault()
            ?? throw LimitsError.Of(LimitsErrorKind.SessionKeyNoOrganization);
        if (picked.Id != credential.OrganizationID)
        {
            try { _store.Save(credential with { OrganizationID = picked.Id }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
            AppLog.Write($"session key: selected org {picked.Id} (hasUsage={picked.HasUsageData})");
        }
        return picked.Limits;
    }

    public SessionKeyCredential? Credential() => _store.Load();

    public void Save(string key, string? organizationID) =>
        _store.Save(new SessionKeyCredential(SessionKeyStore.Normalize(key), organizationID));

    public void Clear() => _store.Clear();

    /// <summary>Reachable organizations, in list order. Those that answer with an HTTP error are left
    /// out: API-only organizations come back as 403.</summary>
    public async Task<List<SessionKeyOrganization>> Organizations(string sessionKey)
    {
        var response = await _http.Get(new Uri(Base), sessionKey);
        MapFailure(response);
        var rows = DecodeRows(response.Data);

        // Organizations without `chat` (API only) reject usage with 403, so they are not asked at all.
        // Without capabilities (schema drift) nothing is filtered: zero candidates would be worse.
        var candidates = rows.Where(r => r.Capabilities?.Contains("chat") ?? true).ToList();
        if (candidates.Count == 0) candidates = rows;

        var results = await Task.WhenAll(candidates.Select(async row =>
        {
            try
            {
                var limits = await Usage(row.Uuid!, sessionKey);
                return new SessionKeyOrganization(row.Uuid!, row.Name ?? row.Uuid!, HasUsageData(limits), limits);
            }
            catch (LimitsError error) when (error.Kind == LimitsErrorKind.HttpStatus)
            {
                return null;   // Only this organization is out of reach; the others go on.
            }
        }));
        // Task.WhenAll keeps the input order: the order Settings shows.
        return results.OfType<SessionKeyOrganization>().ToList();
    }

    private async Task<LimitStatus> Usage(string organizationID, string sessionKey)
    {
        var response = await _http.Get(new Uri($"{Base}/{Uri.EscapeDataString(organizationID)}/usage"), sessionKey);
        MapFailure(response);
        return LimitStatus.Decode(response.Data);
    }

    /// <summary>Throws unless 200. 401 is a dead key (re-enter it); 403 is left to the caller, since it
    /// can be an organization permission.</summary>
    private static void MapFailure(SessionKeyHTTPResponse response)
    {
        switch (response.Status)
        {
            case 200: return;
            case 401: throw LimitsError.Of(LimitsErrorKind.SessionKeyInvalid);
            case 429: throw LimitsError.RateLimited(response.RetryAfter);
            default: throw LimitsError.Http(response.Status);
        }
    }

    /// <summary>An organization in use: some utilization or a scheduled reset.</summary>
    private static bool HasUsageData(LimitStatus limits)
    {
        LimitWindow?[] windows = [limits.FiveHour, limits.SevenDay, limits.SevenDayOpus, limits.SevenDaySonnet];
        if (windows.Any(w => (w?.Utilization ?? 0) > 0 || w?.ResetsAt != null)) return true;
        return (limits.Limits ?? []).Any(e => (e.Percent ?? 0) > 0 || e.ResetsAt != null);
    }

    private sealed record OrganizationRow(
        [property: JsonPropertyName("uuid")] string? Uuid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("capabilities")] List<string>? Capabilities);

    private static List<OrganizationRow> DecodeRows(byte[] data)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<List<OrganizationRow>>(data);
            if (rows == null || rows.Any(r => string.IsNullOrEmpty(r.Uuid))) throw new JsonException("organization without uuid");
            return rows;
        }
        catch (JsonException)
        {
            throw LimitsError.Of(LimitsErrorKind.CredentialFormat);
        }
    }
}

/// <summary>
/// Port of <c>ChainedLimitsProvider</c>: the session key first, then the OAuth path. Users who set up
/// only one of them keep working.
/// </summary>
public sealed class ChainedLimitsProvider(IClaudeLimitsProviding primary, IClaudeLimitsProviding fallback) : IClaudeLimitsProviding
{
    public IClaudeLimitsProviding Primary { get; } = primary;
    public IClaudeLimitsProviding Fallback { get; } = fallback;

    /// <summary>An additional Claude folder: its own session key first, then the folder's Claude Code token.</summary>
    public static ChainedLimitsProvider ForConfigRoot(string root, string? sessionKeyDirectory = null,
        ISessionKeyHTTPClient? http = null, IClaudeLimitsProviding? fallback = null) =>
        new(new SessionKeyLimitsProvider(SessionKeyStore.ForConfigRoot(root, sessionKeyDirectory), http),
            fallback ?? new OAuthLimitsProvider(OAuthAccessTokenCache.ForConfigRoot(root)));

    public async Task<LimitStatus> Fetch(bool allowKeychainPrompt)
    {
        try
        {
            return await Primary.Fetch(allowKeychainPrompt);
        }
        catch (Exception error)
        {
            // Without a key this is the normal flow, so nothing is logged.
            var configured = !(error is LimitsError { Kind: LimitsErrorKind.SessionKeyMissing });
            if (configured) AppLog.Write($"session key limits failed: {error.Message}");
            try
            {
                // With a key configured the fallback is never a user refresh: on macOS that would reopen
                // the Keychain prompt the key exists to avoid. A dead key is fixed by entering a new one.
                return await Fallback.Fetch(configured ? false : allowKeychainPrompt);
            }
            catch (Exception) when (configured)
            {
                // A configured but broken key is the more useful message: re-entering it fixes it.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                throw;   // unreachable
            }
        }
    }
}
