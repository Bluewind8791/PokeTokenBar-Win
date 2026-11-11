using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>AppEnv.allowsLiveLimitsFetch</c>: a limits call with the user's credentials leaves the
/// machine only from the installed app. Tests either inject an <see cref="HttpClient"/> (a fake handler,
/// so nothing leaves the machine) or stop here, even when a real login exists on the machine.
/// </summary>
public static class LiveFetchGate
{
    public static bool AllowsLiveLimitsFetch => AppEnv.IsInstalledApp;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The injected test client, or the shared one when the gate is open.</summary>
    internal static HttpClient Client(HttpClient? injected) =>
        injected ?? (AllowsLiveLimitsFetch ? Shared : throw LimitsError.Of(LimitsErrorKind.LiveFetchNotPermitted));

    /// <summary>A plain public GET (status pages, GitHub) that carries no credential: same gate.</summary>
    internal static HttpClient? PublicClient(HttpClient? injected) =>
        injected ?? (AllowsLiveLimitsFetch ? Shared : null);

    /// <summary>
    /// Retry-After in seconds only; an HTTP-date or junk gives null (the default backoff). Capped at an
    /// hour in case the server sends something excessive.
    /// </summary>
    public static TimeSpan? RetryAfterSeconds(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        var raw = values.FirstOrDefault()?.Trim();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0) return null;
        return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
    }
}

/// <summary>
/// Port of <c>OAuthLimitsProvider</c>: official limit percentages from the usage endpoint with the
/// Claude Code OAuth token. The endpoint is unofficial; a failure only hides the limits section.
/// </summary>
public sealed class OAuthLimitsProvider : IClaudeLimitsProviding
{
    private static readonly Uri UsageURL = new("https://api.anthropic.com/api/oauth/usage");
    private readonly OAuthAccessTokenCache _tokens;
    private readonly HttpClient? _http;
    private readonly OAuthProfileCache _profiles;

    /// <param name="accessTokenCache">Defaults to the <c>~/.claude</c> login; extra accounts pass
    /// <see cref="OAuthAccessTokenCache.ForConfigRoot"/>.</param>
    /// <param name="http">Test client (fake handler). Null uses the live client behind <see cref="LiveFetchGate"/>.</param>
    public OAuthLimitsProvider(OAuthAccessTokenCache? accessTokenCache = null, HttpClient? http = null,
        OAuthProfileCache? profileCache = null)
    {
        _tokens = accessTokenCache ?? OAuthAccessTokenCache.Shared;
        _http = http;
        _profiles = profileCache
            ?? (http == null ? OAuthProfileCache.Shared : new OAuthProfileCache(t => OAuthProfileCache.NetworkIdentity(t, http)));
    }

    public async Task<LimitStatus> Fetch(bool allowKeychainPrompt = false)
    {
        // A user refresh skips the in-memory token: while the previous account's token is still valid
        // no 401 comes, and the cache would hide the new login forever (#300).
        var token = _tokens.AccessToken(allowKeychainPrompt, bypassCache: allowKeychainPrompt);
        var activeToken = token;
        LimitStatus status;
        try
        {
            status = await FetchStatus(token);
        }
        catch (LimitsError error) when (error.Kind == LimitsErrorKind.HttpStatus && error.Status is 401 or 403)
        {
            _tokens.Invalidate();
            var refreshed = _tokens.AccessToken(allowKeychainPrompt, bypassCache: true);
            if (refreshed == token) throw;
            status = await FetchStatus(refreshed);
            activeToken = refreshed;
        }
        // The plan sits in the credential just read, not in the usage response.
        var plan = _tokens.PlanInfo();
        status.SubscriptionType = plan.SubscriptionType;
        status.RateLimitTier = plan.RateLimitTier;
        // Two logins can take turns writing the same credential slot, silently swapping whose limits the
        // bars show. The profile endpoint names the token's real owner. Best effort: no label on failure.
        if (await _profiles.Identity(activeToken) is { } identity)
        {
            status.AccountEmail = identity.Email;
            status.AccountOrganizationName = identity.OrganizationName;
        }
        return status;
    }

    private async Task<LimitStatus> FetchStatus(string accessToken)
    {
        var client = LiveFetchGate.Client(_http);
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageURL);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, timeout.Token);
        var code = (int)response.StatusCode;
        if (code != 200)
        {
            if (code == 429) throw LimitsError.RateLimited(LiveFetchGate.RetryAfterSeconds(response));
            throw LimitsError.Http(code);
        }
        return LimitStatus.Decode(await response.Content.ReadAsByteArrayAsync(timeout.Token));
    }
}

/// <summary>The OAuth token's real owner. The usage response names no account, the profile endpoint does.</summary>
public sealed record AccountIdentity(string Email, string? OrganizationName);

/// <summary>
/// Port of <c>OAuthProfileCache</c>: one profile request per token, since the owner cannot change while
/// the token does not (otherwise every limits poll would double its requests). Failures are not cached:
/// right after a token change, a stale label from the previous account is worse than none.
/// </summary>
public sealed class OAuthProfileCache
{
    public static readonly OAuthProfileCache Shared = new();
    /// <summary>Keyed by token, since several accounts are polled in turn; bounded because tokens rotate.</summary>
    public const int MaxEntries = 8;
    private static readonly Uri ProfileURL = new("https://api.anthropic.com/api/oauth/profile");

    private readonly object _gate = new();
    private readonly Dictionary<string, AccountIdentity> _identities = new(StringComparer.Ordinal);
    private readonly Func<string, Task<AccountIdentity?>> _fetchIdentity;

    public OAuthProfileCache(Func<string, Task<AccountIdentity?>>? fetchIdentity = null)
    {
        _fetchIdentity = fetchIdentity ?? (token => NetworkIdentity(token));
    }

    public async Task<AccountIdentity?> Identity(string accessToken)
    {
        lock (_gate)
        {
            if (_identities.TryGetValue(accessToken, out var cached)) return cached;
        }
        if (await _fetchIdentity(accessToken) is not { } identity) return null;
        lock (_gate)
        {
            if (_identities.Count >= MaxEntries) _identities.Clear();
            _identities[accessToken] = identity;
        }
        return identity;
    }

    public static async Task<AccountIdentity?> NetworkIdentity(string accessToken, HttpClient? http = null)
    {
        if (http == null && !LiveFetchGate.AllowsLiveLimitsFetch) return null;
        try
        {
            var client = LiveFetchGate.Client(http);
            using var request = new HttpRequestMessage(HttpMethod.Get, ProfileURL);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await client.SendAsync(request, timeout.Token);
            if ((int)response.StatusCode != 200) return null;
            return OAuthProfileData.Identity(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Port of <c>OAuthProfileData</c>: profile response parsing, a pure function for fixtures.</summary>
public static class OAuthProfileData
{
    /// <summary>
    /// <c>{"account":{"email":…},"organization":{"name":…}}</c>. Without an email the whole response is
    /// dropped (a partial label misleads); the organization is optional.
    /// </summary>
    public static AccountIdentity? Identity(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object
                || !account.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String
                || email.GetString() is not { Length: > 0 } emailText) return null;
            string? org = null;
            if (root.TryGetProperty("organization", out var organization) && organization.ValueKind == JsonValueKind.Object
                && organization.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } nameText) org = nameText;
            return new AccountIdentity(emailText, org);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Port of <c>OAuthAccessTokenCache</c>. macOS reads the Claude Code credential from the Keychain item
/// <c>Claude Code-credentials</c>, with <c>~/.claude/.credentials.json</c> as a file alternative. Claude
/// Code on Windows writes the same JSON to <c>%USERPROFILE%\.claude\.credentials.json</c> (another
/// account: <c>&lt;config dir&gt;\.credentials.json</c>), so the file is the only source here: no prompt,
/// no Keychain gate. Like the macOS code it only reads; Claude Code refreshes the token itself.
/// </summary>
public sealed class OAuthAccessTokenCache
{
    public static readonly OAuthAccessTokenCache Shared = new();

    private readonly object _gate = new();
    private readonly string _credentialsFilePath;
    /// <summary>The <c>~/.claude</c> login: only it keeps the <c>CLAUDE_CONFIG_DIR</c> guard of the re-login hint.</summary>
    private readonly bool _isDefaultRoot;
    private OAuthCredentialData.Credential? _cachedCredential;

    /// <param name="credentialsFilePath">Null for the default login's file.</param>
    public OAuthAccessTokenCache(string? credentialsFilePath = null)
        : this(credentialsFilePath ?? DefaultCredentialsFilePath, isDefaultRoot: credentialsFilePath == null) { }

    private OAuthAccessTokenCache(string credentialsFilePath, bool isDefaultRoot)
    {
        _credentialsFilePath = credentialsFilePath;
        _isDefaultRoot = isDefaultRoot;
    }

    /// <summary>Token cache for an additional Claude config folder (a <c>CLAUDE_CONFIG_DIR</c> login).</summary>
    public static OAuthAccessTokenCache ForConfigRoot(string root) =>
        new(ClaudeAccountRoots.CredentialsFilePath(root), isDefaultRoot: false);

    public static string DefaultCredentialsFilePath =>
        Path.Combine(ClaudeAccountRoots.DefaultConfigDir(), ".credentials.json");

    /// <param name="allowKeychainPrompt">No-op on Windows (no Keychain). Kept for the call shape.</param>
    /// <param name="bypassCache">A user refresh: the file alone decides, the in-memory token is not used (#300).</param>
    public string AccessToken(bool allowKeychainPrompt = false, bool bypassCache = false)
    {
        lock (_gate)
        {
            // The file comes before the cache. `/login` to another email of the same team rewrites the
            // file with a new *valid* token; answering from the cache until expiry kept the official bars
            // on the previous account while companion EXP kept moving (#227).
            var data = ReadFile(_credentialsFilePath);
            var credential = data == null ? null : OAuthCredentialData.CredentialFrom(data);
            if (credential is { IsExpired: false })
            {
                _cachedCredential = credential;
                return credential.AccessToken;
            }
            // The file lost its token (logout, a leftover being cleaned up): the cache holds until expiry.
            if (!bypassCache && _cachedCredential is { IsExpired: false } cached) return cached.AccessToken;

            // macOS would read the Keychain here on a manual refresh. On Windows the file was the only
            // source, so say what is wrong with it.
            if (credential != null) throw LimitsError.Of(LimitsErrorKind.CredentialExpired);
            if (data == null) throw LimitsError.Of(LimitsErrorKind.NoCredential);
            if (OAuthCredentialData.IsAccountOAuthMissing(data))
                throw LimitsError.Of(AccountOAuthHintApplies() ? LimitsErrorKind.CredentialMissingAccountOAuth : LimitsErrorKind.NoCredential);
            throw LimitsError.Of(LimitsErrorKind.CredentialFormat);
        }
    }

    /// <summary>
    /// A user who moved the config (<c>CLAUDE_CONFIG_DIR</c>) can keep an old default file that holds only
    /// <c>mcpOAuth</c>; the re-login hint would then show on every poll to a logged-in user. An
    /// additional folder's file is the one Claude Code writes for that login, so the guard does not apply.
    /// </summary>
    private bool AccountOAuthHintApplies() =>
        !_isDefaultRoot || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

    /// <summary>Plan of the credential last used. <see cref="AccessToken"/> keeps it in step with the token returned.</summary>
    public (string? SubscriptionType, string? RateLimitTier) PlanInfo()
    {
        lock (_gate) return (_cachedCredential?.SubscriptionType, _cachedCredential?.RateLimitTier);
    }

    public void Invalidate()
    {
        lock (_gate) _cachedCredential = null;
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Port of <c>OAuthCredentialData</c> (the Keychain query builders are macOS-only and dropped).</summary>
public static class OAuthCredentialData
{
    public sealed record Credential(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType, string? RateLimitTier)
    {
        /// <summary>A minute of margin, so a token does not die between the read and the request.</summary>
        public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.AddSeconds(60);
    }

    /// <summary>
    /// Valid JSON without <c>claudeAiOauth</c>: Claude Code 2.1.x can keep only MCP server OAuth there,
    /// which needs a re-login rather than a format fix. An explicit <c>"claudeAiOauth": null</c> (logout)
    /// counts as missing too: the check is "is it an object", not "is the key present".
    /// </summary>
    public static bool IsAccountOAuthMissing(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && !(doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Credential? CredentialFrom(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String
                || token.GetString() is not { Length: > 0 } tokenText) return null;
            return new Credential(
                tokenText,
                oauth.TryGetProperty("expiresAt", out var expires) ? ExpiresAt(expires) : null,
                StringOrNull(oauth, "subscriptionType"),
                StringOrNull(oauth, "rateLimitTier"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Seconds or milliseconds since 1970, as a number or a numeric string.</summary>
    private static DateTimeOffset? ExpiresAt(JsonElement raw)
    {
        double? value = raw.ValueKind switch
        {
            JsonValueKind.Number => raw.GetDouble(),
            JsonValueKind.String when double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
        if (value is not > 0 || !double.IsFinite(value.Value)) return null;
        var seconds = value.Value > 10_000_000_000 ? value.Value / 1000 : value.Value;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
