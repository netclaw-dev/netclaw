// -----------------------------------------------------------------------
// <copyright file="McpOAuthFlowBrokerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpOAuthFlowBrokerTests
{
    private static readonly McpServerName ServerName = new("oauth-server");
    private static readonly Uri AuthorizationUrl = new("https://auth.example/authorize?client_id=one");
    private static readonly Uri RedirectUri = new("http://127.0.0.1:7331/api/mcp/oauth/callback");

    [Fact]
    public void ConcurrentStartsForOneServerShareOpaqueFlow()
    {
        using var broker = CreateBroker();

        var first = broker.StartOrJoin(ServerName);
        var second = broker.StartOrJoin(ServerName);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Same(first.Flow, second.Flow);
        Assert.Equal(43, first.Flow.State.Length);
        Assert.DoesNotContain(ServerName.Value, first.Flow.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstDelegateOwnsUrlAndCodeFollowersFailWithoutCodeReuse()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;

        var owner = flow.HandleAuthorizationRedirectAsync(
            AuthorizationUrl,
            RedirectUri,
            CancellationToken.None);
        var follower = flow.HandleAuthorizationRedirectAsync(
            new Uri("https://auth.example/authorize?client_id=two"),
            RedirectUri,
            CancellationToken.None);

        Assert.Equal(AuthorizationUrl, await flow.WaitForAuthorizationUrlAsync(CancellationToken.None));
        await Assert.ThrowsAsync<McpOAuthAuthorizationInProgressException>(async () => await follower);
        broker.GetForCallback(flow.State).DeliverCode("owner-code");
        Assert.Equal("owner-code", await owner);
        broker.BeginCommit(flow);
        broker.Complete(flow);
        Assert.Equal(McpOAuthFlowStatus.Completed, broker.GetStatusByState(flow.State).Status);
    }

    [Fact]
    public void MissingOrMismatchedStateDoesNotAffectPendingFlow()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;

        Assert.Throws<McpOAuthCallbackException>(() => broker.GetForCallback("wrong-state"));

        Assert.Same(flow, broker.GetForCallback(flow.State));
        Assert.Equal(McpOAuthFlowStatus.Pending, broker.GetStatusByState(flow.State).Status);
    }

    [Fact]
    public async Task ReusedStateCannotDeliverCodeTwice()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);

        flow.DeliverCode("one-time-code");
        Assert.Equal("one-time-code", await owner);

        Assert.Throws<McpOAuthCallbackException>(() => flow.DeliverCode("reused-code"));
    }

    [Fact]
    public async Task TimeProviderExpiryCancelsOwnerAndLeavesFailedTombstone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        using var broker = new McpOAuthFlowBroker(time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);

        time.Advance(McpOAuthFlowBroker.FlowLifetime);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner);
        var terminal = broker.GetStatusByState(flow.State);
        Assert.Equal(McpOAuthFlowStatus.Failed, terminal.Status);
        Assert.Contains("expired", terminal.Error?.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<McpOAuthCallbackException>(() => broker.GetForCallback(flow.State));
    }

    [Fact]
    public async Task ExpiryAtCommitRejectsPublicationClaim()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        using var broker = new McpOAuthFlowBroker(time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);
        flow.DeliverCode("burned-code");
        Assert.Equal("burned-code", await owner);

        time.Advance(McpOAuthFlowBroker.FlowLifetime);

        Assert.Throws<McpOAuthOperationException>(() => broker.BeginCommit(flow));
        Assert.Equal(McpOAuthFlowStatus.Failed, broker.GetStatusByState(flow.State).Status);
    }

    [Fact]
    public async Task ClaimedCommitCannotLoseRaceToExpiry()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        using var broker = new McpOAuthFlowBroker(time, CancellationToken.None);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);
        flow.DeliverCode("commit-code");
        Assert.Equal("commit-code", await owner);
        time.Advance(McpOAuthFlowBroker.FlowLifetime - TimeSpan.FromTicks(1));

        broker.BeginCommit(flow);
        time.Advance(TimeSpan.FromTicks(1));
        broker.Complete(flow);

        Assert.Equal(McpOAuthFlowStatus.Completed, broker.GetStatusByState(flow.State).Status);
    }

    [Fact]
    public async Task StartRequestCancellationDoesNotCancelDaemonOwnedFlow()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        using var requestCancellation = new CancellationTokenSource();
        var request = flow.WaitForAuthorizationUrlAsync(requestCancellation.Token);
        requestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);

        var owner = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);
        Assert.Equal(AuthorizationUrl, await flow.WaitForAuthorizationUrlAsync(CancellationToken.None));
        flow.DeliverCode("still-running");
        Assert.Equal("still-running", await owner);
    }

    [Fact]
    public async Task CallbackRequestCancellationDoesNotCancelExchangeOwner()
    {
        using var broker = CreateBroker();
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);
        flow.DeliverCode("delivered");
        Assert.Equal("delivered", await owner);
        using var requestCancellation = new CancellationTokenSource();
        var callbackWait = flow.WaitForTerminalAsync(requestCancellation.Token);
        requestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await callbackWait);

        broker.BeginCommit(flow);
        broker.Complete(flow);
        Assert.Equal(McpOAuthFlowStatus.Completed, broker.GetStatusByState(flow.State).Status);
    }

    [Fact]
    public async Task DaemonCancellationCancelsOwnerWithoutReturningCodeToFollower()
    {
        using var daemonCancellation = new CancellationTokenSource();
        using var broker = new McpOAuthFlowBroker(TimeProvider.System, daemonCancellation.Token);
        var flow = broker.StartOrJoin(ServerName).Flow;
        var owner = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);
        var follower = flow.HandleAuthorizationRedirectAsync(AuthorizationUrl, RedirectUri, CancellationToken.None);

        daemonCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner);
        await Assert.ThrowsAsync<McpOAuthAuthorizationInProgressException>(async () => await follower);
    }

    [Fact]
    public void TerminalFlowAllowsNewAttemptWithNewStateWhileKeepingOldStatus()
    {
        using var broker = CreateBroker();
        var oldFlow = broker.StartOrJoin(ServerName).Flow;
        broker.Fail(oldFlow, new McpErrorResponse("DCR failed: HTTP 403 Forbidden.", "dynamic client registration", 403));

        var next = broker.StartOrJoin(ServerName);

        Assert.True(next.Created);
        Assert.NotEqual(oldFlow.State, next.Flow.State);
        Assert.Equal(403, broker.GetStatusByState(oldFlow.State).Error?.Status);
    }

    private static McpOAuthFlowBroker CreateBroker()
        => new(TimeProvider.System, CancellationToken.None);
}
