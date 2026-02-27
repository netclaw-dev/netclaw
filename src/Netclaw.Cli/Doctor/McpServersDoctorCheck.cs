using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Doctor check that validates MCP server configuration entries.
/// Checks: required fields per transport type, valid transport values.
/// Non-blocking warning if no MCP servers configured.
/// </summary>
public sealed class McpServersDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.NetclawConfigPath))
            return Task.FromResult(DoctorCheckResult.Pass("mcp-servers", "No config file (skipped)"));

        Dictionary<string, McpServerEntry> servers;
        try
        {
            var text = File.ReadAllText(paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("McpServers", out var mcpSection))
                return Task.FromResult(DoctorCheckResult.Pass("mcp-servers",
                    "No MCP servers configured (use `netclaw mcp add` to add one)"));

            servers = JsonSerializer.Deserialize<Dictionary<string, McpServerEntry>>(mcpSection.GetRawText())
                ?? new();
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error("mcp-servers",
                $"Failed to read MCP config: {ex.Message}"));
        }

        if (servers.Count == 0)
            return Task.FromResult(DoctorCheckResult.Pass("mcp-servers",
                "No MCP servers configured"));

        var errors = new List<string>();

        foreach (var (name, entry) in servers)
        {
            if (entry.Transport is not ("stdio" or "sse" or "http"))
            {
                errors.Add($"{name}: invalid transport '{entry.Transport}' (must be stdio, sse, or http)");
                continue;
            }

            if (entry.Transport is "stdio" && string.IsNullOrWhiteSpace(entry.Command))
                errors.Add($"{name}: stdio transport requires 'Command'");

            if (entry.Transport is "sse" or "http" && string.IsNullOrWhiteSpace(entry.Url))
                errors.Add($"{name}: {entry.Transport} transport requires 'Url'");
        }

        if (errors.Count > 0)
            return Task.FromResult(DoctorCheckResult.Error("mcp-servers",
                $"MCP config issues: {string.Join("; ", errors)}",
                "Run `netclaw mcp list` and fix entries with `netclaw mcp remove` + `netclaw mcp add`"));

        var enabledCount = servers.Count(s => s.Value.Enabled);
        return Task.FromResult(DoctorCheckResult.Pass("mcp-servers",
            $"{servers.Count} server(s) configured ({enabledCount} enabled)"));
    }
}
