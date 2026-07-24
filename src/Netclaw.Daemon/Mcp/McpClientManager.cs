// -----------------------------------------------------------------------
// <copyright file="McpClientManager.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed class McpClientManager : IHostedService, IDisposable, IMcpToolInvoker, IMcpReconnectable
{
    internal static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, McpServerEntry> _serverEntries;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolConfig _toolConfig;
    private readonly McpOAuthCredentialStore _credentialStore;
    private readonly McpOAuthFlowBroker _flowBroker;
    private readonly DaemonConfig _daemonConfig;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly IMcpClientRuntime _clientRuntime;
    private readonly ILogger<McpClientManager> _logger;
    private readonly int _maxToolDescriptionChars;
    private readonly int _maxToolSchemaWarnChars;
    private readonly ConcurrentDictionary<McpServerName, McpServerLifecycle> _servers = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _shutdownSync = new();
    private bool _stopping;
    private bool _disposed;
    private Task? _stopTask;

    public McpClientManager(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry toolRegistry,
        ToolConfig toolConfig,
        McpOAuthCredentialStore credentialStore,
        McpOAuthFlowBroker flowBroker,
        DaemonConfig daemonConfig,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        IMcpClientRuntime clientRuntime,
        ILogger<McpClientManager> logger,
        SessionConfig sessionConfig)
    {
        _serverEntries = serverEntries;
        _toolRegistry = toolRegistry;
        _toolConfig = toolConfig;
        _credentialStore = credentialStore;
        _flowBroker = flowBroker;
        _daemonConfig = daemonConfig;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _clientRuntime = clientRuntime;
        _logger = logger;
        _maxToolDescriptionChars = sessionConfig.Tuning.MaxToolDescriptionChars;
        _maxToolSchemaWarnChars = sessionConfig.Tuning.MaxToolSchemaWarnChars;

        foreach (var (name, entry) in serverEntries)
        {
            var serverName = new McpServerName(name);
            var status = entry.Enabled
                ? new McpServerStatus(serverName, McpConnectionState.Unreachable, 0, "Not connected.", null)
                : new McpServerStatus(serverName, McpConnectionState.Disabled, 0, null, null);
            _servers[serverName] = new McpServerLifecycle(McpServerSnapshot.WithoutConnection(status));
        }
    }

    internal bool IsStopping
    {
        get
        {
            lock (_shutdownSync)
                return _stopping;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in _serverEntries)
        {
            var serverName = new McpServerName(name);
            var lifecycle = _servers[serverName];
            if (!entry.Enabled)
            {
                _logger.LogInformation("MCP server '{Name}' is disabled, skipping", name);
                continue;
            }

            var observed = lifecycle.Snapshot;
            if (observed is not null)
                await ReconnectAsync(lifecycle, entry, observed, cancellationToken, null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_shutdownSync)
        {
            if (_stopTask is not null)
                return _stopTask;

            _stopping = true;
            _lifetimeCancellation.Cancel();
            _stopTask = StopCoreAsync(cancellationToken);
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (var (serverName, lifecycle) in _servers)
        {
            try
            {
                await StopServerAsync(serverName, lifecycle, cancellationToken);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("One or more MCP clients failed during shutdown.", failures);
    }

    public McpClient? GetClient(McpServerName serverName)
        => _servers.GetValueOrDefault(serverName)?.Snapshot?.Client;

    public IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses()
    {
        var statuses = _servers
            .Select(pair => (pair.Key, Snapshot: pair.Value.Snapshot))
            .Where(pair => pair.Snapshot is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Snapshot!.Status);
        return new ReadOnlyDictionary<McpServerName, McpServerStatus>(statuses);
    }

    public IReadOnlyList<string> GetToolNames(McpServerName serverName)
    {
        var snapshot = _servers.GetValueOrDefault(serverName)?.Snapshot;
        return snapshot is null
            ? []
            : snapshot.ToolFunctions.Keys.Order(StringComparer.Ordinal).ToList();
    }

    internal McpServerSnapshot? GetSnapshot(McpServerName serverName)
        => _servers.GetValueOrDefault(serverName)?.Snapshot;

    internal int GetTrackedOwnerCount(McpServerName serverName)
        => _servers.GetValueOrDefault(serverName)?.TrackedOwnerCount ?? 0;

    internal Task? GetInteractiveAuthorizationTask(McpServerName serverName)
        => _servers.GetValueOrDefault(serverName)?.InteractiveAuthorization;

    public async Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default)
    {
        if (!_serverEntries.TryGetValue(serverName.Value, out var entry) || !entry.Enabled)
            return false;

        if (!_servers.TryGetValue(serverName, out var lifecycle) || lifecycle.Snapshot is not { } observed)
            return false;

        return await ReconnectAsync(lifecycle, entry, observed, ct, null);
    }

    internal async Task<McpOAuthStartResponse> StartAuthorizationAsync(
        McpServerName serverName,
        CancellationToken requestCancellation)
    {
        if (!_serverEntries.TryGetValue(serverName.Value, out var entry))
            throw new KeyNotFoundException($"MCP server '{serverName.Value}' not found.");
        if (!entry.Enabled)
        {
            throw new McpOAuthOperationException(new McpErrorResponse(
                $"MCP server '{serverName.Value}' is disabled. Enable it before starting OAuth.",
                "authorization start"));
        }
        if (entry.Transport is "stdio" || string.IsNullOrWhiteSpace(entry.Url))
            throw new InvalidOperationException(
                $"MCP server '{serverName.Value}' has no URL (OAuth requires HTTP transport).");
        if (HasConfiguredAuthorizationHeader(entry))
        {
            throw new McpOAuthOperationException(new McpErrorResponse(
                $"MCP server '{serverName.Value}' uses an operator-configured Authorization header. Remove it before starting OAuth.",
                "authorization start"));
        }
        if (!_servers.TryGetValue(serverName, out var lifecycle))
            throw new InvalidOperationException($"MCP server '{serverName.Value}' is not tracked by the daemon.");

        if (lifecycle.InteractiveAuthorization is { IsCompleted: false }
            && !_flowBroker.TryGetActive(serverName, out _))
        {
            throw new McpOAuthOperationException(new McpErrorResponse(
                "The previous authorization attempt is finishing. Retry after its terminal status is available.",
                "authorization start"));
        }

        var started = _flowBroker.StartOrJoin(serverName);
        if (started.Created)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lifecycle.SetInteractiveAuthorization(completion.Task);
            _ = RunExplicitAuthorizationAsync(lifecycle, entry, started.Flow, completion);
        }

        var authorizationUrl = await started.Flow.WaitForAuthorizationUrlAsync(requestCancellation);
        return new McpOAuthStartResponse(authorizationUrl.ToString(), started.Flow.State);
    }

    public async Task<string> InvokeAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        CancellationToken ct = default)
    {
        var server = new McpServerName(serverName);
        var tool = new ToolName(toolName);
        return await InvokeSharedAsync(server, tool, arguments, ct);
    }

    private async Task<string> InvokeSharedAsync(
        McpServerName serverName,
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        var lease = TryAcquireInvocationLease(serverName, out var snapshot);
        if (lease is null || snapshot is null)
        {
            var reconnected = await TryReconnectAsync(serverName, ct);
            if (!reconnected)
                throw CreateUnavailableException(serverName, toolName);

            lease = TryAcquireInvocationLease(serverName, out snapshot);
            if (lease is null || snapshot is null)
                throw CreateUnavailableException(serverName, toolName);
        }

        Exception? transportFailure = null;
        try
        {
            if (!snapshot.ToolFunctions.TryGetValue(toolName.Value, out var function))
                throw CreateUnavailableException(serverName, toolName);

            using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                ct, lease.InvocationCancellation);
            return await InvokeFunctionAsync(
                function,
                $"{serverName.Value}/{toolName.Value}",
                arguments,
                invocationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException ex) when (!IsTransportOrSessionFailure(ex))
        {
            return $"Error: MCP tool '{serverName.Value}/{toolName.Value}' failed: {ex.Message}";
        }
        catch (Exception ex) when (IsTransportOrSessionFailure(ex))
        {
            transportFailure = ex;
        }
        finally
        {
            lease.Dispose();
        }

        // The failed call may have completed remotely. Release its lease first,
        // reconnect only for later calls, and never replay this invocation.
        await ReconnectAfterTransportFailureAsync(serverName, snapshot, transportFailure!);
        ExceptionDispatchInfo.Capture(transportFailure!).Throw();
        throw new InvalidOperationException("Unreachable exception propagation path.");
    }

    private McpInvocationLease? TryAcquireInvocationLease(
        McpServerName serverName,
        out McpServerSnapshot? snapshot)
    {
        lock (_shutdownSync)
        {
            snapshot = null;
            if (_stopping || !_servers.TryGetValue(serverName, out var lifecycle))
                return null;

            var current = lifecycle.Snapshot;
            if (current is null || !current.IsConnected || current.LeaseOwner is null)
                return null;

            if (!current.LeaseOwner.TryAcquire(out var lease))
                return null;

            snapshot = current;
            return lease;
        }
    }

    private async Task ReconnectAfterTransportFailureAsync(
        McpServerName serverName,
        McpServerSnapshot observed,
        Exception failure)
    {
        if (!_serverEntries.TryGetValue(serverName.Value, out var entry)
            || !_servers.TryGetValue(serverName, out var lifecycle))
            return;

        _logger.LogDebug(failure,
            "MCP transport/session failure on '{ServerName}'; reconnecting for later calls without replay",
            serverName.Value);

        try
        {
            await ReconnectAsync(
                lifecycle,
                entry,
                observed,
                CancellationToken.None,
                _timeProvider.GetUtcNow());
        }
        catch (Exception reconnectError)
        {
            _logger.LogError(reconnectError,
                "MCP server '{ServerName}' failed to reconnect after an invocation failure",
                serverName.Value);
            throw new AggregateException(
                $"MCP invocation on '{serverName.Value}' and its recovery both failed.",
                failure,
                reconnectError);
        }
    }

    private async Task<bool> ReconnectAsync(
        McpServerLifecycle lifecycle,
        McpServerEntry entry,
        McpServerSnapshot observed,
        CancellationToken callerCancellation,
        DateTimeOffset? triggeringFailureAt)
    {
        if (IsStopping)
            return false;

        if (lifecycle.InteractiveAuthorization is { IsCompleted: false })
            return observed.IsConnected;

        using var candidateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation, _lifetimeCancellation.Token);

        try
        {
            await lifecycle.Gate.WaitAsync(candidateCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            if (IsStopping)
                return false;

            var current = lifecycle.Snapshot;
            if (current is null)
                return false;

            // A waiter that observed the same publication reuses the one winning
            // generation. A changed same-generation publication means that the
            // coalesced attempt failed and recorded its failure already.
            if (!ReferenceEquals(current, observed))
                return current.Generation > observed.Generation && current.IsConnected;

            if (lifecycle.InteractiveAuthorization is { IsCompleted: false })
                return current.IsConnected;

            return await BuildAndPublishCandidateAsync(
                lifecycle,
                entry,
                current,
                candidateCancellation.Token,
                triggeringFailureAt,
                null);
        }
        finally
        {
            lifecycle.Gate.Release();
        }
    }

    private async Task<bool> BuildAndPublishCandidateAsync(
        McpServerLifecycle lifecycle,
        McpServerEntry entry,
        McpServerSnapshot current,
        CancellationToken ct,
        DateTimeOffset? triggeringFailureAt,
        McpOAuthFlow? authorizationFlow)
    {
        McpClient? candidate = null;
        McpOAuthClientContext? oauthContext = null;
        Exception? primaryFailure = null;
        try
        {
            var created = await CreateClientAsync(current.Name, entry, authorizationFlow, ct);
            candidate = created.Client;
            oauthContext = created.OAuthContext;

            var initialization = await _clientRuntime.InitializeAsync(candidate, ct);
            var tools = initialization.Tools;
            var functions = CreateFunctionMap(tools);
            var publishedTools = ToolRegistrationExtensions.PrepareMcpTools(
                current.Name.Value,
                tools,
                entry.GrantCategory,
                this,
                _maxToolDescriptionChars,
                _maxToolSchemaWarnChars,
                _logger);
            LogToolDrift(current.Name, tools);

            var lastErrorAt = triggeringFailureAt ?? current.Status.LastErrorAt;
            var connectedStatus = new McpServerStatus(
                current.Name,
                McpConnectionState.Connected,
                functions.Count,
                null,
                lastErrorAt);
            McpServerSnapshot replacement;

            lock (_shutdownSync)
            {
                if (_stopping)
                    return false;
                if (authorizationFlow is null
                    && lifecycle.InteractiveAuthorization is { IsCompleted: false })
                    return current.IsConnected;

                if (authorizationFlow is not null)
                {
                    _flowBroker.BeginCommit(authorizationFlow);
                    if (oauthContext is null)
                        throw new InvalidOperationException("Interactive OAuth candidate has no credential context.");
                    _credentialStore.PromotePending(
                        current.Name,
                        oauthContext,
                        authorizationFlow.State,
                        CancellationToken.None);
                }
                else if (oauthContext is not null)
                {
                    _credentialStore.ClaimActiveEpoch(current.Name, oauthContext, ct);
                }

                var leaseOwner = new McpInvocationLeaseOwner(
                    candidate,
                    _clientRuntime,
                    current.Name,
                    _logger);
                replacement = new McpServerSnapshot(
                    current.Name,
                    candidate,
                    functions,
                    checked(current.Generation + 1),
                    connectedStatus,
                    leaseOwner);
                _toolRegistry.PublishMcpServerTools(
                    current.Name.Value,
                    publishedTools,
                    () =>
                    {
                        lifecycle.Track(leaseOwner);
                        lifecycle.Publish(replacement);
                    });
                candidate = null;
            }

            if (current.LeaseOwner is not null)
                _ = lifecycle.Retire(current.LeaseOwner);
            _logger.LogInformation(
                "MCP server '{Name}' connected as generation {Generation} ({ToolCount} tools)",
                current.Name.Value,
                replacement.Generation,
                tools.Count);
            if (authorizationFlow is not null)
                _flowBroker.Complete(authorizationFlow);
            return true;
        }
        catch (OperationCanceledException ex) when (_lifetimeCancellation.IsCancellationRequested)
        {
            primaryFailure = ex;
            return false;
        }
        catch (OperationCanceledException ex)
        {
            primaryFailure = ex;
            throw;
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            var now = _timeProvider.GetUtcNow();
            var hasOAuthRuntimeHints = HasOAuthRuntimeHints(current.Name, entry);
            var credentialStateRequiresAuthorization = hasOAuthRuntimeHints
                                                       && entry.Url is not null
                                                       && _credentialStore.HasAnyActive(current.Name)
                                                       && _credentialStore.RequiresAuthorization(current.Name, entry.Url);
            var hasCachedTokens = entry.Url is not null
                                  && !credentialStateRequiresAuthorization
                                  && !_credentialStore.RequiresAuthorization(current.Name, entry.Url);
            var failureStatus = credentialStateRequiresAuthorization
                ? CreateAwaitingAuthStatus(current.Name, now)
                : BuildConnectionFailureStatus(
                    current.Name,
                    entry,
                    ex,
                    hasCachedTokens,
                    hasOAuthRuntimeHints,
                    now);
            lifecycle.Publish(WithFailureStatus(current, failureStatus));
            if (current.IsConnected)
            {
                _logger.LogWarning(ex,
                    "MCP server '{Name}' replacement failed; generation {Generation} remains connected",
                    current.Name.Value,
                    current.Generation);
            }
            else
            {
                ReportConnectionFailure(current.Name, failureStatus, ex, hasCachedTokens, hasOAuthRuntimeHints);
            }

            if (authorizationFlow is not null)
            {
                if (IsInvalidClientFailure(ex))
                {
                    _credentialStore.MarkDynamicIdentityRejected(
                        current.Name,
                        entry.Url!,
                        entry.OAuthClientId,
                        CancellationToken.None);
                }

                var error = CreateSafeOAuthError(ex, "connection initialization");
                _logger.LogError(ex,
                    "Explicit MCP OAuth candidate failed for server '{Name}' during {Operation} (provider status {ProviderStatus})",
                    current.Name.Value,
                    error.Operation,
                    error.Status);
                _credentialStore.RemovePending(
                    current.Name,
                    authorizationFlow.State,
                    CancellationToken.None);
                _flowBroker.Fail(authorizationFlow, error);
            }

            return false;
        }
        finally
        {
            if (candidate is not null)
            {
                try
                {
                    await DisposeCandidateAsync(current.Name, candidate);
                }
                catch (Exception disposalFailure)
                {
                    var surfacedFailure = primaryFailure is null
                        ? disposalFailure
                        : new AggregateException(
                            $"MCP candidate '{current.Name.Value}' initialization and disposal both failed.",
                            primaryFailure,
                            disposalFailure);
                    if (!IsStopping)
                    {
                        var failureStatus = CreateUnreachableStatus(
                            current.Name,
                            surfacedFailure,
                            _timeProvider.GetUtcNow());
                        lifecycle.Publish(WithFailureStatus(current, failureStatus));
                    }

                    _logger.LogError(disposalFailure,
                        "Error disposing unpublished MCP client '{Name}'",
                        current.Name.Value);
                    ExceptionDispatchInfo.Capture(surfacedFailure).Throw();
                    throw new InvalidOperationException("Unreachable exception propagation path.");
                }
            }

            if (authorizationFlow is not null
                && _flowBroker.GetStatusByState(authorizationFlow.State).Status is not McpOAuthFlowStatus.Completed)
            {
                try
                {
                    _credentialStore.RemovePending(
                        current.Name,
                        authorizationFlow.State,
                        CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    _logger.LogError(cleanupFailure,
                        "Failed to remove pending OAuth credentials for MCP server '{Name}'",
                        current.Name.Value);
                }
            }
        }
    }

    private async Task RunExplicitAuthorizationAsync(
        McpServerLifecycle lifecycle,
        McpServerEntry entry,
        McpOAuthFlow flow,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                flow.CancellationToken,
                _lifetimeCancellation.Token);
            await lifecycle.Gate.WaitAsync(cancellation.Token);
            try
            {
                if (IsStopping || lifecycle.Snapshot is not { } current)
                {
                    completion.TrySetResult(false);
                    return;
                }

                var result = await BuildAndPublishCandidateAsync(
                    lifecycle,
                    entry,
                    current,
                    cancellation.Token,
                    null,
                    flow);
                completion.TrySetResult(result);
            }
            finally
            {
                lifecycle.Gate.Release();
            }
        }
        catch (OperationCanceledException ex)
        {
            var error = new McpErrorResponse(
                "Authorization was cancelled or expired. Start a new MCP authorization attempt.",
                "authorization exchange");
            _logger.LogWarning(ex, "Explicit MCP OAuth flow was cancelled for '{Name}'", flow.ServerName.Value);
            _credentialStore.RemovePending(flow.ServerName, flow.State, CancellationToken.None);
            _flowBroker.Fail(flow, error);
            completion.TrySetResult(false);
        }
        catch (Exception ex)
        {
            var error = CreateSafeOAuthError(ex, "connection initialization");
            _logger.LogError(ex, "Explicit MCP OAuth flow failed for '{Name}'", flow.ServerName.Value);
            _flowBroker.Fail(flow, error);
            completion.TrySetResult(false);
        }
        finally
        {
            lifecycle.ClearInteractiveAuthorization(completion.Task);
        }
    }

    private McpServerSnapshot WithFailureStatus(McpServerSnapshot current, McpServerStatus failure)
    {
        if (!current.IsConnected)
            return McpServerSnapshot.WithoutConnection(failure, current.Generation);

        var connectedStatus = new McpServerStatus(
            current.Name,
            McpConnectionState.Connected,
            current.ToolFunctions.Count,
            failure.ErrorMessage,
            failure.LastErrorAt);
        return current with { Status = connectedStatus };
    }

    private async Task StopServerAsync(
        McpServerName serverName,
        McpServerLifecycle lifecycle,
        CancellationToken shutdownCancellation)
    {
        await lifecycle.Gate.WaitAsync(CancellationToken.None);
        try
        {
            var snapshot = lifecycle.Snapshot;
            var owners = lifecycle.RetireAll(snapshot?.LeaseOwner);
            _toolRegistry.PublishMcpServerTools(
                serverName.Value,
                [],
                () => lifecycle.Publish(null));

            if (owners.Count == 0)
                return;

            using var cancellationRegistration = shutdownCancellation.Register(
                static state =>
                {
                    foreach (var owner in (IReadOnlyList<McpInvocationLeaseOwner>)state!)
                        owner.CancelInvocations();
                },
                owners);

            var drained = Task.WhenAll(owners.Select(owner => owner.Drained));
            if (!drained.IsCompleted)
            {
                var timeout = Task.Delay(ShutdownDrainTimeout, _timeProvider, CancellationToken.None);
                if (await Task.WhenAny(drained, timeout) != drained)
                {
                    foreach (var owner in owners)
                        owner.CancelInvocations();
                }
            }

            await drained;
            try
            {
                await Task.WhenAll(owners.Select(owner => owner.Disposal));
            }
            finally
            {
                lifecycle.Forget(owners);
            }

            _logger.LogInformation("MCP client '{Name}' shut down", serverName.Value);
        }
        finally
        {
            lifecycle.Gate.Release();
        }
    }

    private async Task DisposeCandidateAsync(McpServerName name, McpClient candidate)
    {
        await _clientRuntime.DisposeAsync(candidate);
        _logger.LogDebug("Unpublished MCP client '{Name}' disposed", name.Value);
    }

    private async Task<string> InvokeFunctionAsync(
        AIFunction function,
        string qualifiedToolName,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        var aiArgs = arguments is { Count: > 0 }
            ? new AIFunctionArguments(arguments)
            : null;
        var result = await _clientRuntime.InvokeAsync(function, aiArgs, ct);
        return McpToolResultFormatter.Format(result, qualifiedToolName);
    }

    private static InvalidOperationException CreateUnavailableException(
        McpServerName serverName,
        ToolName toolName)
        => new($"MCP server '{serverName.Value}' is unavailable or tool '{toolName.Value}' is not registered.");

    private async Task<McpClientCandidate> CreateClientAsync(
        McpServerName name,
        McpServerEntry entry,
        McpOAuthFlow? authorizationFlow,
        CancellationToken ct)
    {
        McpOAuthClientContext? oauthContext = null;
        if (entry.Transport is not "stdio" && !HasConfiguredAuthorizationHeader(entry))
        {
            oauthContext = _credentialStore.CreateContext(
                name,
                entry.Url!,
                entry.OAuthClientId,
                authorizationFlow is not null);
        }

        var transport = CreateTransport(name, entry, oauthContext, authorizationFlow);
        var client = await _clientRuntime.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new()
            {
                Name = "netclaw",
                Title = "Netclaw",
                Version = BuildInfo.Version,
                WebsiteUrl = "https://netclaw.dev",
                Description = "Open-source autonomous operations agent built on Akka.NET",
            },
        }, ct);
        return new McpClientCandidate(client, oauthContext);
    }

    private IClientTransport CreateTransport(
        McpServerName serverName,
        McpServerEntry entry,
        McpOAuthClientContext? oauthContext,
        McpOAuthFlow? authorizationFlow)
    {
        if (entry.Transport is "stdio")
        {
            return _clientRuntime.CreateStdioTransport(new StdioClientTransportOptions
            {
                Command = entry.Command!,
                Arguments = entry.Arguments ?? [],
                EnvironmentVariables = entry.EnvironmentVariables.ToRawNullableValues(StringComparer.OrdinalIgnoreCase),
                Name = serverName.Value,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });
        }

        var headers = entry.Headers.ToRawValues(StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!headers.ContainsKey("User-Agent"))
            headers["User-Agent"] = NetclawUserAgent.Value;
        if (!headers.ContainsKey(NetclawUserAgent.ComponentHeader))
            headers[NetclawUserAgent.ComponentHeader] = "mcp";

        var oauth = HasConfiguredAuthorizationHeader(entry)
            ? null
            : BuildOAuthOptions(serverName, entry, oauthContext!, authorizationFlow);
        return _clientRuntime.CreateHttpTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(entry.Url!),
            Name = serverName.Value,
            AdditionalHeaders = headers,
            TransportMode = entry.Transport is "sse"
                ? HttpTransportMode.Sse
                : HttpTransportMode.StreamableHttp,
            OAuth = oauth,
        });
    }

    private ClientOAuthOptions BuildOAuthOptions(
        McpServerName serverName,
        McpServerEntry entry,
        McpOAuthClientContext context,
        McpOAuthFlow? authorizationFlow)
    {
        var identity = context.SnapshotIdentity();
        var redirectUri = new Uri(
            $"http://127.0.0.1:{_daemonConfig.Port}/api/mcp/oauth/callback");
        var target = authorizationFlow is null
            ? McpOAuthCredentialTarget.Active
            : McpOAuthCredentialTarget.Pending;
        return new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            ClientId = entry.OAuthClientId ?? identity.ClientId,
            ClientSecret = entry.OAuthClientId is null ? identity.ClientSecret : null,
            Scopes = ParseScopes(entry.OAuthScope),
            TokenCache = _credentialStore.CreateTokenCache(
                serverName,
                entry.Url!,
                context,
                target,
                authorizationFlow?.State,
                authorizationFlow?.ExpiresAt,
                withholdAccessToken: authorizationFlow is not null),
            AuthorizationRedirectDelegate = authorizationFlow is null
                ? static (_, _, _) => Task.FromResult<string?>(null)
                : authorizationFlow.HandleAuthorizationRedirectAsync,
            AdditionalAuthorizationParameters = authorizationFlow is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["state"] = authorizationFlow.State },
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "netclaw",
                ResponseDelegate = (response, cancellationToken) =>
                {
                    _credentialStore.CaptureDynamicRegistration(
                        serverName,
                        context,
                        response,
                        cancellationToken);
                    return Task.CompletedTask;
                },
            },
        };
    }

    private bool HasOAuthRuntimeHints(McpServerName serverName, McpServerEntry entry)
        => entry.Transport is not "stdio" && !HasConfiguredAuthorizationHeader(entry);

    private static bool HasConfiguredAuthorizationHeader(McpServerEntry entry)
        => entry.Headers?.Keys.Any(key =>
            string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase)) == true;

    internal static McpServerStatus BuildConnectionFailureStatus(
        McpServerName serverName,
        McpServerEntry entry,
        Exception ex,
        bool hasCachedTokens,
        bool hasOAuthRuntimeHints,
        DateTimeOffset errorAt)
    {
        if (IsAuthFailure(ex))
        {
            if (!hasCachedTokens && entry.Transport is not "stdio" && hasOAuthRuntimeHints)
                return CreateAwaitingAuthStatus(serverName, errorAt);

            return CreateAuthFailedStatus(
                serverName,
                ex,
                oauthManaged: hasCachedTokens || hasOAuthRuntimeHints,
                errorAt);
        }

        return CreateUnreachableStatus(serverName, ex, errorAt);
    }

    internal static McpServerStatus CreateAwaitingAuthStatus(
        McpServerName serverName,
        DateTimeOffset errorAt)
        => new(
            serverName,
            McpConnectionState.AwaitingAuth,
            0,
            $"OAuth authorization required. Run: netclaw mcp auth {serverName.Value}",
            errorAt);

    internal static McpServerStatus CreateAuthFailedStatus(
        McpServerName serverName,
        Exception ex,
        bool oauthManaged,
        DateTimeOffset errorAt)
    {
        var statusText = GetHttpStatusText(ex);
        var detail = string.IsNullOrWhiteSpace(statusText)
            ? "Authentication rejected by server."
            : $"Authentication rejected by server ({statusText}).";
        var guidance = oauthManaged
            ? $" Run: netclaw mcp auth {serverName.Value}"
            : " Check configured credentials or headers.";
        return new McpServerStatus(
            serverName,
            McpConnectionState.AuthFailed,
            0,
            detail + guidance,
            errorAt);
    }

    internal static McpServerStatus CreateUnreachableStatus(
        McpServerName serverName,
        Exception ex,
        DateTimeOffset errorAt)
        => new(
            serverName,
            McpConnectionState.Unreachable,
            0,
            GetSafeConnectionFailure(ex),
            errorAt);

    private static string GetSafeConnectionFailure(Exception ex)
    {
        var status = FindHttpStatus(ex);
        if (status is not null)
            return $"MCP server request failed (HTTP {(int)status.Value} {status.Value}).";
        if (ex is TimeoutException or TaskCanceledException)
            return "MCP server connection timed out.";
        return "Failed to reach MCP server. Check daemon logs for details.";
    }

    private void ReportConnectionFailure(
        McpServerName name,
        McpServerStatus failureStatus,
        Exception ex,
        bool hasCachedTokens,
        bool hasOAuthRuntimeHints)
    {
        if (failureStatus.State is McpConnectionState.AwaitingAuth)
        {
            _logger.LogWarning(ex, "MCP server '{Name}' requires OAuth authorization", name.Value);
            EmitAuthAlert(name,
                $"MCP server '{name.Value}' requires OAuth authorization. Run: netclaw mcp auth {name.Value}",
                "authorization_required");
            return;
        }

        if (failureStatus.State is McpConnectionState.AuthFailed)
        {
            _logger.LogWarning(ex, "MCP server '{Name}' authentication failed", name.Value);
            if (hasOAuthRuntimeHints || hasCachedTokens)
            {
                EmitAuthAlert(name,
                    $"MCP server '{name.Value}' authentication failed. Run: netclaw mcp auth {name.Value}",
                    hasCachedTokens ? "token_rejected" : "credentials_rejected");
            }
            else
            {
                EmitDisconnectedAlert(name,
                    $"MCP server '{name.Value}' authentication failed: {failureStatus.ErrorMessage}");
            }

            return;
        }

        _logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name.Value);
        EmitDisconnectedAlert(name,
            $"MCP server '{name.Value}' connection failed: {failureStatus.ErrorMessage}");
    }

    private void EmitAuthAlert(McpServerName serverName, string summary, string reason)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "mcp.auth.expired",
            AlertType.McpAuthExpired,
            summary,
            AlertSeverity.Warning,
            source: serverName.Value,
            context: new Dictionary<string, string>
            {
                ["serverName"] = serverName.Value,
                ["reason"] = reason,
            }));
    }

    private void EmitDisconnectedAlert(McpServerName serverName, string summary)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "mcp.server.disconnected",
            AlertType.McpServerDisconnected,
            summary,
            AlertSeverity.Warning,
            source: serverName.Value,
            context: new Dictionary<string, string> { ["serverName"] = serverName.Value }));
    }

    private static IEnumerable<string>? ParseScopes(string? scopeString)
    {
        if (string.IsNullOrWhiteSpace(scopeString))
            return null;

        return scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsAuthFailure(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            return true;

        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("AuthorizationRedirectDelegate", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("authorization code", StringComparison.OrdinalIgnoreCase))
            return true;

        return ex.InnerException is not null && IsAuthFailure(ex.InnerException);
    }

    private static bool IsInvalidClientFailure(Exception ex)
        => ex.Message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
           || ex.InnerException is not null && IsInvalidClientFailure(ex.InnerException);

    internal static McpErrorResponse CreateSafeOAuthError(Exception ex, string fallbackOperation)
    {
        var status = FindHttpStatus(ex);
        var exceptions = EnumerateExceptionTree(ex).ToArray();
        var operation = exceptions.Any(candidate =>
                            candidate is McpOAuthStaleCredentialEpochException
                                or IOException
                                or UnauthorizedAccessException
                            || candidate.Message.Contains("secrets", StringComparison.OrdinalIgnoreCase))
            ? "credential persistence"
            : exceptions.Any(candidate =>
                candidate.Message.Contains("registration", StringComparison.OrdinalIgnoreCase)
                || candidate.Message.Contains("register", StringComparison.OrdinalIgnoreCase))
            ? "dynamic client registration"
            : exceptions.Any(candidate =>
                candidate.Message.Contains("token", StringComparison.OrdinalIgnoreCase)
                || candidate.Message.Contains("authorization code", StringComparison.OrdinalIgnoreCase))
                ? "authorization code exchange"
                : fallbackOperation;
        var statusText = status is null
            ? null
            : $"HTTP {(int)status.Value} {status.Value}";
        var message = statusText is null
            ? $"MCP OAuth {operation} failed. Check daemon logs for details."
            : $"MCP OAuth {operation} failed: {statusText}.";
        return new McpErrorResponse(message, operation, status is null ? null : (int)status.Value);
    }

    private static IEnumerable<Exception> EnumerateExceptionTree(Exception root)
    {
        var pending = new Stack<Exception>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    private static HttpStatusCode? FindHttpStatus(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } status })
            return status;
        if (ex.Message.Contains("403", StringComparison.Ordinal)
            || ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return HttpStatusCode.Forbidden;
        if (ex.Message.Contains("401", StringComparison.Ordinal)
            || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return HttpStatusCode.Unauthorized;
        return ex.InnerException is null ? null : FindHttpStatus(ex.InnerException);
    }

    internal static bool IsTransportOrSessionFailure(Exception ex)
    {
        if (ex is HttpRequestException
            or IOException
            or EndOfStreamException
            or TimeoutException
            or ObjectDisposedException)
            return true;

        if (ex is not McpException)
            return false;

        return ex.Message.Contains("transport", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("connection closed", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("session closed", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("stream ended", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetHttpStatusText(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: var statusCode } && statusCode is not null)
        {
            return statusCode switch
            {
                HttpStatusCode.Unauthorized => "401 Unauthorized",
                HttpStatusCode.Forbidden => "403 Forbidden",
                _ => $"{(int)statusCode} {statusCode}"
            };
        }

        return ex.InnerException is null ? null : GetHttpStatusText(ex.InnerException);
    }

    internal static IReadOnlyDictionary<string, AIFunction> CreateFunctionMap(IReadOnlyList<AIFunction> tools)
    {
        var map = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
            map[tool.Name] = tool;
        return new ReadOnlyDictionary<string, AIFunction>(map);
    }

    private void LogToolDrift(McpServerName serverName, IReadOnlyList<AIFunction> discoveredTools)
    {
        var profiles = _toolConfig.AudienceProfiles;
        var allGrantedTools = new HashSet<string>(StringComparer.Ordinal);
        var hasAnyGrants = false;

        foreach (var profile in profiles.GetAllProfiles())
        {
            if (profile.McpServerToolGrants is not { } grants
                || !grants.TryGetValue(serverName.Value, out var tools))
                continue;

            hasAnyGrants = true;
            foreach (var tool in tools)
                allGrantedTools.Add(tool);
        }

        if (!hasAnyGrants)
            return;

        var discoveredNames = new HashSet<string>(
            discoveredTools.Select(t => t.Name), StringComparer.Ordinal);
        var ungranted = discoveredNames.Except(allGrantedTools).ToList();
        var stale = allGrantedTools.Except(discoveredNames).ToList();

        if (ungranted.Count > 0)
        {
            _logger.LogWarning(
                "MCP server '{Name}' exposes {Count} tool(s) not granted to any audience: {Tools}. " +
                "Review and add to McpServerToolGrants if intended.",
                serverName.Value, ungranted.Count, string.Join(", ", ungranted));
        }

        if (stale.Count > 0)
        {
            _logger.LogWarning(
                "McpServerToolGrants for '{Name}' contains {Count} tool(s) not found on server: {Tools}. " +
                "These may have been removed or renamed.",
                serverName.Value, stale.Count, string.Join(", ", stale));
        }
    }

    public void Dispose()
    {
        var emergencyCleanup = false;
        lock (_shutdownSync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _stopping = true;
            _lifetimeCancellation.Cancel();
            emergencyCleanup = _stopTask is null;
        }

        if (emergencyCleanup)
        {
            foreach (var (serverName, lifecycle) in _servers)
            {
                var owners = lifecycle.RetireAll(lifecycle.Snapshot?.LeaseOwner);
                _toolRegistry.PublishMcpServerTools(
                    serverName.Value,
                    [],
                    () => lifecycle.Publish(null));
                foreach (var owner in owners)
                    owner.CancelInvocations();
            }
        }

        _lifetimeCancellation.Dispose();
    }
}

internal interface IMcpClientRuntime
{
    IClientTransport CreateStdioTransport(StdioClientTransportOptions options)
        => new StdioClientTransport(options);

    IClientTransport CreateHttpTransport(HttpClientTransportOptions options)
        => new HttpClientTransport(options);

    Task<McpClient> CreateAsync(
        IClientTransport transport,
        McpClientOptions options,
        CancellationToken cancellationToken);

    ValueTask<McpClientInitialization> InitializeAsync(
        McpClient client,
        CancellationToken cancellationToken);

    ValueTask<object?> InvokeAsync(
        AIFunction function,
        AIFunctionArguments? arguments,
        CancellationToken cancellationToken);

    ValueTask DisposeAsync(McpClient client);
}

internal sealed class McpClientRuntime : IMcpClientRuntime
{
    public Task<McpClient> CreateAsync(
        IClientTransport transport,
        McpClientOptions options,
        CancellationToken cancellationToken)
        => McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);

    public async ValueTask<McpClientInitialization> InitializeAsync(
        McpClient client,
        CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return new McpClientInitialization(tools.Cast<AIFunction>().ToList());
    }

    public ValueTask<object?> InvokeAsync(
        AIFunction function,
        AIFunctionArguments? arguments,
        CancellationToken cancellationToken)
        => function.InvokeAsync(arguments, cancellationToken);

    public ValueTask DisposeAsync(McpClient client) => client.DisposeAsync();
}

internal sealed record McpClientInitialization(
    IReadOnlyList<AIFunction> Tools);

internal sealed record McpClientCandidate(
    McpClient Client,
    McpOAuthClientContext? OAuthContext);

internal sealed class McpServerLifecycle(McpServerSnapshot initialSnapshot)
{
    private McpServerSnapshot? _snapshot = initialSnapshot;
    private Task<bool>? _interactiveAuthorization;
    private readonly List<McpInvocationLeaseOwner> _owners = [];
    private readonly object _ownersSync = new();

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public McpServerSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public Task<bool>? InteractiveAuthorization => Volatile.Read(ref _interactiveAuthorization);

    public int TrackedOwnerCount
    {
        get
        {
            lock (_ownersSync)
                return _owners.Count;
        }
    }

    public void Publish(McpServerSnapshot? snapshot) => Volatile.Write(ref _snapshot, snapshot);

    public void SetInteractiveAuthorization(Task<bool> authorization)
    {
        while (true)
        {
            var current = Volatile.Read(ref _interactiveAuthorization);
            if (current is not null && !current.IsCompleted)
                throw new InvalidOperationException("An interactive authorization operation is already active.");
            if (Interlocked.CompareExchange(ref _interactiveAuthorization, authorization, current) == current)
                return;
        }
    }

    public void ClearInteractiveAuthorization(Task<bool> authorization)
        => Interlocked.CompareExchange(ref _interactiveAuthorization, null, authorization);

    public void Track(McpInvocationLeaseOwner owner)
    {
        var added = false;
        lock (_ownersSync)
        {
            if (!_owners.Contains(owner))
            {
                _owners.Add(owner);
                added = true;
            }
        }

        if (!added)
            return;

        owner.RegisterSuccessfulDisposal(RemoveSuccessfullyDisposed);
    }

    public Task Retire(McpInvocationLeaseOwner owner)
    {
        Track(owner);
        return owner.Retire();
    }

    public IReadOnlyList<McpInvocationLeaseOwner> RetireAll(McpInvocationLeaseOwner? current)
    {
        List<McpInvocationLeaseOwner> owners;
        lock (_ownersSync)
        {
            if (current is not null && !_owners.Contains(current))
                _owners.Add(current);

            owners = _owners.ToList();
        }

        foreach (var owner in owners)
            _ = owner.Retire();

        return owners;
    }

    private void RemoveSuccessfullyDisposed(McpInvocationLeaseOwner owner)
    {
        lock (_ownersSync)
            _owners.Remove(owner);
    }

    public void Forget(IReadOnlyList<McpInvocationLeaseOwner> owners)
    {
        lock (_ownersSync)
        {
            foreach (var owner in owners)
                _owners.Remove(owner);
        }
    }

}

internal sealed record McpServerSnapshot(
    McpServerName Name,
    McpClient? Client,
    IReadOnlyDictionary<string, AIFunction> ToolFunctions,
    long Generation,
    McpServerStatus Status,
    McpInvocationLeaseOwner? LeaseOwner)
{
    private static readonly IReadOnlyDictionary<string, AIFunction> EmptyFunctions =
        new ReadOnlyDictionary<string, AIFunction>(new Dictionary<string, AIFunction>());

    public bool IsConnected
        => Client is not null
           && LeaseOwner is not null
           && Status.State is McpConnectionState.Connected;

    public static McpServerSnapshot WithoutConnection(McpServerStatus status, long generation = 0)
        => new(status.Name, null, EmptyFunctions, generation, status, null);
}

internal sealed class McpInvocationLeaseOwner
{
    private readonly object _sync = new();
    private readonly McpClient _client;
    private readonly IMcpClientRuntime _runtime;
    private readonly McpServerName _serverName;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _invocationCancellation = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeLeases;
    private bool _retired;
    private bool _disposeStarted;
    private bool _disposedSuccessfully;
    private Action<McpInvocationLeaseOwner>? _successfulDisposal;

    public McpInvocationLeaseOwner(
        McpClient client,
        IMcpClientRuntime runtime,
        McpServerName serverName,
        ILogger logger)
    {
        _client = client;
        _runtime = runtime;
        _serverName = serverName;
        _logger = logger;
    }

    public Task Drained => _drained.Task;

    public Task Disposal => _disposed.Task;

    public void RegisterSuccessfulDisposal(Action<McpInvocationLeaseOwner> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var invokeNow = false;
        lock (_sync)
        {
            if (_disposedSuccessfully)
                invokeNow = true;
            else
                _successfulDisposal += callback;
        }

        if (invokeNow)
            callback(this);
    }

    public bool TryAcquire(out McpInvocationLease? lease)
    {
        lock (_sync)
        {
            if (_retired)
            {
                lease = null;
                return false;
            }

            checked { _activeLeases++; }
            lease = new McpInvocationLease(this, _invocationCancellation.Token);
            return true;
        }
    }

    public Task Retire()
    {
        var startDispose = false;
        lock (_sync)
        {
            if (!_retired)
                _retired = true;

            if (_activeLeases == 0)
            {
                _drained.TrySetResult();
                if (!_disposeStarted)
                {
                    _disposeStarted = true;
                    startDispose = true;
                }
            }
        }

        if (startDispose)
            _ = DisposeClientAsync();

        return _disposed.Task;
    }

    public void CancelInvocations()
    {
        try
        {
            _invocationCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug(
                "Invocation cancellation for retired MCP client '{Name}' raced completed disposal",
                _serverName.Value);
        }
    }

    internal void Release()
    {
        var startDispose = false;
        lock (_sync)
        {
            if (_activeLeases <= 0)
                throw new InvalidOperationException("MCP invocation lease released more than once.");

            _activeLeases--;
            if (_retired && _activeLeases == 0)
            {
                _drained.TrySetResult();
                if (!_disposeStarted)
                {
                    _disposeStarted = true;
                    startDispose = true;
                }
            }
        }

        if (startDispose)
            _ = DisposeClientAsync();
    }

    private async Task DisposeClientAsync()
    {
        try
        {
            await _runtime.DisposeAsync(_client);
            Action<McpInvocationLeaseOwner>? successfulDisposal;
            lock (_sync)
            {
                _disposedSuccessfully = true;
                successfulDisposal = _successfulDisposal;
                _successfulDisposal = null;
            }

            successfulDisposal?.Invoke(this);
            _disposed.TrySetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error disposing retired MCP client '{Name}'",
                _serverName.Value);
            _disposed.TrySetException(ex);
        }
        finally
        {
            _invocationCancellation.Dispose();
        }
    }
}

internal sealed class McpInvocationLease : IDisposable
{
    private McpInvocationLeaseOwner? _owner;

    public McpInvocationLease(McpInvocationLeaseOwner owner, CancellationToken invocationCancellation)
    {
        _owner = owner;
        InvocationCancellation = invocationCancellation;
    }

    public CancellationToken InvocationCancellation { get; }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
}

internal enum McpConnectionState
{
    Disabled,
    Connected,
    AwaitingAuth,
    AuthFailed,
    Unreachable,
}

internal sealed record McpServerStatus(
    McpServerName Name,
    McpConnectionState State,
    int ToolCount,
    string? ErrorMessage,
    DateTimeOffset? LastErrorAt);
