using System.Text.Json;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Doctor check that validates MCP server configuration entries and connectivity.
/// First pass: required fields per transport type, valid transport values.
/// Second pass: probe enabled servers for connectivity.
/// </summary>
public sealed class McpServersDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.NetclawConfigPath))
            return DoctorCheckResult.Pass("mcp-servers", "No config file (skipped)");

        Dictionary<string, McpServerEntry> servers;
        try
        {
            var text = File.ReadAllText(paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("McpServers", out var mcpSection))
                return DoctorCheckResult.Pass("mcp-servers",
                    "No MCP servers configured (use `netclaw mcp add` to add one)");

            servers = JsonSerializer.Deserialize<Dictionary<string, McpServerEntry>>(mcpSection.GetRawText())
                ?? new();
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error("mcp-servers",
                $"Failed to read MCP config: {ex.Message}");
        }

        if (servers.Count == 0)
            return DoctorCheckResult.Pass("mcp-servers",
                "No MCP servers configured");

        // First pass: static config validation (fail fast on bad config)
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

        // Second pass: connectivity probe for enabled servers with valid config
        // Merge secrets so probes have auth headers / env vars
        var fullServers = McpCommand.LoadMcpServers(paths);
        var statusMessages = new List<string>();
        var hasAuthFailure = false;
        var enabledCount = 0;
        var connectedCount = 0;
        var failedCount = 0;

        foreach (var (name, entry) in validServers)
        {
            if (entry.Enabled && name.Equals("browser_chrome_devtools", StringComparison.OrdinalIgnoreCase))
            {
                if (!BrowserAutomationRuntimeDetector.HasNodeRuntime())
                {
                    enabledCount++;
                    failedCount++;
                    statusMessages.Add($"{name}: unreachable — Node.js runtime (node+npx) not found");
                    continue;
                }

                var chrome = BrowserAutomationRuntimeDetector.DetectChrome();
                if (!chrome.IsInstalled)
                {
                    enabledCount++;
                    failedCount++;
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
                    statusMessages.Add($"{name}: unreachable — Node.js runtime (node+npx) not found");
                    continue;
                }

                var browser = BrowserAutomationRuntimeDetector.GetPlaywrightBrowserFromArguments(entry.Arguments);
                if (!BrowserAutomationRuntimeDetector.HasPlaywrightBrowserRuntime(browser))
                {
                    enabledCount++;
                    failedCount++;
                    statusMessages.Add($"{name}: unreachable — Playwright {browser} runtime not installed");
                    continue;
                }
            }

            // Use full entry (with secrets merged) if available
            var probeEntry = fullServers.TryGetValue(name, out var full) ? full : entry;
            var probe = await McpCommand.ProbeServerAsync(name, probeEntry, cancellationToken);

            switch (probe.Status)
            {
                case McpProbeStatus.Disabled:
                    statusMessages.Add($"{name}: disabled");
                    break;
                case McpProbeStatus.Connected:
                    enabledCount++;
                    connectedCount++;
                    statusMessages.Add($"{name}: {probe.FormatStatus()}");
                    break;
                case McpProbeStatus.AuthFailed:
                    enabledCount++;
                    failedCount++;
                    hasAuthFailure = true;
                    statusMessages.Add($"{name}: {probe.FormatStatus()}");
                    break;
                case McpProbeStatus.Unreachable:
                    enabledCount++;
                    failedCount++;
                    statusMessages.Add($"{name}: {probe.FormatStatus()}");
                    break;
            }
        }

        var summary = string.Join("; ", statusMessages);

        // Auth failures are always Error severity (won't self-resolve)
        if (hasAuthFailure)
            return DoctorCheckResult.Error("mcp-servers", summary,
                "Check API keys and credentials for failing servers");

        // All enabled servers failed
        if (enabledCount > 0 && failedCount == enabledCount)
            return DoctorCheckResult.Error("mcp-servers", summary,
                "No MCP servers are reachable — check network and server configuration");

        // Some enabled servers failed
        if (failedCount > 0)
            return DoctorCheckResult.Warning("mcp-servers", summary,
                "Some MCP servers are unreachable — check network and server configuration");

        // All good
        return DoctorCheckResult.Pass("mcp-servers", summary);
    }
}
