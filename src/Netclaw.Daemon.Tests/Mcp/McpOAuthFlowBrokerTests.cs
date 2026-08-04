// -----------------------------------------------------------------------
// <copyright file="McpOAuthFlowBrokerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Authentication;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpOAuthFlowBrokerTests
{
    private static readonly McpServerName ServerName = new("oauth-server");
    private static readonly Uri RedirectUri = new("http://127.0.0.1:7331/api/mcp/oauth/callback");

    [Fact]
    public async Task ConcurrentStartsForOneServerShareFlowAndSdkState()
    {
        using var broker = CreateBroker();

        var first = broker.StartOrJoin(ServerName);
        var second = broker.StartOrJoin(ServerName);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Same(first.Flow, second.Flow);

        // The MCP SDK owns `state` from 2.0 onward, so a flow has none until the SDK
        // hands over the authorization URL it built.
        Assert.Null(first.Flow.State);

        _ = InvokeCallbackHandler(first.Flow, AuthorizationUrl("sdk-state"));
        var request = await first.Flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sdk-state", request.State);
        Assert.Equal("sdk-state", first.Flow.State);
        Assert.Same(first.Flow, broker.GetForCallback("sdk-state"));
    }

    [Fact]
    public async Task AuthorizationUrlWithoutStateFailsTheFlowInsteadOfHanging()
    {
        // A virtual clock is what makes this test meaningful: the expiry timer never fires,
        // so the waiting caller can only be released by the failure path under test. On the
        // system clock the five-minute expiry releases it anyway and the assertion passes
        // whether or not the flow fails promptly.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        using var broker = new McpOAuthFlowBroker(time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var waitingStart = flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);

        var handler = InvokeCallbackHandler(flow, new Uri("https://auth.example/authorize?client_id=one"));

        await Assert.ThrowsAsync<McpOAuthOperationException>(async () => await handler);
        Assert.Null(flow.State);
        await Assert.ThrowsAsync<McpOAuthOperationException>(
            async () => await waitingStart.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
        Assert.Equal(McpOAuthFlowStatus.Failed, broker.GetLatestStatus(ServerName).Status);
    }

    [Fact]
    public async Task FailureBeforeTheAuthorizationUrlSurfacesToTheWaitingStartRequest()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        var waiting = flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);

        // Registration and discovery run before the SDK builds an authorization URL, so a
        // failure there leaves the flow with no state. `netclaw mcp auth` must still get the
        // structured reason instead of waiting for a URL that will never arrive.
        broker.Fail(flow, new McpErrorResponse(
            "MCP OAuth dynamic client registration failed: HTTP 403 Forbidden.",
            "dynamic client registration",
            403));

        var error = await Assert.ThrowsAsync<McpOAuthOperationException>(async () => await waiting);
        Assert.Equal(403, error.Error.Status);
        Assert.Null(flow.State);
    }

    [Fact]
    public void StatelessTerminalFlowIsDisposedWhenReplaced()
    {
        using var daemonCancellation = new CancellationTokenSource();
        using var broker = new McpOAuthFlowBroker(TimeProvider.System, daemonCancellation.Token);
        var abandoned = broker.StartOrJoin(ServerName).Flow;
        broker.Fail(abandoned, new McpErrorResponse("discovery failed", "authorization start"));

        // Retrying immediately is the normal reaction to a failed authorization, so the
        // replacement must reclaim the old flow. It was never indexed by state, so nothing
        // else can reach it, and Cancel alone does not release its registration on the daemon
        // shutdown token — only Dispose does.
        var replacement = broker.StartOrJoin(ServerName);

        Assert.True(replacement.Created);
        Assert.NotSame(abandoned, replacement.Flow);
        Assert.True(abandoned.IsDisposed);
    }

    [Fact]
    public async Task CallbackParametersReturnToTheSdkUnmodified()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        var handler = InvokeCallbackHandler(flow, AuthorizationUrl("round-trip"));
        await flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);

        // The SDK compares state against the value it generated and validates iss per
        // RFC 9207, so the daemon must relay both without touching them.
        broker.GetForCallback("round-trip")
            .DeliverAuthorizationResponse("the-code", "round-trip", "https://auth.example");

        var result = await handler;
        Assert.Equal("the-code", result?.Code);
        Assert.Equal("round-trip", result?.State);
        Assert.Equal("https://auth.example", result?.Iss);
    }

    [Fact]
    public async Task FirstHandlerOwnsUrlAndCodeFollowersFailWithoutCodeReuse()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;

        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("owner"));
        var follower = InvokeCallbackHandler(flow, AuthorizationUrl("follower"));

        var request = await flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AuthorizationUrl("owner"), request.Url);
        await Assert.ThrowsAsync<McpOAuthAuthorizationInProgressException>(async () => await follower);

        broker.GetForCallback("owner").DeliverAuthorizationResponse("owner-code", "owner", null);
        Assert.Equal("owner-code", (await owner)?.Code);
        broker.BeginCommit(flow);
        broker.Complete(flow);
        Assert.Equal(McpOAuthFlowStatus.Completed, broker.GetStatusByState("owner").Status);
    }

    [Fact]
    public async Task MissingOrMismatchedStateDoesNotAffectPendingFlow()
    {
        using var broker = CreateBroker();
        var flow = await StartFlowWithStateAsync(broker, "pending-state");

        Assert.Throws<McpOAuthCallbackException>(() => broker.GetForCallback("wrong-state"));

        Assert.Same(flow, broker.GetForCallback("pending-state"));
        Assert.Equal(McpOAuthFlowStatus.Pending, broker.GetStatusByState("pending-state").Status);
    }

    [Fact]
    public async Task ReusedStateCannotDeliverCodeTwice()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("one-time"));

        flow.DeliverAuthorizationResponse("one-time-code", "one-time", null);
        Assert.Equal("one-time-code", (await owner)?.Code);

        Assert.Throws<McpOAuthCallbackException>(
            () => flow.DeliverAuthorizationResponse("reused-code", "one-time", null));
    }

    [Fact]
    public async Task TimeProviderExpiryCancelsOwnerAndLeavesFailedTombstone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        using var broker = new McpOAuthFlowBroker(time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("expiring"));
        await flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);

        time.Advance(McpOAuthFlowBroker.FlowLifetime);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner);
        var terminal = broker.GetStatusByState("expiring");
        Assert.Equal(McpOAuthFlowStatus.Failed, terminal.Status);
        Assert.Contains("expired", terminal.Error?.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<McpOAuthCallbackException>(() => broker.GetForCallback("expiring"));
    }

    [Fact]
    public async Task ExpiryAtCommitRejectsPublicationClaim()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        using var broker = new McpOAuthFlowBroker(time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("burned"));
        flow.DeliverAuthorizationResponse("burned-code", "burned", null);
        Assert.Equal("burned-code", (await owner)?.Code);

        time.Advance(McpOAuthFlowBroker.FlowLifetime);

        Assert.Throws<McpOAuthOperationException>(() => broker.BeginCommit(flow));
        Assert.Equal(McpOAuthFlowStatus.Failed, broker.GetStatusByState("burned").Status);
    }

    [Fact]
    public async Task ClaimedCommitCannotLoseRaceToExpiry()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        using var broker = new McpOAuthFlowBroker(time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("commit"));
        flow.DeliverAuthorizationResponse("commit-code", "commit", null);
        Assert.Equal("commit-code", (await owner)?.Code);
        time.Advance(McpOAuthFlowBroker.FlowLifetime - TimeSpan.FromTicks(1));

        broker.BeginCommit(flow);
        time.Advance(TimeSpan.FromTicks(1));
        broker.Complete(flow);

        Assert.Equal(McpOAuthFlowStatus.Completed, broker.GetStatusByState("commit").Status);
    }

    [Fact]
    public async Task StartRequestCancellationDoesNotCancelDaemonOwnedFlow()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        using var requestCancellation = new CancellationTokenSource();
        var request = flow.WaitForAuthorizationRequestAsync(requestCancellation.Token);
        await requestCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);

        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("still-running"));
        var published = await flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AuthorizationUrl("still-running"), published.Url);
        flow.DeliverAuthorizationResponse("still-running-code", "still-running", null);
        Assert.Equal("still-running-code", (await owner)?.Code);
    }

    [Fact]
    public async Task CallbackRequestCancellationDoesNotCancelExchangeOwner()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("delivered"));
        flow.DeliverAuthorizationResponse("delivered-code", "delivered", null);
        Assert.Equal("delivered-code", (await owner)?.Code);
        using var requestCancellation = new CancellationTokenSource();
        var callbackWait = flow.WaitForTerminalAsync(requestCancellation.Token);
        await requestCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await callbackWait);

        broker.BeginCommit(flow);
        broker.Complete(flow);
        Assert.Equal(McpOAuthFlowStatus.Completed, broker.GetStatusByState("delivered").Status);
    }

    [Fact]
    public async Task DaemonCancellationCancelsOwnerWithoutReturningCodeToFollower()
    {
        using var daemonCancellation = new CancellationTokenSource();
        using var broker = new McpOAuthFlowBroker(TimeProvider.System, daemonCancellation.Token);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = InvokeCallbackHandler(flow, AuthorizationUrl("cancelled"));
        var follower = InvokeCallbackHandler(flow, AuthorizationUrl("cancelled"));

        await daemonCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner);
        await Assert.ThrowsAsync<McpOAuthAuthorizationInProgressException>(async () => await follower);
    }

    [Fact]
    public async Task TerminalFlowAllowsNewAttemptWithNewStateWhileKeepingOldStatus()
    {
        using var broker = CreateBroker();
        var oldFlow = await StartFlowWithStateAsync(broker, "old-state");
        broker.Fail(oldFlow, new McpErrorResponse("DCR failed: HTTP 403 Forbidden.", "dynamic client registration", 403));

        var next = broker.StartOrJoin(ServerName);

        Assert.True(next.Created);
        Assert.NotSame(oldFlow, next.Flow);
        Assert.Equal(403, broker.GetStatusByState("old-state").Error?.Status);
    }

    private static Uri AuthorizationUrl(string state)
        => new($"https://auth.example/authorize?client_id=one&state={state}");

    private static Task<AuthorizationResult?> InvokeCallbackHandler(McpOAuthFlow flow, Uri authorizationUri)
        => flow.HandleAuthorizationCallbackAsync(
            new AuthorizationCallbackContext
            {
                AuthorizationUri = authorizationUri,
                RedirectUri = RedirectUri,
            },
            CancellationToken.None);

    private static async Task<McpOAuthFlow> StartFlowWithStateAsync(McpOAuthFlowBroker broker, string state)
    {
        var flow = broker.StartOrJoin(ServerName).Flow;
        _ = InvokeCallbackHandler(flow, AuthorizationUrl(state));
        await flow.WaitForAuthorizationRequestAsync(TestContext.Current.CancellationToken);
        return flow;
    }

    private static McpOAuthFlowBroker CreateBroker()
        => new(TimeProvider.System, CancellationToken.None);
}
