using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly McpOAuthService _oauthService;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpClientManager> _logger;

    private readonly ConcurrentDictionary<string, McpClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Dictionary<string, AIFunction>> _sharedToolFunctions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, bool> _sessionScopedServers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<ScopedClientHandle>>> _scopedClients =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _scopedCleanupGate = new(1, 1);
    private readonly TimeSpan _scopedClientIdleTimeout = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _scopedCleanupInterval = TimeSpan.FromMinutes(1);
    private long _nextScopedCleanupAtMs;

    public McpClientManager(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry toolRegistry,
        McpOAuthService oauthService,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        ILogger<McpClientManager> logger)
    {
        _serverEntries = serverEntries;
        _toolRegistry = toolRegistry;
        _oauthService = oauthService;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in _serverEntries)
        {
            if (!entry.Enabled)
            {
                _statuses[name] = new McpServerStatus(name, McpConnectionState.Disabled, 0, null);
                _sessionScopedServers.TryRemove(name, out _);
                _logger.LogInformation("MCP server '{Name}' is disabled, skipping", name);
                continue;
            }

            await ConnectAsync(name, entry, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, client) in _clients)
        {
            try
            {
                await client.DisposeAsync();
                _logger.LogInformation("MCP client '{Name}' shut down", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error shutting down MCP client '{Name}'", name);
            }
        }

        _clients.Clear();
        _sharedToolFunctions.Clear();
        _sessionScopedServers.Clear();

        await DisposeAllScopedClientsAsync();
    }

    public McpClient? GetClient(string serverName)
    {
        return _clients.GetValueOrDefault(serverName);
    }

    public IReadOnlyDictionary<string, McpServerStatus> GetServerStatuses() => _statuses;

    public async Task<bool> TryReconnectAsync(string serverName, CancellationToken ct = default)
    {
        if (!_serverEntries.TryGetValue(serverName, out var entry) || !entry.Enabled)
            return false;

        if (_clients.TryRemove(serverName, out var existing))
        {
            try { await existing.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client '{Name}' during reconnect", serverName); }
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
        if (UsesSessionScopedClient(serverName))
            return await InvokeScopedAsync(serverName, toolName, arguments, context, ct);

        return await InvokeSharedAsync(serverName, toolName, arguments, ct);
    }

    private async Task<string> InvokeSharedAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        if (!TryGetSharedFunction(serverName, toolName, out var function) || function is null)
        {
            var reconnected = await TryReconnectAsync(serverName, ct);
            if (!reconnected
                || !TryGetSharedFunction(serverName, toolName, out function)
                || function is null)
            {
                throw new InvalidOperationException(
                    $"MCP server '{serverName}' is unavailable or tool '{toolName}' is not registered.");
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
                toolName, serverName);

            var reconnected = await TryReconnectAsync(serverName, ct);
            if (!reconnected
                || !TryGetSharedFunction(serverName, toolName, out var retryFunction)
                || retryFunction is null)
                throw;

            return await InvokeFunctionAsync(retryFunction, arguments, ct);
        }
    }

    private async Task<string> InvokeScopedAsync(
        string serverName,
        string toolName,
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

            if (!handle.Tools.TryGetValue(toolName, out var function))
            {
                throw new InvalidOperationException(
                    $"MCP tool '{toolName}' is not available on server '{serverName}'.");
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

    private bool TryGetSharedFunction(string serverName, string toolName, out AIFunction? function)
    {
        function = null;

        if (!_sharedToolFunctions.TryGetValue(serverName, out var serverTools))
            return false;

        return serverTools.TryGetValue(toolName, out function);
    }

    private bool UsesSessionScopedClient(string serverName)
    {
        return _sessionScopedServers.TryGetValue(serverName, out var enabled) && enabled;
    }

    private async Task<ScopedClientHandle> GetOrCreateScopedClientHandleAsync(
        string serverName,
        string scopeId,
        CancellationToken ct)
    {
        var key = BuildScopedClientKey(serverName, scopeId);

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

    private async Task<ScopedClientHandle> CreateScopedClientHandleAsync(string serverName)
    {
        if (!_serverEntries.TryGetValue(serverName, out var entry))
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is not configured.");
        }

        var client = await CreateClientAsync(serverName, entry, CancellationToken.None, updateStatusOnAuthFailure: false);
        if (client is null)
            throw new InvalidOperationException($"MCP server '{serverName}' requires OAuth authorization.");

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var toolMap = CreateFunctionMap(tools);

        _logger.LogInformation(
            "Created scoped MCP client for server '{ServerName}' (tools={ToolCount})",
            serverName,
            tools.Count);

        return new ScopedClientHandle(client, toolMap, _timeProvider.GetUtcNow());
    }

    private async Task DisposeScopedClientsForServerAsync(string serverName)
    {
        var prefix = serverName + "::";

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

    private async Task<bool> ConnectAsync(string name, McpServerEntry entry, CancellationToken ct)
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

            _toolRegistry.WithMcpTools(name, tools, entry.GrantCategory, this);
            _statuses[name] = new McpServerStatus(name, McpConnectionState.Connected, tools.Count, null);

            _logger.LogInformation("MCP server '{Name}' connected ({ToolCount} tools)", name, tools.Count);
            return true;
        }
        catch (Exception ex)
        {
            _sharedToolFunctions.TryRemove(name, out _);
            _sessionScopedServers.TryRemove(name, out _);
            _statuses[name] = new McpServerStatus(name, McpConnectionState.Error, 0, ex.Message);
            _logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name);

            _notificationSink.Emit(new OperationalAlert
            {
                AlertId = Guid.NewGuid().ToString("N")[..12],
                Type = "mcp.server.disconnected",
                Category = AlertType.McpServerDisconnected,
                Summary = $"MCP server '{name}' connection failed: {ex.Message}",
                Timestamp = _timeProvider.GetUtcNow(),
                Severity = "warning",
                Source = name,
                Context = new Dictionary<string, string> { ["serverName"] = name }
            });

            return false;
        }
    }

    private async Task<McpClient?> CreateClientAsync(
        string name,
        McpServerEntry entry,
        CancellationToken ct,
        bool updateStatusOnAuthFailure)
    {
        Dictionary<string, string>? headers = entry.Headers is { Count: > 0 }
            ? new Dictionary<string, string>(entry.Headers)
            : null;

        if (entry.Transport is not "stdio")
        {
            var token = await _oauthService.GetValidTokenAsync(name, entry, ct);
            if (token is not null)
            {
                headers ??= new Dictionary<string, string>();
                headers["Authorization"] = $"Bearer {token}";
            }
            else if (updateStatusOnAuthFailure && entry.Url is not null)
            {
                var metadata = await _oauthService.TryDiscoverMetadataAsync(name, entry.Url, ct);
                if (metadata is not null)
                {
                    _statuses[name] = new McpServerStatus(name, McpConnectionState.AwaitingAuth, 0,
                        "OAuth required. Run: netclaw mcp auth " + name);
                    _logger.LogWarning("MCP server '{Name}' requires OAuth authorization", name);

                    _notificationSink.Emit(new OperationalAlert
                    {
                        AlertId = Guid.NewGuid().ToString("N")[..12],
                        Type = "mcp.auth.expired",
                        Category = AlertType.McpAuthExpired,
                        Summary = $"MCP server '{name}' requires OAuth authorization. Run: netclaw mcp auth {name}",
                        Timestamp = _timeProvider.GetUtcNow(),
                        Severity = "warning",
                        Source = name,
                        Context = new Dictionary<string, string> { ["serverName"] = name }
                    });

                    return null;
                }
            }
        }

        var transport = CreateTransport(name, entry, headers);

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new() { Name = "netclaw", Version = "0.1.0" },
        }, cancellationToken: ct);
    }

    private IClientTransport CreateTransport(
        string serverName,
        McpServerEntry entry,
        Dictionary<string, string>? headers)
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
                Name = serverName,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });
        }

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(entry.Url!),
            Name = serverName,
            AdditionalHeaders = headers,
            TransportMode = entry.Transport is "sse"
                ? HttpTransportMode.Sse
                : HttpTransportMode.AutoDetect,
        });
    }

    private static string[] BuildStdioArguments(string serverName, McpServerEntry entry)
    {
        var args = entry.Arguments is { Length: > 0 }
            ? entry.Arguments.ToList()
            : [];

        if (IsPlaywrightServer(serverName, entry)
            && !args.Contains("--isolated", StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--isolated");
        }

        return args.ToArray();
    }

    private static Dictionary<string, AIFunction> CreateFunctionMap(IList<McpClientTool> tools)
    {
        var map = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
            map[tool.Name] = tool;

        return map;
    }

    private static bool RequiresSessionScopedClient(string serverName, McpServerEntry entry)
        => IsPlaywrightServer(serverName, entry);

    private static bool IsPlaywrightServer(string serverName, McpServerEntry entry)
    {
        if (serverName.Equals(PlaywrightServerName, StringComparison.OrdinalIgnoreCase))
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
    Error,
    AwaitingAuth,
}

internal sealed record McpServerStatus(
    string Name,
    McpConnectionState State,
    int ToolCount,
    string? ErrorMessage);
