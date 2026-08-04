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
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed class McpClientManager : IHostedService, IDisposable, IMcpToolInvoker, IMcpReconnectable
{
    private readonly Dictionary<string, McpServerEntry> _serverEntries;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolConfig _toolConfig;
    private readonly McpOAuthCredentialStore _credentialStore;
    private readonly McpOAuthClientRegistrar _registrar;
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
        McpOAuthClientRegistrar registrar,
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
        _registrar = registrar;
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

        var started = _flowBroker.StartOrJoin(serverName);
        if (started.Created)
            _ = RunExplicitAuthorizationAsync(lifecycle, entry, started.Flow);

        var request = await started.Flow.WaitForAuthorizationRequestAsync(requestCancellation);
        return new McpOAuthStartResponse(request.Url.ToString(), request.State, started.Flow.ExpiresAt);
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
        var snapshot = TryGetConnectedSnapshot(serverName);
        if (snapshot is null)
        {
            if (!await TryReconnectAsync(serverName, ct))
                throw CreateUnavailableException(serverName, toolName);

            snapshot = TryGetConnectedSnapshot(serverName)
                       ?? throw CreateUnavailableException(serverName, toolName);
        }

        Exception? transportFailure = null;
        try
        {
            if (!snapshot.ToolFunctions.TryGetValue(toolName.Value, out var function))
                throw CreateUnavailableException(serverName, toolName);

            return await InvokeFunctionAsync(
                serverName,
                function,
                $"{serverName.Value}/{toolName.Value}",
                arguments,
                ct);
        }
        catch (McpException ex) when (!IsTransportOrSessionFailure(ex))
        {
            return $"Error: MCP tool '{serverName.Value}/{toolName.Value}' failed: {ex.Message}";
        }
        catch (Exception ex) when (IsTransportOrSessionFailure(ex))
        {
            transportFailure = ex;
        }

        // The failed call may have completed remotely. Reconnect only for later calls,
        // and never replay this invocation.
        await ReconnectAfterTransportFailureAsync(serverName, snapshot, transportFailure!);
        ExceptionDispatchInfo.Capture(transportFailure!).Throw();
        throw new InvalidOperationException("Unreachable exception propagation path.");
    }

    private McpServerSnapshot? TryGetConnectedSnapshot(McpServerName serverName)
    {
        lock (_shutdownSync)
        {
            if (_stopping || !_servers.TryGetValue(serverName, out var lifecycle))
                return null;

            var current = lifecycle.Snapshot;
            return current is not null && current.IsConnected ? current : null;
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

        if (_flowBroker.TryGetActive(observed.Name, out _))
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

            if (_flowBroker.TryGetActive(current.Name, out _))
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
        McpOAuthTokenCache? oauthCache = null;
        Exception? candidateFailure = null;
        try
        {
            var created = await CreateClientAsync(current.Name, entry, authorizationFlow, ct);
            candidate = created.Client;
            oauthCache = created.OAuthCache;

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
                    && _flowBroker.TryGetActive(current.Name, out _))
                    return current.IsConnected;

                if (authorizationFlow is not null)
                {
                    _flowBroker.BeginCommit(authorizationFlow);
                    if (oauthCache is null)
                        throw new InvalidOperationException("Interactive OAuth candidate has no token cache.");
                }

                if (oauthCache is not null)
                    _credentialStore.Publish(oauthCache, CancellationToken.None);

                replacement = new McpServerSnapshot(
                    current.Name,
                    candidate,
                    functions,
                    checked(current.Generation + 1),
                    connectedStatus);
                // Connection first, tools second. A tool the model can see is then always
                // dispatchable, because dispatch resolves it from this snapshot.
                lifecycle.Publish(replacement);
                _toolRegistry.PublishMcpServerTools(current.Name.Value, publishedTools);
                candidate = null;
                oauthCache = null;
            }

            if (current.Client is not null)
                await DisposeReplacedAsync(current.Name, current.Client);
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
            candidateFailure = ex;
            return false;
        }
        catch (OperationCanceledException ex)
        {
            candidateFailure = ex;
            throw;
        }
        catch (Exception ex)
        {
            candidateFailure = ex;
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
                // The server says this client id is not one of its own. Keeping it would
                // make every future authorization attempt fail the same way.
                if (entry.OAuthClientId is null && IsInvalidClientFailure(ex))
                    _credentialStore.ForgetClientIdentity(current.Name, entry.Url!, CancellationToken.None);

                var error = CreateSafeOAuthError(ex, "connection initialization");
                _logger.LogError(ex,
                    "Explicit MCP OAuth candidate failed for server '{Name}' during {Operation} (provider status {ProviderStatus})",
                    current.Name.Value,
                    error.Operation,
                    error.Status);
                _flowBroker.Fail(authorizationFlow, error);
            }

            return false;
        }
        finally
        {
            if (oauthCache is not null)
                _credentialStore.Discard(oauthCache);
            if (candidate is not null)
            {
                try
                {
                    await DisposeCandidateAsync(current.Name, candidate);
                }
                catch (Exception disposalFailure)
                {
                    _logger.LogError(disposalFailure,
                        "Error disposing unpublished MCP client '{Name}'",
                        current.Name.Value);
                    if (candidateFailure is not null)
                    {
                        throw new AggregateException(
                            "MCP candidate initialization and disposal both failed.",
                            candidateFailure,
                            disposalFailure);
                    }
                    throw;
                }
            }

        }
    }

    private async Task RunExplicitAuthorizationAsync(
        McpServerLifecycle lifecycle,
        McpServerEntry entry,
        McpOAuthFlow flow)
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
                    return;

                await BuildAndPublishCandidateAsync(
                    lifecycle,
                    entry,
                    current,
                    cancellation.Token,
                    null,
                    flow);
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
            _flowBroker.Fail(flow, error);
        }
        catch (Exception ex)
        {
            var error = CreateSafeOAuthError(ex, "connection initialization");
            _logger.LogError(ex, "Explicit MCP OAuth flow failed for '{Name}'", flow.ServerName.Value);
            _flowBroker.Fail(flow, error);
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
            var client = lifecycle.Snapshot?.Client;
            // Tools first, connection second. The model stops seeing the server's tools
            // before dispatch loses the snapshot that resolves them.
            _toolRegistry.PublishMcpServerTools(serverName.Value, []);
            lifecycle.Publish(null);

            if (client is null)
                return;

            // An invocation still in flight is cancelled by the client going away rather
            // than drained. Draining lives in the separate client-lifecycle work.
            await _clientRuntime.DisposeAsync(client);
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

    private async Task DisposeReplacedAsync(McpServerName name, McpClient replaced)
    {
        try
        {
            await _clientRuntime.DisposeAsync(replaced);
        }
        catch (Exception ex)
        {
            // The replacement is already published and serving; a failed disposal of the
            // old client leaks a connection but must not fail the reconnect.
            _logger.LogError(ex, "Error disposing replaced MCP client '{Name}'", name.Value);
        }
    }

    private async Task<string> InvokeFunctionAsync(
        McpServerName serverName,
        AIFunction function,
        string qualifiedToolName,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        var aiArgs = arguments is { Count: > 0 }
            ? new AIFunctionArguments(arguments)
            : null;
        var result = await _clientRuntime.InvokeAsync(function, aiArgs, ct);

        if (McpToolResultFormatter.TryGetErrorDetail(result, out var detail))
            ReportToolFailure(serverName, qualifiedToolName, detail);

        return McpToolResultFormatter.Format(result, qualifiedToolName);
    }

    /// <summary>
    /// Records a failure the MCP server reported inside an otherwise successful response.
    /// The detail reaches the model but no exception reaches the transport layer, so
    /// without this the daemon log keeps only the result length and an operator has
    /// nothing to debug from.
    /// </summary>
    private void ReportToolFailure(McpServerName serverName, string qualifiedToolName, string detail)
    {
        // Redact before logging: an MCP error body can echo the arguments it rejected, and
        // daemon logs leave the box when OTLP export is enabled.
        _logger.LogWarning(
            "MCP tool '{Tool}' reported a failure: {Detail}",
            qualifiedToolName,
            SecretOutputRedactor.Redact(detail));

        if (!IsAuthFailureMessage(detail))
            return;

        MarkToolAuthFailure(serverName);
    }

    /// <summary>
    /// Moves a server out of <see cref="McpConnectionState.Connected"/> when its
    /// credential is rejected at call time. The transport stays healthy in this case, so
    /// status would otherwise report a working server while every invocation fails, and
    /// the one state that needs operator action would be the one state never shown.
    /// </summary>
    private void MarkToolAuthFailure(McpServerName serverName)
    {
        if (!_servers.TryGetValue(serverName, out var lifecycle))
            return;

        var current = lifecycle.Snapshot;
        if (current is null || current.Status.State is McpConnectionState.AuthFailed)
            return;

        var status = new McpServerStatus(
            serverName,
            McpConnectionState.AuthFailed,
            current.Status.ToolCount,
            $"Authentication rejected by server. Run: netclaw mcp auth {serverName.Value}",
            _timeProvider.GetUtcNow());
        lifecycle.Publish(current with { Status = status });

        _logger.LogWarning(
            "MCP server '{Name}' rejected an authenticated tool call; reauthorization is required",
            serverName.Value);
        EmitAuthAlert(
            serverName,
            $"MCP server '{serverName.Value}' authentication failed. Run: netclaw mcp auth {serverName.Value}",
            "authentication_failed");
    }

    /// <summary>
    /// Explains why a tool could not run. This message is what the agent reports, so a
    /// server that is only waiting on authorization must name that remedy: "unavailable"
    /// reads as a broken server and sends the operator looking for the wrong problem.
    /// </summary>
    private InvalidOperationException CreateUnavailableException(
        McpServerName serverName,
        ToolName toolName)
    {
        var state = _servers.TryGetValue(serverName, out var lifecycle)
            ? lifecycle.Snapshot?.Status.State
            : null;

        return state is McpConnectionState.AuthFailed or McpConnectionState.AwaitingAuth
            ? new InvalidOperationException(
                $"MCP server '{serverName.Value}' requires authorization. " +
                $"Run: netclaw mcp auth {serverName.Value}")
            : new InvalidOperationException(
                $"MCP server '{serverName.Value}' is unavailable or tool '{toolName.Value}' is not registered.");
    }

    private async Task<McpClientCandidate> CreateClientAsync(
        McpServerName name,
        McpServerEntry entry,
        McpOAuthFlow? authorizationFlow,
        CancellationToken ct)
    {
        McpOAuthTokenCache? oauthCache = null;
        if (entry.Transport is not "stdio" && !HasConfiguredAuthorizationHeader(entry))
        {
            oauthCache = _credentialStore.CreateTokenCache(
                name,
                entry.Url!,
                entry.OAuthClientId,
                authorizationFlow is not null);
        }

        try
        {
            // Register only while explicitly authorizing. A background reconnect with no
            // stored identity belongs to a server nobody has authorized yet, and it would
            // fail at the redirect delegate regardless — registering there would create
            // client records for servers the operator never opted into.
            if (oauthCache is not null
                && authorizationFlow is not null
                && _credentialStore.GetIdentity(oauthCache).ClientId is null)
            {
                var registered = await _registrar.TryRegisterAsync(
                    name,
                    entry.Url!,
                    BuildRedirectUri(),
                    ct);
                if (registered is not null)
                    _credentialStore.AdoptClientIdentity(oauthCache, registered);
            }

            var transport = CreateTransport(name, entry, oauthCache, authorizationFlow);
            var client = await _clientRuntime.CreateAsync(transport, BuildClientOptions(authorizationFlow), ct);
            return new McpClientCandidate(client, oauthCache);
        }
        catch
        {
            if (oauthCache is not null)
                _credentialStore.Discard(oauthCache);
            throw;
        }
    }

    /// <summary>
    /// Builds the client options, stretching both connect timeouts when an operator is
    /// waiting at a browser.
    /// <para>
    /// The SDK defaults are machine-scale: a 5 second <c>server/discover</c> probe and a
    /// 60 second initialization budget. A server that answers the probe with 401 sends the
    /// SDK into the authorization callback handler, which cannot return until the operator
    /// finishes. The probe timeout then cancels that wait, the SDK falls back to the
    /// <c>initialize</c> handshake, and it calls the handler a second time for the same
    /// flow — which the single-owner guard rejects, ending the authorization the operator
    /// was still working through. Both timeouts therefore match the flow lifetime while a
    /// flow exists. A background reconnect keeps the defaults, because its handler returns
    /// immediately and nothing waits.
    /// </para>
    /// </summary>
    private static McpClientOptions BuildClientOptions(McpOAuthFlow? authorizationFlow)
    {
        var options = new McpClientOptions
        {
            ClientInfo = new()
            {
                Name = "netclaw",
                Title = "Netclaw",
                Version = BuildInfo.Version,
                WebsiteUrl = "https://netclaw.dev",
                Description = "Open-source autonomous operations agent built on Akka.NET",
            },
        };

        if (authorizationFlow is not null)
        {
            options.InitializationTimeout = McpOAuthFlowBroker.FlowLifetime;
            options.DiscoverProbeTimeout = McpOAuthFlowBroker.FlowLifetime;
        }

        return options;
    }

    private IClientTransport CreateTransport(
        McpServerName serverName,
        McpServerEntry entry,
        McpOAuthTokenCache? oauthCache,
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
            : BuildOAuthOptions(entry, oauthCache!, authorizationFlow);
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
        McpServerEntry entry,
        McpOAuthTokenCache cache,
        McpOAuthFlow? authorizationFlow)
    {
        var identity = _credentialStore.GetIdentity(cache);
        return new ClientOAuthOptions
        {
            RedirectUri = BuildRedirectUri(),
            ClientId = entry.OAuthClientId ?? identity.ClientId,
            ClientSecret = entry.OAuthClientId is null ? identity.ClientSecret : null,
            Scopes = ParseScopes(entry.OAuthScope),
            TokenCache = cache,

            // A background reconnect has no flow and therefore no operator at a browser.
            // Returning null makes the SDK fail the connection instead of blocking on a
            // redirect nobody will complete.
            AuthorizationCallbackHandler = authorizationFlow is null
                ? static (_, _) => Task.FromResult<AuthorizationResult?>(null)
                : authorizationFlow.HandleAuthorizationCallbackAsync,

            // DynamicClientRegistration is deliberately left unset. McpOAuthClientRegistrar
            // owns registration because the SDK hard-codes client_secret_post and cannot
            // register against public-client-only servers (csharp-sdk#1611). A non-null
            // ClientId here short-circuits the SDK's registration path entirely.
        };
    }

    private Uri BuildRedirectUri()
        => new($"http://127.0.0.1:{_daemonConfig.Port}/api/mcp/oauth/callback");

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

        if (IsAuthFailureMessage(ex.Message))
            return true;

        return ex.InnerException is not null && IsAuthFailure(ex.InnerException);
    }

    /// <summary>
    /// Recognizes an authentication rejection from message text alone. A tool-level
    /// failure carries no exception and no HTTP status, so the wording is all there is.
    /// </summary>
    private static bool IsAuthFailureMessage(string message)
        => message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
           || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
           || message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
           || message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
           || message.Contains("invalid_token", StringComparison.OrdinalIgnoreCase)
           || message.Contains("token expired", StringComparison.OrdinalIgnoreCase)
           || message.Contains("AuthorizationCallbackHandler", StringComparison.OrdinalIgnoreCase)
           || message.Contains("authorization code", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies a stored client registration the authorization server will never accept
    /// again. The caller only acts on this when the client id came from dynamic registration,
    /// so an operator-pinned OAuthClientId is never discarded behind their back.
    /// </summary>
    private static bool IsInvalidClientFailure(Exception ex)
        => ex.Message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
           // A registration is bound to the issuer that granted it. When the resource server
           // moves to a new issuer, SDK 2.0 refuses to reuse the old one and offers no remedy
           // of its own, so the stale identity has to go or every retry repeats the failure.
           || ex.Message.Contains("authorization server changed", StringComparison.OrdinalIgnoreCase)
           || ex.InnerException is not null && IsInvalidClientFailure(ex.InnerException);

    internal static McpErrorResponse CreateSafeOAuthError(Exception ex, string fallbackOperation)
    {
        var status = FindHttpStatus(ex);
        var exceptions = EnumerateExceptionTree(ex).ToArray();
        var operation = exceptions.Any(candidate =>
                            candidate is McpOAuthRetiredCredentialWriterException
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
        foreach (var candidate in Enum.GetValues<HttpStatusCode>())
        {
            if (ex.Message.Contains($"status {candidate}", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains($"HTTP {(int)candidate}", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
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
                _toolRegistry.PublishMcpServerTools(serverName.Value, []);
                lifecycle.Publish(null);
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
    McpOAuthTokenCache? OAuthCache);

internal sealed class McpServerLifecycle(McpServerSnapshot initialSnapshot)
{
    private McpServerSnapshot? _snapshot = initialSnapshot;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public McpServerSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public void Publish(McpServerSnapshot? snapshot) => Volatile.Write(ref _snapshot, snapshot);
}

internal sealed record McpServerSnapshot(
    McpServerName Name,
    McpClient? Client,
    IReadOnlyDictionary<string, AIFunction> ToolFunctions,
    long Generation,
    McpServerStatus Status)
{
    private static readonly IReadOnlyDictionary<string, AIFunction> EmptyFunctions =
        new ReadOnlyDictionary<string, AIFunction>(new Dictionary<string, AIFunction>());

    public bool IsConnected
        => Client is not null && Status.State is McpConnectionState.Connected;

    public static McpServerSnapshot WithoutConnection(McpServerStatus status, long generation = 0)
        => new(status.Name, null, EmptyFunctions, generation, status);
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
