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
    public async Task SdkRedirectDelegateFlow_PerformsDiscoveryDcrPkceExchangeAndStoresTokens()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        var ct = timeout.Token;

        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        var tokenCache = new RecordingTokenCache();
        var broker = OperatorPromptBroker.Authorizing(server, "netclaw-broker-state");
        DynamicClientRegistrationResponse? dcrResponse = null;

        var oauth = new ClientOAuthOptions
        {
            RedirectUri = RedirectUri,
            AdditionalAuthorizationParameters = new Dictionary<string, string>
            {
                ["state"] = broker.State,
            },
            AuthorizationRedirectDelegate = broker.HandleAuthorizationAsync,
            TokenCache = tokenCache,
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "netclaw-oauth-spike",
                ResponseDelegate = (response, _) =>
                {
                    dcrResponse = response;
                    return Task.CompletedTask;
                },
            },
        };

        await using var client = await CreateClientAsync(server, oauth, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Contains(tools, tool => tool.Name == "oauth_probe");

        Assert.Equal(1, server.ProtectedResourceDiscoveryCount);
        Assert.Equal(1, server.AuthorizationServerDiscoveryCount);
        Assert.Equal(1, server.UnauthorizedMcpChallengeCount);
        Assert.True(server.AuthorizedMcpRequestCount >= 1);

        var registration = Assert.Single(server.DynamicClientRegistrations);
        Assert.Contains(RedirectUri.ToString(), registration.RedirectUris);
        Assert.Equal("client_secret_post", registration.RequestedTokenEndpointAuthMethod);
        Assert.Equal("fake.read fake.write", registration.Scope);
        Assert.NotNull(dcrResponse);
        Assert.Equal(registration.ClientId, dcrResponse!.ClientId);
        Assert.Equal(registration.ClientSecret, dcrResponse.ClientSecret);

        var authorization = Assert.Single(server.AuthorizationRequests);
        Assert.Equal(registration.ClientId, authorization.ClientId);
        Assert.Equal(RedirectUri.ToString(), authorization.RedirectUri);
        Assert.Equal(server.McpEndpoint.ToString(), authorization.Resource);
        Assert.Equal("fake.read fake.write", authorization.Scope);
        Assert.Equal(broker.State, authorization.State);
        Assert.Equal("S256", authorization.CodeChallengeMethod);
        Assert.False(string.IsNullOrWhiteSpace(authorization.CodeChallenge));

        Assert.Equal(1, broker.DelegateInvocationCount);
        Assert.Equal(1, broker.OperatorPromptCount);
        Assert.Equal(broker.State, broker.ReturnedState);
        Assert.Equal(authorization.Code, broker.ReturnedCode);
        Assert.NotNull(broker.DeliveredAuthorizationUrl);
        Assert.Equal(broker.State, GetQueryValue(broker.DeliveredAuthorizationUrl!, "state"));
        Assert.Equal(authorization.CodeChallenge, GetQueryValue(broker.DeliveredAuthorizationUrl!, "code_challenge"));
        Assert.Equal("S256", GetQueryValue(broker.DeliveredAuthorizationUrl!, "code_challenge_method"));

        var tokenRequest = Assert.Single(server.TokenRequests);
        Assert.Equal(authorization.Code, tokenRequest.Code);
        Assert.Equal(registration.ClientId, tokenRequest.ClientId);
        Assert.Equal(registration.ClientSecret, tokenRequest.ClientSecret);
        Assert.Equal(RedirectUri.ToString(), tokenRequest.RedirectUri);
        Assert.Equal(server.McpEndpoint.ToString(), tokenRequest.Resource);
        Assert.True(tokenRequest.PkceVerified);

        var storedTokens = tokenCache.StoredTokens;
        var stored = Assert.Single(storedTokens);
        Assert.Equal(tokenRequest.IssuedAccessToken, stored.AccessToken);
        Assert.Equal(tokenRequest.IssuedRefreshToken, stored.RefreshToken);
        Assert.Equal("Bearer", stored.TokenType);
        Assert.Equal("fake.read fake.write", stored.Scope);
        Assert.Equal(3600, stored.ExpiresIn);
    }

    [Fact]
    public async Task ProductionFlowFollowerFailsClassifiedWhileOwnerCompletesWithOneCodeExchange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var flowBroker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var flow = flowBroker.StartOrJoin(new McpServerName("production-flow")).Flow;
        var tokenCache = new RecordingTokenCache();
        var oauth = new ClientOAuthOptions
        {
            RedirectUri = RedirectUri,
            AdditionalAuthorizationParameters = new Dictionary<string, string>
            {
                ["state"] = flow.State,
            },
            AuthorizationRedirectDelegate = flow.HandleAuthorizationRedirectAsync,
            TokenCache = tokenCache,
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "netclaw-production-flow-test",
            },
        };

        var owner = CreateClientAsync(server, oauth, ct);
        var authorizationUrl = await flow.WaitForAuthorizationUrlAsync(ct);
        var follower = CreateClientAsync(server, oauth, ct);

        var followerError = await CaptureExceptionAsync(follower);
        Assert.True(ContainsException<McpOAuthAuthorizationInProgressException>(followerError));
        Assert.False(owner.IsCompleted);
        Assert.Empty(server.TokenRequests);

        var authorization = await server.AuthorizeAsync(authorizationUrl, RedirectUri, ct);
        flow.DeliverCode(authorization.Code);
        await using var ownerClient = await owner;
        var tools = await ownerClient.ListToolsAsync(cancellationToken: ct);
        flowBroker.BeginCommit(flow);
        flowBroker.Complete(flow);

        Assert.Contains(tools, tool => tool.Name == "oauth_probe");
        var tokenRequest = Assert.Single(server.TokenRequests);
        Assert.Equal(authorization.Code, tokenRequest.Code);
        Assert.True(tokenRequest.PkceVerified);
        Assert.Single(tokenCache.StoredTokens);
        Assert.Equal(McpOAuthFlowStatus.Completed, flowBroker.GetStatusByState(flow.State).Status);
    }

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
        flow.DeliverCode(authorization.Code);

        var terminal = await flow.WaitForTerminalAsync(ct);

        Assert.True(
            terminal.Status is McpOAuthFlowStatus.Completed,
            $"{terminal.Error?.Error} :: {harness.Manager.GetServerStatuses()[harness.ServerName].ErrorMessage} :: {harness.Logger.LastException}");
        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[harness.ServerName].State);
        Assert.Contains("oauth_probe", harness.Manager.GetToolNames(harness.ServerName));
        var active = harness.Credentials.GetEnvelopeForTests(harness.ServerName).Active;
        Assert.NotNull(active);
        Assert.Equal("client-1", active!.ClientId);
        Assert.Equal("secret-client-1", active.ClientSecret?.Value);
        Assert.Null(harness.Credentials.GetEnvelopeForTests(harness.ServerName).Pending);
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
            harness.Credentials.GetEnvelopeForTests(harness.ServerName).Active?.AccessToken.Value);
        Assert.Null(harness.Credentials.GetEnvelopeForTests(harness.ServerName).Pending);
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
        flow.DeliverCode(authorization.Code);
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
        var firstOperation = Assert.IsAssignableFrom<Task>(
            harness.Manager.GetInteractiveAuthorizationTask(harness.ServerName));
        var firstAuthorization = await server.AuthorizeAsync(
            new Uri(firstStart.AuthorizationUrl),
            RedirectUri,
            ct);
        var firstFlow = harness.Broker.GetForCallback(firstStart.State);
        firstFlow.DeliverCode(firstAuthorization.Code);
        Assert.Equal(
            McpOAuthFlowStatus.Failed,
            (await firstFlow.WaitForTerminalAsync(ct)).Status);
        await firstOperation.WaitAsync(ct);

        var secondStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var secondAuthorization = await server.AuthorizeAsync(
            new Uri(secondStart.AuthorizationUrl),
            RedirectUri,
            ct);
        var secondFlow = harness.Broker.GetForCallback(secondStart.State);
        secondFlow.DeliverCode(secondAuthorization.Code);
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
        flow.DeliverCode(authorization.Code);
        Assert.Equal(McpOAuthFlowStatus.Completed, (await flow.WaitForTerminalAsync(ct)).Status);
    }

    [Fact]
    public async Task RuntimeFlowExpiryRemovesDurablePendingCredentialsWithoutRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path, timeProvider: time);
        var barrier = new InitializationBarrier();
        harness.Runtime.InitializationBarrier = barrier;
        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var operation = Assert.IsAssignableFrom<Task>(
            harness.Manager.GetInteractiveAuthorizationTask(harness.ServerName));
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverCode(authorization.Code);
        await barrier.Reached.Task.WaitAsync(ct);
        Assert.Equal(started.State, harness.Credentials.GetEnvelopeForTests(harness.ServerName).Pending?.FlowId);

        time.Advance(McpOAuthFlowBroker.FlowLifetime);
        await operation.WaitAsync(ct);

        Assert.Equal(McpOAuthFlowStatus.Failed, harness.Broker.GetStatusByState(started.State).Status);
        Assert.Null(harness.Credentials.GetEnvelopeForTests(harness.ServerName).Pending);
        barrier.Release.TrySetResult(true);
    }

    [Fact]
    public async Task FailedToolListingRemovesPendingAndPreservesActiveCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using (var first = CreateManagerHarness(server, directory.Path))
        {
            await CompleteManagerAuthorizationAsync(server, first, ct);
        }

        await using var failing = CreateManagerHarness(server, directory.Path, failToolListing: true);
        var oldAccess = failing.Credentials.GetEnvelopeForTests(failing.ServerName).Active!.AccessToken.Value;
        var started = await failing.Manager.StartAuthorizationAsync(failing.ServerName, ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = failing.Broker.GetForCallback(started.State);
        flow.DeliverCode(authorization.Code);

        var terminal = await flow.WaitForTerminalAsync(ct);

        Assert.Equal(McpOAuthFlowStatus.Failed, terminal.Status);
        Assert.Equal(oldAccess, failing.Credentials.GetEnvelopeForTests(failing.ServerName).Active?.AccessToken.Value);
        Assert.Null(failing.Credentials.GetEnvelopeForTests(failing.ServerName).Pending);
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
        var active = harness.Credentials.GetEnvelopeForTests(harness.ServerName).Active!;
        harness.Runtime.FailNextToolListing();

        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverCode(authorization.Code);
        var terminal = await flow.WaitForTerminalAsync(ct);

        var retained = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(harness.ServerName));
        var retainedCredentials = harness.Credentials.GetEnvelopeForTests(harness.ServerName).Active;
        Assert.Equal(McpOAuthFlowStatus.Failed, terminal.Status);
        Assert.Same(published.Client, retained.Client);
        Assert.Equal(published.Generation, retained.Generation);
        Assert.Equal(published.ToolFunctions.Keys, retained.ToolFunctions.Keys);
        Assert.Equal(active.AccessToken.Value, retainedCredentials?.AccessToken.Value);
        Assert.Equal(active.RefreshToken?.Value, retainedCredentials?.RefreshToken?.Value);
        Assert.Equal(active.CredentialEpoch, retainedCredentials?.CredentialEpoch);
        Assert.Null(harness.Credentials.GetEnvelopeForTests(harness.ServerName).Pending);
    }

    [Fact]
    public async Task PendingStoreConflictReportsCredentialPersistenceTerminalError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);
        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var competingContext = harness.Credentials.CreateContext(
            harness.ServerName,
            server.McpEndpoint.ToString(),
            null,
            true);
        var competingCache = harness.Credentials.CreateTokenCache(
            harness.ServerName,
            server.McpEndpoint.ToString(),
            competingContext,
            McpOAuthCredentialTarget.Pending,
            "competing-flow",
            TimeProvider.System.GetUtcNow().AddMinutes(5),
            true);
        await competingCache.StoreTokensAsync(
            new TokenContainer
            {
                AccessToken = "competing-access",
                RefreshToken = "competing-refresh",
                TokenType = "Bearer",
                ObtainedAt = TimeProvider.System.GetUtcNow(),
                ExpiresIn = 3600,
            },
            ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverCode(authorization.Code);

        var terminal = await flow.WaitForTerminalAsync(ct);

        Assert.Equal(McpOAuthFlowStatus.Failed, terminal.Status);
        Assert.Equal("credential persistence", terminal.Error?.Operation);
        Assert.DoesNotContain("competing-flow", terminal.Error?.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromotionEpochConflictReportsCredentialPersistenceTerminalError()
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
        flow.DeliverCode(authorization.Code);
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

        Assert.Equal(McpOAuthFlowStatus.Failed, terminal.Status);
        Assert.Equal("credential persistence", terminal.Error?.Operation);
        Assert.Equal(
            "active-rotated-during-flow",
            harness.Credentials.GetEnvelopeForTests(harness.ServerName).Active?.AccessToken.Value);
        Assert.Null(harness.Credentials.GetEnvelopeForTests(harness.ServerName).Pending);
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
    public async Task InvalidClientMarkerSurvivesFailedReplacementAndForcesAnotherFreshDcr()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct);
        using var directory = new DisposableTempDir();
        await using (var first = CreateManagerHarness(server, directory.Path))
        {
            await CompleteManagerAuthorizationAsync(server, first, ct);
        }

        server.RejectClient("client-1");
        await using var harness = CreateManagerHarness(server, directory.Path);
        var rejectedStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var rejectedAuthorization = await server.AuthorizeAsync(new Uri(rejectedStart.AuthorizationUrl), RedirectUri, ct);
        var rejectedFlow = harness.Broker.GetForCallback(rejectedStart.State);
        rejectedFlow.DeliverCode(rejectedAuthorization.Code);
        var rejectedTerminal = await rejectedFlow.WaitForTerminalAsync(ct);

        Assert.Equal(McpOAuthFlowStatus.Failed, rejectedTerminal.Status);
        Assert.Equal(
            "client-1",
            harness.Credentials.GetEnvelopeForTests(harness.ServerName).RejectedDynamicClientId);

        var replacementStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);

        Assert.Contains("client_id=client-2", replacementStart.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Equal(2, server.DynamicClientRegistrations.Count);
        Assert.Equal(
            "client-1",
            harness.Credentials.GetEnvelopeForTests(harness.ServerName).RejectedDynamicClientId);

        server.RejectClient("client-2");
        var replacementAuthorization = await server.AuthorizeAsync(
            new Uri(replacementStart.AuthorizationUrl),
            RedirectUri,
            ct);
        var replacementFlow = harness.Broker.GetForCallback(replacementStart.State);
        replacementFlow.DeliverCode(replacementAuthorization.Code);
        Assert.Equal(
            McpOAuthFlowStatus.Failed,
            (await replacementFlow.WaitForTerminalAsync(ct)).Status);
        Assert.Equal(
            "client-1",
            harness.Credentials.GetEnvelopeForTests(harness.ServerName).RejectedDynamicClientId);

        var thirdStart = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        Assert.Contains("client_id=client-3", thirdStart.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Equal(3, server.DynamicClientRegistrations.Count);
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
            repointed.Credentials.GetEnvelopeForTests(repointed.ServerName).Active?.ResourceIdentity);
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
        Assert.Null(harness.Credentials.GetEnvelopeForTests(harness.ServerName).Active?.ResourceIdentity);
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
                  "Active": {
                    "AccessToken": "expired-access",
                    "ExpiresAt": "2020-01-01T00:00:00+00:00",
                    "ResourceIdentity": "{{canonical}}"
                  }
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
    public async Task BodylessDcr403ProducesStructuredTerminalError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeOAuthMcpServer.StartAsync(ct, rejectDcrWithoutBody: true);
        using var directory = new DisposableTempDir();
        await using var harness = CreateManagerHarness(server, directory.Path);

        var error = await Assert.ThrowsAsync<McpOAuthOperationException>(async () =>
            await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct));

        Assert.Equal("dynamic client registration", error.Error.Operation);
        Assert.Contains("403", error.Error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("token", error.Error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403", harness.Logger.LastMessage, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains(harness.Logger.Exceptions, exception =>
            exception.ToString().Contains("secret-value", StringComparison.Ordinal));
    }

    private static async Task CompleteManagerAuthorizationAsync(
        FakeOAuthMcpServer server,
        ManagerOAuthHarness harness,
        CancellationToken ct)
    {
        var started = await harness.Manager.StartAuthorizationAsync(harness.ServerName, ct);
        var authorization = await server.AuthorizeAsync(new Uri(started.AuthorizationUrl), RedirectUri, ct);
        var flow = harness.Broker.GetForCallback(started.State);
        flow.DeliverCode(authorization.Code);
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public Exception? LastException { get; private set; }

        public string? LastMessage { get; private set; }

        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LastMessage = formatter(state, exception);
            if (exception is not null)
            {
                LastException = exception;
                Exceptions.Add(exception);
            }
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

    private static async Task<McpClient> CreateClientAsync(
        FakeOAuthMcpServer server,
        ClientOAuthOptions oauth,
        CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = server.McpEndpoint,
            Name = "fake-oauth-mcp",
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = oauth,
        }, server.CreateHttpClient(), ownsHttpClient: true);

        try
        {
            return await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "netclaw-oauth-spike",
                    Version = "1.0.0",
                },
            }, cancellationToken: ct);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static bool ContainsException<TException>(Exception? exception)
        where TException : Exception
    {
        while (exception is not null)
        {
            if (exception is TException)
                return true;
            exception = exception.InnerException;
        }

        return false;
    }

    private static string? GetQueryValue(Uri uri, string name)
        => ParseQuery(uri.Query).GetValueOrDefault(name);

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

    private sealed class OperatorPromptBroker
    {
        private readonly FakeOAuthMcpServer _server;
        private readonly object _sync = new();
        private Task<BrowserAuthorizationResult>? _authorizationTask;
        private int _delegateInvocationCount;
        private int _operatorPromptCount;

        private OperatorPromptBroker(FakeOAuthMcpServer server, string state)
        {
            _server = server;
            State = state;
        }

        public string State { get; }

        public int DelegateInvocationCount => Volatile.Read(ref _delegateInvocationCount);

        public int OperatorPromptCount => Volatile.Read(ref _operatorPromptCount);

        public Uri? DeliveredAuthorizationUrl { get; private set; }

        public string? ReturnedCode { get; private set; }

        public string? ReturnedState { get; private set; }

        public static OperatorPromptBroker Authorizing(FakeOAuthMcpServer server, string state)
            => new(server, state);

        public async Task<string?> HandleAuthorizationAsync(
            Uri authorizationUri,
            Uri redirectUri,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _delegateInvocationCount);

            Task<BrowserAuthorizationResult> authorizationTask;
            lock (_sync)
            {
                if (_authorizationTask is null)
                {
                    PublishPrompt(authorizationUri);
                    _authorizationTask = _server.AuthorizeAsync(authorizationUri, redirectUri, cancellationToken);
                }

                authorizationTask = _authorizationTask;
            }

            var result = await authorizationTask.WaitAsync(cancellationToken);
            ReturnedCode = result.Code;
            ReturnedState = result.State;
            return result.Code;
        }

        private void PublishPrompt(Uri authorizationUri)
        {
            lock (_sync)
            {
                if (DeliveredAuthorizationUrl is not null)
                    return;

                DeliveredAuthorizationUrl = authorizationUri;
                Interlocked.Increment(ref _operatorPromptCount);
            }
        }
    }

    private sealed class RecordingTokenCache : ITokenCache
    {
        private readonly List<TokenContainer> _storedTokens = [];
        private readonly object _sync = new();
        private TokenContainer? _current;

        public IReadOnlyList<TokenContainer> StoredTokens
        {
            get
            {
                lock (_sync)
                    return _storedTokens.Select(Clone).ToList();
            }
        }

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
                return new ValueTask<TokenContainer?>(_current is null ? null : Clone(_current));
        }

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clone = Clone(tokens);
            lock (_sync)
            {
                _current = clone;
                _storedTokens.Add(Clone(tokens));
            }

            return default;
        }

        private static TokenContainer Clone(TokenContainer tokens)
            => new()
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn,
                ObtainedAt = tokens.ObtainedAt,
                Scope = tokens.Scope,
                TokenType = tokens.TokenType,
            };
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

        public void FailNextTokenExchange() => _state.FailNextTokenExchange();

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
            if (!string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
                return Results.BadRequest("Only authorization_code is supported by the fake server.");

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
            var issuedAccessToken = $"access-{Interlocked.Increment(ref _tokenSequence)}";
            var issuedRefreshToken = $"refresh-{_tokenSequence}";

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
            return Results.Json(new
            {
                access_token = issuedAccessToken,
                refresh_token = issuedRefreshToken,
                token_type = "Bearer",
                expires_in = 3600,
                scope = authorizationCode.Scope,
            });
        }

        public bool TryAcceptBearer(string authorizationHeader)
        {
            const string prefix = "Bearer ";
            if (!authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var token = authorizationHeader[prefix.Length..];
            return _acceptedAccessTokens.ContainsKey(token);
        }

        public void AcceptBearer(string token) => _acceptedAccessTokens[token] = 0;

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
