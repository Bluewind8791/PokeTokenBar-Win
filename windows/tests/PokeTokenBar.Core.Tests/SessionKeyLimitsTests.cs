using System.Text;
using PokeTokenBar.Core;
using static PokeTokenBar.Core.Tests.LimitFixtures;

namespace PokeTokenBar.Core.Tests;

/// <summary>path → response; records every path and key. Organization usage is fetched in parallel.</summary>
internal sealed class StubSessionKeyHTTP(Dictionary<string, SessionKeyHTTPResponse> responses) : ISessionKeyHTTPClient
{
    private readonly object _gate = new();
    private readonly List<string> _paths = [];

    public List<string> Paths() { lock (_gate) return _paths.ToList(); }

    public Task<SessionKeyHTTPResponse> Get(Uri url, string sessionKey)
    {
        lock (_gate) _paths.Add(url.AbsolutePath);
        return Task.FromResult(responses.GetValueOrDefault(url.AbsolutePath) ?? SessionKeyFixtures.Fail(404));
    }
}

internal static class SessionKeyFixtures
{
    public const string OrgsJSON = """
        [
          {"uuid":"org-personal","name":"user@example.com's Organization",
           "capabilities":["chat"],"rate_limit_tier":"default_claude_ai"},
          {"uuid":"org-api","name":"Individual Org",
           "capabilities":["api","api_individual"],"rate_limit_tier":"auto_api_evaluation"},
          {"uuid":"org-team","name":"Team Plan",
           "capabilities":["raven","chat"],"rate_limit_tier":"default_raven"}
        ]
        """;

    /// <summary>A login-only organization: utilization 0, resets_at null. In reality it comes first.</summary>
    public const string IdleUsageJSON = """
        {"five_hour":{"utilization":0,"resets_at":null},
         "seven_day":{"utilization":0,"resets_at":null},
         "seven_day_opus":null,"seven_day_sonnet":null,
         "limits":[{"kind":"session","group":"session","percent":0,"severity":"normal","resets_at":null}]}
        """;

    public const string ActiveUsageJSON = """
        {"five_hour":{"utilization":7,"resets_at":"2026-08-21T17:20:00.186770+00:00"},
         "seven_day":{"utilization":30,"resets_at":"2026-08-25T23:00:00.186790+00:00"},
         "seven_day_opus":null,"seven_day_sonnet":null,
         "limits":[{"kind":"session","group":"session","percent":7,"severity":"normal",
                    "resets_at":"2026-08-21T17:20:00.186770+00:00"}]}
        """;

    public const string ForbiddenJSON =
        """{"type":"error","error":{"type":"permission_error","message":"Invalid authorization for organization"}}""";

    public static SessionKeyHTTPResponse Ok(string json) => new(200, Encoding.UTF8.GetBytes(json), null);

    public static SessionKeyHTTPResponse Fail(int status, string json = "{}", TimeSpan? retryAfter = null) =>
        new(status, Encoding.UTF8.GetBytes(json), retryAfter);

    public const string OrgsPath = "/api/organizations";
    public static string UsagePath(string id) => $"/api/organizations/{id}/usage";

    public static string Key(char c) => "sk-ant-sid02-" + new string(c, 60);
}

/// <summary>Port of SessionKeyLimitsTests.swift: credential storage, organization pick, OAuth fallback chain.</summary>
public sealed class SessionKeyLimitsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SessionKeyStore _store;

    public SessionKeyLimitsTests() => _store = new SessionKeyStore(Path.Combine(_dir.Path, "session-key.dat"));

    public void Dispose() => _dir.Dispose();

    private void Seed(string? key = null, string? org = null) =>
        _store.Save(new SessionKeyCredential(key ?? SessionKeyFixtures.Key('a'), org));

    private static async Task<LimitsError> Throws(Func<Task> action) => await Assert.ThrowsAsync<LimitsError>(action);

    // Store

    /// <summary>DPAPI replaces the macOS owner-only plain file: the key must not be readable on disk.</summary>
    [Fact]
    public void StoreRoundTripsCredentialEncryptedOnDisk()
    {
        Seed(org: "org-team");
        var loaded = _store.Load();
        Assert.Equal("org-team", loaded?.OrganizationID);
        Assert.StartsWith("sk-ant-sid02-", loaded?.Key);

        var raw = File.ReadAllBytes(_store.FilePath);
        Assert.DoesNotContain("sk-ant-", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("org-team", Encoding.UTF8.GetString(raw));

        _store.Clear();
        Assert.Null(_store.Load());
    }

    [Fact]
    public void AnUnreadableFileIsNoCredential()
    {
        File.WriteAllText(_store.FilePath, """{"key":"sk-ant-plaintext"}""");
        Assert.Null(_store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("not-a-key-but-long-enough-to-pass-length-checks-only")]
    [InlineData("sk-ant-\0embedded-nul-plus-padding-to-reach-minimum-length-xxxx")]
    public void StoreRejectsMalformedKeys(string bad)
    {
        var error = Assert.Throws<LimitsError>(() => SessionKeyStore.Normalize(bad));
        Assert.Equal(LimitsErrorKind.SessionKeyMalformed, error.Kind);
    }

    [Fact]
    public void StoreNormalizesSurroundingWhitespace()
    {
        var raw = "  " + SessionKeyFixtures.Key('b') + "\n";
        Assert.Equal(raw.Trim(), SessionKeyStore.Normalize(raw));
    }

    // Organization pick

    /// <summary>The reference implementation (`organizations.first!`) shows 0% forever on this fixture.</summary>
    [Fact]
    public async Task FetchSkipsForbiddenOrgAndPicksTheOneWithUsage()
    {
        Seed();
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok(SessionKeyFixtures.OrgsJSON),
            [SessionKeyFixtures.UsagePath("org-personal")] = SessionKeyFixtures.Ok(SessionKeyFixtures.IdleUsageJSON),
            [SessionKeyFixtures.UsagePath("org-api")] = SessionKeyFixtures.Fail(403, SessionKeyFixtures.ForbiddenJSON),
            [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON),
        });
        var limits = await new SessionKeyLimitsProvider(_store, http).Fetch(false);

        Assert.Equal(7, limits.FiveHour?.Utilization);
        Assert.Equal(30, limits.SevenDay?.Utilization);
        Assert.Equal("org-team", _store.Load()?.OrganizationID);
    }

    [Fact]
    public async Task FetchIgnoresKeychainPromptFlag()
    {
        Seed(org: "org-team");
        var http = new StubSessionKeyHTTP(new() { [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON) });
        var provider = new SessionKeyLimitsProvider(_store, http);
        foreach (var allow in new[] { false, true })
            Assert.Equal(7, (await provider.Fetch(allow)).FiveHour?.Utilization);
    }

    [Fact]
    public async Task FetchReusesCachedOrgWithoutListingOrganizations()
    {
        Seed(org: "org-team");
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok(SessionKeyFixtures.OrgsJSON),
            [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON),
        });
        await new SessionKeyLimitsProvider(_store, http).Fetch(false);
        Assert.Equal([SessionKeyFixtures.UsagePath("org-team")], http.Paths());
    }

    [Fact]
    public async Task FetchRerunsDiscoveryWhenCachedOrgLosesAccess()
    {
        Seed(org: "org-api");
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok(SessionKeyFixtures.OrgsJSON),
            [SessionKeyFixtures.UsagePath("org-personal")] = SessionKeyFixtures.Ok(SessionKeyFixtures.IdleUsageJSON),
            [SessionKeyFixtures.UsagePath("org-api")] = SessionKeyFixtures.Fail(403, SessionKeyFixtures.ForbiddenJSON),
            [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON),
        });
        var limits = await new SessionKeyLimitsProvider(_store, http).Fetch(false);
        Assert.Equal(7, limits.FiveHour?.Utilization);
        Assert.Equal("org-team", _store.Load()?.OrganizationID);
    }

    [Fact]
    public async Task FetchUsesSingleChatOrgWithoutProbingApiOnlyOrgs()
    {
        Seed();
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok("""
                [{"uuid":"org-solo","name":"Me","capabilities":["chat"]},
                 {"uuid":"org-api","name":"API","capabilities":["api"]}]
                """),
            [SessionKeyFixtures.UsagePath("org-solo")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON),
        });
        var limits = await new SessionKeyLimitsProvider(_store, http).Fetch(false);
        Assert.Equal(7, limits.FiveHour?.Utilization);
        Assert.DoesNotContain(SessionKeyFixtures.UsagePath("org-api"), http.Paths());
    }

    // Schema drift

    [Fact]
    public async Task OrganizationsKeepRowsMissingCapabilitiesAndFallBackToUUIDName()
    {
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok("""[{"uuid":"org-unknown-shape"}]"""),
            [SessionKeyFixtures.UsagePath("org-unknown-shape")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON),
        });
        var orgs = await new SessionKeyLimitsProvider(_store, http).Organizations(SessionKeyFixtures.Key('d'));
        Assert.Equal(["org-unknown-shape"], orgs.Select(o => o.Id));
        Assert.Equal("org-unknown-shape", orgs[0].Name);
    }

    [Fact]
    public async Task FetchFallsBackToAllRowsWhenNoChatCapableOrgExists()
    {
        Seed();
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok("""[{"uuid":"org-api-1","name":"API One","capabilities":["api"]}]"""),
            [SessionKeyFixtures.UsagePath("org-api-1")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON),
        });
        Assert.Equal(7, (await new SessionKeyLimitsProvider(_store, http).Fetch(false)).FiveHour?.Utilization);
    }

    [Fact]
    public async Task UsageDetectionUsesLimitsArrayWhenLegacyWindowsAreAbsent()
    {
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok("""
                [{"uuid":"org-empty","name":"Empty","capabilities":["chat"]},
                 {"uuid":"org-new","name":"New shape","capabilities":["chat"]}]
                """),
            [SessionKeyFixtures.UsagePath("org-empty")] = SessionKeyFixtures.Ok("{}"),
            [SessionKeyFixtures.UsagePath("org-new")] = SessionKeyFixtures.Ok("""
                {"limits":[{"kind":"session","group":"session","resets_at":"2026-08-21T17:20:00.186770+00:00"}]}
                """),
        });
        var orgs = await new SessionKeyLimitsProvider(_store, http).Organizations(SessionKeyFixtures.Key('e'));
        Assert.Equal([false, true], orgs.Select(o => o.HasUsageData));
    }

    // Error mapping

    [Fact]
    public async Task FetchThrowsMissingWhenNoCredentialStored()
    {
        var http = new StubSessionKeyHTTP([]);
        var error = await Throws(() => new SessionKeyLimitsProvider(_store, http).Fetch(false));
        Assert.Equal(LimitsErrorKind.SessionKeyMissing, error.Kind);
        Assert.Empty(http.Paths());
    }

    /// <summary>[Measured] A dead key gets 403, not 401, on the non-org-scoped organization list.</summary>
    [Fact]
    public async Task ForbiddenOrganizationListMapsToInvalidKey()
    {
        Seed();
        var http = new StubSessionKeyHTTP(new() { [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Fail(403) });
        var error = await Throws(() => new SessionKeyLimitsProvider(_store, http).Fetch(false));
        Assert.Equal(LimitsErrorKind.SessionKeyInvalid, error.Kind);
    }

    [Fact]
    public async Task UnauthorizedOrganizationListMapsToInvalidKey()
    {
        Seed();
        var http = new StubSessionKeyHTTP(new() { [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Fail(401) });
        var error = await Throws(() => new SessionKeyLimitsProvider(_store, http).Fetch(false));
        Assert.Equal(LimitsErrorKind.SessionKeyInvalid, error.Kind);
    }

    [Fact]
    public async Task UnauthorizedUsageMapsToInvalidKeyWithoutRediscovery()
    {
        Seed(org: "org-team");
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok(SessionKeyFixtures.OrgsJSON),
            [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Fail(401),
        });
        var error = await Throws(() => new SessionKeyLimitsProvider(_store, http).Fetch(false));
        Assert.Equal(LimitsErrorKind.SessionKeyInvalid, error.Kind);
        Assert.DoesNotContain(SessionKeyFixtures.OrgsPath, http.Paths());
    }

    [Fact]
    public async Task RateLimitedMapsToBackoffErrorWithRetryAfter()
    {
        Seed(org: "org-team");
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Fail(429, retryAfter: TimeSpan.FromSeconds(120)),
        });
        var error = await Throws(() => new SessionKeyLimitsProvider(_store, http).Fetch(false));
        Assert.Equal(LimitsError.RateLimited(TimeSpan.FromSeconds(120)), error);
    }

    [Fact]
    public async Task NoAccessibleOrganizationIsItsOwnError()
    {
        Seed();
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok(SessionKeyFixtures.OrgsJSON),
            [SessionKeyFixtures.UsagePath("org-personal")] = SessionKeyFixtures.Fail(403, SessionKeyFixtures.ForbiddenJSON),
            [SessionKeyFixtures.UsagePath("org-api")] = SessionKeyFixtures.Fail(403, SessionKeyFixtures.ForbiddenJSON),
            [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Fail(403, SessionKeyFixtures.ForbiddenJSON),
        });
        var error = await Throws(() => new SessionKeyLimitsProvider(_store, http).Fetch(false));
        Assert.Equal(LimitsErrorKind.SessionKeyNoOrganization, error.Kind);
    }

    [Fact]
    public async Task OrganizationsReportsCandidatesWithAccessAndUsageFlags()
    {
        var http = new StubSessionKeyHTTP(new()
        {
            [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Ok(SessionKeyFixtures.OrgsJSON),
            [SessionKeyFixtures.UsagePath("org-personal")] = SessionKeyFixtures.Ok(SessionKeyFixtures.IdleUsageJSON),
            [SessionKeyFixtures.UsagePath("org-api")] = SessionKeyFixtures.Fail(403, SessionKeyFixtures.ForbiddenJSON),
            [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON),
        });
        var orgs = await new SessionKeyLimitsProvider(_store, http).Organizations(SessionKeyFixtures.Key('c'));
        Assert.Equal(["org-personal", "org-team"], orgs.Select(o => o.Id));
        Assert.Equal([false, true], orgs.Select(o => o.HasUsageData));
        Assert.Equal("Team Plan", orgs[^1].Name);
    }

    // Chain (session key first, OAuth fallback)

    [Fact]
    public async Task ChainPrefersSessionKeyAndNeverCallsFallback()
    {
        Seed(org: "org-team");
        var http = new StubSessionKeyHTTP(new() { [SessionKeyFixtures.UsagePath("org-team")] = SessionKeyFixtures.Ok(SessionKeyFixtures.ActiveUsageJSON) });
        var fallback = new ScriptedLimits(ScriptedLimits.Ok(Status(99)));
        var chain = new ChainedLimitsProvider(new SessionKeyLimitsProvider(_store, http), fallback);
        Assert.Equal(7, (await chain.Fetch(false)).FiveHour?.Utilization);
        Assert.Empty(fallback.PromptFlags);
    }

    [Fact]
    public async Task ChainFallsBackToOAuthWhenNoSessionKeyStored()
    {
        var chain = new ChainedLimitsProvider(new SessionKeyLimitsProvider(_store, new StubSessionKeyHTTP([])),
            new ScriptedLimits(ScriptedLimits.Ok(Status(42))));
        Assert.Equal(42, (await chain.Fetch(false)).FiveHour?.Utilization);
    }

    [Fact]
    public async Task ChainSurfacesFallbackErrorWhenSessionKeyIsAbsent()
    {
        var chain = new ChainedLimitsProvider(new SessionKeyLimitsProvider(_store, new StubSessionKeyHTTP([])),
            new ScriptedLimits(ScriptedLimits.Fail(LimitsError.Of(LimitsErrorKind.CredentialMissingAccountOAuth))));
        var error = await Throws(() => chain.Fetch(false));
        Assert.Equal(LimitsErrorKind.CredentialMissingAccountOAuth, error.Kind);
    }

    [Fact]
    public async Task ChainSurfacesSessionKeyErrorWhenKeyIsPresentButBroken()
    {
        Seed();
        var chain = new ChainedLimitsProvider(
            new SessionKeyLimitsProvider(_store, new StubSessionKeyHTTP(new() { [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Fail(401) })),
            new ScriptedLimits(ScriptedLimits.Fail(LimitsError.Of(LimitsErrorKind.NoCredential))));
        var error = await Throws(() => chain.Fetch(false));
        Assert.Equal(LimitsErrorKind.SessionKeyInvalid, error.Kind);
    }

    [Fact]
    public async Task ChainStillFallsBackWhenSessionKeyIsBroken()
    {
        Seed();
        var chain = new ChainedLimitsProvider(
            new SessionKeyLimitsProvider(_store, new StubSessionKeyHTTP(new() { [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Fail(401) })),
            new ScriptedLimits(ScriptedLimits.Ok(Status(42))));
        Assert.Equal(42, (await chain.Fetch(false)).FiveHour?.Utilization);
    }

    /// <summary>With a key configured the fallback never gets a user refresh (the macOS Keychain prompt guard).</summary>
    [Fact]
    public async Task ConfiguredButBrokenKeyNeverPassesAUserRefreshToTheFallback()
    {
        Seed();
        var fallback = new ScriptedLimits(ScriptedLimits.Ok(Status(42)));
        var chain = new ChainedLimitsProvider(
            new SessionKeyLimitsProvider(_store, new StubSessionKeyHTTP(new() { [SessionKeyFixtures.OrgsPath] = SessionKeyFixtures.Fail(401) })),
            fallback);
        await chain.Fetch(true);
        Assert.Equal([false], fallback.PromptFlags);
    }

    /// <summary>The other side of the gate: without a key the user refresh passes through unchanged.</summary>
    [Fact]
    public async Task WithoutAStoredKeyAUserRefreshPassesThrough()
    {
        var fallback = new ScriptedLimits(ScriptedLimits.Ok(Status(7)));
        var chain = new ChainedLimitsProvider(new SessionKeyLimitsProvider(_store, new StubSessionKeyHTTP([])), fallback);
        await chain.Fetch(true);
        Assert.Equal([true], fallback.PromptFlags);
    }

    /// <summary>The real HTTP client: cookie and browser headers go on the request, Retry-After is read.</summary>
    [Fact]
    public async Task HttpClientSessionKeyClientSendsTheCookieAndReadsRetryAfter()
    {
        var handler = new StubHandler(_ =>
        {
            var response = StubHandler.Json("{}", System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "17");
            return response;
        });
        var response = await new HttpClientSessionKeyClient(handler.Client())
            .Get(new Uri("https://claude.ai/api/organizations"), "sk-ant-x");
        Assert.Equal(429, response.Status);
        Assert.Equal(TimeSpan.FromSeconds(17), response.RetryAfter);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("sessionKey=sk-ant-x", string.Join("", request.Headers.GetValues("Cookie")));
        Assert.Equal("https://claude.ai", string.Join("", request.Headers.GetValues("Origin")));
    }

    [Fact]
    public async Task TheLiveSessionKeyClientIsGatedOutsideTheApp()
    {
        var error = await Throws(() => new HttpClientSessionKeyClient().Get(new Uri("https://claude.ai/api/organizations"), "sk-ant-x"));
        Assert.Equal(LimitsErrorKind.LiveFetchNotPermitted, error.Kind);
    }
}

/// <summary>Port of AccountSessionKeyChainTests: a session key per additional Claude folder.</summary>
public sealed class AccountSessionKeyChainTests : IDisposable
{
    private const string UsageJSON = """{"five_hour":{"utilization":42,"resets_at":null},"seven_day":{"utilization":10,"resets_at":null}}""";
    private readonly TempDir _dir = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "example", ".claude-personal");

    public void Dispose() => _dir.Dispose();

    private void SaveKey(string organizationID) =>
        SessionKeyStore.ForConfigRoot(_root, _dir.Path).Save(new SessionKeyCredential("sk-ant-sid01-" + new string('a', 40), organizationID));

    [Fact]
    public void EachFolderHasItsOwnKeyFileNextToTheDefaultOne()
    {
        var store = SessionKeyStore.ForConfigRoot(_root, _dir.Path);
        Assert.Equal($"session-key-{ClaudeAccountRoots.PathKey(_root)}.dat", Path.GetFileName(store.FilePath));
        Assert.Equal(_dir.Path, Path.GetDirectoryName(store.FilePath));
        var work = SessionKeyStore.ForConfigRoot(Path.Combine(Path.GetTempPath(), "example", ".claude-work"), _dir.Path);
        Assert.NotEqual(work.FilePath, store.FilePath);
    }

    [Fact]
    public async Task AnAutomaticPollUsesTheFolderKeyInsteadOfItsToken()
    {
        SaveKey("org-personal");
        var token = new ScriptedLimits(ScriptedLimits.Fail(LimitsError.Of(LimitsErrorKind.NoCredential)));
        var chain = ChainedLimitsProvider.ForConfigRoot(_root, _dir.Path,
            new StubSessionKeyHTTP(new() { ["/api/organizations/org-personal/usage"] = SessionKeyFixtures.Ok(UsageJSON) }), token);
        Assert.Equal(42, (await chain.Fetch(false)).FiveHour?.Utilization);
        Assert.Empty(token.PromptFlags);
    }

    [Fact]
    public async Task WithoutAKeyTheFolderTokenWorksAsBefore()
    {
        var token = new ScriptedLimits(ScriptedLimits.Ok(Status(5)));
        var chain = ChainedLimitsProvider.ForConfigRoot(_root, _dir.Path, new StubSessionKeyHTTP([]), token);
        Assert.Equal(5, (await chain.Fetch(true)).FiveHour?.Utilization);
        Assert.Equal([true], token.PromptFlags);
    }

    [Fact]
    public async Task ADeadKeyNeverFallsBackWithAUserRefresh()
    {
        SaveKey("org-personal");
        var token = new ScriptedLimits(ScriptedLimits.Fail(LimitsError.Of(LimitsErrorKind.NoCredential)));
        var chain = ChainedLimitsProvider.ForConfigRoot(_root, _dir.Path,
            new StubSessionKeyHTTP(new() { ["/api/organizations/org-personal/usage"] = SessionKeyFixtures.Fail(401) }), token);
        var error = await Assert.ThrowsAsync<LimitsError>(() => chain.Fetch(true));
        Assert.Equal(LimitsErrorKind.SessionKeyInvalid, error.Kind);
        Assert.Equal([false], token.PromptFlags);
    }

    [Fact]
    public void TheFolderOrganizationWinsThenTheDefaultRule()
    {
        static SessionKeyOrganization Org(string id, bool hasUsage) => new(id, id, hasUsage, new LimitStatus());
        var personal = Org("org-personal", true);
        var team = Org("org-team", false);
        Assert.Equal("org-team", SessionKeyOrganization.AccountOrganization([personal, team], "org-team")?.Id);
        Assert.Equal("org-personal", SessionKeyOrganization.AccountOrganization([team, personal], "org-gone")?.Id);
        Assert.Equal("org-team", SessionKeyOrganization.AccountOrganization([team], null)?.Id);
        Assert.Null(SessionKeyOrganization.AccountOrganization([], "org-team"));
    }
}

/// <summary>The default key file follows PTB_STATE_DIR, so QA and demo runs never touch the real key.</summary>
[Collection("Environment")]
public sealed class SessionKeyStoreLocationTests
{
    [Fact]
    public void DefaultPathHonorsStateDirOverride()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PTB_STATE_DIR");
        Environment.SetEnvironmentVariable("PTB_STATE_DIR", dir.Path);
        try
        {
            var store = new SessionKeyStore();
            Assert.Equal(dir.Path, Path.GetDirectoryName(store.FilePath));
            store.Save(new SessionKeyCredential(SessionKeyFixtures.Key('f'), "org"));
            Assert.True(File.Exists(store.FilePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTB_STATE_DIR", previous);
        }
    }
}
