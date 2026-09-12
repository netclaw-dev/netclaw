// -----------------------------------------------------------------------
// <copyright file="McpClientManagerLifecycleTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpClientManagerLifecycleTests
{
    private static readonly McpServerName ServerName = new("test");
    private static readonly DateTimeOffset InitialTime = DateTimeOffset.Parse("2026-07-22T12:00:00Z");

    [Fact]
    public async Task ConcurrentReconnects_CreateOneCandidateAndPublishOneGeneration()
    {
        var runtime = new ControlledMcpClientRuntime();
        var initial = runtime.Enqueue(new ClientPlan("old_tool"));
        var initializeReplacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = runtime.Enqueue(new ClientPlan("new_tool")
        {
            Initialize = ct => initializeReplacement.Task.WaitAsync(ct),
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var reconnects = Enumerable.Range(0, 16)
            .Select(_ => harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken))
            .ToArray();
        await replacement.Created.Task;

        Assert.Equal(2, runtime.CreateCount);
        initializeReplacement.SetResult();
        Assert.All(await Task.WhenAll(reconnects), Assert.True);

        var snapshot = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(2, snapshot.Generation);
        Assert.Same(replacement.Client, snapshot.Client);
        AssertPublishedTools(harness, "new_tool");
        await initial.Disposed.Task;
        Assert.Equal(1, initial.DisposeCount);
        Assert.Equal(0, replacement.DisposeCount);
    }

    [Fact]
    public async Task FailedCandidate_DisposesOnlyCandidateAndKeepsPublishedSnapshot()
    {
        var runtime = new ControlledMcpClientRuntime();
        var initial = runtime.Enqueue(new ClientPlan("old_tool"));
        var failed = runtime.Enqueue(new ClientPlan("unused")
        {
            Initialize = _ => Task.FromException(new InvalidOperationException("list failed")),
        });
        var recovered = runtime.Enqueue(new ClientPlan("new_tool"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var original = harness.Manager.GetSnapshot(ServerName);
        time.Advance(TimeSpan.FromMinutes(3));

        Assert.False(await harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));

        await failed.Disposed.Task;
        var retained = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Same(original?.Client, retained.Client);
        Assert.Equal(1, retained.Generation);
        Assert.Equal(McpConnectionState.Connected, retained.Status.State);
        Assert.Equal(1, retained.Status.ToolCount);
        var failureAt = time.GetUtcNow();
        Assert.Equal(failureAt, retained.Status.LastErrorAt);
        AssertPublishedTools(harness, "old_tool");
        Assert.Equal(0, initial.DisposeCount);
        Assert.Equal(1, failed.DisposeCount);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Same(recovered.Client, recovery.Client);
        Assert.Equal(failureAt, recovery.Status.LastErrorAt);
        Assert.NotEqual(time.GetUtcNow(), recovery.Status.LastErrorAt);
        AssertPublishedTools(harness, "new_tool");
    }

    [Fact]
    public async Task ReplacementsAndShutdown_DisposeEveryClientExactlyOnce()
    {
        var runtime = new ControlledMcpClientRuntime();
        var first = runtime.Enqueue(new ClientPlan("one"));
        var second = runtime.Enqueue(new ClientPlan("two"));
        var third = runtime.Enqueue(new ClientPlan("three"));
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(await harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.True(await harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));
        await harness.Manager.StopAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(first.Disposed.Task, second.Disposed.Task, third.Disposed.Task);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, third.DisposeCount);
        Assert.Equal(3, runtime.CreateCount);
        Assert.Empty(harness.Registry.GetToolsForServer(ServerName, int.MaxValue));
    }

    [Fact]
    public async Task ToolLevelAuthFailure_MovesServerOutOfConnected()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            // An expired credential reaches the agent as an ordinary successful response
            // carrying isError, not as a transport 401. The transport stays healthy, so
            // without reclassification the server keeps reporting Connected while every
            // call fails, and the operator is never told to reauthorize.
            Invoke = (_, _) => Task.FromResult<object?>(JsonDocument.Parse(
                """{"content":[{"type":"text","text":"Unauthorized: token expired"}],"isError":true}""")
                .RootElement.Clone()),
        });
        // Netclaw holds OAuth tokens for this server, so the words in the result describe a
        // credential that `netclaw mcp auth` can renew.
        var entry = HttpEntry();
        await using var harness = CreateHarness(runtime, entry);
        await SeedOAuthTokensAsync(harness, entry);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            McpConnectionState.Connected,
            harness.Manager.GetServerStatuses()[ServerName].State);

        await InvokeAsync(harness.Manager, TestContext.Current.CancellationToken);

        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains($"netclaw mcp auth {ServerName.Value}", status.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolLevelAuthWordsWithoutStoredTokens_KeepTheServerConnected()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromResult<object?>(JsonDocument.Parse(
                """{"content":[{"type":"text","text":"Unauthorized: token expired"}],"isError":true}""")
                .RootElement.Clone()),
        });
        // An HTTP server with no headers and no stored tokens. Netclaw runs no OAuth flow
        // for it, so free result text is not evidence that its credential died.
        await using var harness = CreateHarness(runtime, HttpEntry());
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await InvokeAsync(harness.Manager, TestContext.Current.CancellationToken);

        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.Connected, status.State);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Contains("reported a failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedUnauthorizedWithoutStoredTokens_NamesTheConfiguredCredential()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            // A static key that expires mid-session never reaches the tool. Every HTTP
            // MCP server rejects it at the transport, so the status is a typed 401.
            Invoke = (_, _) => Task.FromException<object?>(
                new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized)),
        });
        var alerts = new RecordingNotificationSink();
        // The key rides in X-Api-Key, not in Authorization. The remedy must still name the
        // operator's own credential, so no decision here may read a header name.
        await using var harness = new ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime),
            alerts,
            ApiKeyHeaderEntry());
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        // Netclaw holds no tokens for this server, so `netclaw mcp auth` cannot repair it.
        Assert.Contains(
            "Check configured credentials or headers",
            status.ErrorMessage ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "netclaw mcp auth",
            status.ErrorMessage ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Contains("invocation failed (HTTP 401)", StringComparison.Ordinal));
        var toolCallAlert = Assert.Single(alerts.Alerts);
        Assert.Equal(AlertType.McpServerDisconnected, toolCallAlert.Category);

        // The next call reconnects. The same credential fails initialize the same way, so
        // the agent gets the same remedy rather than a fixed OAuth one.
        runtime.Enqueue(new ClientPlan("run")
        {
            Initialize = _ => Task.FromException(
                new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized)),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        Assert.Contains("credentials or headers", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("netclaw mcp auth", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, runtime.CreateCount);
    }

    [Fact]
    public async Task TypedUnauthorizedWithStoredTokens_NamesTheAuthCommand()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromException<object?>(
                new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized)),
        });
        var alerts = new RecordingNotificationSink();
        var entry = HttpEntry();
        await using var harness = new ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime),
            alerts,
            entry);
        // The daemon holds tokens for this server. A 401 then means the SDK could not
        // refresh them, so the operator must authorize again.
        await SeedOAuthTokensAsync(harness, entry);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains($"netclaw mcp auth {ServerName.Value}", status.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        var alert = Assert.Single(alerts.Alerts);
        Assert.Equal(AlertType.McpAuthExpired, alert.Category);
        Assert.Equal("credentials_rejected", alert.Context?["reason"]);
    }

    [Fact]
    public async Task OAuthChallengeOnAToolCall_NamesTheAuthCommand()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            // The SDK engages its OAuth handler on a Bearer challenge and throws this when
            // it cannot finish. The exception itself proves the server runs OAuth, so the
            // remedy is `netclaw mcp auth` even before any token is stored.
            Invoke = (_, _) => Task.FromException<object?>(new McpException(
                "Failed to handle unauthorized response with 'Bearer' scheme. " +
                "The AuthorizationCallbackHandler returned a null authorization result.")),
        });
        var alerts = new RecordingNotificationSink();
        await using var harness = new ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime),
            alerts,
            HttpEntry());
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<McpException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains($"netclaw mcp auth {ServerName.Value}", status.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(AlertType.McpAuthExpired, Assert.Single(alerts.Alerts).Category);
    }

    [Fact]
    public async Task TypedForbiddenOnAToolCall_KeepsTheServerConnected()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromException<object?>(
                new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden)),
        });
        var alerts = new RecordingNotificationSink();
        await using var harness = new ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime),
            alerts,
            StaticHeaderEntry());
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        // A 403 denies one action. The credential still works, so a demotion would take a
        // healthy server down and reconnect it for nothing.
        Assert.Equal(
            McpConnectionState.Connected,
            harness.Manager.GetServerStatuses()[ServerName].State);
        Assert.Equal(1, runtime.CreateCount);
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task ToolLevelAuthWordsFromAStaticHeaderServer_KeepTheServerConnected()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromResult<object?>(JsonDocument.Parse(
                """{"content":[{"type":"text","text":"Forbidden: you do not own this domain"}],"isError":true}""")
                .RootElement.Clone()),
        });
        await using var harness = CreateHarness(runtime, StaticHeaderEntry());
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await InvokeAsync(harness.Manager, TestContext.Current.CancellationToken);

        // A tool that proxies a REST API answers "Forbidden" for an ordinary business
        // error. The operator owns this header, so AuthFailed would name a remedy that
        // cannot work and would keep the server down until the daemon restarts.
        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.Connected, status.State);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Contains("reported a failure", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "netclaw mcp auth",
            status.ErrorMessage ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolLevelAuthWordsFromAStdioServer_KeepTheServerConnected()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromResult<object?>(JsonDocument.Parse(
                """{"content":[{"type":"text","text":"Unauthorized: token expired"}],"isError":true}""")
                .RootElement.Clone()),
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await InvokeAsync(harness.Manager, TestContext.Current.CancellationToken);

        // A stdio server has no OAuth flow, so `netclaw mcp auth` cannot repair it.
        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.Connected, status.State);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Contains("reported a failure", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "netclaw mcp auth",
            status.ErrorMessage ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvocationAgainstAnAuthFailedServer_NamesTheRemedy()
    {
        // The remedy only exists while Netclaw holds tokens, so this case seeds them.
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromResult<object?>(JsonDocument.Parse(
                """{"content":[{"type":"text","text":"Unauthorized: token expired"}],"isError":true}""")
                .RootElement.Clone()),
        });
        // The reconnect that follows must fail the way a dead credential does. The SDK
        // engages its OAuth handler on a Bearer challenge and throws this McpException when
        // it cannot complete the flow.
        runtime.Enqueue(new ClientPlan("run")
        {
            Initialize = _ => Task.FromException(new McpException(
                "Failed to handle unauthorized response with 'Bearer' scheme. " +
                "The AuthorizationCallbackHandler returned a null authorization result.")),
        });
        var entry = HttpEntry();
        await using var harness = CreateHarness(runtime, entry);
        await SeedOAuthTokensAsync(harness, entry);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        await InvokeAsync(harness.Manager, TestContext.Current.CancellationToken);
        Assert.Equal(
            McpConnectionState.AuthFailed,
            harness.Manager.GetServerStatuses()[ServerName].State);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        // The tool-level failure sets AuthFailed. The reconnect fails the same way, and a
        // stored token that the server rejects keeps the state at AuthFailed.
        Assert.Equal(
            McpConnectionState.AuthFailed,
            harness.Manager.GetServerStatuses()[ServerName].State);
        // This text is what the agent repeats to the operator. "unavailable" would send
        // them looking for a broken server rather than a credential to renew.
        Assert.Contains($"netclaw mcp auth {ServerName.Value}", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolLevelFailureDetail_ReachesTheDaemonLog()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromResult<object?>(JsonDocument.Parse(
                """{"content":[{"type":"text","text":"database_not_found: no such data source"}],"isError":true}""")
                .RootElement.Clone()),
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await InvokeAsync(harness.Manager, TestContext.Current.CancellationToken);

        // The detail reaches the model either way. Logging it is what gives an operator
        // something to debug from, instead of only the result length.
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Contains("database_not_found: no such data source", StringComparison.Ordinal));
        // A non-auth failure must not change connection state.
        Assert.Equal(
            McpConnectionState.Connected,
            harness.Manager.GetServerStatuses()[ServerName].State);
    }

    [Fact]
    public async Task CancellationAndApplicationErrors_DoNotReconnectOrDisposeHealthyClient()
    {
        var invocationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = async (call, ct) =>
            {
                if (call == 1)
                {
                    invocationEntered.TrySetResult();
                    await neverCompletes.Task.WaitAsync(ct);
                    return "unreachable";
                }

                if (call == 2)
                {
                    return JsonDocument.Parse(
                        """{"content":[{"type":"text","text":"declared failure"}],"isError":true}""")
                        .RootElement.Clone();
                }

                if (call == 3)
                    throw new McpException("application MCP failure");

                throw new InvalidOperationException("application failure");
            },
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        using var callerCancellation = new CancellationTokenSource();
        var cancelledCall = InvokeAsync(harness.Manager, callerCancellation.Token);
        await invocationEntered.Task;
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCall);

        var toolError = await InvokeAsync(harness.Manager, TestContext.Current.CancellationToken);
        Assert.Equal("Error: MCP tool 'test/run' reported a failure: declared failure", toolError);
        // A JSON-RPC error reaches the caller. The adapter, not the manager, turns it into
        // the tool result, so the tool call also gets a failure receipt.
        var applicationMcpError = await Assert.ThrowsAsync<McpException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));
        Assert.Equal("application MCP failure", applicationMcpError.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        Assert.Equal(1, runtime.CreateCount);
        Assert.Equal(4, plan.InvocationCount);
        Assert.Equal(0, plan.DisposeCount);
        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task ThrownToolCall_LogsOneWarningThatNamesTheServerAndTheTool()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromException<object?>(new McpException("application MCP failure")),
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<McpException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        // The adapter turns the exception into a tool result, so this line is the only
        // record of the failed call an operator can read in the daemon log.
        var warning = Assert.Single(
            harness.Logger.Entries,
            entry => entry.Contains("invocation failed", StringComparison.Ordinal));
        Assert.Contains($"'{ServerName.Value}/run'", warning, StringComparison.Ordinal);
        Assert.Equal(
            "application MCP failure",
            Assert.Single(harness.Logger.Exceptions).Message);
    }

    [Fact]
    public async Task CallerCancelledToolCall_LogsNoInvocationWarning()
    {
        var invocationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = async (_, ct) =>
            {
                invocationEntered.TrySetResult();
                await neverCompletes.Task.WaitAsync(ct);
                return "unreachable";
            },
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        using var callerCancellation = new CancellationTokenSource();
        var cancelledCall = InvokeAsync(harness.Manager, callerCancellation.Token);
        await invocationEntered.Task;
        await callerCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCall);

        // A caller that aborts a call did not see a server failure. A Warning here sends
        // the operator after a healthy server.
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Contains("invocation failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShutdownRacingReconnect_PublishesNothingAndDisposesEveryClient()
    {
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ControlledMcpClientRuntime();
        var initial = runtime.Enqueue(new ClientPlan("old"));
        var candidate = runtime.Enqueue(new ClientPlan("new")
        {
            Initialize = ct => initialization.Task.WaitAsync(ct),
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var reconnect = harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken);
        await candidate.Created.Task;

        var stop = harness.Manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(harness.Manager.IsStopping);
        Assert.False(await reconnect);
        await stop;

        await Task.WhenAll(initial.Disposed.Task, candidate.Disposed.Task);
        Assert.Null(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(1, initial.DisposeCount);
        Assert.Equal(1, candidate.DisposeCount);
        Assert.Equal(2, runtime.CreateCount);
        Assert.Empty(harness.Registry.GetToolsForServer(ServerName, int.MaxValue));
    }

    [Fact]
    public async Task TransportFailure_ReconnectsForLaterCallsAndDoesNotReplay()
    {
        var runtime = new ControlledMcpClientRuntime();
        var initial = runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromException<object?>(new HttpRequestException("session lost")),
        });
        var replacement = runtime.Enqueue(new ClientPlan("run"));
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        Assert.Equal("session lost", error.Message);
        await initial.Disposed.Task;
        Assert.Equal(1, initial.InvocationCount);
        Assert.Equal(1, initial.DisposeCount);
        Assert.Equal(0, replacement.InvocationCount);
        Assert.Equal(2, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ApplicationHttpStatus_ReachesTheCallerWithoutAReconnect(HttpStatusCode status)
    {
        var runtime = new ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromException<object?>(
                new HttpRequestException("boom", null, status)),
        });
        // A replacement is queued but must stay unused. Without it a reconnect would fail
        // inside the runtime before it counts the client, and CreateCount would still
        // read 1. With it, one reconnect drives CreateCount to 2 and this test fails.
        var replacement = runtime.Enqueue(new ClientPlan("run"));
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        // A server that answers with a status is reachable. A new session cannot change the
        // answer, and each reconnect costs about five more requests against a server that
        // may already enforce a request budget.
        Assert.Equal(status, error.StatusCode);
        // The Warning line is the only operator-visible record of a thrown tool call, so it
        // must name the tool and the status the server sent.
        var warning = Assert.Single(
            harness.Logger.Entries,
            entry => entry.Contains($"{ServerName.Value}/run", StringComparison.Ordinal));
        Assert.Contains($"(HTTP {(int)status})", warning, StringComparison.Ordinal);
        Assert.Equal(1, runtime.CreateCount);
        Assert.Equal(0, plan.DisposeCount);
        Assert.Null(replacement.Client);
        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task SessionExpiryStatus_ReconnectsForLaterCalls()
    {
        var runtime = new ControlledMcpClientRuntime();
        var initial = runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (_, _) => Task.FromException<object?>(
                new HttpRequestException("session expired", null, HttpStatusCode.NotFound)),
        });
        var replacement = runtime.Enqueue(new ClientPlan("run"));
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));

        // Streamable HTTP reports an expired session as 404, so a new session repairs it.
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        // The token makes a missed reconnect fail the run instead of hanging it.
        await initial.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, runtime.CreateCount);
        Assert.Equal(0, replacement.InvocationCount);
        Assert.Equal(2, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task CandidateInitializationAndDisposalFailures_AreLoudAndPriorToolsRemainPublished()
    {
        var runtime = new ControlledMcpClientRuntime();
        var initial = runtime.Enqueue(new ClientPlan("old_tool"));
        var candidate = runtime.Enqueue(new ClientPlan("new_tool")
        {
            Initialize = _ => Task.FromException(new InvalidOperationException("candidate init failed")),
            DisposeFailure = new IOException("candidate dispose failed"),
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));

        Assert.Contains(failure.InnerExceptions, ex => ex.Message == "candidate init failed");
        Assert.Contains(failure.InnerExceptions, ex => ex.Message == "candidate dispose failed");
        AssertPublishedTools(harness, "old_tool");
        Assert.Equal(0, initial.DisposeCount);
        Assert.Equal(1, candidate.DisposeCount);
    }

    [Fact]
    public async Task ApplicationExceptionsWrappingTransportLikeErrors_DoNotReconnect()
    {
        var runtime = new ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new ClientPlan("run")
        {
            Invoke = (call, _) => Task.FromException<object?>(call switch
            {
                1 => new InvalidOperationException("wrapped IO", new IOException("io")),
                2 => new InvalidOperationException("wrapped timeout", new TimeoutException("timeout")),
                _ => new InvalidOperationException("wrapped disposed", new ObjectDisposedException("application")),
            }),
        });
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        for (var call = 0; call < 3; call++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InvokeAsync(harness.Manager, TestContext.Current.CancellationToken));
        }

        Assert.Equal(3, plan.InvocationCount);
        Assert.Equal(1, runtime.CreateCount);
        Assert.Equal(0, plan.DisposeCount);
        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task ReplacementDisposesTheRetiredClientAndDisposeWithoutStopClearsPublishedState()
    {
        var runtime = new ControlledMcpClientRuntime();
        var retired = runtime.Enqueue(new ClientPlan("old_tool"));
        var current = runtime.Enqueue(new ClientPlan("new_tool"));
        var harness = CreateHarness(runtime);
        try
        {
            await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(await harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));

            // The replaced client is disposed as soon as its replacement is published.
            // Waiting for in-flight calls to drain first belongs to the separate
            // client-lifecycle work, not to this one.
            await retired.Disposed.Task;
            Assert.Equal(1, retired.DisposeCount);
            AssertPublishedTools(harness, "new_tool");

            harness.DisposeManagerWithoutStop();

            Assert.Null(harness.Manager.GetSnapshot(ServerName));
            Assert.Empty(harness.Registry.GetToolsForServer(ServerName, int.MaxValue));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailedReplacement_KeepsConnectedStatusWithoutUnavailableAlert()
    {
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("old_tool"));
        runtime.Enqueue(new ClientPlan("new_tool")
        {
            Initialize = _ => Task.FromException(new InvalidOperationException("replacement list failed")),
        });
        var alerts = new RecordingNotificationSink();
        await using var harness = CreateHarness(runtime, new FakeTimeProvider(InitialTime), alerts);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(await harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));

        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.Connected, status.State);
        Assert.Equal(1, status.ToolCount);
        Assert.Equal("Failed to reach MCP server. Check daemon logs for details.", status.ErrorMessage);
        AssertPublishedTools(harness, "old_tool");
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task ProviderErrorBodyNeverLeaksThroughStatusOrNotification()
    {
        const string providerBody = "code=auth-code access_token=access-value client_secret=secret-value";
        var runtime = new ControlledMcpClientRuntime();
        runtime.Enqueue(new ClientPlan("unused")
        {
            Initialize = _ => Task.FromException(new InvalidOperationException(providerBody)),
        });
        var alerts = new RecordingNotificationSink();
        await using var harness = CreateHarness(runtime, new FakeTimeProvider(InitialTime), alerts);

        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var status = harness.Manager.GetServerStatuses()[ServerName];
        var alert = Assert.Single(alerts.Alerts);
        Assert.DoesNotContain("auth-code", status.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("access-value", status.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", status.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-code", alert.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("access-value", alert.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", alert.Summary, StringComparison.Ordinal);
    }

    private static Task<string> InvokeAsync(McpClientManager manager, CancellationToken cancellationToken)
        => InvokeAsync(manager, "run", cancellationToken);

    private static Task<string> InvokeAsync(
        McpClientManager manager,
        string toolName,
        CancellationToken cancellationToken)
        => manager.InvokeAsync(
            ServerName.Value,
            toolName,
            null,
            TestToolExecutionContext.CreateBoundWithoutApproval(
                "slack/thread-1",
                null,
                TrustAudience.Team,
                "slack").Invocation,
            cancellationToken);

    private static void AssertPublishedTools(ManagerHarness harness, params string[] expected)
    {
        Assert.Equal(expected, harness.Manager.GetToolNames(ServerName));
        Assert.Equal(
            expected,
            harness.Registry
                .GetToolsForServer(ServerName, int.MaxValue)
                .OfType<McpToolAdapter>()
                .Select(tool => tool.BareToolName)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(expected.Length, harness.Manager.GetServerStatuses()[ServerName].ToolCount);
    }

    private static ManagerHarness CreateHarness(ControlledMcpClientRuntime runtime)
        => new(
            runtime,
            new FakeTimeProvider(InitialTime),
            NullNotificationSink.Instance);

    private static ManagerHarness CreateHarness(
        ControlledMcpClientRuntime runtime,
        FakeTimeProvider timeProvider)
        => new(runtime, timeProvider, NullNotificationSink.Instance);

    private static ManagerHarness CreateHarness(
        ControlledMcpClientRuntime runtime,
        FakeTimeProvider timeProvider,
        IOperationalNotificationSink notificationSink)
        => new(runtime, timeProvider, notificationSink);

    private static ManagerHarness CreateHarness(
        ControlledMcpClientRuntime runtime,
        McpServerEntry entry)
        => new(
            runtime,
            new FakeTimeProvider(InitialTime),
            NullNotificationSink.Instance,
            entry);

    // A plain HTTP server. Whether `netclaw mcp auth` can repair it depends on the stored
    // token state, not on this entry. The controlled runtime opens no connection, so the
    // URL is never dialled.
    internal static McpServerEntry HttpEntry()
        => new()
        {
            Enabled = true,
            Transport = "http",
            Url = "http://127.0.0.1:1/mcp",
        };

    // An HTTP server the operator authenticates with a key in a header that is not named
    // Authorization. No auth decision may treat it differently from a static bearer.
    private static McpServerEntry ApiKeyHeaderEntry()
        => new()
        {
            Enabled = true,
            Transport = "http",
            Url = "http://127.0.0.1:1/mcp",
            Headers = new Dictionary<string, SensitiveString>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Api-Key"] = new("operator-key"),
            },
        };

    // Publishes usable OAuth tokens through the real store, which is what "the daemon uses
    // OAuth for this server" means at runtime.
    private static async Task SeedOAuthTokensAsync(ManagerHarness harness, McpServerEntry entry)
    {
        var cache = harness.Credentials.CreateTokenCache(
            ServerName,
            entry.Url!,
            "static-client",
            explicitAuthorization: true);
        await cache.StoreTokensAsync(
            new TokenContainer
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                TokenType = "Bearer",
                Scope = "read",
                ExpiresIn = 3600,
                ObtainedAt = InitialTime,
            },
            TestContext.Current.CancellationToken);
        harness.Credentials.Publish(cache, TestContext.Current.CancellationToken);
    }

    // An HTTP server the operator authenticates with a static header. It never uses OAuth.
    private static McpServerEntry StaticHeaderEntry()
        => new()
        {
            Enabled = true,
            Transport = "http",
            Url = "http://127.0.0.1:1/mcp",
            Headers = new Dictionary<string, SensitiveString>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = new("Bearer operator-key"),
            },
        };

    internal sealed class ManagerHarness : IAsyncDisposable
    {
        private readonly McpOAuthFlowBroker _flowBroker;
        private bool _stopFailureObserved;
        private bool _managerDisposed;

        public ManagerHarness(
            ControlledMcpClientRuntime runtime,
            FakeTimeProvider timeProvider)
            : this(runtime, timeProvider, NullNotificationSink.Instance)
        {
        }

        public ManagerHarness(
            ControlledMcpClientRuntime runtime,
            FakeTimeProvider timeProvider,
            IOperationalNotificationSink notificationSink)
            : this(
                runtime,
                timeProvider,
                notificationSink,
                new McpServerEntry
                {
                    Enabled = true,
                    Transport = "stdio",
                    Command = "not-launched-by-controlled-runtime",
                })
        {
        }

        public ManagerHarness(
            ControlledMcpClientRuntime runtime,
            FakeTimeProvider timeProvider,
            IOperationalNotificationSink notificationSink,
            McpServerEntry entry)
        {
            var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            paths.EnsureDirectoriesExist();
            Credentials = new McpOAuthCredentialStore(
                paths,
                timeProvider,
                new NullSecretsProtector(),
                NullLogger<McpOAuthCredentialStore>.Instance);
            _flowBroker = new McpOAuthFlowBroker(timeProvider, CancellationToken.None);
            Registry = new ToolRegistry();
            var dependencies = McpManagerTestDependencies.Create();
            SkillRegistry = dependencies.SkillRegistry;
            SkillIndex = dependencies.SkillIndex;
            Logger = new RecordingLogger<McpClientManager>();
            Manager = new McpClientManager(
                new Dictionary<string, McpServerEntry> { [ServerName.Value] = entry },
                Registry,
                dependencies.SkillRegistry,
                dependencies.SkillIndexPublisher,
                dependencies.ToolAccessPolicy,
                dependencies.ToolConfig,
                Credentials,
                McpOAuthTestDoubles.UnusedRegistrar(),
                _flowBroker,
                new DaemonConfig(),
                notificationSink,
                timeProvider,
                runtime,
                Logger,
                new SessionConfig());
        }

        public McpClientManager Manager { get; }

        // The manager reads this store to decide whether the daemon uses OAuth for a
        // server, so a test seeds tokens through it rather than through a stub.
        public McpOAuthCredentialStore Credentials { get; }

        public ToolRegistry Registry { get; }

        public SkillRegistry SkillRegistry { get; }

        public SkillIndexContextLayer SkillIndex { get; }

        public RecordingLogger<McpClientManager> Logger { get; }

        public void MarkStopFailureObserved() => _stopFailureObserved = true;

        public void DisposeManagerWithoutStop()
        {
            Manager.Dispose();
            _managerDisposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopFailureObserved && !_managerDisposed)
                await Manager.StopAsync(TestContext.Current.CancellationToken);
            if (!_managerDisposed)
                Manager.Dispose();
            _flowBroker.Dispose();
        }
    }

    internal sealed class ControlledMcpClientRuntime : IMcpClientRuntime
    {
        private readonly ConcurrentQueue<ClientPlan> _plans = new();
        private readonly ConcurrentDictionary<McpClient, ClientPlan> _clients = new();
        private readonly ConcurrentDictionary<AIFunction, ClientPlan> _functions = new();
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public ClientPlan Enqueue(ClientPlan plan)
        {
            _plans.Enqueue(plan);
            return plan;
        }

        public Task<McpClient> CreateAsync(
            IClientTransport transport,
            McpClientOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_plans.TryDequeue(out var plan))
                throw new InvalidOperationException("No controlled MCP client plan was queued.");

            var implementationType = typeof(McpClient).Assembly.GetType(
                "ModelContextProtocol.Client.McpClientImpl",
                throwOnError: true)!;
            var client = (McpClient)RuntimeHelpers.GetUninitializedObject(implementationType);
            plan.Client = client;
            plan.NotificationHandlers = options.Handlers.NotificationHandlers?.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal) ?? new Dictionary<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>();
            _clients[client] = plan;
            Interlocked.Increment(ref _createCount);
            plan.Created.TrySetResult();
            return Task.FromResult<McpClient>(client);
        }

        public async ValueTask<McpClientInitialization> InitializeAsync(
            McpClient client,
            CancellationToken cancellationToken)
        {
            var plan = _clients[client];
            if (plan.Initialize is not null)
                await plan.Initialize(cancellationToken);

            var functions = BuildFunctions(plan);
            return new McpClientInitialization(functions.Values.ToList(), plan.Prompts);
        }

        public McpCatalogNotificationProfile GetCatalogNotificationProfile(McpClient client)
            => _clients[client].NotificationProfile;

        public Task ListenForCatalogChangesAsync(
            McpClient client,
            RequestId requestId,
            SubscriptionsListenNotifications notifications,
            CancellationToken cancellationToken)
        {
            var plan = _clients[client];
            Interlocked.Increment(ref plan.ListenCountStorage);
            plan.ListenRequestId = requestId;
            plan.ListenNotifications = notifications;
            plan.ListenStarted.TrySetResult();
            return plan.Listen is null
                ? Task.CompletedTask
                : plan.Listen(cancellationToken);
        }

        public async ValueTask<IReadOnlyList<AIFunction>> ListToolsAsync(
            McpClient client,
            CancellationToken cancellationToken)
        {
            var plan = _clients[client];
            var refreshCount = Interlocked.Increment(ref plan.RefreshCountStorage);
            if (plan.BeforeListTools is not null)
                await plan.BeforeListTools(refreshCount, cancellationToken);
            if (plan.ListFailure is not null)
                throw plan.ListFailure;
            return BuildFunctions(plan).Values.ToList();
        }

        public ValueTask<IReadOnlyList<Prompt>> ListPromptsAsync(
            McpClient client,
            CancellationToken cancellationToken)
        {
            var plan = _clients[client];
            Interlocked.Increment(ref plan.PromptRefreshCountStorage);
            if (plan.PromptListFailure is not null)
                return ValueTask.FromException<IReadOnlyList<Prompt>>(plan.PromptListFailure);
            return ValueTask.FromResult<IReadOnlyList<Prompt>>(plan.Prompts);
        }

        public ValueTask<GetPromptResult> GetPromptAsync(
            McpClient client,
            string promptName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            var plan = _clients[client];
            Interlocked.Increment(ref plan.PromptInvocationCountStorage);
            plan.LastPromptName = promptName;
            plan.LastPromptArguments = new Dictionary<string, string>(arguments, StringComparer.Ordinal);
            if (plan.GetPromptFailure is not null)
                return ValueTask.FromException<GetPromptResult>(plan.GetPromptFailure);
            return plan.GetPromptResult is null
                ? ValueTask.FromException<GetPromptResult>(
                    new InvalidOperationException("The controlled prompt result is not configured."))
                : ValueTask.FromResult(plan.GetPromptResult);
        }

        private IReadOnlyDictionary<string, AIFunction> BuildFunctions(ClientPlan plan)
        {
            var functions = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in plan.ToolNames)
            {
                var function = AIFunctionFactory.Create(() => "unused", name, name);
                functions[name] = function;
                _functions[function] = plan;
            }

            return functions;
        }

        public async ValueTask<object?> InvokeAsync(
            AIFunction function,
            AIFunctionArguments? arguments,
            CancellationToken cancellationToken)
        {
            var plan = _functions[function];
            var call = Interlocked.Increment(ref plan.InvocationCountStorage);
            return plan.Invoke is null
                ? "ok"
                : await plan.Invoke(call, cancellationToken);
        }

        public ValueTask DisposeAsync(McpClient client)
        {
            var plan = _clients[client];
            Interlocked.Increment(ref plan.DisposeCountStorage);
            plan.Disposed.TrySetResult();
            return plan.DisposeFailure is null
                ? ValueTask.CompletedTask
                : new ValueTask(Task.FromException(plan.DisposeFailure));
        }
    }

    internal sealed class ClientPlan(params string[] toolNames)
    {
        public string[] ToolNames { get; set; } = toolNames;

        public IReadOnlyList<Prompt> Prompts { get; set; } = [];

        public GetPromptResult? GetPromptResult { get; set; }

        public Func<CancellationToken, Task>? Initialize { get; init; }

        public Func<int, CancellationToken, Task<object?>>? Invoke { get; init; }

        public Func<CancellationToken, Task>? Listen { get; init; }

        public Func<int, CancellationToken, Task>? BeforeListTools { get; init; }

        public McpCatalogNotificationProfile NotificationProfile { get; init; } =
            new("2025-11-25", false, false);

        public Exception? DisposeFailure { get; init; }

        public Exception? ListFailure { get; set; }

        public Exception? PromptListFailure { get; set; }

        public Exception? GetPromptFailure { get; set; }

        public string? LastPromptName { get; set; }

        public IReadOnlyDictionary<string, string>? LastPromptArguments { get; set; }

        public IReadOnlyDictionary<string, Func<JsonRpcNotification, CancellationToken, ValueTask>> NotificationHandlers { get; set; } =
            new Dictionary<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>();

        public RequestId ListenRequestId { get; set; }

        public SubscriptionsListenNotifications? ListenNotifications { get; set; }

        public TaskCompletionSource Created { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ListenStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public McpClient? Client { get; set; }

        public int InvocationCountStorage;

        public int DisposeCountStorage;

        public int RefreshCountStorage;

        public int PromptRefreshCountStorage;

        public int PromptInvocationCountStorage;

        public int ListenCountStorage;

        public int InvocationCount => Volatile.Read(ref InvocationCountStorage);

        public int DisposeCount => Volatile.Read(ref DisposeCountStorage);

        public int RefreshCount => Volatile.Read(ref RefreshCountStorage);

        public int PromptRefreshCount => Volatile.Read(ref PromptRefreshCountStorage);

        public int PromptInvocationCount => Volatile.Read(ref PromptInvocationCountStorage);

        public int ListenCount => Volatile.Read(ref ListenCountStorage);

        public ValueTask NotifyAsync(
            string method,
            JsonNode? parameters,
            CancellationToken cancellationToken = default)
        {
            if (!NotificationHandlers.TryGetValue(method, out var handler))
                throw new InvalidOperationException($"No notification handler is registered for '{method}'.");

            return handler(new JsonRpcNotification { Method = method, Params = parameters }, cancellationToken);
        }
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }

}
