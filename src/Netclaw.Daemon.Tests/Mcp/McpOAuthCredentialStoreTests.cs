// -----------------------------------------------------------------------
// <copyright file="McpOAuthCredentialStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
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
    public async Task EveryAdapterReadsSharedPersistedActiveView()
    {
        var store = CreateStore();
        var context = store.CreateContext(ServerName, Resource, "static-client", false);
        var writer = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);
        var reader = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);

        await writer.StoreTokensAsync(Tokens("access-one", "refresh-one"), CancellationToken.None);
        var loaded = await reader.GetTokensAsync(CancellationToken.None);

        Assert.Equal("access-one", loaded?.AccessToken);
        Assert.Equal("refresh-one", loaded?.RefreshToken);
    }

    [Fact]
    public async Task ConcurrentStoresForOneServerKeepMemoryAndDiskCoherent()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        var context = store.CreateContext(ServerName, Resource, "static-client", false);
        var adapters = Enumerable.Range(0, 12)
            .Select(_ => store.CreateTokenCache(
                ServerName,
                Resource,
                context,
                McpOAuthCredentialTarget.Active,
                null,
                null,
                false))
            .ToArray();

        await Task.WhenAll(adapters.Select((adapter, index) => Task.Run(async () =>
            await adapter.StoreTokensAsync(
                Tokens($"access-{index}", $"refresh-{index}"),
                TestContext.Current.CancellationToken))));

        var memory = store.GetEnvelopeForTests(ServerName).Active;
        var restarted = CreateStore(paths).GetEnvelopeForTests(ServerName).Active;
        Assert.NotNull(memory);
        Assert.Equal(memory!.AccessToken.Value, restarted?.AccessToken.Value);
        Assert.Equal(memory.RefreshToken?.Value, restarted?.RefreshToken?.Value);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotAdvanceSharedMemory()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.SecretsPath);
        var store = CreateStore(paths);
        var context = store.CreateContext(ServerName, Resource, "static-client", false);
        var cache = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await cache.StoreTokensAsync(Tokens("not-published", null), CancellationToken.None));

        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
        Assert.Null(store.GetEnvelopeForTests(ServerName).Active);
    }

    [Fact]
    public async Task StoreHonorsCancellationBeforeDiskOrMemoryMutation()
    {
        var store = CreateStore();
        var context = store.CreateContext(ServerName, Resource, "static-client", false);
        var cache = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await cache.StoreTokensAsync(Tokens("cancelled", null), cancellation.Token));

        Assert.Null(store.GetEnvelopeForTests(ServerName).Active);
    }

    [Fact]
    public async Task RefreshResponseWithoutRefreshTokenRetainsPriorToken()
    {
        var store = CreateStore();
        var context = store.CreateContext(ServerName, Resource, "static-client", false);
        var cache = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);
        await cache.StoreTokensAsync(Tokens("old-access", "keep-refresh"), CancellationToken.None);

        await cache.StoreTokensAsync(Tokens("new-access", null), CancellationToken.None);

        var active = store.GetEnvelopeForTests(ServerName).Active;
        Assert.Equal("new-access", active?.AccessToken.Value);
        Assert.Equal("keep-refresh", active?.RefreshToken?.Value);
    }

    [Fact]
    public async Task PromotedInteractiveCacheWritesLaterRefreshToActiveAndSurvivesRestart()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        var context = store.CreateContext(ServerName, Resource, "static-client", true);
        var cache = store.CreateTokenCache(
            ServerName,
            Resource,
            context,
            McpOAuthCredentialTarget.Pending,
            "published-flow",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await cache.StoreTokensAsync(Tokens("authorized-access", "authorized-refresh"), CancellationToken.None);
        store.PromotePending(ServerName, context, "published-flow", CancellationToken.None);

        await cache.StoreTokensAsync(Tokens("refreshed-after-publication", "rotated-refresh"), CancellationToken.None);

        var active = store.GetEnvelopeForTests(ServerName).Active;
        var restarted = CreateStore(paths).GetEnvelopeForTests(ServerName).Active;
        Assert.Equal("refreshed-after-publication", active?.AccessToken.Value);
        Assert.Equal("rotated-refresh", active?.RefreshToken?.Value);
        Assert.Equal(active?.CredentialEpoch, restarted?.CredentialEpoch);
        Assert.Equal("refreshed-after-publication", restarted?.AccessToken.Value);
    }

    [Fact]
    public async Task OmittedRefreshTokenNeverCrossesResourceOrPendingFlowBoundary()
    {
        var store = CreateStore();
        await StoreActiveAsync(store, Resource, "old-resource-access");
        var changedResource = "https://other.example.com/tools";
        var context = store.CreateContext(ServerName, changedResource, "static-client", true);
        var cache = store.CreateTokenCache(
            ServerName,
            changedResource,
            context,
            McpOAuthCredentialTarget.Pending,
            "new-resource-flow",
            _time.GetUtcNow().AddMinutes(5),
            true);

        await cache.StoreTokensAsync(Tokens("new-resource-access", null), CancellationToken.None);

        Assert.Null(store.GetEnvelopeForTests(ServerName).Pending?.Credentials.RefreshToken);
        Assert.Equal("refresh", store.GetEnvelopeForTests(ServerName).Active?.RefreshToken?.Value);
    }

    [Fact]
    public async Task PendingRefreshRetentionIsLimitedToOwningFlowAndEpoch()
    {
        var store = CreateStore();
        await StoreActiveAsync(store, Resource, "active-access");
        var owner = store.CreateContext(ServerName, Resource, "static-client", true);
        var ownerCache = store.CreateTokenCache(
            ServerName,
            Resource,
            owner,
            McpOAuthCredentialTarget.Pending,
            "owner-flow",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await ownerCache.StoreTokensAsync(Tokens("pending-one", "flow-refresh"), CancellationToken.None);
        await ownerCache.StoreTokensAsync(Tokens("pending-two", null), CancellationToken.None);

        var competing = store.CreateContext(ServerName, Resource, "static-client", true);
        var competingCache = store.CreateTokenCache(
            ServerName,
            Resource,
            competing,
            McpOAuthCredentialTarget.Pending,
            "competing-flow",
            _time.GetUtcNow().AddMinutes(5),
            true);

        await Assert.ThrowsAsync<McpOAuthStaleCredentialEpochException>(async () =>
            await competingCache.StoreTokensAsync(Tokens("competing", null), CancellationToken.None));
        Assert.Equal("flow-refresh", store.GetEnvelopeForTests(ServerName).Pending?.Credentials.RefreshToken?.Value);
        Assert.Equal("owner-flow", store.GetEnvelopeForTests(ServerName).Pending?.FlowId);
    }

    [Fact]
    public async Task RetiredGenerationCannotOverwriteNewOwnerEpoch()
    {
        var store = CreateStore();
        var retiredContext = store.CreateContext(ServerName, Resource, "static-client", false);
        var retiredCache = store.CreateTokenCache(
            ServerName, Resource, retiredContext, McpOAuthCredentialTarget.Active, null, null, false);
        await retiredCache.StoreTokensAsync(Tokens("generation-one", "refresh-one"), CancellationToken.None);

        var currentContext = store.CreateContext(ServerName, Resource, "static-client", false);
        store.CreateTokenCache(
            ServerName, Resource, currentContext, McpOAuthCredentialTarget.Active, null, null, false);
        store.ClaimActiveEpoch(ServerName, currentContext, CancellationToken.None);

        await Assert.ThrowsAsync<McpOAuthStaleCredentialEpochException>(async () =>
            await retiredCache.StoreTokensAsync(Tokens("stale-overwrite", "stale-refresh"), CancellationToken.None));

        var active = store.GetEnvelopeForTests(ServerName).Active;
        Assert.Equal("generation-one", active?.AccessToken.Value);
        Assert.Equal(currentContext.OwnerEpoch, active?.CredentialEpoch);
    }

    [Fact]
    public async Task CrossProcessStaleWriterCannotOverwriteRotatedCredentials()
    {
        var paths = Paths();
        var seed = CreateStore(paths);
        await StoreActiveAsync(seed, Resource, "seed-access");
        var firstProcess = CreateStore(paths);
        var staleProcess = CreateStore(paths);
        var firstContext = firstProcess.CreateContext(ServerName, Resource, "static-client", false);
        var staleContext = staleProcess.CreateContext(ServerName, Resource, "static-client", false);
        var firstCache = firstProcess.CreateTokenCache(
            ServerName, Resource, firstContext, McpOAuthCredentialTarget.Active, null, null, false);
        var staleCache = staleProcess.CreateTokenCache(
            ServerName, Resource, staleContext, McpOAuthCredentialTarget.Active, null, null, false);

        await firstCache.StoreTokensAsync(Tokens("rotated-access", "rotated-refresh"), CancellationToken.None);
        firstProcess.ClaimActiveEpoch(ServerName, firstContext, CancellationToken.None);
        await Assert.ThrowsAsync<McpOAuthStaleCredentialEpochException>(async () =>
            await staleCache.StoreTokensAsync(Tokens("stale-access", "stale-refresh"), CancellationToken.None));

        var durable = CreateStore(paths).GetEnvelopeForTests(ServerName).Active;
        Assert.Equal("rotated-access", durable?.AccessToken.Value);
        Assert.Equal("rotated-refresh", durable?.RefreshToken?.Value);
        Assert.Equal("rotated-access", staleProcess.GetEnvelopeForTests(ServerName).Active?.AccessToken.Value);
    }

    [Fact]
    public async Task CrossProcessStaleWriterCannotOverwritePromotedCredentials()
    {
        var paths = Paths();
        var seed = CreateStore(paths);
        await StoreActiveAsync(seed, Resource, "old-active");
        var staleProcess = CreateStore(paths);
        var staleContext = staleProcess.CreateContext(ServerName, Resource, "static-client", false);
        var staleCache = staleProcess.CreateTokenCache(
            ServerName, Resource, staleContext, McpOAuthCredentialTarget.Active, null, null, false);
        var authorizingProcess = CreateStore(paths);
        var authContext = authorizingProcess.CreateContext(ServerName, Resource, "static-client", true);
        var pending = authorizingProcess.CreateTokenCache(
            ServerName,
            Resource,
            authContext,
            McpOAuthCredentialTarget.Pending,
            "promoted-flow",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await pending.StoreTokensAsync(Tokens("promoted-access", "promoted-refresh"), CancellationToken.None);
        authorizingProcess.PromotePending(ServerName, authContext, "promoted-flow", CancellationToken.None);

        await Assert.ThrowsAsync<McpOAuthStaleCredentialEpochException>(async () =>
            await staleCache.StoreTokensAsync(Tokens("stale-access", "stale-refresh"), CancellationToken.None));

        var durable = CreateStore(paths).GetEnvelopeForTests(ServerName).Active;
        Assert.Equal("promoted-access", durable?.AccessToken.Value);
        Assert.Equal("promoted-refresh", durable?.RefreshToken?.Value);
    }

    [Fact]
    public async Task DynamicClientIdentityAndSecretSurviveRestartWithoutMetadataFile()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        var context = store.CreateContext(ServerName, Resource, null, false);
        context.CaptureDynamicRegistration(new DynamicClientRegistrationResponse
        {
            ClientId = "dynamic-client",
            ClientSecret = "dynamic-secret",
        });
        var cache = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);
        await cache.StoreTokensAsync(Tokens("access", "refresh"), CancellationToken.None);

        var restarted = CreateStore(paths);
        var restored = restarted.CreateContext(ServerName, Resource, null, false).SnapshotIdentity();

        Assert.Equal("dynamic-client", restored.ClientId);
        Assert.Equal("dynamic-secret", restored.ClientSecret);
        Assert.True(restored.DynamicClientRegistration);
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "mcp-oauth-metadata.json")));
    }

    [Fact]
    public async Task RawSecretsFileNeverContainsOAuthTokensOrDcrClientSecret()
    {
        var paths = Paths();
        var protector = SecretsProtection.CreateProtector(paths);
        var store = new McpOAuthCredentialStore(
            paths,
            _time,
            protector,
            NullLogger<McpOAuthCredentialStore>.Instance);
        var context = store.CreateContext(ServerName, Resource, null, false);
        context.CaptureDynamicRegistration(new DynamicClientRegistrationResponse
        {
            ClientId = "encrypted-client-id",
            ClientSecret = "dcr-secret-must-not-leak",
        });
        var cache = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);

        await cache.StoreTokensAsync(
            Tokens("access-token-must-not-leak", "refresh-token-must-not-leak"),
            CancellationToken.None);

        var raw = File.ReadAllText(paths.SecretsPath);
        Assert.DoesNotContain("access-token-must-not-leak", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token-must-not-leak", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("dcr-secret-must-not-leak", raw, StringComparison.Ordinal);
        Assert.Contains("ENC:", raw, StringComparison.Ordinal);
        var restarted = new McpOAuthCredentialStore(
            paths,
            _time,
            protector,
            NullLogger<McpOAuthCredentialStore>.Instance);
        var active = restarted.GetEnvelopeForTests(ServerName).Active;
        Assert.Equal("access-token-must-not-leak", active?.AccessToken.Value);
        Assert.Equal("refresh-token-must-not-leak", active?.RefreshToken?.Value);
        Assert.Equal("dcr-secret-must-not-leak", active?.ClientSecret?.Value);
    }

    [Fact]
    public async Task LegacyMetadataFileRemainsByteForByteUntouched()
    {
        var paths = Paths();
        var metadataPath = Path.Combine(paths.ConfigDirectory, "mcp-oauth-metadata.json");
        var original = Encoding.UTF8.GetBytes(
            "{\n  \"legacy\": { \"clientId\": \"old-client\", \"note\": \"leave exactly as-is\" }\n}\n");
        File.WriteAllBytes(metadataPath, original);
        var store = CreateStore(paths);

        await StoreActiveAsync(store, Resource, "new-access");
        _ = CreateStore(paths);

        Assert.Equal(original, File.ReadAllBytes(metadataPath));
    }

    [Theory]
    [InlineData("HTTPS://MCP.EXAMPLE.COM:443/tools?tenant=one#fragment")]
    [InlineData("https://mcp.example.com/tools?tenant=one")]
    public async Task EquivalentResourceSpellingsReturnCredentials(string equivalent)
    {
        var store = CreateStore();
        await StoreActiveAsync(store, Resource);

        var cache = store.CreateTokenCache(
            ServerName,
            equivalent,
            store.CreateContext(ServerName, equivalent, null, false),
            McpOAuthCredentialTarget.Active,
            null,
            null,
            false);

        Assert.Equal("access", (await cache.GetTokensAsync(CancellationToken.None))?.AccessToken);
    }

    [Theory]
    [InlineData("http://mcp.example.com/tools?tenant=one")]
    [InlineData("https://mcp.example.com/other?tenant=one")]
    [InlineData("https://mcp.example.com/tools?tenant=two")]
    public async Task ChangedResourceWithholdsAllDynamicCredentialsAndPreservesDisk(string changed)
    {
        var store = CreateStore();
        await StoreDynamicActiveAsync(store, Resource);

        var context = store.CreateContext(ServerName, changed, null, false);
        var cache = store.CreateTokenCache(
            ServerName, changed, context, McpOAuthCredentialTarget.Active, null, null, false);

        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
        Assert.Null(context.SnapshotIdentity().ClientId);
        Assert.True(store.RequiresAuthorization(ServerName, changed));
        Assert.Equal("access", store.GetEnvelopeForTests(ServerName).Active?.AccessToken.Value);
    }

    [Fact]
    public void StaticClientIdRemainsAuthoritativeAfterResourceChange()
    {
        var store = CreateStore();

        var context = store.CreateContext(ServerName, "https://other.example/mcp", "configured-client", false);

        Assert.Equal("configured-client", context.SnapshotIdentity().ClientId);
        Assert.False(context.SnapshotIdentity().DynamicClientRegistration);
    }

    [Fact]
    public async Task LegacyUnboundRecordFailsClosedWithoutStampingBinding()
    {
        var paths = Paths();
        File.WriteAllText(paths.SecretsPath, """
            {
              "McpOAuthTokens": {
                "test-server": {
                  "AccessToken": "legacy-access",
                  "RefreshToken": "legacy-refresh",
                  "ClientId": "legacy-client",
                  "McpServerUrl": "https://mcp.example.com/tools"
                }
              }
            }
            """);
        var store = CreateStore(paths);
        var context = store.CreateContext(ServerName, Resource, null, false);
        var cache = store.CreateTokenCache(
            ServerName, Resource, context, McpOAuthCredentialTarget.Active, null, null, false);

        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
        Assert.Null(context.SnapshotIdentity().ClientId);
        Assert.Null(store.GetEnvelopeForTests(ServerName).Active?.ResourceIdentity);
        Assert.Contains("legacy-access", File.ReadAllText(paths.SecretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingCredentialsPromoteOnlyForMatchingSuccessfulFlow()
    {
        var store = CreateStore();
        await StoreActiveAsync(store, Resource, "active-access");
        var context = store.CreateContext(ServerName, Resource, "static-client", true);
        var pending = store.CreateTokenCache(
            ServerName,
            Resource,
            context,
            McpOAuthCredentialTarget.Pending,
            "flow-one",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await pending.StoreTokensAsync(Tokens("pending-access", "pending-refresh"), CancellationToken.None);

        Assert.Equal("active-access", store.GetEnvelopeForTests(ServerName).Active?.AccessToken.Value);
        Assert.Equal("pending-access", store.GetEnvelopeForTests(ServerName).Pending?.Credentials.AccessToken.Value);

        store.PromotePending(ServerName, context, "flow-one", CancellationToken.None);

        Assert.Equal("pending-access", store.GetEnvelopeForTests(ServerName).Active?.AccessToken.Value);
        Assert.Null(store.GetEnvelopeForTests(ServerName).Pending);
    }

    [Fact]
    public async Task FailedCandidateRemovesOnlyPendingCredentials()
    {
        var store = CreateStore();
        await StoreActiveAsync(store, Resource, "active-access");
        var context = store.CreateContext(ServerName, Resource, "static-client", true);
        var pending = store.CreateTokenCache(
            ServerName,
            Resource,
            context,
            McpOAuthCredentialTarget.Pending,
            "failed-flow",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await pending.StoreTokensAsync(Tokens("failed-candidate", null), CancellationToken.None);

        store.RemovePending(ServerName, "failed-flow", CancellationToken.None);

        Assert.Equal("active-access", store.GetEnvelopeForTests(ServerName).Active?.AccessToken.Value);
        Assert.Null(store.GetEnvelopeForTests(ServerName).Pending);
    }

    [Fact]
    public async Task RestartPrunesAbandonedPendingWithoutChangingActive()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        await StoreActiveAsync(store, Resource, "active-access");
        var pending = store.CreateTokenCache(
            ServerName,
            Resource,
            store.CreateContext(ServerName, Resource, null, true),
            McpOAuthCredentialTarget.Pending,
            "abandoned",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await pending.StoreTokensAsync(Tokens("abandoned-access", null), CancellationToken.None);

        var restarted = CreateStore(paths);

        Assert.Equal("active-access", restarted.GetEnvelopeForTests(ServerName).Active?.AccessToken.Value);
        Assert.Null(restarted.GetEnvelopeForTests(ServerName).Pending);
    }

    [Fact]
    public async Task RestartKeepsPromotedCredentialsAfterCrashBeforeRuntimePublication()
    {
        var paths = Paths();
        var store = CreateStore(paths);
        await StoreActiveAsync(store, Resource, "old-active");
        using var broker = new McpOAuthFlowBroker(_time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var redirectOwner = flow.HandleAuthorizationRedirectAsync(
            new Uri("https://auth.example/authorize"),
            new Uri("http://127.0.0.1:5199/api/mcp/oauth/callback"),
            CancellationToken.None);
        flow.DeliverCode("commit-code");
        Assert.Equal("commit-code", await redirectOwner);
        var context = store.CreateContext(ServerName, Resource, "static-client", true);
        var pending = store.CreateTokenCache(
            ServerName,
            Resource,
            context,
            McpOAuthCredentialTarget.Pending,
            flow.State,
            _time.GetUtcNow().AddMinutes(5),
            true);
        await pending.StoreTokensAsync(Tokens("promoted-before-crash", "new-refresh"), CancellationToken.None);
        broker.BeginCommit(flow);
        store.PromotePending(ServerName, context, flow.State, CancellationToken.None);

        // Simulate process loss after durable promotion but before runtime publication/Complete.
        var restarted = CreateStore(paths);

        Assert.Equal("promoted-before-crash", restarted.GetEnvelopeForTests(ServerName).Active?.AccessToken.Value);
        Assert.Null(restarted.GetEnvelopeForTests(ServerName).Pending);
    }

    [Fact]
    public async Task RejectedDynamicIdentityRemainsWithheldAcrossRepeatedAttempts()
    {
        var store = CreateStore();
        await StoreDynamicActiveAsync(store, Resource);
        store.MarkDynamicIdentityRejected(ServerName, Resource, null, CancellationToken.None);

        var next = store.CreateContext(ServerName, Resource, null, true).SnapshotIdentity();
        var later = store.CreateContext(ServerName, Resource, null, true).SnapshotIdentity();

        Assert.Null(next.ClientId);
        Assert.Null(later.ClientId);
        Assert.Equal("dynamic-client", store.GetEnvelopeForTests(ServerName).RejectedDynamicClientId);
    }

    [Fact]
    public async Task RejectedMarkerNeverDiscardsConfiguredStaticClientId()
    {
        var store = CreateStore();
        await StoreDynamicActiveAsync(store, Resource);
        store.MarkDynamicIdentityRejected(ServerName, Resource, null, CancellationToken.None);

        var context = store.CreateContext(ServerName, Resource, "static-client", true).SnapshotIdentity();

        Assert.Equal("static-client", context.ClientId);
        Assert.False(context.DynamicClientRegistration);
    }

    [Fact]
    public async Task FailedFlowAfterReplacementCaptureKeepsRejectedMarkerAndWithholdsOldIdentity()
    {
        var store = CreateStore();
        await StoreDynamicActiveAsync(store, Resource);
        store.MarkDynamicIdentityRejected(ServerName, Resource, null, CancellationToken.None);
        var context = store.CreateContext(ServerName, Resource, null, true);

        store.CaptureDynamicRegistration(
            ServerName,
            context,
            new DynamicClientRegistrationResponse
            {
                ClientId = "replacement-client",
                ClientSecret = "replacement-secret",
            },
            CancellationToken.None);
        var pending = store.CreateTokenCache(
            ServerName,
            Resource,
            context,
            McpOAuthCredentialTarget.Pending,
            "failed-replacement",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await pending.StoreTokensAsync(Tokens("failed-access", "failed-refresh"), CancellationToken.None);
        store.RemovePending(ServerName, "failed-replacement", CancellationToken.None);

        var nextAttempt = store.CreateContext(ServerName, Resource, null, true).SnapshotIdentity();
        Assert.Equal("dynamic-client", store.GetEnvelopeForTests(ServerName).RejectedDynamicClientId);
        Assert.Equal("replacement-client", context.SnapshotIdentity().ClientId);
        Assert.Null(nextAttempt.ClientId);
    }

    [Fact]
    public async Task PromotedReplacementClearsRejectedMarker()
    {
        var store = CreateStore();
        await StoreDynamicActiveAsync(store, Resource);
        store.MarkDynamicIdentityRejected(ServerName, Resource, null, CancellationToken.None);
        var context = store.CreateContext(ServerName, Resource, null, true);
        store.CaptureDynamicRegistration(
            ServerName,
            context,
            new DynamicClientRegistrationResponse
            {
                ClientId = "replacement-client",
                ClientSecret = "replacement-secret",
            },
            CancellationToken.None);
        var pending = store.CreateTokenCache(
            ServerName,
            Resource,
            context,
            McpOAuthCredentialTarget.Pending,
            "successful-replacement",
            _time.GetUtcNow().AddMinutes(5),
            true);
        await pending.StoreTokensAsync(Tokens("replacement-access", "replacement-refresh"), CancellationToken.None);

        store.PromotePending(ServerName, context, "successful-replacement", CancellationToken.None);

        Assert.Null(store.GetEnvelopeForTests(ServerName).RejectedDynamicClientId);
        Assert.Equal("replacement-client", store.GetEnvelopeForTests(ServerName).Active?.ClientId);
    }

    private async Task StoreActiveAsync(
        McpOAuthCredentialStore store,
        string resource,
        string accessToken = "access")
    {
        var context = store.CreateContext(ServerName, resource, "static-client", false);
        var cache = store.CreateTokenCache(
            ServerName, resource, context, McpOAuthCredentialTarget.Active, null, null, false);
        await cache.StoreTokensAsync(Tokens(accessToken, "refresh"), CancellationToken.None);
    }

    private async Task StoreDynamicActiveAsync(McpOAuthCredentialStore store, string resource)
    {
        var context = store.CreateContext(ServerName, resource, null, false);
        context.CaptureDynamicRegistration(new DynamicClientRegistrationResponse
        {
            ClientId = "dynamic-client",
            ClientSecret = "dynamic-secret",
        });
        var cache = store.CreateTokenCache(
            ServerName, resource, context, McpOAuthCredentialTarget.Active, null, null, false);
        await cache.StoreTokensAsync(Tokens("access", "refresh"), CancellationToken.None);
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

    private McpOAuthCredentialStore CreateStore(NetclawPaths? paths = null)
        => new(
            paths ?? Paths(),
            _time,
            new NullSecretsProtector(),
            NullLogger<McpOAuthCredentialStore>.Instance);

    public void Dispose() => _dir.Dispose();
}
