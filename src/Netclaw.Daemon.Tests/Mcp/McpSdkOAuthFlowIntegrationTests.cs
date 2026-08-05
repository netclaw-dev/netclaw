// -----------------------------------------------------------------------
// <copyright file="McpSdkOAuthFlowIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpSdkOAuthFlowIntegrationTests
{
    private static readonly Uri RedirectUri = new("http://127.0.0.1:5199/api/mcp/oauth/callback");

    [Fact]
    public async Task ManagerExplicitAuthorization_PublishesOnlyAfterSdkExchangeAndToolListing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path, port: 7331);

        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var authorization = await server.AuthorizeAsync(
            new Uri(started.AuthorizationUrl),
            new Uri("http://127.0.0.1:7331/api/mcp/oauth/callback"),
            ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverAuthorizationResponse(authorization.Code, authorization.State, null);

        var terminal = await flow.WaitForTerminalAsync(ct);

        Assert.True(
            terminal.Status is McpOAuthFlowStatus.Completed,
            $"{terminal.Error?.Error} :: {harness.Manager.GetServerStatuses()[harness.ServerName].ErrorMessage} :: {harness.Logger.LastException}");
        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Contains("oauth_probe", harness.Manager.GetToolNames(harness.ServerName));
        var active = harness.Credentials.GetActiveForTests(harness.ServerName);
        Assert.NotNull(active);
        Assert.Equal("client-1", active!.ClientId);
        Assert.Equal("secret-client-1", active.ClientSecret?.Value);
        Assert.Equal("http://127.0.0.1:7331/api/mcp/oauth/callback", server.DynamicClientRegistrations.Single().RedirectUris.Single());

        await harness.Runtime.LastHttpOptions!.OAuth!.TokenCache!.StoreTokensAsync(
            new TokenContainer
            {
                AccessToken = "refresh-after-publication",
                RefreshToken = "rotated-after-publication",
                TokenType = "Bearer",
                ObtainedAt = TimeProvider.System.GetUtcNow(),
                ExpiresIn = 3600,
            },
            ct);
        Assert.Equal(
            "refresh-after-publication",
            harness.Credentials.GetActiveForTests(harness.ServerName)?.AccessToken.Value);
    }

    [Fact]
    public async Task ExplicitAuthorizationGivesTheOperatorTimeToFinishInTheBrowser()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);

        // The SDK ships a 5 second server/discover probe and a 60 second initialization
        // budget. Both are machine-scale. A server that answers the probe with 401 sends the
        // SDK into the callback handler, which cannot return until the operator finishes in
        // a browser; the probe timeout then cancels that wait and the SDK calls the handler
        // again for the same flow, ending the authorization the operator was still doing.
        var options = harness.Runtime.LastClientOptions;
        Assert.NotNull(options);
        Assert.Equal(McpOAuthFlowBroker.FlowLifetime, options!.DiscoverProbeTimeout);
        Assert.Equal(McpOAuthFlowBroker.FlowLifetime, options.InitializationTimeout);
    }

    [Fact]
    public async Task BackgroundReconnectKeepsTheSdkDefaultConnectTimeouts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        server.AcceptBearer("startup-access");
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        var canonical = McpOAuthCredentialStore.CanonicalizeResource(server.McpEndpoint.ToString());
        File.WriteAllText(paths.SecretsPath, $$"""
            {
              "McpOAuthTokens": {
                "fake-oauth": {
                  "AccessToken": "startup-access",
                  "ClientId": "startup-client",
                  "ResourceIdentity": "{{canonical}}"
                }
              }
            }
            """);
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAsync(ct);

        // Nobody is waiting on a background reconnect: its handler returns immediately, so
        // stretching these would only delay a genuinely unreachable server.
        var options = harness.Runtime.LastClientOptions;
        Assert.NotNull(options);
        Assert.NotEqual(McpOAuthFlowBroker.FlowLifetime, options!.DiscoverProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.InitializationTimeout);
    }

    [Fact]
    public async Task ConcurrentManagerStartsShareCandidateUrlCredentialWriteAndGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);

        var first = harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var second = harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var starts = await Task.WhenAll(first, second);

        Assert.Equal(starts[0], starts[1]);
        Assert.Equal(1, harness.Runtime.CreateCount);

        var authorization = await server.AuthorizeAsync(
            new Uri(starts[0].AuthorizationUrl),
            RedirectUri,
            ct);
        var flow = harness.Broker.GetForCallback(starts[0].State);
        flow.DeliverAuthorizationResponse(authorization.Code, authorization.State, null);
        Assert.Equal(McpOAuthFlowStatus.Completed, (await flow.WaitForTerminalAsync(ct)).Status);

        Assert.Single(server.DynamicClientRegistrations);
        Assert.Single(server.TokenRequests);
        Assert.Equal(1, harness.Manager.GetSnapshot(harness.ServerName)?.Generation);
    }

    [Fact]
    public async Task FailedExchangeAfterCodeDeliveryRequiresNewStatePkceVerifierAndTokenRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);
        server.FailNextTokenExchange();
        var firstStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var firstAuthorization = await server.AuthorizeAsync(
            new Uri(firstStart.AuthorizationUrl),
            RedirectUri,
            ct);
        var firstFlow = harness.Broker.GetForCallback(firstStart.State);
        firstFlow.DeliverAuthorizationResponse(firstAuthorization.Code, firstAuthorization.State, null);
        Assert.Equal(
            McpOAuthFlowStatus.Failed,
            (await firstFlow.WaitForTerminalAsync(ct)).Status);

        var secondStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var secondAuthorization = await server.AuthorizeAsync(
            new Uri(secondStart.AuthorizationUrl),
            RedirectUri,
            ct);
        var secondFlow = harness.Broker.GetForCallback(secondStart.State);
        secondFlow.DeliverAuthorizationResponse(secondAuthorization.Code, secondAuthorization.State, null);
        Assert.Equal(
            McpOAuthFlowStatus.Completed,
            (await secondFlow.WaitForTerminalAsync(ct)).Status);

        Assert.NotEqual(firstStart.State, secondStart.State);
        var authorizations = server.AuthorizationRequests;
        Assert.Equal(2, authorizations.Count);
        Assert.NotEqual(authorizations[0].CodeChallenge, authorizations[1].CodeChallenge);
        var tokenRequests = server.TokenRequests;
        Assert.Equal(2, tokenRequests.Count);
        Assert.NotEqual(tokenRequests[0].Code, tokenRequests[1].Code);
        Assert.NotEqual(tokenRequests[0].CodeVerifier, tokenRequests[1].CodeVerifier);
        Assert.True(tokenRequests[0].PkceVerified);
        Assert.True(tokenRequests[1].PkceVerified);
    }

    [Fact]
    public async Task ReconnectWhileAuthorizationPendingDoesNotCreateCompetingCandidate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);

        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var reconnected = await harness.Manager.TryReconnectAsync(harness.ServerName, ct);

        Assert.False(reconnected);
        Assert.Equal(1, harness.Runtime.CreateCount);

        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverAuthorizationResponse(authorization.Code, authorization.State, null);
        Assert.Equal(McpOAuthFlowStatus.Completed, (await flow.WaitForTerminalAsync(ct)).Status);
    }

    [Fact]
    public async Task RuntimeFlowExpiryDiscardsCandidateCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path, timeProvider: time);
        var barrier = new InitializationBarrier();
        harness.Runtime.InitializationBarrier = barrier;
        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverAuthorizationResponse(authorization.Code, authorization.State, null);
        await barrier.Reached.Task.WaitAsync(ct);
        Assert.Null(harness.Credentials.GetActiveForTests(harness.ServerName));

        time.Advance(McpOAuthFlowBroker.FlowLifetime);
        Assert.Equal(McpOAuthFlowStatus.Failed, harness.Broker.GetStatusByState(started.State).Status);
        Assert.Null(harness.Credentials.GetActiveForTests(harness.ServerName));
        barrier.Release.TrySetResult(true);
    }

    [Fact]
    public async Task FailedExplicitReplacementPreservesSameLiveConnectionAndActiveCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);
        await CompleteManagerAuthorizationAsync(server, harness, ct);
        var published = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(harness.ServerName));
        var active = harness.Credentials.GetActiveForTests(harness.ServerName)!;
        harness.Runtime.FailNextToolListing();

        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverAuthorizationResponse(authorization.Code, authorization.State, null);
        var terminal = await flow.WaitForTerminalAsync(ct);

        var retained = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(harness.ServerName));
        var retainedCredentials = harness.Credentials.GetActiveForTests(harness.ServerName);
        Assert.Equal(McpOAuthFlowStatus.Failed, terminal.Status);
        Assert.Same(published.Client, retained.Client);
        Assert.Equal(published.Generation, retained.Generation);
        Assert.Equal(published.ToolFunctions.Keys, retained.ToolFunctions.Keys);
        Assert.Equal(active.AccessToken.Value, retainedCredentials?.AccessToken.Value);
        Assert.Equal(active.RefreshToken?.Value, retainedCredentials?.RefreshToken?.Value);
    }

    [Fact]
    public async Task ExplicitAuthorizationSupersedesActiveRefreshThatFinishesFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);
        await CompleteManagerAuthorizationAsync(server, harness, ct);
        var publishedCache = harness.Runtime.LastHttpOptions!.OAuth!.TokenCache!;
        var barrier = new InitializationBarrier();
        harness.Runtime.InitializationBarrier = barrier;
        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverAuthorizationResponse(authorization.Code, authorization.State, null);
        await barrier.Reached.Task.WaitAsync(ct);
        try
        {
            await publishedCache.StoreTokensAsync(
                new TokenContainer
                {
                    AccessToken = "active-rotated-during-flow",
                    RefreshToken = "active-refresh-rotated-during-flow",
                    TokenType = "Bearer",
                    ObtainedAt = TimeProvider.System.GetUtcNow(),
                    ExpiresIn = 3600,
                },
                ct);
        }
        finally
        {
            barrier.Release.TrySetResult(true);
        }

        var terminal = await flow.WaitForTerminalAsync(ct);

        Assert.Equal(McpOAuthFlowStatus.Completed, terminal.Status);
        Assert.NotEqual(
            "active-rotated-during-flow",
            harness.Credentials.GetActiveForTests(harness.ServerName)?.AccessToken.Value);
    }

    [Fact]
    public async Task RestartUsesStoredDynamicIdentityWithoutMetadataOrReregistration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using (var first = CreateManagerHarness(server, directory.Path))
        {
            await CompleteManagerAuthorizationAsync(server, first, ct);
        }

        await using var restarted = CreateManagerHarness(server, directory.Path);
        await restarted.Manager.StartAsync(ct);

        Assert.Equal(McpConnectionState.Connected, restarted.Manager.GetServerStatuses()[restarted.ServerName].State);
        Assert.Single(server.DynamicClientRegistrations);
        Assert.Equal("client-1", restarted.Runtime.LastHttpOptions?.OAuth?.ClientId);
        Assert.Equal("secret-client-1", restarted.Runtime.LastHttpOptions?.OAuth?.ClientSecret);
        Assert.False(File.Exists(Path.Combine(directory.Path, "config", "mcp-oauth-metadata.json")));
    }

    [Fact]
    public async Task StoredCredentialsRefreshAfterRestartWithoutReauthorization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using (var first = CreateManagerHarness(server, directory.Path))
        {
            await CompleteManagerAuthorizationAsync(server, first, ct);
        }

        await using var restarted = CreateManagerHarness(server, directory.Path);
        await restarted.Manager.StartAsync(ct);
        Assert.Equal(McpConnectionState.Connected, restarted.Manager.GetServerStatuses()[restarted.ServerName].State);

        // SDK 2.0 only redeems a refresh token when the stored container reports the same
        // client registration the provider holds — client id, secret, issuer, and token
        // endpoint auth method. Drop the access token so the resource server answers 401 and
        // the SDK must take that path.
        var issuedAccessToken = server.TokenRequests[^1].IssuedAccessToken;
        server.RevokeAccessToken(issuedAccessToken);
        var authorizationsBefore = server.AuthorizationRequests.Count;

        await restarted.Manager.TryReconnectAsync(restarted.ServerName, ct);

        Assert.Equal(1, server.RefreshGrantCount);
        Assert.Equal(McpConnectionState.Connected, restarted.Manager.GetServerStatuses()[restarted.ServerName].State);
        // A refresh that silently falls through to interactive authorization is the failure
        // this test exists to catch, so no new authorization request may appear.
        Assert.Equal(authorizationsBefore, server.AuthorizationRequests.Count);
    }

    [Fact]
    public async Task RejectedClientIdentityIsDiscardedSoTheNextAuthorizationRegistersAfresh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using (var first = CreateManagerHarness(server, directory.Path))
        {
            await CompleteManagerAuthorizationAsync(server, first, ct);
        }

        // The provider drops the registration behind our back.
        server.RejectClient("client-1");
        await using var harness = CreateManagerHarness(server, directory.Path);
        var rejectedStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var rejectedAuthorization = await server.AuthorizeAsync(new Uri(rejectedStart.AuthorizationUrl), RedirectUri, ct);
        var rejectedFlow = harness.Broker.GetForCallback(rejectedStart.State);
        rejectedFlow.DeliverAuthorizationResponse(rejectedAuthorization.Code, rejectedAuthorization.State, null);

        Assert.Equal(McpOAuthFlowStatus.Failed, (await rejectedFlow.WaitForTerminalAsync(ct)).Status);

        // The dead identity is discarded rather than marked, so recovery needs no extra
        // persisted state and survives a restart the same way.
        Assert.Null(harness.Credentials.GetActiveForTests(harness.ServerName)?.ClientId);

        var replacementStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        Assert.Contains("client_id=client-2", replacementStart.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Equal(2, server.DynamicClientRegistrations.Count);

        var replacementAuthorization = await server.AuthorizeAsync(
            new Uri(replacementStart.AuthorizationUrl), RedirectUri, ct);
        var replacementFlow = harness.Broker.GetForCallback(replacementStart.State);
        replacementFlow.DeliverAuthorizationResponse(replacementAuthorization.Code, replacementAuthorization.State, null);

        Assert.Equal(McpOAuthFlowStatus.Completed, (await replacementFlow.WaitForTerminalAsync(ct)).Status);
        Assert.Equal("client-2", harness.Credentials.GetActiveForTests(harness.ServerName)?.ClientId);
    }

    [Fact]
    public async Task RepointedProfileWithholdsOldCredentialsAndReportsAwaitingAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using (var first = CreateManagerHarness(server, directory.Path))
        {
            await CompleteManagerAuthorizationAsync(server, first, ct);
        }

        await using var repointed = CreateManagerHarness(
            server,
            directory.Path,
            endpointOverride: "https://changed-resource.test/mcp");
        await repointed.Manager.StartAsync(ct);

        Assert.Equal(
            McpConnectionState.AwaitingAuth,
            repointed.Manager.GetServerStatuses()[repointed.ServerName].State);
        Assert.Equal(
            server.McpEndpoint.ToString(),
            repointed.Credentials.GetActiveForTests(repointed.ServerName)?.ResourceIdentity);
    }

    [Fact]
    public async Task ExactLegacyResourceMatchReconnectsWithoutReregistration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        server.AcceptBearer("legacy-access");
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.SecretsPath, $$"""
            {
              "McpOAuthTokens": {
                "fake-oauth": {
                  "AccessToken": "legacy-access",
                  "RefreshToken": "legacy-refresh",
                  "ClientId": "legacy-client",
                  "McpServerUrl": "{{server.McpEndpoint}}"
                }
              }
            }
            """);
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAsync(ct);

        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Equal("legacy-client", harness.Runtime.LastHttpOptions?.OAuth?.ClientId);
        Assert.Empty(server.DynamicClientRegistrations);
        Assert.Equal(
            McpOAuthCredentialStore.CanonicalizeResource(server.McpEndpoint.ToString()),
            harness.Credentials.GetActiveForTests(harness.ServerName)?.ResourceIdentity);
    }

    [Fact]
    public async Task LegacyCredentialsWithoutAnIssuerRequireOneReauthorizationAtExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        server.AcceptRefreshToken("legacy-refresh", "legacy-client");
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        var canonical = McpOAuthCredentialStore.CanonicalizeResource(server.McpEndpoint.ToString());

        // The shape every install written before SDK 2.0 carries at the moment its access
        // token lapses: a refresh token the authorization server would honour, but no
        // AuthorizationServer. Release 1.4.1 refreshed this silently. SDK 2.0 gates refresh on
        // the stored issuer, and Netclaw deliberately does not fill that value from what the
        // resource server advertises — a repointed server could then satisfy the SDK's own
        // issuer binding with a value of its choosing. One reauthorization is the trade.
        File.WriteAllText(paths.SecretsPath, $$"""
            {
              "McpOAuthTokens": {
                "fake-oauth": {
                  "AccessToken": "expired-legacy-access",
                  "RefreshToken": "legacy-refresh",
                  "ClientId": "legacy-client",
                  "ExpiresAt": "2020-01-01T00:00:00+00:00",
                  "ResourceIdentity": "{{canonical}}"
                }
              }
            }
            """);
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAsync(ct);

        Assert.Equal(0, server.RefreshGrantCount);
        var status = harness.Manager.GetServerStatuses()[harness.ServerName];
        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains("netclaw mcp auth fake-oauth", status.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyUnboundCredentialsReportAwaitingAuthWithoutBeingStamped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.SecretsPath, """
            {
              "McpOAuthTokens": {
                "fake-oauth": {
                  "AccessToken": "legacy-access",
                  "RefreshToken": "legacy-refresh",
                  "ClientId": "legacy-client"
                }
              }
            }
            """);
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAsync(ct);

        Assert.Equal(
            McpConnectionState.AwaitingAuth,
            harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Null(harness.Credentials.GetActiveForTests(harness.ServerName)?.ResourceIdentity);
        Assert.Contains("legacy-access", File.ReadAllText(paths.SecretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredAccessWithoutRefreshReportsAwaitingAuthRemedy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        var canonical = McpOAuthCredentialStore.CanonicalizeResource(server.McpEndpoint.ToString());
        File.WriteAllText(paths.SecretsPath, $$"""
            {
              "McpOAuthTokens": {
                "fake-oauth": {
                  "AccessToken": "expired-access",
                  "ExpiresAt": "2020-01-01T00:00:00+00:00",
                  "ResourceIdentity": "{{canonical}}"
                }
              }
            }
            """);
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAsync(ct);

        var status = harness.Manager.GetServerStatuses()[harness.ServerName];
        Assert.Equal(McpConnectionState.AwaitingAuth, status.State);
        Assert.Contains("netclaw mcp auth fake-oauth", status.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonInteractiveStartupReportsAwaitingAuthWithoutBrokerFlow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAsync(ct);

        var status = harness.Manager.GetServerStatuses()[harness.ServerName];
        Assert.Equal(McpConnectionState.AwaitingAuth, status.State);
        Assert.Contains($"netclaw mcp auth {harness.ServerName.Value}", status.ErrorMessage);
        Assert.Equal(McpOAuthFlowStatus.NotStarted, harness.Broker.GetStatus(harness.ServerName));
    }

    [Fact]
    public async Task ExplicitAuthorizationRejectsDisabledEntryWithoutFlowOrPublication()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path, enabled: false);

        var error = await Assert.ThrowsAsync<McpOAuthOperationException>(async () =>
            await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct));

        Assert.Contains("disabled", error.Error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            McpConnectionState.Disabled,
            harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Empty(harness.Manager.GetToolNames(harness.ServerName));
        Assert.Equal(McpOAuthFlowStatus.NotStarted, harness.Broker.GetStatus(harness.ServerName));
        Assert.Equal(0, harness.Runtime.CreateCount);
    }

    [Fact]
    public async Task NoAuthServerKeepsSdkOAuthDormant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct, requireOAuth: false);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);

        await harness.Manager.StartAsync(ct);

        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Equal(0, server.ProtectedResourceDiscoveryCount);
        Assert.Equal(0, server.AuthorizationServerDiscoveryCount);
        Assert.Empty(server.DynamicClientRegistrations);
        Assert.NotNull(harness.Runtime.LastHttpOptions?.OAuth);
    }

    [Fact]
    public async Task StaticHeadersAndUserAgentRemainAuthoritativeWithDormantOAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(
            ct,
            acceptedBearer: "operator-token");
        using var directory = new DisposableTempDir();
        var headers = new Dictionary<string, SensitiveString>
        {
            ["Authorization"] = new("Bearer operator-token"),
            ["User-Agent"] = new("operator-agent/9.1"),
            ["X-Operator"] = new("kept"),
        };
        await using var harness = CreateManagerHarness(server, directory.Path, headers: headers);

        await harness.Manager.StartAsync(ct);

        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Equal("Bearer operator-token", server.LastMcpHeaders["Authorization"]);
        Assert.Equal("operator-agent/9.1", server.LastMcpHeaders["User-Agent"]);
        Assert.Equal("kept", server.LastMcpHeaders["X-Operator"]);
        Assert.Equal(0, server.ProtectedResourceDiscoveryCount);
        Assert.Empty(server.DynamicClientRegistrations);
        Assert.Null(harness.Runtime.LastHttpOptions?.OAuth);
    }

    [Fact]
    public async Task ChallengedStaticAuthorizationIsNotReplacedBySdkOAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        var headers = new Dictionary<string, SensitiveString>
        {
            ["Authorization"] = new("Bearer operator-token-that-provider-rejects"),
        };
        await using var harness = CreateManagerHarness(server, directory.Path, headers: headers);

        await harness.Manager.StartAsync(ct);

        Assert.Equal(McpConnectionState.AuthFailed, harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Equal(
            "Bearer operator-token-that-provider-rejects",
            server.LastMcpHeaders["Authorization"]);
        Assert.Null(harness.Runtime.LastHttpOptions?.OAuth);
        Assert.Equal(0, server.ProtectedResourceDiscoveryCount);
        Assert.Empty(server.DynamicClientRegistrations);
    }

    [Fact]
    public async Task ProviderBodySecretsStayInDaemonLogAndOutOfPublicOAuthErrors()
    {
        const string providerBody = "code=oauth-code access_token=token-value client_secret=secret-value";
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct, dcrRejectionBody: providerBody);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);

        var error = await Assert.ThrowsAsync<McpOAuthOperationException>(async () =>
            await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct));
        var terminal = harness.Broker.GetLatestStatus(harness.ServerName);

        Assert.DoesNotContain("oauth-code", error.Error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", terminal.Error?.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", terminal.Error?.Error, StringComparison.Ordinal);

        // Not the daemon log either. Logs are OTLP-exported when telemetry is enabled, so a
        // provider that echoes credentials in an error body would otherwise have them shipped
        // off the machine.
        var logged = string.Join("\n", harness.Logger.Exceptions.Select(e => e.ToString()));
        Assert.DoesNotContain("oauth-code", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", logged, StringComparison.Ordinal);

        // The failing endpoint and status still reach the log, so the rejection stays
        // diagnosable without the raw body.
        Assert.Contains(harness.Logger.Exceptions, exception =>
            exception.ToString().Contains("dynamic client registration", StringComparison.Ordinal)
            && exception.ToString().Contains("HTTP 403", StringComparison.Ordinal));
    }

    private static async Task CompleteManagerAuthorizationAsync(
        FakeOAuthMcpServer server,
        ManagerOAuthHarness harness,
        CancellationToken ct)
    {
        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverAuthorizationResponse(authorization.Code, authorization.State, null);
        Assert.Equal(McpOAuthFlowStatus.Completed, (await flow.WaitForTerminalAsync(ct)).Status);
    }

    private static ManagerOAuthHarness CreateManagerHarness(
        FakeOAuthMcpServer server,
        string basePath,
        int port = DaemonConfig.DefaultPort,
        bool failToolListing = false,
        Dictionary<string, SensitiveString>? headers = null,
        string? endpointOverride = null,
        bool enabled = true,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        var credentials = new McpOAuthCredentialStore(
            paths,
            timeProvider,
            new NullSecretsProtector(),
            NullLogger<McpOAuthCredentialStore>.Instance);
        var broker = new McpOAuthFlowBroker(timeProvider, CancellationToken.None);
        var runtime = new FakeServerMcpRuntime(server, failToolListing);
        var logger = new RecordingLogger<McpClientManager>();
        var serverName = new McpServerName("fake-oauth");
        var manager = new McpClientManager(
            new Dictionary<string, McpServerEntry>
            {
                [serverName.Value] = new()
                {
                    Enabled = enabled,
                    Transport = "http",
                    Url = endpointOverride ?? server.McpEndpoint.ToString(),
                    Headers = headers,
                },
            },
            new ToolRegistry(),
            new ToolConfig(),
            credentials,
            McpOAuthTestDoubles.RegistrarFor(server.CreateHttpClient()),
            broker,
            new DaemonConfig { Port = port },
            NullNotificationSink.Instance,
            timeProvider,
            runtime,
            logger,
            new SessionConfig());
        return new ManagerOAuthHarness(manager, credentials, broker, runtime, logger, serverName);
    }

    private sealed class ManagerOAuthHarness(
        McpClientManager manager,
        McpOAuthCredentialStore credentials,
        McpOAuthFlowBroker broker,
        FakeServerMcpRuntime runtime,
        RecordingLogger<McpClientManager> logger,
        McpServerName serverName) : IAsyncDisposable
    {
        public McpClientManager Manager { get; } = manager;

        public McpOAuthCredentialStore Credentials { get; } = credentials;

        public McpOAuthFlowBroker Broker { get; } = broker;

        public FakeServerMcpRuntime Runtime { get; } = runtime;

        public RecordingLogger<McpClientManager> Logger { get; } = logger;

        public McpServerName ServerName { get; } = serverName;

        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync(CancellationToken.None);
            Manager.Dispose();
            Broker.Dispose();
        }
    }

    private sealed class FakeServerMcpRuntime(
        FakeOAuthMcpServer server,
        bool failToolListing) : IMcpClientRuntime
    {
        private int _createCount;
        private int _failNextToolListing;

        public int CreateCount => Volatile.Read(ref _createCount);

        public HttpClientTransportOptions? LastHttpOptions { get; private set; }

        public McpClientOptions? LastClientOptions { get; private set; }

        public InitializationBarrier? InitializationBarrier { get; set; }

        public void FailNextToolListing() => Interlocked.Exchange(ref _failNextToolListing, 1);

        public IClientTransport CreateHttpTransport(HttpClientTransportOptions options)
        {
            LastHttpOptions = options;
            return new HttpClientTransport(options, server.CreateHttpClient(), ownsHttpClient: true);
        }

        public Task<McpClient> CreateAsync(
            IClientTransport transport,
            McpClientOptions options,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCount);
            LastClientOptions = options;
            return McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
        }

        public async ValueTask<McpClientInitialization> InitializeAsync(
            McpClient client,
            CancellationToken cancellationToken)
        {
            if (failToolListing || Interlocked.Exchange(ref _failNextToolListing, 0) == 1)
                throw new InvalidOperationException("Controlled tool listing failure.");
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var barrier = InitializationBarrier;
            if (barrier is not null)
            {
                barrier.Reached.TrySetResult(true);
                await barrier.Release.Task.WaitAsync(cancellationToken);
            }
            return new McpClientInitialization(tools.Cast<AIFunction>().ToList());
        }

        public ValueTask<IReadOnlyList<AIFunction>> ListToolsAsync(
            McpClient client,
            CancellationToken cancellationToken)
            => ListToolsCoreAsync(client, cancellationToken);

        private async ValueTask<IReadOnlyList<AIFunction>> ListToolsCoreAsync(
            McpClient client,
            CancellationToken cancellationToken)
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return tools.Cast<AIFunction>().ToList();
        }

        public ValueTask<object?> InvokeAsync(
            AIFunction function,
            AIFunctionArguments? arguments,
            CancellationToken cancellationToken)
            => function.InvokeAsync(arguments, cancellationToken);

        public ValueTask DisposeAsync(McpClient client) => client.DisposeAsync();
    }

    private sealed class InitializationBarrier
    {
        public TaskCompletionSource<bool> Reached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private sealed class FakeOAuthMcpServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly FakeOAuthMcpServerState _state;

        private FakeOAuthMcpServer(WebApplication app, FakeOAuthMcpServerState state)
        {
            _app = app;
            _state = state;
        }

        public Uri McpEndpoint => _state.McpEndpoint;

        public int ProtectedResourceDiscoveryCount => _state.ProtectedResourceDiscoveryCount;

        public int AuthorizationServerDiscoveryCount => _state.AuthorizationServerDiscoveryCount;

        public int UnauthorizedMcpChallengeCount => _state.UnauthorizedMcpChallengeCount;

        public int AuthorizedMcpRequestCount => _state.AuthorizedMcpRequestCount;

        public IReadOnlyList<DynamicClientRegistrationObservation> DynamicClientRegistrations
            => _state.DynamicClientRegistrations;

        public IReadOnlyList<AuthorizationObservation> AuthorizationRequests
            => _state.AuthorizationRequests;

        public IReadOnlyList<TokenRequestObservation> TokenRequests => _state.TokenRequests;

        public IReadOnlyDictionary<string, string> LastMcpHeaders => _state.LastMcpHeaders;

        public void RejectClient(string clientId) => _state.RejectClient(clientId);

        public void AcceptBearer(string token) => _state.AcceptBearer(token);

        public void AcceptRefreshToken(string token, string clientId) => _state.AcceptRefreshToken(token, clientId);

        public void FailNextTokenExchange() => _state.FailNextTokenExchange();

        public void RevokeAccessToken(string token) => _state.RevokeAccessToken(token);

        public int RefreshGrantCount => _state.RefreshGrantCount;

        public static async Task<FakeOAuthMcpServer> StartAsync(
            CancellationToken ct,
            bool requireOAuth = true,
            string? acceptedBearer = null,
            bool rejectDcrWithoutBody = false,
            string? dcrRejectionBody = null)
        {
            var origin = new Uri("https://oauth-mcp.test");
            var state = new FakeOAuthMcpServerState(
                origin,
                requireOAuth,
                rejectDcrWithoutBody,
                dcrRejectionBody);
            if (acceptedBearer is not null)
                state.AcceptBearer(acceptedBearer);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(state);
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = "fake-oauth-mcp",
                        Version = "1.0.0",
                    };
                    options.ServerInstructions = "In-process OAuth MCP fake for SDK integration tests.";
                })
                .WithHttpTransport()
                .WithTools<FakeOAuthMcpTools>();

            var app = builder.Build();
            app.Use(async (ctx, next) =>
            {
                if (!ctx.Request.Path.StartsWithSegments("/mcp"))
                {
                    await next(ctx);
                    return;
                }

                state.RecordMcpHeaders(ctx.Request.Headers);
                if (!state.RequireOAuth)
                {
                    await next(ctx);
                    return;
                }

                if (ctx.Request.Headers.TryGetValue("Authorization", out var authorization)
                    && state.TryAcceptBearer(authorization.ToString()))
                {
                    state.RecordAuthorizedMcpRequest();
                    await next(ctx);
                    return;
                }

                state.RecordUnauthorizedMcpChallenge();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.Append("WWW-Authenticate",
                    $"Bearer resource_metadata=\"{state.ProtectedResourceMetadataEndpoint}\", scope=\"fake.read fake.write\"");
            });

            Func<HttpContext, Task<IResult>> registerHandler = state.HandleDynamicClientRegistrationAsync;
            Func<HttpContext, Task<IResult>> tokenHandler = state.HandleTokenAsync;
            app.MapGet("/.well-known/oauth-protected-resource/mcp", () => state.HandleProtectedResourceMetadata());
            app.MapGet("/.well-known/oauth-authorization-server", () => state.HandleAuthorizationServerMetadata());
            app.MapPost("/oauth/register", registerHandler);
            app.MapGet("/oauth/authorize", (HttpContext ctx) => state.HandleAuthorize(ctx));
            app.MapPost("/oauth/token", tokenHandler);
            app.MapMcp("/mcp");

            await app.StartAsync(ct);
            return new FakeOAuthMcpServer(app, state);
        }

        public HttpClient CreateHttpClient()
        {
            var client = _app.GetTestClient();
            client.BaseAddress = _state.Origin;
            return client;
        }

        public async Task<BrowserAuthorizationResult> AuthorizeAsync(
            Uri authorizationUri,
            Uri redirectUri,
            CancellationToken ct)
        {
            using var client = CreateHttpClient();
            using var response = await client.GetAsync(authorizationUri, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location;
            Assert.NotNull(location);
            Assert.Equal(redirectUri.GetLeftPart(UriPartial.Path), location!.GetLeftPart(UriPartial.Path));
            var query = ParseQuery(location.Query);
            var code = Assert.Contains("code", query);
            var state = Assert.Contains("state", query);
            return new BrowserAuthorizationResult(code, state);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }

    private sealed class FakeOAuthMcpTools
    {
        [McpServerTool(Name = "oauth_probe")]
        [Description("Returns a deterministic value once OAuth has succeeded.")]
        public static string OAuthProbe() => "oauth-ok";
    }

    private sealed class FakeOAuthMcpServerState
    {
        private readonly ConcurrentDictionary<string, RegisteredClient> _clients = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AuthorizationCodeRecord> _authorizationCodes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _acceptedAccessTokens = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _refreshTokens = new(StringComparer.Ordinal);
        private int _refreshGrantCount;
        private readonly ConcurrentDictionary<string, byte> _rejectedClients = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<DynamicClientRegistrationObservation> _registrations = new();
        private readonly ConcurrentQueue<AuthorizationObservation> _authorizations = new();
        private readonly ConcurrentQueue<TokenRequestObservation> _tokenRequests = new();
        private int _clientSequence;
        private int _codeSequence;
        private int _tokenSequence;
        private int _protectedResourceDiscoveryCount;
        private int _authorizationServerDiscoveryCount;
        private int _unauthorizedMcpChallengeCount;
        private int _authorizedMcpRequestCount;
        private int _failNextTokenExchange;
        private IReadOnlyDictionary<string, string> _lastMcpHeaders = new Dictionary<string, string>();

        public FakeOAuthMcpServerState(
            Uri origin,
            bool requireOAuth,
            bool rejectDcrWithoutBody,
            string? dcrRejectionBody)
        {
            Origin = origin;
            RequireOAuth = requireOAuth;
            RejectDcrWithoutBody = rejectDcrWithoutBody;
            DcrRejectionBody = dcrRejectionBody;
            McpEndpoint = new Uri(origin, "/mcp");
            ProtectedResourceMetadataEndpoint = new Uri(origin, "/.well-known/oauth-protected-resource/mcp");
            AuthorizationEndpoint = new Uri(origin, "/oauth/authorize");
            TokenEndpoint = new Uri(origin, "/oauth/token");
            RegistrationEndpoint = new Uri(origin, "/oauth/register");
        }

        public Uri Origin { get; }

        public bool RequireOAuth { get; }

        private bool RejectDcrWithoutBody { get; }

        private string? DcrRejectionBody { get; }

        public Uri McpEndpoint { get; }

        public Uri ProtectedResourceMetadataEndpoint { get; }

        private Uri AuthorizationEndpoint { get; }

        private Uri TokenEndpoint { get; }

        private Uri RegistrationEndpoint { get; }

        public int ProtectedResourceDiscoveryCount => Volatile.Read(ref _protectedResourceDiscoveryCount);

        public int AuthorizationServerDiscoveryCount => Volatile.Read(ref _authorizationServerDiscoveryCount);

        public int UnauthorizedMcpChallengeCount => Volatile.Read(ref _unauthorizedMcpChallengeCount);

        public int AuthorizedMcpRequestCount => Volatile.Read(ref _authorizedMcpRequestCount);

        public IReadOnlyList<DynamicClientRegistrationObservation> DynamicClientRegistrations => _registrations.ToArray();

        public IReadOnlyList<AuthorizationObservation> AuthorizationRequests => _authorizations.ToArray();

        public IReadOnlyList<TokenRequestObservation> TokenRequests => _tokenRequests.ToArray();

        public IReadOnlyDictionary<string, string> LastMcpHeaders => _lastMcpHeaders;

        public IResult HandleProtectedResourceMetadata()
        {
            Interlocked.Increment(ref _protectedResourceDiscoveryCount);
            return Results.Json(new
            {
                resource = McpEndpoint.ToString(),
                authorization_servers = new[] { Origin.ToString().TrimEnd('/') },
                scopes_supported = new[] { "fake.read", "fake.write" },
            });
        }

        public IResult HandleAuthorizationServerMetadata()
        {
            Interlocked.Increment(ref _authorizationServerDiscoveryCount);
            return Results.Json(new
            {
                issuer = Origin.ToString().TrimEnd('/'),
                authorization_endpoint = AuthorizationEndpoint.ToString(),
                token_endpoint = TokenEndpoint.ToString(),
                registration_endpoint = RegistrationEndpoint.ToString(),
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
                code_challenge_methods_supported = new[] { "S256" },
            });
        }

        public async Task<IResult> HandleDynamicClientRegistrationAsync(HttpContext context)
        {
            if (RejectDcrWithoutBody)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (DcrRejectionBody is not null)
                return Results.Text(DcrRejectionBody, statusCode: StatusCodes.Status403Forbidden);

            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = document.RootElement;
            var redirectUris = ReadStringArray(root, "redirect_uris");
            var grantTypes = ReadStringArray(root, "grant_types");
            var responseTypes = ReadStringArray(root, "response_types");
            var requestedTokenMethod = ReadOptionalString(root, "token_endpoint_auth_method");
            var scope = ReadOptionalString(root, "scope");
            var clientId = $"client-{Interlocked.Increment(ref _clientSequence)}";
            var clientSecret = $"secret-{clientId}";

            _clients[clientId] = new RegisteredClient(clientId, clientSecret, redirectUris);
            _registrations.Enqueue(new DynamicClientRegistrationObservation(
                clientId,
                clientSecret,
                redirectUris,
                grantTypes,
                responseTypes,
                requestedTokenMethod,
                scope));

            return Results.Json(new
            {
                client_id = clientId,
                client_secret = clientSecret,
                client_id_issued_at = 1,
                client_secret_expires_at = 0,
                redirect_uris = redirectUris,
                grant_types = grantTypes,
                response_types = responseTypes,
                token_endpoint_auth_method = "client_secret_post",
            });
        }

        public IResult HandleAuthorize(HttpContext context)
        {
            var query = context.Request.Query;
            var clientId = Required(query, "client_id");
            if (!_clients.TryGetValue(clientId, out var client))
                return Results.BadRequest($"Unknown client_id '{clientId}'.");

            var redirectUri = Required(query, "redirect_uri");
            if (!client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
                return Results.BadRequest("redirect_uri was not registered.");

            var codeChallenge = Required(query, "code_challenge");
            var codeChallengeMethod = Required(query, "code_challenge_method");
            var code = $"code-{Interlocked.Increment(ref _codeSequence)}";
            var observation = new AuthorizationObservation(
                ClientId: clientId,
                RedirectUri: redirectUri,
                ResponseType: Required(query, "response_type"),
                CodeChallenge: codeChallenge,
                CodeChallengeMethod: codeChallengeMethod,
                Resource: Optional(query, "resource"),
                Scope: Optional(query, "scope"),
                State: Optional(query, "state"),
                Code: code);

            _authorizationCodes[code] = new AuthorizationCodeRecord(
                clientId,
                redirectUri,
                codeChallenge,
                codeChallengeMethod,
                observation.Resource,
                observation.Scope);
            _authorizations.Enqueue(observation);

            var separator = redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var location = $"{redirectUri}{separator}code={Uri.EscapeDataString(code)}";
            if (!string.IsNullOrEmpty(observation.State))
                location += $"&state={Uri.EscapeDataString(observation.State)}";

            return Results.Redirect(location);
        }

        public async Task<IResult> HandleTokenAsync(HttpContext context)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var grantType = form["grant_type"].ToString();
            if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
                return HandleRefreshGrant(form);
            if (!string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
                return Results.BadRequest($"Unsupported grant_type '{grantType}'.");

            var code = form["code"].ToString();
            if (!_authorizationCodes.TryGetValue(code, out var authorizationCode))
                return Results.BadRequest("Unknown code.");

            var clientId = form["client_id"].ToString();
            if (_rejectedClients.ContainsKey(clientId))
                return Results.BadRequest(new { error = "invalid_client" });
            if (!_clients.TryGetValue(clientId, out var client))
                return Results.BadRequest("Unknown client_id.");

            var clientSecret = form["client_secret"].ToString();
            if (!string.Equals(clientSecret, client.ClientSecret, StringComparison.Ordinal))
                return Results.BadRequest("Invalid client_secret.");

            var redirectUri = form["redirect_uri"].ToString();
            var codeVerifier = form["code_verifier"].ToString();
            var pkceVerified = string.Equals(
                authorizationCode.CodeChallenge,
                ComputeCodeChallenge(codeVerifier),
                StringComparison.Ordinal);
            var sequence = Interlocked.Increment(ref _tokenSequence);
            var issuedAccessToken = $"access-{sequence}";
            var issuedRefreshToken = $"refresh-{sequence}";

            var observation = new TokenRequestObservation(
                ClientId: clientId,
                ClientSecret: clientSecret,
                Code: code,
                RedirectUri: redirectUri,
                CodeVerifier: codeVerifier,
                Resource: form["resource"].ToString(),
                PkceVerified: pkceVerified,
                IssuedAccessToken: issuedAccessToken,
                IssuedRefreshToken: issuedRefreshToken);
            _tokenRequests.Enqueue(observation);

            if (!string.Equals(clientId, authorizationCode.ClientId, StringComparison.Ordinal)
                || !string.Equals(redirectUri, authorizationCode.RedirectUri, StringComparison.Ordinal)
                || !pkceVerified
                || !authorizationCode.TryUse())
            {
                return Results.BadRequest("Invalid authorization code exchange.");
            }

            if (Interlocked.Exchange(ref _failNextTokenExchange, 0) == 1)
                return Results.StatusCode(StatusCodes.Status500InternalServerError);

            _acceptedAccessTokens[issuedAccessToken] = 0;
            _refreshTokens[issuedRefreshToken] = clientId;
            return Results.Json(new
            {
                access_token = issuedAccessToken,
                refresh_token = issuedRefreshToken,
                token_type = "Bearer",
                expires_in = 3600,
                scope = authorizationCode.Scope,
            });
        }

        /// <summary>
        /// Redeems a refresh token the way a rotating-token authorization server does: the old
        /// refresh token is consumed and a new pair is issued.
        /// </summary>
        private IResult HandleRefreshGrant(IFormCollection form)
        {
            var presented = form["refresh_token"].ToString();
            if (!_refreshTokens.TryRemove(presented, out var clientId))
                return Results.BadRequest(new { error = "invalid_grant" });

            var requestedClientId = form["client_id"].ToString();
            if (!string.Equals(requestedClientId, clientId, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "invalid_client" });

            var sequence = Interlocked.Increment(ref _tokenSequence);
            var issuedAccessToken = $"access-{sequence}";
            var issuedRefreshToken = $"refresh-{sequence}";
            _refreshTokens[issuedRefreshToken] = clientId;
            _acceptedAccessTokens[issuedAccessToken] = 0;
            Interlocked.Increment(ref _refreshGrantCount);

            return Results.Json(new
            {
                access_token = issuedAccessToken,
                refresh_token = issuedRefreshToken,
                token_type = "Bearer",
                expires_in = 3600,
            });
        }

        /// <summary>Stops accepting an access token, which makes the resource server answer 401.</summary>
        public void RevokeAccessToken(string token) => _acceptedAccessTokens.TryRemove(token, out _);

        public int RefreshGrantCount => Volatile.Read(ref _refreshGrantCount);

        public bool TryAcceptBearer(string authorizationHeader)
        {
            const string prefix = "Bearer ";
            if (!authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var token = authorizationHeader[prefix.Length..];
            return _acceptedAccessTokens.ContainsKey(token);
        }

        public void AcceptBearer(string token) => _acceptedAccessTokens[token] = 0;

        public void AcceptRefreshToken(string token, string clientId) => _refreshTokens[token] = clientId;

        public void RejectClient(string clientId) => _rejectedClients[clientId] = 0;

        public void FailNextTokenExchange() => Interlocked.Exchange(ref _failNextTokenExchange, 1);

        public void RecordMcpHeaders(IHeaderDictionary headers)
            => _lastMcpHeaders = headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

        public void RecordUnauthorizedMcpChallenge()
            => Interlocked.Increment(ref _unauthorizedMcpChallengeCount);

        public void RecordAuthorizedMcpRequest()
            => Interlocked.Increment(ref _authorizedMcpRequestCount);

        private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind is not JsonValueKind.Array)
                return [];

            return property.EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => value is not null)
                .Select(value => value!)
                .ToList();
        }

        private static string? ReadOptionalString(JsonElement root, string propertyName)
            => root.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.String
                ? property.GetString()
                : null;

        private static string Required(IQueryCollection query, string name)
        {
            var value = Optional(query, name);
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException($"Missing required query parameter '{name}'.");

            return value;
        }

        private static string? Optional(IQueryCollection query, string name)
            => query.TryGetValue(name, out var values) ? values.ToString() : null;

        private static string ComputeCodeChallenge(string codeVerifier)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
            return Convert.ToBase64String(hash)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }

    private sealed record RegisteredClient(
        string ClientId,
        string ClientSecret,
        IReadOnlyList<string> RedirectUris);

    private sealed class AuthorizationCodeRecord(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string codeChallengeMethod,
        string? resource,
        string? scope)
    {
        private int _used;

        public string ClientId { get; } = clientId;

        public string RedirectUri { get; } = redirectUri;

        public string CodeChallenge { get; } = codeChallenge;

        public string CodeChallengeMethod { get; } = codeChallengeMethod;

        public string? Resource { get; } = resource;

        public string? Scope { get; } = scope;

        public bool TryUse() => Interlocked.CompareExchange(ref _used, 1, 0) == 0;
    }

    private sealed record BrowserAuthorizationResult(string Code, string? State);

    private sealed record DynamicClientRegistrationObservation(
        string ClientId,
        string ClientSecret,
        IReadOnlyList<string> RedirectUris,
        IReadOnlyList<string> GrantTypes,
        IReadOnlyList<string> ResponseTypes,
        string? RequestedTokenEndpointAuthMethod,
        string? Scope);

    private sealed record AuthorizationObservation(
        string ClientId,
        string RedirectUri,
        string ResponseType,
        string CodeChallenge,
        string CodeChallengeMethod,
        string? Resource,
        string? Scope,
        string? State,
        string Code);

    private sealed record TokenRequestObservation(
        string ClientId,
        string ClientSecret,
        string Code,
        string RedirectUri,
        string CodeVerifier,
        string Resource,
        bool PkceVerified,
        string IssuedAccessToken,
        string IssuedRefreshToken);
}
