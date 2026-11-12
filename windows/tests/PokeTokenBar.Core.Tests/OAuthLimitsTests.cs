using System.Net;
using System.Text;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

internal static class CredentialFiles
{
    /// <summary>The shape Claude Code writes (expiresAt in milliseconds, as observed on Windows).</summary>
    public static void Write(string path, string token, string subscription = "max", TimeSpan? expiresIn = null,
        string? tier = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1)).ToUnixTimeMilliseconds();
        var tierJSON = tier == null ? "" : $",\"rateLimitTier\":\"{tier}\"";
        File.WriteAllText(path,
            $"{{\"claudeAiOauth\":{{\"accessToken\":\"{token}\",\"refreshToken\":\"r\",\"expiresAt\":{expiresAt}," +
            $"\"scopes\":[\"user:inference\"],\"subscriptionType\":\"{subscription}\"{tierJSON}}},\"mcpOAuth\":{{}}}}");
    }
}

/// <summary>
/// Port of the Claude cases of CredentialSwitchCacheTests.swift (#227, #300) against the Windows
/// credentials file, plus what replaces the Keychain fallback: the file's own state decides the error.
/// </summary>
public sealed class CredentialSwitchCacheTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string File_ => Path.Combine(_dir.Path, ".credentials.json");

    public void Dispose() => _dir.Dispose();

    private static LimitsErrorKind Kind(Action action) => Assert.Throws<LimitsError>(action).Kind;

    [Fact]
    public void AutoPollPicksUpInPlaceAccountSwitch()
    {
        CredentialFiles.Write(File_, "token-account-a", "max");
        var cache = new OAuthAccessTokenCache(File_);
        Assert.Equal("token-account-a", cache.AccessToken());
        Assert.Equal("max", cache.PlanInfo().SubscriptionType);

        CredentialFiles.Write(File_, "token-account-b", "team");
        Assert.Equal("token-account-b", cache.AccessToken());
        Assert.Equal("team", cache.PlanInfo().SubscriptionType);
    }

    [Fact]
    public void AutoPollKeepsCacheWhenCredentialsFileDisappears()
    {
        CredentialFiles.Write(File_, "token-account-a");
        var cache = new OAuthAccessTokenCache(File_);
        cache.AccessToken();
        File.Delete(File_);
        Assert.Equal("token-account-a", cache.AccessToken());
    }

    /// <summary><c>"claudeAiOauth": null</c> is a logout in place; it must not wipe a live cache.</summary>
    [Fact]
    public void AutoPollKeepsCacheWhenFileDropsAccountOAuth()
    {
        CredentialFiles.Write(File_, "token-account-a");
        var cache = new OAuthAccessTokenCache(File_);
        cache.AccessToken();
        File.WriteAllText(File_, """{"claudeAiOauth":null}""");
        Assert.Equal("token-account-a", cache.AccessToken());
    }

    /// <summary>#300 on Windows: a user refresh never answers from memory; the file alone decides.</summary>
    [Fact]
    public void UserRefreshDoesNotAnswerFromTheCache()
    {
        CredentialFiles.Write(File_, "token-account-a");
        var cache = new OAuthAccessTokenCache(File_);
        cache.AccessToken();
        File.Delete(File_);
        Assert.Equal(LimitsErrorKind.NoCredential, Kind(() => cache.AccessToken(allowKeychainPrompt: true, bypassCache: true)));
    }

    [Fact]
    public void TheFileStateNamesTheError()
    {
        var cache = new OAuthAccessTokenCache(File_);
        Assert.Equal(LimitsErrorKind.NoCredential, Kind(() => cache.AccessToken()));

        CredentialFiles.Write(File_, "token-old", expiresIn: TimeSpan.FromSeconds(30));
        Assert.Equal(LimitsErrorKind.CredentialExpired, Kind(() => cache.AccessToken()));
        Assert.True(LimitsError.IsAuthRejection(LimitsError.Of(LimitsErrorKind.CredentialExpired)), "reads like a rejected token");

        File.WriteAllText(File_, """{"mcpOAuth":{"x":{}}}""");
        Assert.Equal(LimitsErrorKind.CredentialMissingAccountOAuth, Kind(() => cache.AccessToken()));

        File.WriteAllText(File_, "{not json");
        Assert.Equal(LimitsErrorKind.CredentialFormat, Kind(() => cache.AccessToken()));
    }

    [Fact]
    public void ExpiryIsReadInMillisecondsSecondsOrText()
    {
        var future = DateTimeOffset.UtcNow.AddHours(1);
        foreach (var raw in new[] { $"{future.ToUnixTimeMilliseconds()}", $"{future.ToUnixTimeSeconds()}", $"\"{future.ToUnixTimeSeconds()}\"" })
        {
            var credential = OAuthCredentialData.CredentialFrom(Encoding.UTF8.GetBytes(
                $"{{\"claudeAiOauth\":{{\"accessToken\":\"t\",\"expiresAt\":{raw}}}}}"));
            Assert.NotNull(credential);
            Assert.False(credential.IsExpired, raw);
            Assert.Equal(future.ToUnixTimeSeconds(), credential.ExpiresAt!.Value.ToUnixTimeSeconds());
        }
        var noExpiry = OAuthCredentialData.CredentialFrom("""{"claudeAiOauth":{"accessToken":"t"}}"""u8.ToArray());
        Assert.False(noExpiry!.IsExpired);
        Assert.Null(OAuthCredentialData.CredentialFrom("""{"claudeAiOauth":{"accessToken":""}}"""u8.ToArray()));
    }
}

/// <summary>Port of ConfigRootTokenCacheTests and the Windows reading of KeychainAutoPathTests.</summary>
public sealed class ConfigRootTokenCacheTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void FolderCacheReadsThatFoldersCredentialsFile()
    {
        CredentialFiles.Write(Path.Combine(_root.Path, ".credentials.json"), "token-work", "team");
        var cache = OAuthAccessTokenCache.ForConfigRoot(_root.Path);
        Assert.Equal("token-work", cache.AccessToken());
        Assert.Equal("team", cache.PlanInfo().SubscriptionType);
    }

    /// <summary>The folder's own file decides the re-login hint.</summary>
    [Fact]
    public void FolderFileWithoutAccountOAuthAsksForReLogin()
    {
        File.WriteAllText(Path.Combine(_root.Path, ".credentials.json"), """{"mcpOAuth":{}}""");
        var error = Assert.Throws<LimitsError>(() => OAuthAccessTokenCache.ForConfigRoot(_root.Path).AccessToken());
        Assert.Equal(LimitsErrorKind.CredentialMissingAccountOAuth, error.Kind);
    }

    /// <summary>No Keychain on Windows: automatic and user refresh read the same file and fail the same way.</summary>
    [Fact]
    public async Task FolderWithoutFileFailsTheSameOnEveryPath()
    {
        var missing = Path.Combine(_root.Path, "no-such-root");
        foreach (var allowPrompt in new[] { false, true })
        {
            var error = await Assert.ThrowsAsync<LimitsError>(() =>
                new OAuthLimitsProvider(OAuthAccessTokenCache.ForConfigRoot(missing)).Fetch(allowPrompt));
            Assert.Equal(LimitsErrorKind.NoCredential, error.Kind);
        }
    }
}

/// <summary>Port of OAuthProfileCacheTests.</summary>
public sealed class OAuthProfileCacheTests
{
    [Fact]
    public async Task OneFetchPerTokenWhenAccountsAlternate()
    {
        var calls = new List<string>();
        var cache = new OAuthProfileCache(token =>
        {
            calls.Add(token);
            return Task.FromResult<AccountIdentity?>(new AccountIdentity($"{token}@example.com", null));
        });
        foreach (var token in new[] { "a", "b", "a", "b" })
            Assert.Equal($"{token}@example.com", (await cache.Identity(token))?.Email);
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public async Task FailuresAreNotCached()
    {
        var calls = 0;
        var cache = new OAuthProfileCache(_ =>
            Task.FromResult(++calls == 1 ? null : new AccountIdentity("late@example.com", null)));
        Assert.Null(await cache.Identity("t"));
        Assert.Equal("late@example.com", (await cache.Identity("t"))?.Email);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CacheIsBounded()
    {
        var calls = 0;
        var cache = new OAuthProfileCache(token =>
        {
            calls++;
            return Task.FromResult<AccountIdentity?>(new AccountIdentity(token, null));
        });
        for (var i = 0; i <= OAuthProfileCache.MaxEntries; i++) await cache.Identity($"t{i}");
        await cache.Identity("t0");
        Assert.Equal(OAuthProfileCache.MaxEntries + 2, calls);
    }

    [Fact]
    public void ProfileParsingNeedsAnEmailAndDropsAnEmptyOrganization()
    {
        Assert.Equal(new AccountIdentity("me@corp.example", "Corp"),
            OAuthProfileData.Identity("""{"account":{"email":"me@corp.example"},"organization":{"name":"Corp"}}"""u8.ToArray()));
        Assert.Equal(new AccountIdentity("me@example.com", null),
            OAuthProfileData.Identity("""{"account":{"email":"me@example.com"},"organization":{"name":""}}"""u8.ToArray()));
        Assert.Null(OAuthProfileData.Identity("""{"account":{"email":""}}"""u8.ToArray()));
        Assert.Null(OAuthProfileData.Identity("not json"u8.ToArray()));
    }

    /// <summary>The server's current plan name: how a rename (Max 20x → Ultra) reaches the screen without an app
    /// change. Missing or blank must be null so the tier fallback stays alive (#463).</summary>
    [Fact]
    public void ProfileParsingReadsThePlanDisplayName()
    {
        Assert.Equal(new AccountIdentity("dev@example.com", null, "Ultra"),
            OAuthProfileData.Identity("""
                {"account":{"email":"dev@example.com"},
                "organization":{"organization_type":"claude_max","rate_limit_tier":"default_claude_max_20x",
                "plan_display_name":"Ultra","plan_display_label":"Ultra plan"}}
                """u8.ToArray()));
        Assert.Equal("Max", OAuthProfileData.Identity(
            """{"account":{"email":"dev@example.com"},"organization":{"plan_display_name":"  Max  "}}"""u8.ToArray())?.PlanDisplayName);
        Assert.Null(OAuthProfileData.Identity(
            """{"account":{"email":"dev@example.com"},"organization":{"plan_display_name":"   "}}"""u8.ToArray())?.PlanDisplayName);
        Assert.Null(OAuthProfileData.Identity("""{"account":{"email":"dev@example.com"}}"""u8.ToArray())?.PlanDisplayName);
    }
}

/// <summary>The OAuth provider against a fake transport (no request leaves the machine).</summary>
public sealed class OAuthLimitsProviderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string File_ => Path.Combine(_dir.Path, ".credentials.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task FetchSendsTheTokenAndLabelsPlanAndAccount()
    {
        CredentialFiles.Write(File_, "token-a", "max", tier: "default_claude_max_20x");
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/oauth/usage" => StubHandler.Json("""{"five_hour":{"utilization":11},"seven_day":{"utilization":22}}"""),
            "/api/oauth/profile" => StubHandler.Json("""{"account":{"email":"me@example.com"},"organization":{"name":"me@example.com's Organization"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var status = await new OAuthLimitsProvider(new OAuthAccessTokenCache(File_), handler.Client()).Fetch();

        Assert.Equal(11, status.FiveHour?.Utilization);
        Assert.Equal("Ultra", status.PlanDisplay);
        Assert.Equal("me@example.com", status.AccountDisplay);
        var usage = handler.Requests.First(r => r.RequestUri!.AbsolutePath == "/api/oauth/usage");
        Assert.Equal("Bearer token-a", usage.Headers.Authorization?.ToString());
        Assert.Equal("oauth-2025-04-20", string.Join("", usage.Headers.GetValues("anthropic-beta")));
    }

    /// <summary>The profile's plan name reaches the status and wins over the credential's tier (#463).</summary>
    [Fact]
    public async Task FetchLabelsThePlanWithTheProfileName()
    {
        CredentialFiles.Write(File_, "token-a", "max", tier: "default_claude_max_20x");
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/oauth/usage" => StubHandler.Json("""{"five_hour":{"utilization":11}}"""),
            "/api/oauth/profile" => StubHandler.Json("""{"account":{"email":"me@example.com"},"organization":{"plan_display_name":"Max Team"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var status = await new OAuthLimitsProvider(new OAuthAccessTokenCache(File_), handler.Client()).Fetch();

        Assert.Equal("Max Team", status.PlanDisplay);
    }

    /// <summary>A 401 re-reads the file once: a token Claude Code rewrote meanwhile is retried.</summary>
    [Fact]
    public async Task ARejectedTokenIsRetriedWithTheRewrittenOne()
    {
        CredentialFiles.Write(File_, "token-old");
        var authorizations = new List<string>();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/oauth/usage") return new HttpResponseMessage(HttpStatusCode.NotFound);
            authorizations.Add(request.Headers.Authorization!.ToString());
            if (authorizations.Count == 1)
            {
                CredentialFiles.Write(File_, "token-new");
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return StubHandler.Json("""{"five_hour":{"utilization":5}}""");
        });
        var status = await new OAuthLimitsProvider(new OAuthAccessTokenCache(File_), handler.Client()).Fetch();
        Assert.Equal(5, status.FiveHour?.Utilization);
        Assert.Equal(["Bearer token-old", "Bearer token-new"], authorizations);
    }

    [Fact]
    public async Task TheSameRejectedTokenIsNotRetried()
    {
        CredentialFiles.Write(File_, "token-old");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<LimitsError>(() =>
            new OAuthLimitsProvider(new OAuthAccessTokenCache(File_), handler.Client()).Fetch());
        Assert.Equal(LimitsError.Http(401), error);
        Assert.True(LimitsError.IsAuthRejection(error));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitCarriesRetryAfter()
    {
        CredentialFiles.Write(File_, "token-a");
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            return response;
        });
        var error = await Assert.ThrowsAsync<LimitsError>(() =>
            new OAuthLimitsProvider(new OAuthAccessTokenCache(File_), handler.Client()).Fetch());
        Assert.Equal(LimitsError.RateLimited(TimeSpan.FromSeconds(120)), error);
    }

    [Theory]
    [InlineData("120", 120.0)]
    [InlineData(" 1.5 ", 1.5)]
    [InlineData("99999", 3600.0)]
    [InlineData("0", null)]
    [InlineData("-3", null)]
    [InlineData("Wed, 21 Oct 2015 07:28:00 GMT", null)]
    public void RetryAfterReadsSecondsOnly(string raw, double? expected)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", raw);
        Assert.Equal(expected, LiveFetchGate.RetryAfterSeconds(response)?.TotalSeconds);
    }
}

/// <summary>
/// Port of LiveCredentialCallGateTests: the suite never calls a live limits endpoint with the real login
/// found on the machine. The credential read is real; the gate sits at the network boundary.
/// </summary>
public sealed class LiveCredentialCallGateTests
{
    [Fact]
    public void GateIsClosedUnderTests()
    {
        Assert.False(AppEnv.IsInstalledApp);
        Assert.False(LiveFetchGate.AllowsLiveLimitsFetch);
    }

    [Fact]
    public async Task ClaudeLimitsFetchDoesNotReachTheNetwork()
    {
        var error = await Assert.ThrowsAsync<LimitsError>(() => new OAuthLimitsProvider().Fetch(false));
        var data = File.Exists(OAuthAccessTokenCache.DefaultCredentialsFilePath)
            ? File.ReadAllBytes(OAuthAccessTokenCache.DefaultCredentialsFilePath) : null;
        if (data != null && OAuthCredentialData.CredentialFrom(data) is { IsExpired: false })
            Assert.Equal(LimitsErrorKind.LiveFetchNotPermitted, error.Kind);
        else
            // No live login on this machine: the fetch ended at the credential, before the gate.
            Assert.NotEqual(LimitsErrorKind.LiveFetchNotPermitted, error.Kind);
    }

    /// <summary>Keeps the next networking limits provider from shipping without the gate.</summary>
    [Fact]
    public void EveryNetworkingLimitsProviderIsGated()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PokeTokenBar.Core"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var core = Path.Combine(dir.FullName, "src", "PokeTokenBar.Core");
        var scanned = new List<string>();
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(core, "*LimitsProvider.cs"))
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("HttpClient", StringComparison.Ordinal)) continue;
            scanned.Add(Path.GetFileName(file));
            // Comment lines do not count: a commented-out guard must not satisfy the scan.
            var gated = source.Split('\n').Select(l => l.Trim())
                .Any(l => !l.StartsWith("//", StringComparison.Ordinal) && l.Contains("LiveFetchGate.", StringComparison.Ordinal));
            if (!gated) offenders.Add(Path.GetFileName(file));
        }
        Assert.NotEmpty(scanned);
        Assert.Empty(offenders);
    }
}
