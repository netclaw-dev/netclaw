// -----------------------------------------------------------------------
// <copyright file="McpClientManager.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed class McpClientManager : IHostedService, IDisposable, IMcpToolInvoker
{
    private const string PlaywrightServerName = "browser_playwright";

    private readonly Dictionary<string, McpServerEntry> _serverEntries;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolConfig _toolConfig;
    private readonly McpOAuthService _oauthService;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpClientManager> _logger;
    private readonly int _maxToolDescriptionChars;
    private readonly int _maxToolSchemaWarnChars;

    private readonly ConcurrentDictionary<McpServerName, McpClient> _clients = new();

    private readonly ConcurrentDictionary<McpServerName, Dictionary<string, AIFunction>> _sharedToolFunctions = new();

    private readonly ConcurrentDictionary<McpServerName, McpServerStatus> _statuses = new();

    private readonly ConcurrentDictionary<McpServerName, bool> _sessionScopedServers = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<ScopedClientHandle>>> _scopedClients =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _scopedCleanupGate = new(1, 1);
    private readonly TimeSpan _scopedClientIdleTimeout = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _scopedCleanupInterval = TimeSpan.FromMinutes(1);
    private long _nextScopedCleanupAtMs;

    public McpClientManager(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry toolRegistry,
        ToolConfig toolConfig,
        McpOAuthService oauthService,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        ILogger<McpClientManager> logger,
        SessionConfig? sessionConfig = null)
    {
        _serverEntries = serverEntries;
        _toolRegistry = toolRegistry;
        _toolConfig = toolConfig;
        _oauthService = oauthService;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _logger = logger;
        _maxToolDescriptionChars = sessionConfig?.Tuning.MaxToolDescriptionChars ?? 0;
        _maxToolSchemaWarnChars = sessionConfig?.Tuning.MaxToolSchemaWarnChars ?? 0;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in _serverEntries)
        {
            var serverName = new McpServerName(name);
            if (!entry.Enabled)
            {
                _statuses[serverName] = new McpServerStatus(serverName, McpConnectionState.Disabled, 0, null);
                _sessionScopedServers.TryRemove(serverName, out _);
                _logger.LogInformation("MCP server '{Name}' is disabled, skipping", name);
                continue;
            }

            await ConnectAsync(serverName, entry, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, client) in _clients)
        {
            try
            {
                await client.DisposeAsync();
                _logger.LogInformation("MCP client '{Name}' shut down", name.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error shutting down MCP client '{Name}'", name.Value);
            }
        }

        _clients.Clear();
        _sharedToolFunctions.Clear();
        _sessionScopedServers.Clear();

        await DisposeAllScopedClientsAsync();
    }

    public McpClient? GetClient(McpServerName serverName)
    {
        return _clients.GetValueOrDefault(serverName);
    }

    public IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses() => _statuses;

    /// <summary>
    /// Returns discovered tool names for a connected MCP server.
    /// </summary>
    public IReadOnlyList<string> GetToolNames(McpServerName serverName)
    {
        if (!_sharedToolFunctions.TryGetValue(serverName, out var tools))
            return [];

        return tools.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default)
    {
        if (!_serverEntries.TryGetValue(serverName.Value, out var entry) || !entry.Enabled)
            return false;

        if (_clients.TryRemove(serverName, out var existing))
        {
            try { await existing.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client '{Name}' during reconnect", serverName.Value); }
        }

        _sharedToolFunctions.TryRemove(serverName, out _);
        await DisposeScopedClientsForServerAsync(serverName);

        return await ConnectAsync(serverName, entry, ct);
    }

    public async Task<string> InvokeAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolExecutionContext? context,
        CancellationToken ct = default)
    {
        var server = new McpServerName(serverName);
        var tool = new ToolName(toolName);

        if (UsesSessionScopedClient(server))
            return await InvokeScopedAsync(server, tool, arguments, context, ct);

        return await InvokeSharedAsync(server, tool, arguments, ct);
    }

    private async Task<string> InvokeSharedAsync(
        McpServerName serverName,
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        if (!TryGetSharedFunction(serverName, toolName.Value, out var function) || function is null)
        {
            var reconnected = await TryReconnectAsync(serverName, ct);
            if (!reconnected
                || !TryGetSharedFunction(serverName, toolName.Value, out function)
                || function is null)
            {
                throw new InvalidOperationException(
                    $"MCP server '{serverName.Value}' is unavailable or tool '{toolName.Value}' is not registered.");
            }
        }

        try
        {
            return await InvokeFunctionAsync(function, arguments, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "MCP tool '{ToolName}' failed on shared client '{ServerName}', attempting reconnect",
                toolName.Value, serverName.Value);

            var reconnected = await TryReconnectAsync(serverName, ct);
            if (!reconnected
                || !TryGetSharedFunction(serverName, toolName.Value, out var retryFunction)
                || retryFunction is null)
                throw;

            return await InvokeFunctionAsync(retryFunction, arguments, ct);
        }
    }

    private async Task<string> InvokeScopedAsync(
        McpServerName serverName,
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        ToolExecutionContext? context,
        CancellationToken ct)
    {
        var scopeId = ResolveScopeId(context);
        var handle = await GetOrCreateScopedClientHandleAsync(serverName, scopeId, ct);

        await CleanupIdleScopedClientsIfDueAsync(ct);
        await handle.ExecutionGate.WaitAsync(ct);

        try
        {
            handle.Touch(_timeProvider.GetUtcNow());

            if (!handle.Tools.TryGetValue(toolName.Value, out var function))
            {
                throw new InvalidOperationException(
                    $"MCP tool '{toolName.Value}' is not available on server '{serverName.Value}'.");
            }

            return await InvokeFunctionAsync(function, arguments, ct);
        }
        finally
        {
            handle.Touch(_timeProvider.GetUtcNow());
            handle.ExecutionGate.Release();
        }
    }

    private static async Task<string> InvokeFunctionAsync(
        AIFunction function,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        var aiArgs = arguments is { Count: > 0 }
            ? new AIFunctionArguments(arguments)
            : null;

        var result = await function.InvokeAsync(aiArgs, ct);
        return result?.ToString() ?? "";
    }

    private bool TryGetSharedFunction(McpServerName serverName, string toolName, out AIFunction? function)
    {
        function = null;

        if (!_sharedToolFunctions.TryGetValue(serverName, out var serverTools))
            return false;

        return serverTools.TryGetValue(toolName, out function);
    }

    private bool UsesSessionScopedClient(McpServerName serverName)
    {
        return _sessionScopedServers.TryGetValue(serverName, out var enabled) && enabled;
    }

    private async Task<ScopedClientHandle> GetOrCreateScopedClientHandleAsync(
        McpServerName serverName,
        string scopeId,
        CancellationToken ct)
    {
        var key = BuildScopedClientKey(serverName.Value, scopeId);

        while (true)
        {
            var lazy = _scopedClients.GetOrAdd(key, _ =>
                new Lazy<Task<ScopedClientHandle>>(
                    () => CreateScopedClientHandleAsync(serverName),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                var handle = await lazy.Value.WaitAsync(ct);
                handle.Touch(_timeProvider.GetUtcNow());
                return handle;
            }
            catch
            {
                _scopedClients.TryRemove(new KeyValuePair<string, Lazy<Task<ScopedClientHandle>>>(key, lazy));
                await DisposeLazyScopedHandleAsync(lazy);
                throw;
            }
        }
    }

    private async Task<ScopedClientHandle> CreateScopedClientHandleAsync(McpServerName serverName)
    {
        if (!_serverEntries.TryGetValue(serverName.Value, out var entry))
        {
            throw new InvalidOperationException($"MCP server '{serverName.Value}' is not configured.");
        }

        var client = await CreateClientAsync(serverName, entry, CancellationToken.None, updateStatusOnAuthFailure: false);
        if (client is null)
            throw new InvalidOperationException($"MCP server '{serverName.Value}' requires OAuth authorization.");

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var toolMap = CreateFunctionMap(tools);

        _logger.LogInformation(
            "Created scoped MCP client for server '{ServerName}' (tools={ToolCount})",
            serverName.Value,
            tools.Count);

        return new ScopedClientHandle(client, toolMap, _timeProvider.GetUtcNow());
    }

    private async Task DisposeScopedClientsForServerAsync(McpServerName serverName)
    {
        var prefix = serverName.Value + "::";

        foreach (var (key, _) in _scopedClients.ToArray())
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_scopedClients.TryRemove(key, out var lazy))
                await DisposeLazyScopedHandleAsync(lazy);
        }
    }

    private async Task DisposeAllScopedClientsAsync()
    {
        foreach (var (key, _) in _scopedClients.ToArray())
        {
            if (_scopedClients.TryRemove(key, out var lazy))
                await DisposeLazyScopedHandleAsync(lazy);
        }
    }

    private async Task DisposeLazyScopedHandleAsync(Lazy<Task<ScopedClientHandle>> lazy)
    {
        if (!lazy.IsValueCreated)
            return;

        try
        {
            var handle = await lazy.Value;
            await handle.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing scoped MCP client handle");
        }
    }

    private async Task CleanupIdleScopedClientsIfDueAsync(CancellationToken ct)
    {
        if (_scopedClients.IsEmpty)
            return;

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (nowMs < Volatile.Read(ref _nextScopedCleanupAtMs))
            return;

        if (!await _scopedCleanupGate.WaitAsync(0, ct))
            return;

        try
        {
            nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (nowMs < Volatile.Read(ref _nextScopedCleanupAtMs))
                return;

            Volatile.Write(
                ref _nextScopedCleanupAtMs,
                nowMs + (long)_scopedCleanupInterval.TotalMilliseconds);

            var idleBeforeMs = nowMs - (long)_scopedClientIdleTimeout.TotalMilliseconds;

            foreach (var (key, lazy) in _scopedClients.ToArray())
            {
                if (!lazy.IsValueCreated)
                    continue;

                Task<ScopedClientHandle> handleTask;
                try
                {
                    handleTask = lazy.Value;
                }
                catch
                {
                    continue;
                }

                if (!handleTask.IsCompletedSuccessfully)
                    continue;

                var handle = handleTask.Result;
                if (handle.LastUsedAtMs > idleBeforeMs)
                    continue;

                if (handle.ExecutionGate.CurrentCount == 0)
                    continue;

                if (_scopedClients.TryRemove(new KeyValuePair<string, Lazy<Task<ScopedClientHandle>>>(key, lazy)))
                    await handle.DisposeAsync();
            }
        }
        finally
        {
            _scopedCleanupGate.Release();
        }
    }

    private async Task<bool> ConnectAsync(McpServerName name, McpServerEntry entry, CancellationToken ct)
    {
        try
        {
            var client = await CreateClientAsync(name, entry, ct, updateStatusOnAuthFailure: true);
            if (client is null)
                return false;

            var tools = await client.ListToolsAsync(cancellationToken: ct);

            _clients[name] = client;
            _sharedToolFunctions[name] = CreateFunctionMap(tools);
            _sessionScopedServers[name] = RequiresSessionScopedClient(name, entry);

            _toolRegistry.WithMcpTools(name.Value, tools, entry.GrantCategory, this,
                _maxToolDescriptionChars, _maxToolSchemaWarnChars, _logger);
            _statuses[name] = new McpServerStatus(name, McpConnectionState.Connected, tools.Count, null);

            LogToolDrift(name, tools);

            _logger.LogInformation("MCP server '{Name}' connected ({ToolCount} tools)", name.Value, tools.Count);
            return true;
        }
        catch (Exception ex)
        {
            _sharedToolFunctions.TryRemove(name, out _);
            _sessionScopedServers.TryRemove(name, out _);

            var hasCachedTokens = _oauthService.GetTokenSet(name) is not null;
            var hasOAuthRuntimeHints = HasOAuthRuntimeHints(name, entry);
            var failureStatus = BuildConnectionFailureStatus(name, entry, ex, hasCachedTokens, hasOAuthRuntimeHints);
            _statuses[name] = failureStatus;

            if (failureStatus.State is McpConnectionState.AwaitingAuth)
            {
                _logger.LogWarning(ex, "MCP server '{Name}' requires OAuth authorization", name.Value);
                EmitAuthAlert(name, $"MCP server '{name.Value}' requires OAuth authorization. Run: netclaw mcp auth {name.Value}", "authorization_required");
            }
            else if (failureStatus.State is McpConnectionState.AuthFailed)
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
                    EmitDisconnectedAlert(name, $"MCP server '{name.Value}' authentication failed: {failureStatus.ErrorMessage}");
                }
            }
            else
            {
                _logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name.Value);
                EmitDisconnectedAlert(name, $"MCP server '{name.Value}' connection failed: {failureStatus.ErrorMessage}");
            }

            return false;
        }
    }

    private async Task<McpClient?> CreateClientAsync(
        McpServerName name,
        McpServerEntry entry,
        CancellationToken ct,
        bool updateStatusOnAuthFailure)
    {
        // For HTTP transports without cached tokens, check if OAuth is needed
        // before attempting a connection that would fail with 401.
        if (entry.Transport is not "stdio" && updateStatusOnAuthFailure && entry.Url is not null)
        {
            var hasTokens = _oauthService.GetTokenSet(name) is not null;
            if (!hasTokens)
            {
                var metadata = await _oauthService.TryDiscoverMetadataAsync(name, entry.Url, ct);
                if (metadata is not null)
                {
                    _statuses[name] = CreateAwaitingAuthStatus(name);
                    _logger.LogWarning("MCP server '{Name}' requires OAuth authorization", name.Value);
                    EmitAuthAlert(name, $"MCP server '{name.Value}' requires OAuth authorization. Run: netclaw mcp auth {name.Value}", "authorization_required");

                    return null;
                }
            }
        }

        var transport = CreateTransport(name, entry);

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new() { Name = "netclaw", Version = "0.1.0" },
        }, cancellationToken: ct);
    }

    private IClientTransport CreateTransport(McpServerName serverName, McpServerEntry entry)
    {
        if (entry.Transport is "stdio")
        {
            var args = BuildStdioArguments(serverName, entry);

            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = entry.Command!,
                Arguments = args,
                EnvironmentVariables = entry.EnvironmentVariables is { Count: > 0 }
                    ? entry.EnvironmentVariables.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (string?)kvp.Value,
                        StringComparer.OrdinalIgnoreCase)
                    : null,
                Name = serverName.Value,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });
        }

        Dictionary<string, string>? headers = entry.Headers is { Count: > 0 }
            ? new Dictionary<string, string>(entry.Headers)
            : null;

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(entry.Url!),
            Name = serverName.Value,
            AdditionalHeaders = headers,
            TransportMode = entry.Transport is "sse"
                ? HttpTransportMode.Sse
                : HttpTransportMode.AutoDetect,
            OAuth = BuildOAuthOptions(serverName, entry),
        });
    }

    private ClientOAuthOptions? BuildOAuthOptions(McpServerName serverName, McpServerEntry entry)
    {
        var metadata = _oauthService.GetCachedMetadata(serverName);

        // Only wire OAuth if server is known to need it (has metadata or static config)
        if (metadata is null && string.IsNullOrWhiteSpace(entry.OAuthClientId))
            return null;

        var serverNameCapture = serverName;
        return new ClientOAuthOptions
        {
            RedirectUri = new Uri("http://127.0.0.1:5199/api/mcp/oauth/callback"),
            ClientId = metadata?.ClientId ?? entry.OAuthClientId,
            Scopes = ParseScopes(entry.OAuthScope),
            TokenCache = _oauthService.CreateTokenCache(serverName),
            // Return null to suppress the SDK's default browser-open behavior;
            // Netclaw handles interactive auth via `netclaw mcp auth`.
            AuthorizationRedirectDelegate = static (_, _, _) => Task.FromResult<string?>(null),
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "netclaw",
                ResponseDelegate = (response, _) =>
                {
                    _oauthService.UpdateMetadataClientId(serverNameCapture, response.ClientId);
                    return Task.CompletedTask;
                },
            },
        };
    }

    private bool HasOAuthRuntimeHints(McpServerName serverName, McpServerEntry entry)
        => !string.IsNullOrWhiteSpace(entry.OAuthClientId)
           || !string.IsNullOrWhiteSpace(entry.OAuthScope)
           || _oauthService.GetCachedMetadata(serverName) is not null;

    internal static McpServerStatus BuildConnectionFailureStatus(
        McpServerName serverName,
        McpServerEntry entry,
        Exception ex,
        bool hasCachedTokens,
        bool hasOAuthRuntimeHints)
    {
        if (IsAuthFailure(ex))
        {
            if (!hasCachedTokens && entry.Transport is not "stdio" && hasOAuthRuntimeHints)
                return CreateAwaitingAuthStatus(serverName);

            return CreateAuthFailedStatus(serverName, ex, oauthManaged: hasCachedTokens || hasOAuthRuntimeHints);
        }

        return CreateUnreachableStatus(serverName, ex);
    }

    internal static McpServerStatus CreateAwaitingAuthStatus(McpServerName serverName)
        => new(serverName, McpConnectionState.AwaitingAuth, 0,
            $"OAuth authorization required. Run: netclaw mcp auth {serverName.Value}");

    internal static McpServerStatus CreateAuthFailedStatus(McpServerName serverName, Exception ex, bool oauthManaged)
    {
        var statusText = GetHttpStatusText(ex);
        var detail = string.IsNullOrWhiteSpace(statusText)
            ? "Authentication rejected by server."
            : $"Authentication rejected by server ({statusText}).";
        var guidance = oauthManaged
            ? $" Run: netclaw mcp auth {serverName.Value}"
            : " Check configured credentials or headers.";
        return new(serverName, McpConnectionState.AuthFailed, 0, detail + guidance);
    }

    internal static McpServerStatus CreateUnreachableStatus(McpServerName serverName, Exception ex)
        => new(serverName, McpConnectionState.Unreachable, 0,
            string.IsNullOrWhiteSpace(ex.Message) ? "Failed to reach MCP server." : ex.Message);

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
        // HttpRequestException with 401/403
        if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
            return true;

        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check inner exceptions for auth failures
        if (ex.InnerException is not null)
            return IsAuthFailure(ex.InnerException);

        return false;
    }

    private static string? GetHttpStatusText(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: var statusCode } && statusCode is not null)
        {
            return statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "401 Unauthorized",
                System.Net.HttpStatusCode.Forbidden => "403 Forbidden",
                _ => $"{(int)statusCode} {statusCode}"
            };
        }

        if (ex.InnerException is not null)
            return GetHttpStatusText(ex.InnerException);

        return null;
    }

    private static string[] BuildStdioArguments(McpServerName serverName, McpServerEntry entry)
    {
        var args = entry.Arguments is { Length: > 0 }
            ? entry.Arguments.ToList()
            : [];

        if (IsPlaywrightServer(serverName, entry)
            && !args.Contains("--isolated", StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--isolated");
        }

        return [.. args];
    }

    private static Dictionary<string, AIFunction> CreateFunctionMap(IList<McpClientTool> tools)
    {
        var map = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
            map[tool.Name] = tool;

        return map;
    }

    private static bool RequiresSessionScopedClient(McpServerName serverName, McpServerEntry entry)
        => IsPlaywrightServer(serverName, entry);

    private static bool IsPlaywrightServer(McpServerName serverName, McpServerEntry entry)
    {
        if (serverName.Value.Equals(PlaywrightServerName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(entry.Command)
            && entry.Command.Contains("playwright", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (entry.Arguments is not { Length: > 0 })
            return false;

        foreach (var arg in entry.Arguments)
        {
            if (arg.Contains("@playwright/mcp", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("playwright/mcp", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("playwright-mcp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string ResolveScopeId(ToolExecutionContext? context)
    {
        if (!string.IsNullOrWhiteSpace(context?.SessionId))
            return context.SessionId!;

        return $"sessionless/{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";
    }

    private static string BuildScopedClientKey(string serverName, string scopeId)
        => $"{serverName}::{scopeId}";

    /// <summary>
    /// Compares discovered tools against <see cref="ToolAudienceProfile.McpServerToolGrants"/>
    /// across all audience profiles and logs warnings for drift.
    /// </summary>
    private void LogToolDrift(McpServerName serverName, IList<McpClientTool> discoveredTools)
    {
        var profiles = _toolConfig.AudienceProfiles;
        var allGrantedTools = new HashSet<string>(StringComparer.Ordinal);
        var hasAnyGrants = false;

        foreach (var profile in profiles.GetAllProfiles())
        {
            if (profile.McpServerToolGrants is not { } grants)
                continue;

            if (!grants.TryGetValue(serverName.Value, out var tools))
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
        foreach (var client in _clients.Values)
        {
            try { (client as IDisposable)?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client during shutdown"); }
        }

        foreach (var lazy in _scopedClients.Values)
        {
            if (!lazy.IsValueCreated)
                continue;

            try
            {
                var task = lazy.Value;
                if (!task.IsCompletedSuccessfully)
                    continue;

                task.Result.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing scoped MCP client during shutdown");
            }
        }

        _clients.Clear();
        _sharedToolFunctions.Clear();
        _scopedClients.Clear();
        _sessionScopedServers.Clear();
    }

    private sealed class ScopedClientHandle : IAsyncDisposable, IDisposable
    {
        private int _disposed;

        public ScopedClientHandle(
            McpClient client,
            Dictionary<string, AIFunction> tools,
            DateTimeOffset createdAt)
        {
            Client = client;
            Tools = tools;
            Touch(createdAt);
        }

        public McpClient Client { get; }
        public Dictionary<string, AIFunction> Tools { get; }
        public SemaphoreSlim ExecutionGate { get; } = new(1, 1);

        private long _lastUsedAtMs;

        public long LastUsedAtMs => Volatile.Read(ref _lastUsedAtMs);

        public void Touch(DateTimeOffset now)
        {
            Volatile.Write(ref _lastUsedAtMs, now.ToUnixTimeMilliseconds());
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            ExecutionGate.Dispose();
            await Client.DisposeAsync();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            ExecutionGate.Dispose();
            (Client as IDisposable)?.Dispose();
        }
    }
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
    string? ErrorMessage);
