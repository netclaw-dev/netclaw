// -----------------------------------------------------------------------
// <copyright file="McpOAuthCredentialStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Authentication;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpOAuthCredentialStoreTests : IDisposable
{
    private static readonly McpServerName ServerName = new("test-server");
    private const string Resource = "https://mcp.example.com/tools?tenant=one";
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task AuthorizationCandidateStaysLocalUntilPublication()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        var active = await PublishStaticAsync(store, "active-access", "active-refresh");
        var candidate = store.CreateTokenCache(ServerName, Resource, "static-client", true);

        await candidate.StoreTokensAsync(Tokens("candidate-access", "candidate-refresh"), CancellationToken.None);

        Assert.Equal("candidate-access", (await candidate.GetTokensAsync(CancellationToken.None))?.AccessToken);
        Assert.Equal("active-access", store.GetActiveForTests(ServerName)?.AccessToken.Value);
        Assert.DoesNotContain("candidate-access", File.ReadAllText(paths.SecretsPath), StringComparison.Ordinal);
        store.Discard(candidate);
        Assert.Equal("active-access", (await active.GetTokensAsync(CancellationToken.None))?.AccessToken);
    }

    [Fact]
    public async Task PublishedCandidatePersistsRefreshesAndSurvivesRestart()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        var candidate = store.CreateTokenCache(ServerName, Resource, "static-client", true);
        await candidate.StoreTokensAsync(Tokens("authorized", "first-refresh"), CancellationToken.None);
        store.Publish(candidate, CancellationToken.None);

        await candidate.StoreTokensAsync(Tokens("refreshed", "rotated-refresh"), CancellationToken.None);

        var restarted = CreateStore(paths).GetActiveForTests(ServerName);
        Assert.Equal("refreshed", restarted?.AccessToken.Value);
        Assert.Equal("rotated-refresh", restarted?.RefreshToken?.Value);
    }

    [Fact]
    public async Task CommitFailureLeavesActiveStateUntouched()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.SecretsPath);
        var store = CreateStore(paths);
        var candidate = store.CreateTokenCache(ServerName, Resource, "static-client", true);
        await candidate.StoreTokensAsync(Tokens("not-published", null), CancellationToken.None);

        // A directory standing where the file should be surfaces as IOException on Unix and
        // UnauthorizedAccessException on Windows, and the latter derives from SystemException
        // rather than IOException. Either way the replace failed, which is what this asserts.
        var failure = Assert.ThrowsAny<Exception>(() => store.Publish(candidate, CancellationToken.None));
        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"Expected a file-access failure, got {failure.GetType().FullName}: {failure.Message}");

        Assert.Null(store.GetActiveForTests(ServerName));
        Assert.False(candidate.Published);
    }

    [Fact]
    public async Task OmittedRefreshTokenRetainsMatchingClientToken()
    {
        var store = CreateStore();
        var active = await PublishStaticAsync(store, "old-access", "keep-refresh");

        await active.StoreTokensAsync(Tokens("new-access", null), CancellationToken.None);

        Assert.Equal("keep-refresh", store.GetActiveForTests(ServerName)?.RefreshToken?.Value);
    }

    [Fact]
    public async Task ExplicitAuthorizationSupersedesRefreshThatFinishesFirst()
    {
        var store = CreateStore();
        var active = await PublishStaticAsync(store, "old-access", "old-refresh");
        var candidate = store.CreateTokenCache(ServerName, Resource, "static-client", true);
        await active.StoreTokensAsync(Tokens("raced-access", "raced-refresh"), CancellationToken.None);
        await candidate.StoreTokensAsync(Tokens("authorized-access", null), CancellationToken.None);

        store.Publish(candidate, CancellationToken.None);

        var committed = store.GetActiveForTests(ServerName);
        Assert.Equal("authorized-access", committed?.AccessToken.Value);
        Assert.Equal("raced-refresh", committed?.RefreshToken?.Value);
    }

    [Fact]
    public async Task OrdinaryCandidatePersistsRotatedTokenBeforePublication()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        var active = await PublishStaticAsync(store, "seed", "seed-refresh");
        var candidate = store.CreateTokenCache(ServerName, Resource, "static-client", false);

        await candidate.StoreTokensAsync(Tokens("rotated", "rotated-refresh"), CancellationToken.None);
        store.Discard(candidate);

        Assert.Equal("rotated", CreateStore(paths).GetActiveForTests(ServerName)?.AccessToken.Value);
        Assert.Equal("rotated", (await active.GetTokensAsync(CancellationToken.None))?.AccessToken);
    }

    [Fact]
    public async Task RetiredCacheCannotOverwritePublishedCredentials()
    {
        var store = CreateStore();
        var retired = await PublishStaticAsync(store, "generation-one", "refresh-one");
        var current = store.CreateTokenCache(ServerName, Resource, "static-client", false);
        store.Publish(current, CancellationToken.None);

        await Assert.ThrowsAsync<McpOAuthRetiredCredentialWriterException>(async () =>
            await retired.StoreTokensAsync(Tokens("stale", "stale-refresh"), CancellationToken.None));

        Assert.Equal("generation-one", store.GetActiveForTests(ServerName)?.AccessToken.Value);
    }

    [Fact]
    public async Task OrdinaryCandidateCannotOverwriteConcurrentRefresh()
    {
        var store = CreateStore();
        var active = await PublishStaticAsync(store, "seed", "seed-refresh");
        var candidate = store.CreateTokenCache(ServerName, Resource, "static-client", false);
        await active.StoreTokensAsync(Tokens("latest", "latest-refresh"), CancellationToken.None);
        await Assert.ThrowsAsync<McpOAuthRetiredCredentialWriterException>(async () =>
            await candidate.StoreTokensAsync(
                Tokens("stale-candidate", "stale-refresh"), CancellationToken.None));
        Assert.Equal("latest", store.GetActiveForTests(ServerName)?.AccessToken.Value);
    }

    [Fact]
    public async Task DynamicClientIdentityAndSecretSurviveRestart()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        var candidate = store.CreateTokenCache(ServerName, Resource, null, true);
        store.AdoptClientIdentity(candidate, new McpOAuthClientIdentity("dynamic-client", "dynamic-secret", DynamicClientRegistration: true));
        await candidate.StoreTokensAsync(Tokens("access", "refresh"), CancellationToken.None);
        store.Publish(candidate, CancellationToken.None);

        var restarted = CreateStore(paths);
        var restored = restarted.GetIdentity(
            restarted.CreateTokenCache(ServerName, Resource, null, false));

        Assert.Equal("dynamic-client", restored.ClientId);
        Assert.Equal("dynamic-secret", restored.ClientSecret);
        Assert.True(restored.DynamicClientRegistration);
    }

    [Fact]
    public async Task RawSecretsFileDoesNotContainOAuthSecrets()
    {
        var paths = Paths();
        var protector = SecretsProtection.CreateProtector(paths);
        var store = new McpOAuthCredentialStore(
            paths, _time, protector, NullLogger<McpOAuthCredentialStore>.Instance);
        var candidate = store.CreateTokenCache(ServerName, Resource, null, true);
        store.AdoptClientIdentity(candidate, new McpOAuthClientIdentity("dynamic-client", "client-secret-must-not-leak", DynamicClientRegistration: true));
        await candidate.StoreTokensAsync(
            Tokens("access-must-not-leak", "refresh-must-not-leak"), CancellationToken.None);
        store.Publish(candidate, CancellationToken.None);

        var raw = File.ReadAllText(paths.SecretsPath);
        Assert.DoesNotContain("access-must-not-leak", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-must-not-leak", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret-must-not-leak", raw, StringComparison.Ordinal);
        Assert.Contains("ENC:", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyMetadataFileRemainsUntouched()
    {
        var paths = Paths();
        var metadataPath = Path.Combine(paths.ConfigDirectory, "mcp-oauth-metadata.json");
        var original = Encoding.UTF8.GetBytes("{\n  \"legacy\": true\n}\n");
        File.WriteAllBytes(metadataPath, original);

        await PublishStaticAsync(CreateStore(paths), "access", "refresh");

        Assert.Equal(original, File.ReadAllBytes(metadataPath));
    }

    [Theory]
    [InlineData("HTTPS://MCP.EXAMPLE.COM:443/tools?tenant=one#fragment")]
    [InlineData("https://mcp.example.com/tools?tenant=one")]
    public async Task EquivalentResourceSpellingsReturnCredentials(string equivalent)
    {
        var store = CreateStore();
        await PublishStaticAsync(store, "access", "refresh");

        var cache = store.CreateTokenCache(ServerName, equivalent, null, false);

        Assert.Equal("access", (await cache.GetTokensAsync(CancellationToken.None))?.AccessToken);
    }

    [Fact]
    public async Task ChangedResourceWithholdsTokensAndDynamicIdentity()
    {
        var store = CreateStore();
        await PublishDynamicAsync(store);

        var cache = store.CreateTokenCache(ServerName, "https://other.example/mcp", null, false);

        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
        Assert.Null(store.GetIdentity(cache).ClientId);
        Assert.True(store.RequiresAuthorization(ServerName, "https://other.example/mcp"));
        Assert.Equal("access", store.GetActiveForTests(ServerName)?.AccessToken.Value);
    }

    [Fact]
    public void StaticClientIdRemainsAuthoritativeAfterResourceChange()
    {
        var store = CreateStore();
        var cache = store.CreateTokenCache(
            ServerName, "https://other.example/mcp", "configured-client", false);

        Assert.Equal("configured-client", store.GetIdentity(cache).ClientId);
        Assert.False(store.GetIdentity(cache).DynamicClientRegistration);
    }

    [Fact]
    public async Task ExactLegacyResourceMatchMigratesCredentialsBeforeUse()
    {
        var paths = Paths();
        WriteLegacyCredentials(paths, Resource);
        var store = CreateStore(paths);

        var cache = store.CreateTokenCache(ServerName, Resource, null, false);

        Assert.Equal("legacy-access", (await cache.GetTokensAsync(CancellationToken.None))?.AccessToken);
        Assert.Equal("legacy-client", store.GetIdentity(cache).ClientId);
        Assert.True(store.GetIdentity(cache).DynamicClientRegistration);
        var migrated = store.GetActiveForTests(ServerName);
        Assert.Equal(Resource, migrated?.ResourceIdentity);

        // Retained, not erased: an endpoint corrected later still has something to match
        // against, and a rollback to a release that reads this field keeps its binding.
        Assert.Equal(Resource, migrated?.McpServerUrl);
        var restartedStore = CreateStore(paths);
        var restarted = restartedStore.CreateTokenCache(ServerName, Resource, null, false);
        Assert.Equal("legacy-client", restartedStore.GetIdentity(restarted).ClientId);
    }

    [Fact]
    public void LegacyMigrationFailureExposesNoCredentials()
    {
        var paths = Paths();
        WriteLegacyCredentials(paths, Resource);
        var store = new McpOAuthCredentialStore(
            paths,
            _time,
            new ThrowingProtectSecretsProtector(),
            NullLogger<McpOAuthCredentialStore>.Instance);

        Assert.Throws<InvalidOperationException>(
            () => store.CreateTokenCache(ServerName, Resource, null, false));

        Assert.Null(store.GetActiveForTests(ServerName)?.ResourceIdentity);
        Assert.Contains("legacy-access", File.ReadAllText(paths.SecretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpiredLegacyCredentialWithoutObtainedAtIsNotReportedExpired()
    {
        // Legacy records predate ObtainedAt, so it deserializes as 0001-01-01. Measuring
        // the lifetime from there yields ~6.4e10 seconds, which saturates the int
        // conversion and makes the SDK treat a perfectly good token as long expired —
        // sending the operator back through authorization on every upgrade.
        var paths = Paths();
        var expiresAt = _time.GetUtcNow().AddDays(30);
        WriteLegacyCredentials(paths, Resource, expiresAt, refreshToken: null);
        var store = CreateStore(paths);

        var cache = store.CreateTokenCache(ServerName, Resource, null, false);
        var tokens = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.Equal("legacy-access", tokens!.AccessToken);
        Assert.NotNull(tokens.ExpiresIn);
        Assert.InRange(tokens.ExpiresIn!.Value, 1, (int)TimeSpan.FromDays(31).TotalSeconds);
        Assert.Equal(_time.GetUtcNow(), tokens.ObtainedAt);

        // Netclaw's own view has to agree with the SDK's, or status and behavior diverge.
        Assert.False(store.RequiresAuthorization(ServerName, Resource));
    }

    [Theory]
    // Trailing slash and path case describe the same endpoint as Resource.
    [InlineData("https://mcp.example.com/tools/?tenant=one")]
    [InlineData("https://mcp.example.com/TOOLS?tenant=one")]
    public async Task LegacyResourceEquivalentToConfiguredEndpointMigrates(string legacyResource)
    {
        var paths = Paths();
        WriteLegacyCredentials(paths, legacyResource);
        var store = CreateStore(paths);

        var cache = store.CreateTokenCache(ServerName, Resource, null, false);

        Assert.Equal("legacy-access", (await cache.GetTokensAsync(CancellationToken.None))?.AccessToken);
        Assert.Equal(Resource, store.GetActiveForTests(ServerName)?.ResourceIdentity);
    }

    [Theory]
    // A different host, scheme, or port is a different audience — never migrate.
    [InlineData("https://other.example.com/tools?tenant=one")]
    [InlineData("http://mcp.example.com/tools?tenant=one")]
    [InlineData("https://mcp.example.com:8443/tools?tenant=one")]
    // Narrowing origin -> path is allowed; widening path -> sibling path is not.
    [InlineData("https://mcp.example.com/other?tenant=one")]
    // The query can select a tenant, so a different one is a different resource.
    [InlineData("https://mcp.example.com/tools?tenant=two")]
    [InlineData("https://mcp.example.com/tools")]
    // A bare origin cannot be distinguished from a pre-repoint configured URL, so it is
    // not bound to an endpoint whose query may select a tenant.
    [InlineData("https://mcp.example.com")]
    public async Task LegacyResourceFromADifferentAudienceFailsClosed(string legacyResource)
    {
        var paths = Paths();
        WriteLegacyCredentials(paths, legacyResource);
        var store = CreateStore(paths);

        var cache = store.CreateTokenCache(ServerName, Resource, null, false);

        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
        Assert.Null(store.GetIdentity(cache).ClientId);
        Assert.Contains("legacy-access", File.ReadAllText(paths.SecretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyBareOriginMigratesToAPathEndpointWithoutAQuery()
    {
        // The narrowing that providers actually produce: the authorization server declares
        // the origin as the protected resource, the MCP endpoint sits on a path beneath it,
        // and nothing about the binding is tenant-scoped.
        const string endpoint = "https://mcp.example.com/tools";
        var paths = Paths();
        WriteLegacyCredentials(paths, "https://mcp.example.com");
        var store = CreateStore(paths);

        var cache = store.CreateTokenCache(ServerName, endpoint, null, false);

        Assert.Equal("legacy-access", (await cache.GetTokensAsync(CancellationToken.None))?.AccessToken);
        Assert.Equal(
            McpOAuthCredentialStore.CanonicalizeResource(endpoint),
            store.GetActiveForTests(ServerName)?.ResourceIdentity);
    }

    [Fact]
    public void UnreadableSecretsFileLeavesTheDaemonStartable()
    {
        // LoadDurableState runs in a singleton constructor and decrypts the whole secrets
        // file. Throwing here would take down every channel, webhook, and schedule over a
        // credential that only costs a reauthorization.
        var paths = Paths();
        File.WriteAllText(paths.SecretsPath, "{ this is not valid json");

        var store = CreateStore(paths);

        Assert.Null(store.GetActiveForTests(ServerName));
    }

    [Fact]
    public async Task ForgettingARejectedClientIdentityKeepsTokensAndForcesReregistration()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        await PublishDynamicAsync(store);

        store.ForgetClientIdentity(ServerName, Resource, CancellationToken.None);

        // The identity is gone, so the next authorization registers afresh instead of
        // reusing an id the server has already refused.
        Assert.Null(store.GetIdentity(store.CreateTokenCache(ServerName, Resource, null, true)).ClientId);
        var active = store.GetActiveForTests(ServerName);
        Assert.Null(active?.ClientId);
        Assert.False(active?.DynamicClientRegistration);

        // Tokens are not collateral damage; only the client identity was rejected.
        Assert.Equal("access", active?.AccessToken.Value);
        Assert.Equal("refresh", active?.RefreshToken?.Value);

        Assert.Null(CreateStore(paths).GetActiveForTests(ServerName)?.ClientId);
    }

    [Fact]
    public async Task NewDynamicIdentityDoesNotInheritOldRefreshToken()
    {
        var store = CreateStore();
        await PublishDynamicAsync(store);
        store.ForgetClientIdentity(ServerName, Resource, CancellationToken.None);
        var replacement = store.CreateTokenCache(ServerName, Resource, null, true);
        store.AdoptClientIdentity(replacement, new McpOAuthClientIdentity("new-client", "new-secret", DynamicClientRegistration: true));
        await replacement.StoreTokensAsync(Tokens("new-access", null), CancellationToken.None);

        store.Publish(replacement, CancellationToken.None);

        Assert.Null(store.GetActiveForTests(ServerName)?.RefreshToken);
    }

    private async Task<McpOAuthTokenCache> PublishStaticAsync(
        McpOAuthCredentialStore store,
        string accessToken,
        string? refreshToken)
    {
        var cache = store.CreateTokenCache(ServerName, Resource, "static-client", false);
        await cache.StoreTokensAsync(Tokens(accessToken, refreshToken), CancellationToken.None);
        store.Publish(cache, CancellationToken.None);
        return cache;
    }

    private async Task<McpOAuthTokenCache> PublishDynamicAsync(McpOAuthCredentialStore store)
    {
        var cache = store.CreateTokenCache(ServerName, Resource, null, true);
        store.AdoptClientIdentity(cache, new McpOAuthClientIdentity("dynamic-client", "dynamic-secret", DynamicClientRegistration: true));
        await cache.StoreTokensAsync(Tokens("access", "refresh"), CancellationToken.None);
        store.Publish(cache, CancellationToken.None);
        return cache;
    }

    private TokenContainer Tokens(string accessToken, string? refreshToken) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        TokenType = "Bearer",
        Scope = "read write",
        ExpiresIn = 3600,
        ObtainedAt = _time.GetUtcNow(),
    };

    private NetclawPaths Paths()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    /// <summary>
    /// A pre-ResourceIdentity record exactly as older releases wrote it: no
    /// <c>ObtainedAt</c>, no <c>TokenType</c>, no provenance flag. Pass
    /// <paramref name="expiresAt"/> to cover the shape that carries an expiry, which is
    /// where the missing <c>ObtainedAt</c> becomes load-bearing.
    /// </summary>
    private static void WriteLegacyCredentials(
        NetclawPaths paths,
        string resource,
        DateTimeOffset? expiresAt = null,
        string? refreshToken = "legacy-refresh")
    {
        var expiry = expiresAt is { } value
            ? $"""
                  "ExpiresAt": "{value:O}",
              """
            : string.Empty;
        var refresh = refreshToken is null
            ? string.Empty
            : $"""
                  "RefreshToken": "{refreshToken}",
              """;
        File.WriteAllText(paths.SecretsPath, $$"""
            {
              "McpOAuthTokens": {
                "test-server": {
                  "AccessToken": "legacy-access",
            {{refresh}}{{expiry}}
                  "ClientId": "legacy-client",
                  "McpServerUrl": "{{resource}}"
                }
              }
            }
            """);
    }

    private McpOAuthCredentialStore CreateStore(NetclawPaths? paths = null)
        => new(
            paths ?? Paths(),
            _time,
            new NullSecretsProtector(),
            NullLogger<McpOAuthCredentialStore>.Instance);

    private sealed class ThrowingProtectSecretsProtector : ISecretsProtector
    {
        public string Protect(string plaintext) => throw new InvalidOperationException("secrets write failed");

        public string Unprotect(string ciphertext) => ciphertext;
    }

    public void Dispose() => _dir.Dispose();
}
