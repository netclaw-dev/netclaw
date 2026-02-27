using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using Netclaw.Configuration;

namespace Netclaw.Cli.Mcp;

internal enum McpProbeStatus { Connected, AuthFailed, Unreachable, Disabled }

internal readonly record struct McpProbeResult(
    McpProbeStatus Status, int ToolCount, string? ErrorMessage)
{
    public string FormatStatus() => Status switch
    {
        McpProbeStatus.Connected => $"connected ({ToolCount} tools)",
        McpProbeStatus.AuthFailed => $"auth failed ({ErrorMessage})",
        McpProbeStatus.Unreachable => $"unreachable — {ErrorMessage}",
        McpProbeStatus.Disabled => "disabled",
        _ => "unknown"
    };
}

/// <summary>
/// Handles <c>netclaw mcp</c> CLI subcommands: add, list, get, remove, enable, disable.
/// </summary>
internal static class McpCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, NetclawPaths paths)
    {
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "add" => RunAdd(args, paths),
            "list" => await RunListAsync(paths),
            "get" => RunGet(args, paths),
            "remove" => RunRemove(args, paths),
            "enable" => RunToggle(args, paths, enabled: true),
            "disable" => RunToggle(args, paths, enabled: false),
            "help" or "-h" or "--help" => WriteHelp(),
            _ => WriteHelp()
        };
    }

    private static int RunAdd(string[] args, NetclawPaths paths)
    {
        // Parse: netclaw mcp add [--transport <type>] [--env KEY=VALUE]... [--header "Key: Value"]... <name> [command/url] [-- args...]
        string? transport = null;
        var envVars = new Dictionary<string, string>();
        var headers = new Dictionary<string, string>();
        string? name = null;
        string? commandOrUrl = null;
        string[]? commandArgs = null;

        var positional = new List<string>();
        var afterDash = false;
        var dashArgs = new List<string>();

        for (var i = 2; i < args.Length; i++)
        {
            if (afterDash)
            {
                dashArgs.Add(args[i]);
                continue;
            }

            if (args[i] == "--")
            {
                afterDash = true;
                continue;
            }

            if (args[i] is "--transport" or "-t" && i + 1 < args.Length)
            {
                transport = args[++i];
                continue;
            }

            if (args[i] == "--env" && i + 1 < args.Length)
            {
                var kv = args[++i];
                var eqIdx = kv.IndexOf('=');
                if (eqIdx > 0)
                    envVars[kv[..eqIdx]] = kv[(eqIdx + 1)..];
                continue;
            }

            if (args[i] == "--header" && i + 1 < args.Length)
            {
                var hv = args[++i];
                var colonIdx = hv.IndexOf(':');
                if (colonIdx > 0)
                    headers[hv[..colonIdx].Trim()] = hv[(colonIdx + 1)..].Trim();
                continue;
            }

            positional.Add(args[i]);
        }

        if (positional.Count < 1)
        {
            Console.WriteLine("Usage: netclaw mcp add [--transport stdio|http|sse] [--env KEY=VALUE] <name> [command|url] [-- args...]");
            return 1;
        }

        name = positional[0];
        transport ??= "stdio";

        if (transport is "stdio")
        {
            if (afterDash && dashArgs.Count > 0)
            {
                // netclaw mcp add --transport stdio memorizer -- npx -y @memorizer/mcp-server
                commandOrUrl = dashArgs[0];
                commandArgs = dashArgs.Skip(1).ToArray();
            }
            else if (positional.Count >= 2)
            {
                // netclaw mcp add --transport stdio memorizer npx
                commandOrUrl = positional[1];
                commandArgs = positional.Skip(2).ToArray();
            }

            if (string.IsNullOrWhiteSpace(commandOrUrl))
            {
                Console.WriteLine("Error: stdio transport requires a command. Usage: netclaw mcp add --transport stdio <name> -- <command> [args...]");
                return 1;
            }
        }
        else
        {
            // http/sse — positional[1] is the URL
            if (positional.Count >= 2)
                commandOrUrl = positional[1];

            if (string.IsNullOrWhiteSpace(commandOrUrl))
            {
                Console.WriteLine($"Error: {transport} transport requires a URL. Usage: netclaw mcp add --transport {transport} <name> <url>");
                return 1;
            }
        }

        // Build entry for netclaw.json (non-secret parts)
        var entry = new McpServerEntry
        {
            Transport = transport,
            Enabled = true
        };

        if (transport is "stdio")
        {
            entry.Command = commandOrUrl;
            entry.Arguments = commandArgs is { Length: > 0 } ? commandArgs : null;
        }
        else
        {
            entry.Url = commandOrUrl;
        }

        // Non-sensitive env vars go to netclaw.json; all env vars also go to secrets.json for security
        // Headers always go to secrets.json (they may contain auth tokens)
        var (config, secrets) = LoadConfigFiles(paths);

        var mcpServers = GetOrCreateSection(config, "McpServers");
        mcpServers[name] = SerializeEntry(entry);

        WriteConfigFile(paths.NetclawConfigPath, config);

        // Write sensitive values to secrets.json
        if (envVars.Count > 0 || headers.Count > 0)
        {
            var secretMcp = GetOrCreateSection(secrets, "McpServers");
            var serverSecrets = new Dictionary<string, object>();

            if (envVars.Count > 0)
                serverSecrets["EnvironmentVariables"] = envVars;
            if (headers.Count > 0)
                serverSecrets["Headers"] = headers;

            secretMcp[name] = JsonSerializer.SerializeToElement(serverSecrets);
            WriteConfigFile(paths.SecretsPath, secrets);
        }

        Console.WriteLine($"Added MCP server '{name}' ({transport})");
        return 0;
    }

    private static async Task<int> RunListAsync(NetclawPaths paths)
    {
        var servers = LoadMcpServers(paths);

        if (servers.Count == 0)
        {
            Console.WriteLine("No MCP servers configured.");
            Console.WriteLine("Run `netclaw mcp add` to add one.");
            return 0;
        }

        Console.WriteLine($"{"Name",-20} {"Transport",-10} {"Enabled",-8} {"Status"}");
        foreach (var (name, entry) in servers)
        {
            var enabled = entry.Enabled ? "yes" : "no";
            var probe = await ProbeServerAsync(name, entry);
            Console.WriteLine($"{name,-20} {entry.Transport,-10} {enabled,-8} {probe.FormatStatus()}");
        }

        return 0;
    }

    private static int RunGet(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: netclaw mcp get <name>");
            return 1;
        }

        var name = args[2];
        var servers = LoadMcpServers(paths);

        if (!servers.TryGetValue(name, out var entry))
        {
            Console.WriteLine($"MCP server '{name}' not found.");
            return 1;
        }

        Console.WriteLine($"Name:       {name}");
        Console.WriteLine($"Transport:  {entry.Transport}");

        if (entry.Command is not null)
        {
            var cmdLine = entry.Arguments is { Length: > 0 }
                ? $"{entry.Command} {string.Join(' ', entry.Arguments)}"
                : entry.Command;
            Console.WriteLine($"Command:    {cmdLine}");
        }

        if (entry.Url is not null)
            Console.WriteLine($"URL:        {entry.Url}");

        Console.WriteLine($"Enabled:    {(entry.Enabled ? "yes" : "no")}");

        if (entry.EnvironmentVariables is { Count: > 0 })
        {
            Console.WriteLine("Env vars:");
            foreach (var (k, _) in entry.EnvironmentVariables)
                Console.WriteLine($"  {k}=***REDACTED***");
        }

        if (entry.Headers is { Count: > 0 })
        {
            Console.WriteLine("Headers:");
            foreach (var (k, _) in entry.Headers)
                Console.WriteLine($"  {k}: ***REDACTED***");
        }

        return 0;
    }

    private static int RunRemove(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: netclaw mcp remove <name>");
            return 1;
        }

        var name = args[2];
        var (config, secrets) = LoadConfigFiles(paths);

        var removed = false;
        var mcpServers = GetSectionOrNull(config, "McpServers");
        if (mcpServers?.Remove(name) == true)
        {
            WriteConfigFile(paths.NetclawConfigPath, config);
            removed = true;
        }

        var secretMcp = GetSectionOrNull(secrets, "McpServers");
        if (secretMcp?.Remove(name) == true)
        {
            WriteConfigFile(paths.SecretsPath, secrets);
            removed = true;
        }

        if (removed)
        {
            Console.WriteLine($"Removed MCP server '{name}'");
            return 0;
        }

        Console.WriteLine($"MCP server '{name}' not found.");
        return 1;
    }

    private static int RunToggle(string[] args, NetclawPaths paths, bool enabled)
    {
        if (args.Length < 3)
        {
            Console.WriteLine($"Usage: netclaw mcp {(enabled ? "enable" : "disable")} <name>");
            return 1;
        }

        var name = args[2];
        var (config, _) = LoadConfigFiles(paths);

        var mcpServers = GetSectionOrNull(config, "McpServers");
        if (mcpServers is null || !mcpServers.ContainsKey(name))
        {
            Console.WriteLine($"MCP server '{name}' not found.");
            return 1;
        }

        // Deserialize, toggle, re-serialize
        var entry = JsonSerializer.Deserialize<McpServerEntry>(
            JsonSerializer.Serialize(mcpServers[name]), JsonOptions) ?? new McpServerEntry();
        entry.Enabled = enabled;
        mcpServers[name] = SerializeEntry(entry);

        WriteConfigFile(paths.NetclawConfigPath, config);
        Console.WriteLine($"{(enabled ? "Enabled" : "Disabled")} MCP server '{name}'");
        return 0;
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<McpProbeResult> ProbeServerAsync(
        string name, McpServerEntry entry, CancellationToken ct = default)
    {
        if (!entry.Enabled)
            return new McpProbeResult(McpProbeStatus.Disabled, 0, null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            await using var client = await CreateOneOffClientAsync(name, entry);
            var tools = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);
            return new McpProbeResult(McpProbeStatus.Connected, tools.Count, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            var statusText = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "401 Unauthorized",
                System.Net.HttpStatusCode.Forbidden => "403 Forbidden",
                _ => ex.StatusCode.ToString()
            };
            return new McpProbeResult(McpProbeStatus.AuthFailed, 0, statusText);
        }
        catch (HttpRequestException ex)
        {
            return new McpProbeResult(McpProbeStatus.Unreachable, 0, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new McpProbeResult(McpProbeStatus.Unreachable, 0, "connection timed out");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return new McpProbeResult(McpProbeStatus.Unreachable, 0, ex.Message);
        }
    }

    internal static async Task<McpClient> CreateOneOffClientAsync(string name, McpServerEntry entry)
    {
        IClientTransport transport;

        if (entry.Transport is "stdio")
        {
            var envVars = entry.EnvironmentVariables is { Count: > 0 }
                ? new Dictionary<string, string?>(entry.EnvironmentVariables!)
                : null;

            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = entry.Command!,
                Arguments = entry.Arguments ?? [],
                EnvironmentVariables = envVars,
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
                AdditionalHeaders = entry.Headers is { Count: > 0 }
                    ? new Dictionary<string, string>(entry.Headers) : null,
                TransportMode = entry.Transport is "sse"
                    ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
            });
        }

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new() { Name = "netclaw", Version = "0.1.0" },
        });
    }

    // ── Config file helpers ──

    private static (Dictionary<string, object> config, Dictionary<string, object> secrets) LoadConfigFiles(NetclawPaths paths)
    {
        var config = LoadJsonDict(paths.NetclawConfigPath);
        var secrets = LoadJsonDict(paths.SecretsPath);
        return (config, secrets);
    }

    private static Dictionary<string, object> LoadJsonDict(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object> { ["configVersion"] = 1 };

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object> { ["configVersion"] = 1 };
    }

    internal static Dictionary<string, McpServerEntry> LoadMcpServers(NetclawPaths paths)
    {
        // Merge netclaw.json and secrets.json McpServers sections
        var configText = File.Exists(paths.NetclawConfigPath)
            ? File.ReadAllText(paths.NetclawConfigPath) : "{}";
        var secretsText = File.Exists(paths.SecretsPath)
            ? File.ReadAllText(paths.SecretsPath) : "{}";

        using var configDoc = JsonDocument.Parse(configText);
        using var secretsDoc = JsonDocument.Parse(secretsText);

        var result = new Dictionary<string, McpServerEntry>();

        if (configDoc.RootElement.TryGetProperty("McpServers", out var configServers))
        {
            foreach (var prop in configServers.EnumerateObject())
            {
                var entry = JsonSerializer.Deserialize<McpServerEntry>(prop.Value.GetRawText()) ?? new McpServerEntry();
                result[prop.Name] = entry;
            }
        }

        // Merge secrets on top
        if (secretsDoc.RootElement.TryGetProperty("McpServers", out var secretServers))
        {
            foreach (var prop in secretServers.EnumerateObject())
            {
                if (!result.TryGetValue(prop.Name, out var entry))
                    continue;

                if (prop.Value.TryGetProperty("EnvironmentVariables", out var envVars))
                {
                    entry.EnvironmentVariables ??= new Dictionary<string, string>();
                    foreach (var ev in envVars.EnumerateObject())
                        entry.EnvironmentVariables[ev.Name] = ev.Value.GetString() ?? "";
                }

                if (prop.Value.TryGetProperty("Headers", out var hdrs))
                {
                    entry.Headers ??= new Dictionary<string, string>();
                    foreach (var h in hdrs.EnumerateObject())
                        entry.Headers[h.Name] = h.Value.GetString() ?? "";
                }
            }
        }

        return result;
    }

    private static Dictionary<string, object> GetOrCreateSection(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var existing))
        {
            if (existing is JsonElement je)
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                    ?? new Dictionary<string, object>();
                dict[key] = parsed;
                return parsed;
            }

            return (Dictionary<string, object>)existing;
        }

        var section = new Dictionary<string, object>();
        dict[key] = section;
        return section;
    }

    private static Dictionary<string, object>? GetSectionOrNull(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var existing))
            return null;

        if (existing is JsonElement je)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                ?? new Dictionary<string, object>();
            dict[key] = parsed;
            return parsed;
        }

        return existing as Dictionary<string, object>;
    }

    private static JsonElement SerializeEntry(McpServerEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void WriteConfigFile(string path, Dictionary<string, object> data)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static int WriteHelp()
    {
        Console.WriteLine("Usage: netclaw mcp <subcommand>");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  add        Add an MCP server profile");
        Console.WriteLine("  list       List configured MCP servers");
        Console.WriteLine("  get        Show details for an MCP server");
        Console.WriteLine("  remove     Remove an MCP server profile");
        Console.WriteLine("  enable     Enable a disabled MCP server");
        Console.WriteLine("  disable    Disable an MCP server without removing it");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  netclaw mcp add --transport stdio memorizer -- npx -y @memorizer/mcp-server");
        Console.WriteLine("  netclaw mcp add --transport http --header \"Authorization: Bearer tok-...\" myapi https://api.example.com/mcp");
        Console.WriteLine("  netclaw mcp list");
        return 0;
    }
}
