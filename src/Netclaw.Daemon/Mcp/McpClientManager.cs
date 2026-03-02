using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Manages MCP client connections at daemon startup. Creates clients for each enabled
/// <see cref="McpServerEntry"/>, discovers tools, and registers them in <see cref="ToolRegistry"/>.
/// Failed connections are logged but don't block startup.
/// </summary>
internal sealed class McpClientManager : IHostedService, IDisposable
{
    private readonly Dictionary<string, McpServerEntry> _serverEntries;
    private readonly ToolRegistry _toolRegistry;
    private readonly McpOAuthService _oauthService;
    private readonly ILogger<McpClientManager> _logger;
    private readonly Dictionary<string, McpClient> _clients = new();
    private readonly Dictionary<string, McpServerStatus> _statuses = new();

    public McpClientManager(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry toolRegistry,
        McpOAuthService oauthService,
        ILogger<McpClientManager> logger)
    {
        _serverEntries = serverEntries;
        _toolRegistry = toolRegistry;
        _oauthService = oauthService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in _serverEntries)
        {
            if (!entry.Enabled)
            {
                _statuses[name] = new McpServerStatus(name, McpConnectionState.Disabled, 0, null);
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

        // Dispose existing client if any
        if (_clients.TryGetValue(serverName, out var existing))
        {
            try { await existing.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client '{Name}' during reconnect", serverName); }
            _clients.Remove(serverName);
        }

        return await ConnectAsync(serverName, entry, ct);
    }

    private async Task<bool> ConnectAsync(string name, McpServerEntry entry, CancellationToken ct)
    {
        try
        {
            // For HTTP/SSE servers, check if we already have an OAuth token
            // (from a previous `netclaw mcp auth` run). If so, inject it.
            // If the server requires OAuth but we have no token, auto-detect
            // via well-known metadata and set AwaitingAuth.
            Dictionary<string, string>? headers = entry.Headers is { Count: > 0 }
                ? new Dictionary<string, string>(entry.Headers) : null;

            if (entry.Transport is not "stdio")
            {
                var token = await _oauthService.GetValidTokenAsync(name, entry, ct);
                if (token is not null)
                {
                    headers ??= new Dictionary<string, string>();
                    headers["Authorization"] = $"Bearer {token}";
                }
                else if (entry.Url is not null)
                {
                    // No token — check if this server requires OAuth
                    var metadata = await _oauthService.TryDiscoverMetadataAsync(name, entry.Url, ct);
                    if (metadata is not null)
                    {
                        _statuses[name] = new McpServerStatus(name, McpConnectionState.AwaitingAuth, 0,
                            "OAuth required. Run: netclaw mcp auth " + name);
                        _logger.LogWarning("MCP server '{Name}' requires OAuth authorization", name);
                        return false;
                    }
                }
            }

            IClientTransport transport;

            if (entry.Transport is "stdio")
            {
                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = entry.Command!,
                    Arguments = entry.Arguments ?? [],
                    EnvironmentVariables = entry.EnvironmentVariables is { Count: > 0 }
                        ? new Dictionary<string, string?>(entry.EnvironmentVariables!)
                        : null,
                    Name = name,
                    ShutdownTimeout = TimeSpan.FromSeconds(10),
                });
            }
            else
            {
                transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(entry.Url!),
                    Name = name,
                    AdditionalHeaders = headers,
                    TransportMode = entry.Transport is "sse"
                        ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
                });
            }

            var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new() { Name = "netclaw", Version = "0.1.0" },
            }, cancellationToken: ct);

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            _clients[name] = client;
            _toolRegistry.WithMcpTools(name, tools, entry.GrantCategory);
            _statuses[name] = new McpServerStatus(name, McpConnectionState.Connected, tools.Count, null);

            _logger.LogInformation("MCP server '{Name}' connected ({ToolCount} tools)", name, tools.Count);
            return true;
        }
        catch (Exception ex)
        {
            _statuses[name] = new McpServerStatus(name, McpConnectionState.Error, 0, ex.Message);
            _logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name);
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            try { (client as IDisposable)?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client during shutdown"); }
        }
        _clients.Clear();
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
