// -----------------------------------------------------------------------
// <copyright file="McpServersDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Net;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Doctor check that validates MCP server configuration entries and MCP health.
/// First pass: required fields per transport type, valid transport values.
/// Second pass: prefer daemon-reported runtime truth; fall back to explicit
/// offline connectivity checks when daemon status is unavailable.
/// </summary>
public sealed class McpServersDoctorCheck : IDoctorCheck
{
    private readonly NetclawPaths _paths;
    private readonly DaemonApi _daemonApi;
    private readonly Func<Netclaw.Tools.McpServerName, McpServerEntry, CancellationToken, Task<McpProbeResult>> _probeServer;

    public McpServersDoctorCheck(NetclawPaths paths, DaemonApi daemonApi)
        : this(paths, daemonApi, McpCommand.ProbeServerAsync)
    {
    }

    internal McpServersDoctorCheck(
        NetclawPaths paths,
        DaemonApi daemonApi,
        Func<Netclaw.Tools.McpServerName, McpServerEntry, CancellationToken, Task<McpProbeResult>> probeServer)
    {
        _paths = paths;
        _daemonApi = daemonApi;
        _probeServer = probeServer;
    }

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.NetclawConfigPath))
            return DoctorCheckResult.Pass("mcp-servers", "No config file (skipped)");

        Dictionary<string, McpServerEntry> servers;
        try
        {
            var text = File.ReadAllText(_paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("McpServers", out var mcpSection))
                return DoctorCheckResult.Pass("mcp-servers",
                    "No MCP servers configured (use `netclaw mcp add` to add one)");

            servers = JsonSerializer.Deserialize<Dictionary<string, McpServerEntry>>(mcpSection.GetRawText())
                ?? [];
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error("mcp-servers",
                $"Failed to read MCP config: {ex.Message}");
        }

        if (servers.Count == 0)
            return DoctorCheckResult.Pass("mcp-servers", "No MCP servers configured");

        var configErrors = new List<string>();
        var validServers = new Dictionary<string, McpServerEntry>();

        foreach (var (name, entry) in servers)
        {
            if (entry.Transport is not ("stdio" or "sse" or "http"))
            {
                configErrors.Add($"{name}: invalid transport '{entry.Transport}' (must be stdio, sse, or http)");
                continue;
            }

            if (entry.Transport is "stdio" && string.IsNullOrWhiteSpace(entry.Command))
            {
                configErrors.Add($"{name}: stdio transport requires 'Command'");
                continue;
            }

            if (entry.Transport is "sse" or "http" && string.IsNullOrWhiteSpace(entry.Url))
            {
                configErrors.Add($"{name}: {entry.Transport} transport requires 'Url'");
                continue;
            }

            validServers[name] = entry;
        }

        if (configErrors.Count > 0)
            return DoctorCheckResult.Error("mcp-servers",
                $"MCP config issues: {string.Join("; ", configErrors)}",
                "Run `netclaw mcp list` and fix entries with `netclaw mcp remove` + `netclaw mcp add`");

        JsonElement? daemonStatuses = null;
        string? daemonError = null;

        try
        {
            daemonStatuses = await _daemonApi.GetMcpServerStatusesAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            daemonError = $"could not reach daemon at {_daemonApi.Endpoint}";
        }
        catch (OperationCanceledException)
        {
            daemonError = $"daemon timed out at {_daemonApi.Endpoint}";
        }
        catch (Exception ex)
        {
            daemonError = $"daemon status request failed: {ex.Message}";
        }

        if (daemonStatuses is not null)
            return EvaluateDaemonStatuses(validServers, daemonStatuses.Value);

        return await EvaluateOfflineProbesAsync(validServers, daemonError ?? "daemon status unavailable", cancellationToken);
    }

    private async Task<DoctorCheckResult> EvaluateOfflineProbesAsync(
        IReadOnlyDictionary<string, McpServerEntry> validServers,
        string daemonError,
        CancellationToken cancellationToken)
    {
        var fullServers = McpCommand.LoadMcpServers(_paths);
        var statusMessages = new List<string>
        {
            $"daemon status unavailable: {daemonError}"
        };
        var hasConnectivityFailure = false;
        var hasUnverifiableAuth = false;
        var enabledCount = 0;
        var failedCount = 0;

        foreach (var (name, entry) in validServers)
        {
            if (entry.Enabled && name.Equals("browser_chrome_devtools", StringComparison.OrdinalIgnoreCase))
            {
                if (!BrowserAutomationRuntimeDetector.HasNodeRuntime())
                {
                    enabledCount++;
                    failedCount++;
                    hasConnectivityFailure = true;
                    statusMessages.Add($"{name}: unreachable — Node.js runtime (node+npx) not found");
                    continue;
                }

                var chrome = BrowserAutomationRuntimeDetector.DetectChrome();
                if (!chrome.IsInstalled)
                {
                    enabledCount++;
                    failedCount++;
                    hasConnectivityFailure = true;
                    statusMessages.Add($"{name}: unreachable — local Chrome executable not found");
                    continue;
                }
            }

            if (entry.Enabled && name.Equals("browser_playwright", StringComparison.OrdinalIgnoreCase))
            {
                if (!BrowserAutomationRuntimeDetector.HasNodeRuntime())
                {
                    enabledCount++;
                    failedCount++;
                    hasConnectivityFailure = true;
                    statusMessages.Add($"{name}: unreachable — Node.js runtime (node+npx) not found");
                    continue;
                }

                var browser = BrowserAutomationRuntimeDetector.GetPlaywrightBrowserFromArguments(entry.Arguments);
                if (!BrowserAutomationRuntimeDetector.HasPlaywrightBrowserRuntime(browser))
                {
                    enabledCount++;
                    failedCount++;
                    hasConnectivityFailure = true;
                    statusMessages.Add($"{name}: unreachable — Playwright {browser} runtime not installed");
                    continue;
                }
            }

            var probeEntry = fullServers.TryGetValue(name, out var full) ? full : entry;
            var probe = await _probeServer(new Netclaw.Tools.McpServerName(name), probeEntry, cancellationToken);

            switch (probe.Status)
            {
                case McpProbeStatus.Disabled:
                    statusMessages.Add($"{name}: disabled");
                    break;
                case McpProbeStatus.Connected:
                    enabledCount++;
                    statusMessages.Add($"{name}: offline check passed ({probe.ToolCount} tools)");
                    break;
                case McpProbeStatus.AuthFailed:
                    enabledCount++;
                    if (IsOAuthHttpServer(probeEntry))
                    {
                        hasUnverifiableAuth = true;
                        statusMessages.Add($"{name}: auth cannot be verified offline — daemon unavailable");
                    }
                    else
                    {
                        failedCount++;
                        hasConnectivityFailure = true;
                        statusMessages.Add($"{name}: {probe.FormatStatus()}");
                    }

                    break;
                case McpProbeStatus.AwaitingAuth:
                    enabledCount++;
                    hasUnverifiableAuth = true;
                    statusMessages.Add($"{name}: auth cannot be verified offline — daemon unavailable");
                    break;
                case McpProbeStatus.Unreachable:
                    enabledCount++;
                    failedCount++;
                    hasConnectivityFailure = true;
                    statusMessages.Add($"{name}: {probe.FormatStatus()}");
                    break;
            }
        }

        var summary = string.Join("; ", statusMessages);

        if (hasConnectivityFailure)
        {
            if (enabledCount > 0 && failedCount == enabledCount)
            {
                return DoctorCheckResult.Error("mcp-servers", summary,
                    "Restore daemon connectivity, then verify MCP server endpoints and local runtimes.");
            }

            return DoctorCheckResult.Warning("mcp-servers", summary,
                "Restore daemon connectivity for authoritative auth status; investigate unreachable endpoints or local runtimes.");
        }

        if (hasUnverifiableAuth)
        {
            return DoctorCheckResult.Warning("mcp-servers", summary,
                "Start the daemon to verify live MCP authentication state.");
        }

        return DoctorCheckResult.Pass("mcp-servers", summary);
    }

    private static DoctorCheckResult EvaluateDaemonStatuses(
        IReadOnlyDictionary<string, McpServerEntry> validServers,
        JsonElement daemonStatuses)
    {
        var statusMessages = new List<string>();
        var hasAuthFailure = false;
        var hasConnectivityFailure = false;
        var hasAwaitingAuth = false;
        var enabledCount = 0;
        var failedCount = 0;

        foreach (var (name, entry) in validServers)
        {
            if (!entry.Enabled)
            {
                statusMessages.Add($"{name}: disabled");
                continue;
            }

            enabledCount++;
            if (!daemonStatuses.TryGetProperty(name, out var statusEntry))
            {
                failedCount++;
                hasConnectivityFailure = true;
                statusMessages.Add($"{name}: status unavailable — restart daemon to load this config");
                continue;
            }

            var state = statusEntry.GetProperty("state").GetString();
            var error = statusEntry.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;
            var toolCount = statusEntry.TryGetProperty("toolCount", out var toolProp) ? toolProp.GetInt32() : 0;

            switch (state)
            {
                case "Connected":
                    statusMessages.Add($"{name}: connected ({toolCount} tools)");
                    break;
                case "AwaitingAuth":
                    hasAwaitingAuth = true;
                    statusMessages.Add($"{name}: awaiting auth — run: netclaw mcp auth {name}");
                    break;
                case "AuthFailed":
                    failedCount++;
                    hasAuthFailure = true;
                    statusMessages.Add($"{name}: auth failed ({error ?? "authentication rejected"})");
                    break;
                case "Unreachable":
                    failedCount++;
                    hasConnectivityFailure = true;
                    statusMessages.Add($"{name}: unreachable — {error ?? "connection failed"}");
                    break;
                case "Disabled":
                    statusMessages.Add($"{name}: disabled");
                    break;
                default:
                    failedCount++;
                    hasConnectivityFailure = true;
                    statusMessages.Add($"{name}: status unavailable — {state ?? "unknown"}");
                    break;
            }
        }

        var summary = string.Join("; ", statusMessages);

        if (hasAuthFailure)
            return DoctorCheckResult.Error("mcp-servers", summary,
                "Re-authorize affected MCP servers with `netclaw mcp auth <name>`.");

        if (hasConnectivityFailure)
        {
            if (enabledCount > 0 && failedCount == enabledCount)
            {
                return DoctorCheckResult.Error("mcp-servers", summary,
                    "Fix unreachable MCP endpoints or restart the daemon after configuration changes.");
            }

            return DoctorCheckResult.Warning("mcp-servers", summary,
                "Investigate unreachable MCP endpoints or restart the daemon after configuration changes.");
        }

        if (hasAwaitingAuth)
            return DoctorCheckResult.Warning("mcp-servers", summary,
                "Complete OAuth for MCP servers that are awaiting authorization.");

        return DoctorCheckResult.Pass("mcp-servers", summary);
    }

    private static bool IsOAuthHttpServer(McpServerEntry entry)
        => entry.Transport is "http" or "sse"
           && (!string.IsNullOrWhiteSpace(entry.OAuthClientId)
               || !string.IsNullOrWhiteSpace(entry.OAuthScope));
}
