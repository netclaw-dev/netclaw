// -----------------------------------------------------------------------
// <copyright file="McpCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Providers.OAuth;
using Netclaw.Tools;

namespace Netclaw.Cli.Mcp;

internal enum McpProbeStatus { Connected, AuthFailed, Unreachable, Disabled, AwaitingAuth }

internal readonly record struct McpProbeResult(
    McpProbeStatus Status, int ToolCount, string? ErrorMessage)
{
    public string FormatStatus() => Status switch
    {
        McpProbeStatus.Connected => $"connected ({ToolCount} tools)",
        McpProbeStatus.AuthFailed => $"auth failed ({ErrorMessage})",
        McpProbeStatus.Unreachable => $"unreachable — {ErrorMessage}",
        McpProbeStatus.Disabled => "disabled",
        McpProbeStatus.AwaitingAuth => "awaiting auth — run: netclaw mcp auth <name>",
        _ => "unknown"
    };
}

/// <summary>
/// Handles <c>netclaw mcp</c> CLI subcommands: add, list, get, remove, enable, disable.
/// </summary>
internal static class McpCommand
{
    public static async Task<int> RunAsync(string[] args, NetclawPaths paths, DaemonApi? daemonApi = null, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "add" => RunAdd(args, paths, writer),
            "auth" => await RunAuthAsync(args, paths, daemonApi, writer),
            "list" => await RunListAsync(paths, daemonApi, writer),
            "get" => RunGet(args, paths, writer),
            "remove" => RunRemove(args, paths, writer),
            "enable" => RunToggle(args, paths, enabled: true, writer),
            "disable" => RunToggle(args, paths, enabled: false, writer),
            "tools" => await RunToolsAsync(args, paths, daemonApi, writer),
            "permissions" => await RunToolsAsync(args, paths, daemonApi, writer),
            "help" or "-h" or "--help" => WriteHelp(writer),
            _ => WriteHelp(writer)
        };
    }

    internal static int RunAdd(string[] args, NetclawPaths paths, TextWriter writer)
    {
        // Parse: netclaw mcp add [--transport <type>] [--client-id <id>] [--scope <scopes>] [--env KEY=VALUE]... [--header "Key: Value"]... [--grant-all] <name> [command/url] [-- args...]
        string? transport = null;
        string? oauthClientId = null;
        string? oauthScope = null;
        var envVars = new Dictionary<string, string>();
        var headers = new Dictionary<string, string>();
        var grantAll = false;
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

            if (args[i] == "--grant-all")
            {
                grantAll = true;
                continue;
            }

            if (args[i] is "--transport" or "-t" && i + 1 < args.Length)
            {
                transport = args[++i];
                continue;
            }

            if (args[i] == "--client-id" && i + 1 < args.Length)
            {
                oauthClientId = args[++i];
                continue;
            }

            if (args[i] == "--scope" && i + 1 < args.Length)
            {
                oauthScope = args[++i];
                continue;
            }

            if (args[i] == "--env" && i + 1 < args.Length)
            {
                var kv = args[++i];
                var eqIdx = kv.IndexOf('=', StringComparison.Ordinal);
                if (eqIdx > 0)
                    envVars[kv[..eqIdx]] = kv[(eqIdx + 1)..];
                continue;
            }

            if (args[i] == "--header" && i + 1 < args.Length)
            {
                var hv = args[++i];
                var colonIdx = hv.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx > 0)
                    headers[hv[..colonIdx].Trim()] = hv[(colonIdx + 1)..].Trim();
                continue;
            }

            positional.Add(args[i]);
        }

        if (positional.Count < 1)
        {
            writer.WriteLine("Usage: netclaw mcp add [--transport stdio|http|sse] [--env KEY=VALUE] <name> [command|url] [-- args...]");
            return 1;
        }

        var serverName = new McpServerName(positional[0]);
        transport ??= "stdio";

        if (transport is "stdio")
        {
            if (afterDash && dashArgs.Count > 0)
            {
                // netclaw mcp add --transport stdio memorizer -- npx -y @memorizer/mcp-server
                commandOrUrl = dashArgs[0];
                commandArgs = [.. dashArgs.Skip(1)];
            }
            else if (positional.Count >= 2)
            {
                // netclaw mcp add --transport stdio memorizer npx
                commandOrUrl = positional[1];
                commandArgs = [.. positional.Skip(2)];
            }

            if (string.IsNullOrWhiteSpace(commandOrUrl))
            {
                writer.WriteLine("Error: stdio transport requires a command. Usage: netclaw mcp add --transport stdio <name> -- <command> [args...]");
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
                writer.WriteLine($"Error: {transport} transport requires a URL. Usage: netclaw mcp add --transport {transport} <name> <url>");
                return 1;
            }
        }

        // Build entry for netclaw.json (non-secret parts)
        var entry = new McpServerEntry
        {
            Transport = transport,
            Enabled = true,
            OAuthClientId = oauthClientId,
            OAuthScope = oauthScope,
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
        var (config, _) = LoadConfigFiles(paths);

        var mcpServers = GetOrCreateSection(config, "McpServers");
        mcpServers[serverName.Value] = SerializeEntry(entry);

        ApplySecureDefaultsForNewServer(config, serverName, grantAll);

        WriteConfigFile(paths.NetclawConfigPath, config);

        // Write sensitive values to secrets.json
        if (envVars.Count > 0 || headers.Count > 0)
        {
            UpdateSecretsFile(paths, secrets =>
            {
                var secretMcp = GetOrCreateSection(secrets, "McpServers");
                var serverSecrets = new Dictionary<string, object>();

                if (envVars.Count > 0)
                    serverSecrets["EnvironmentVariables"] = envVars;
                if (headers.Count > 0)
                    serverSecrets["Headers"] = headers;

                secretMcp[serverName.Value] = JsonSerializer.SerializeToElement(serverSecrets);
                return true;
            });
        }

        writer.WriteLine($"Added MCP server '{serverName.Value}' ({transport})");
        writer.WriteLine();
        if (grantAll)
        {
            writer.WriteLine("Security: all tools are reachable for any audience that allows this server");
            writer.WriteLine("          (legacy null-grants behavior — you passed --grant-all).");
        }
        else
        {
            writer.WriteLine("Security: Personal grants all tools (auto-approved). Team/Public grant 0 tools");
            writer.WriteLine("          until you opt in via `netclaw mcp permissions`.");
        }
        writer.WriteLine("Approval defaults: Personal=Auto, Team=Approval, Public=Deny");
        writer.WriteLine($"Next: run `netclaw mcp permissions` to grant tools and adjust approvals for '{serverName.Value}'.");
        return 0;
    }

    /// <summary>
    /// Writes secure defaults for a newly added MCP server across all audience
    /// profiles. Personal gets <c>Auto</c> approval and no per-tool grants
    /// (all tools pass). Team/Public get empty <c>McpServerToolGrants[serverName]</c>
    /// lists (skipped when <paramref name="grantAll"/> is true) and
    /// <c>ApprovalPolicy.McpServerDefaults[serverName]</c> entries
    /// (Team=Approval, Public=Deny). The <c>ApprovalPolicy</c> section is
    /// created if missing.
    /// </summary>
    private static void ApplySecureDefaultsForNewServer(
        Dictionary<string, object> config, McpServerName serverName, bool grantAll)
    {
        var toolsSection = GetOrCreateSection(config, "Tools");
        var profilesSection = GetOrCreateSection(toolsSection, "AudienceProfiles");

        (TrustAudience audience, ToolApprovalMode approvalMode)[] audiences =
        [
            (TrustAudience.Personal, ToolApprovalMode.Auto),
            (TrustAudience.Team, ToolApprovalMode.Approval),
            (TrustAudience.Public, ToolApprovalMode.Deny),
        ];

        foreach (var (audience, approvalMode) in audiences)
        {
            var audienceName = audience.ToString();
            var audienceSection = GetOrCreateSection(profilesSection, audienceName);

            if (!grantAll && audience != TrustAudience.Personal)
            {
                var grants = GetOrCreateSection(audienceSection, "McpServerToolGrants");
                grants[serverName.Value] = new List<string>();
            }

            var approvalPolicy = GetOrCreateSection(audienceSection, "ApprovalPolicy");
            var serverDefaults = GetOrCreateSection(approvalPolicy, "McpServerDefaults");
            serverDefaults[serverName.Value] = approvalMode.ToString();
        }
    }

    private static async Task<int> RunAuthAsync(string[] args, NetclawPaths paths, DaemonApi? daemonApi, TextWriter writer)
    {
        if (args.Length < 3)
        {
            writer.WriteLine("Usage: netclaw mcp auth <name>");
            return 1;
        }

        if (daemonApi is null)
        {
            writer.WriteLine("Error: DaemonApi not available. Is the daemon configured?");
            return 1;
        }

        var serverName = new McpServerName(args[2]);
        var servers = LoadMcpServers(paths);

        if (!servers.TryGetValue(serverName.Value, out var entry))
        {
            writer.WriteLine($"MCP server '{serverName.Value}' not found.");
            return 1;
        }

        if (entry.Transport is "stdio" || string.IsNullOrWhiteSpace(entry.Url))
        {
            writer.WriteLine($"MCP server '{serverName.Value}' uses stdio transport. OAuth is only for HTTP/SSE servers.");
            return 1;
        }

        // 1. Start the OAuth flow via daemon
        writer.WriteLine($"Starting OAuth authorization for '{serverName.Value}'...");

        HttpResponseMessage startResponse;
        try
        {
            startResponse = await daemonApi.StartMcpOAuthAsync(serverName.Value);
        }
        catch (HttpRequestException)
        {
            writer.WriteLine("Error: Could not reach the daemon. Is it running? (netclaw run)");
            return 1;
        }

        if (!startResponse.IsSuccessStatusCode)
        {
            writer.WriteLine($"Error: {await ReadMcpErrorAsync(startResponse)}");
            return 1;
        }

        var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var authUrl = startResult.GetProperty("authorizationUrl").GetString()!;
        var flowState = startResult.GetProperty("state").GetString()!;
        // Wait until the deadline the daemon reports rather than assume the flow lifetime.
        // A client that gives up first tells the operator the attempt timed out while the
        // daemon is still ready to accept their callback.
        // A daemon older than this CLI does not report the deadline. The CLI and the
        // daemon swap separately during an upgrade, so a newer CLI against an older
        // daemon is a normal window rather than a broken install, and crashing here
        // would take out `netclaw mcp auth` for the length of it. Fall back to the
        // lifetime that daemon enforces.
        var deadline = startResult.TryGetProperty("expiresAt", out var expiresAt)
            ? expiresAt.GetDateTimeOffset()
            : DateTimeOffset.UtcNow.AddMinutes(5);

        // 2. Open browser (detect headless first)
        var canOpenBrowser = BrowserDetection.CanOpenBrowser();
        writer.WriteLine();

        if (canOpenBrowser)
        {
            writer.WriteLine("Opening browser for authorization...");
            try
            {
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            }
            catch
            {
                canOpenBrowser = false;
            }
        }

        if (!canOpenBrowser)
        {
            writer.WriteLine("Could not open browser automatically (headless environment detected).");
        }

        writer.WriteLine();
        writer.WriteLine($"Authorization URL: {authUrl}");
        if (TryEmitOsc52Copy(writer, authUrl))
            writer.WriteLine("Copied authorization URL to clipboard via OSC 52.");
        writer.WriteLine();
        writer.WriteLine("Complete the authorization in your browser, then either:");
        writer.WriteLine("  1. Wait for the callback to be received automatically");
        writer.WriteLine("  2. Paste the redirect URL below if the callback can't reach this machine");
        writer.WriteLine();

        // 3. Start polling + listen for paste concurrently
        writer.Write("Waiting for authorization");
        var remaining = deadline - DateTimeOffset.UtcNow;
        using var cts = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var pollTask = PollMcpOAuthStatusAsync(daemonApi, flowState, writer, cts.Token);
        var pasteTask = ReadPasteRedirectAsync(daemonApi, writer, cts.Token);

        var completedTask = await Task.WhenAny(pollTask, pasteTask);
        cts.Cancel();

        if (completedTask == pollTask)
        {
            var pollResult = await pollTask;
            if (pollResult)
            {
                writer.WriteLine($"Authorization complete for '{serverName.Value}'.");
                return 0;
            }
        }
        else if (completedTask == pasteTask)
        {
            var pasteResult = await pasteTask;
            if (pasteResult)
            {
                writer.WriteLine($"Authorization complete for '{serverName.Value}'.");
                return 0;
            }
        }

        writer.WriteLine($"Authorization failed or timed out for '{serverName.Value}'.");
        writer.WriteLine("Try again with: netclaw mcp auth " + serverName.Value);
        return 1;
    }

    private static async Task<bool> PollMcpOAuthStatusAsync(
        DaemonApi daemonApi, string state, TextWriter writer, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, ct);
                writer.Write(".");

                var statusResponse = await daemonApi.GetMcpOAuthStatusByStateAsync(state, ct);
                var status = statusResponse.GetProperty("status").GetString();

                if (status is "Completed")
                {
                    writer.WriteLine();
                    return true;
                }

                if (status is "Failed")
                {
                    writer.WriteLine();
                    if (statusResponse.TryGetProperty("error", out var error)
                        && error.ValueKind is JsonValueKind.Object
                        && error.TryGetProperty("error", out var message)
                        && !string.IsNullOrWhiteSpace(message.GetString()))
                        writer.WriteLine($"Error: {message.GetString()}");
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                writer.Write("!"); // transient poll error — keep trying
            }
        }

        writer.WriteLine();
        return false;
    }

    private static async Task<bool> ReadPasteRedirectAsync(
        DaemonApi daemonApi, TextWriter writer, CancellationToken ct)
    {
        // Skip paste fallback when stdin is redirected (CI, piped input)
        if (Console.IsInputRedirected)
            return await WaitUntilCancelledAsync(ct);

        return await ReadPasteRedirectAsync(
            writer,
            token => Task.Run(Console.ReadLine, token),
            (code, state, iss, token) => daemonApi.McpOAuthCallbackAsync(code, state, iss, token),
            ct);
    }

    internal static async Task<bool> ReadPasteRedirectAsync(
        TextWriter writer,
        Func<CancellationToken, Task<string?>> readLineAsync,
        Func<string, string, string?, CancellationToken, Task<HttpResponseMessage>> submitRedirectAsync,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await readLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            // End-of-input means paste fallback is unavailable; keep polling alive.
            if (line is null)
                return await WaitUntilCancelledAsync(ct);

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!OAuthRedirectParser.TryParse(line, out var code, out var state, out var iss, out var error))
            {
                writer.WriteLine($"Invalid redirect URL: {error}");
                continue;
            }

            try
            {
                using var response = await submitRedirectAsync(code, state, iss, ct);
                if (response.IsSuccessStatusCode)
                    return true;

                writer.WriteLine($"Redirect URL was rejected: {await ReadMcpErrorAsync(response)}");
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                writer.WriteLine($"Failed to submit redirect URL: {ex.Message}");
            }
        }

        return false;
    }

    internal static async Task<string> ReadMcpErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind is JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind is JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(error.GetString()))
                    return error.GetString()!;
            }
        }
        catch (JsonException)
        {
            return FormatHttpError(response);
        }

        return FormatHttpError(response);
    }

    private static string FormatHttpError(HttpResponseMessage response)
    {
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? response.StatusCode.ToString()
            : response.ReasonPhrase;
        return $"HTTP {(int)response.StatusCode} {reason}";
    }

    private static async Task<bool> WaitUntilCancelledAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    internal static bool TryEmitOsc52Copy(TextWriter writer, string text)
    {
        if (string.IsNullOrEmpty(text)
            || !ReferenceEquals(writer, Console.Out)
            || Console.IsOutputRedirected)
            return false;

        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.IsNullOrWhiteSpace(term)
            || string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return false;

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        writer.Write($"\u001b]52;c;{payload}\u0007");
        return true;
    }

    private static async Task<int> RunListAsync(NetclawPaths paths, DaemonApi? daemonApi, TextWriter writer)
    {
        var servers = LoadMcpServers(paths);

        if (servers.Count == 0)
        {
            writer.WriteLine("No MCP servers configured.");
            writer.WriteLine("Run `netclaw mcp add` to add one.");
            return 0;
        }

        JsonElement? daemonStatuses = null;
        string? daemonError = null;

        if (daemonApi is null)
        {
            daemonError = "daemon API not configured";
        }
        else
        {
            try
            {
                daemonStatuses = await daemonApi.GetMcpServerStatusesAsync();
            }
            catch (HttpRequestException)
            {
                daemonError = $"could not reach daemon at {daemonApi.Endpoint}";
            }
            catch (OperationCanceledException)
            {
                daemonError = $"daemon timed out at {daemonApi.Endpoint}";
            }
            catch (Exception ex)
            {
                daemonError = $"daemon status request failed: {ex.Message}";
            }
        }

        if (daemonError is not null)
        {
            writer.WriteLine($"Live MCP status unavailable: {daemonError}");
            writer.WriteLine("Start the daemon with `netclaw daemon start` or `netclaw run`.");
            writer.WriteLine();
        }

        writer.WriteLine($"{"Name",-20} {"Transport",-10} {"Enabled",-8} {"Status"}");
        foreach (var (name, entry) in servers)
        {
            var serverName = new McpServerName(name);
            var enabled = entry.Enabled ? "yes" : "no";
            var statusText = !entry.Enabled
                ? "disabled"
                : daemonStatuses is not null
                    ? FormatDaemonStatus(daemonStatuses, serverName)
                        ?? "status unavailable — restart daemon to load this config"
                    : "status unavailable — daemon unavailable";

            writer.WriteLine($"{name,-20} {entry.Transport,-10} {enabled,-8} {statusText}");
        }

        return daemonError is null ? 0 : 1;
    }

    private static string? FormatDaemonStatus(JsonElement? daemonStatuses, McpServerName serverName)
    {
        if (daemonStatuses is null)
            return null;

        if (!daemonStatuses.Value.TryGetProperty(serverName.Value, out var entry))
            return null;

        var state = entry.GetProperty("state").GetString();
        var toolCount = entry.GetProperty("toolCount").GetInt32();
        var error = entry.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;

        return state switch
        {
            "Connected" => $"connected ({toolCount} tools)",
            "AwaitingAuth" => "awaiting auth — run: netclaw mcp auth " + serverName.Value,
            "AuthFailed" => $"auth failed ({error ?? "authentication rejected"})",
            "Unreachable" => $"unreachable — {error ?? "connection failed"}",
            "Disabled" => "disabled",
            _ => state,
        };
    }

    private static int RunGet(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (args.Length < 3)
        {
            writer.WriteLine("Usage: netclaw mcp get <name>");
            return 1;
        }

        var serverName = new McpServerName(args[2]);
        var servers = LoadMcpServers(paths);

        if (!servers.TryGetValue(serverName.Value, out var entry))
        {
            writer.WriteLine($"MCP server '{serverName.Value}' not found.");
            return 1;
        }

        writer.WriteLine($"Name:       {serverName.Value}");
        writer.WriteLine($"Transport:  {entry.Transport}");

        if (entry.Command is not null)
        {
            var cmdLine = entry.Arguments is { Length: > 0 }
                ? $"{entry.Command} {string.Join(' ', entry.Arguments)}"
                : entry.Command;
            writer.WriteLine($"Command:    {cmdLine}");
        }

        if (entry.Url is not null)
            writer.WriteLine($"URL:        {entry.Url}");

        writer.WriteLine($"Enabled:    {(entry.Enabled ? "yes" : "no")}");

        if (entry.OAuthClientId is not null)
            writer.WriteLine($"Client ID:  {entry.OAuthClientId}");
        if (entry.OAuthScope is not null)
            writer.WriteLine($"Scope:      {entry.OAuthScope}");

        if (entry.EnvironmentVariables is { Count: > 0 })
        {
            writer.WriteLine("Env vars:");
            foreach (var (k, _) in entry.EnvironmentVariables)
                writer.WriteLine($"  {k}=***REDACTED***");
        }

        if (entry.Headers is { Count: > 0 })
        {
            writer.WriteLine("Headers:");
            foreach (var (k, _) in entry.Headers)
                writer.WriteLine($"  {k}: ***REDACTED***");
        }

        return 0;
    }

    private static int RunRemove(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (args.Length < 3)
        {
            writer.WriteLine("Usage: netclaw mcp remove <name>");
            return 1;
        }

        var serverName = new McpServerName(args[2]);
        var (config, _) = LoadConfigFiles(paths);

        var removed = false;
        var mcpServers = GetSectionOrNull(config, "McpServers");
        if (mcpServers?.Remove(serverName.Value) == true)
        {
            WriteConfigFile(paths.NetclawConfigPath, config);
            removed = true;
        }

        var removedSecrets = ConfigFileHelper.UpdateSecretsFile(paths, (secrets, _) =>
        {
            var secretMcp = GetSectionOrNull(secrets, "McpServers");
            var removedSecret = secretMcp?.Remove(serverName.Value) == true;
            return (removedSecret, removedSecret);
        });
        removed |= removedSecrets;

        if (removed)
        {
            writer.WriteLine($"Removed MCP server '{serverName.Value}'");
            return 0;
        }

        writer.WriteLine($"MCP server '{serverName.Value}' not found.");
        return 1;
    }

    private static int RunToggle(string[] args, NetclawPaths paths, bool enabled, TextWriter writer)
    {
        if (args.Length < 3)
        {
            writer.WriteLine($"Usage: netclaw mcp {(enabled ? "enable" : "disable")} <name>");
            return 1;
        }

        var serverName = new McpServerName(args[2]);
        var (config, _) = LoadConfigFiles(paths);

        var mcpServers = GetSectionOrNull(config, "McpServers");
        if (mcpServers is null || !mcpServers.ContainsKey(serverName.Value))
        {
            writer.WriteLine($"MCP server '{serverName.Value}' not found.");
            return 1;
        }

        // Deserialize, toggle, re-serialize
        var entry = JsonSerializer.Deserialize<McpServerEntry>(
            JsonSerializer.Serialize(mcpServers[serverName.Value])) ?? new McpServerEntry();
        entry.Enabled = enabled;
        mcpServers[serverName.Value] = SerializeEntry(entry);

        WriteConfigFile(paths.NetclawConfigPath, config);
        writer.WriteLine($"{(enabled ? "Enabled" : "Disabled")} MCP server '{serverName.Value}'");
        return 0;
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<McpProbeResult> ProbeServerAsync(
        McpServerName serverName, McpServerEntry entry, CancellationToken ct = default)
    {
        if (!entry.Enabled)
            return new McpProbeResult(McpProbeStatus.Disabled, 0, null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            await using var client = await CreateOneOffClientAsync(serverName, entry);
            var tools = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);
            return new McpProbeResult(McpProbeStatus.Connected, tools.Count, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            // If the server has OAuth config hints, report AwaitingAuth instead of AuthFailed
            if (!string.IsNullOrWhiteSpace(entry.OAuthClientId) || !string.IsNullOrWhiteSpace(entry.OAuthScope))
                return new McpProbeResult(McpProbeStatus.AwaitingAuth, 0, null);

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
            var status = ex.StatusCode is null
                ? "connection failed"
                : $"HTTP {(int)ex.StatusCode.Value} {ex.StatusCode.Value}";
            return new McpProbeResult(McpProbeStatus.Unreachable, 0, status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new McpProbeResult(McpProbeStatus.Unreachable, 0, "connection timed out");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return new McpProbeResult(McpProbeStatus.Unreachable, 0, "connection failed");
        }
    }

    internal static async Task<McpClient> CreateOneOffClientAsync(McpServerName serverName, McpServerEntry entry)
    {
        IClientTransport transport;

        if (entry.Transport is "stdio")
        {
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = entry.Command!,
                Arguments = entry.Arguments ?? [],
                EnvironmentVariables = entry.EnvironmentVariables.ToRawNullableValues(StringComparer.OrdinalIgnoreCase),
                Name = serverName.Value,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });
        }
        else
        {
            var headers = entry.Headers.ToRawValues(StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!headers.ContainsKey("User-Agent"))
                headers["User-Agent"] = NetclawUserAgent.Value;
            if (!headers.ContainsKey(NetclawUserAgent.ComponentHeader))
                headers[NetclawUserAgent.ComponentHeader] = "mcp-probe";

            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(entry.Url!),
                Name = serverName.Value,
                AdditionalHeaders = headers,
                TransportMode = entry.Transport is "sse"
                    ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
            });
        }

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new()
            {
                Name = "netclaw",
                Title = "Netclaw",
                Version = BuildInfo.Version,
                WebsiteUrl = "https://netclaw.dev",
                Description = "Open-source autonomous operations agent built on Akka.NET",
            },
        });
    }

    // ── Config file helpers (delegated to ConfigFileHelper) ──

    private static (Dictionary<string, object> config, Dictionary<string, object> secrets)
        LoadConfigFiles(NetclawPaths paths) => ConfigFileHelper.LoadConfigFiles(paths);

    private static Dictionary<string, object> GetOrCreateSection(
        Dictionary<string, object> dict, string key) => ConfigFileHelper.GetOrCreateSection(dict, key);

    private static Dictionary<string, object>? GetSectionOrNull(
        Dictionary<string, object> dict, string key) => ConfigFileHelper.GetSectionOrNull(dict, key);

    private static void WriteConfigFile(string path, Dictionary<string, object> data)
        => ConfigFileHelper.WriteConfigFile(path, data);

    private static void UpdateSecretsFile(NetclawPaths paths, Func<Dictionary<string, object>, bool> update)
        => ConfigFileHelper.UpdateSecretsFile(paths, (secrets, _) => update(secrets));

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
                    entry.EnvironmentVariables ??= [];
                    foreach (var ev in envVars.EnumerateObject())
                    {
                        var decrypted = ConfigFileHelper.DecryptIfEncrypted(paths, ev.Value.GetString());
                        entry.EnvironmentVariables[ev.Name] = new SensitiveString(decrypted);
                    }
                }

                if (prop.Value.TryGetProperty("Headers", out var hdrs))
                {
                    entry.Headers ??= [];
                    foreach (var h in hdrs.EnumerateObject())
                    {
                        var decrypted = ConfigFileHelper.DecryptIfEncrypted(paths, h.Value.GetString());
                        entry.Headers[h.Name] = new SensitiveString(decrypted);
                    }
                }
            }
        }

        return result;
    }

    private static JsonElement SerializeEntry(McpServerEntry entry)
    {
        var json = JsonSerializer.Serialize(entry);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<int> RunToolsAsync(string[] args, NetclawPaths paths, DaemonApi? daemonApi, TextWriter writer)
    {
        // Parse: netclaw mcp tools <server> [--snapshot] [--audience <name>] [--grant t1,t2] [--revoke t1,t2]
        string? serverNameRaw = null;
        string? audienceFilter = null;
        var snapshot = false;
        List<string>? grantTools = null;
        List<string>? revokeTools = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--snapshot":
                    snapshot = true;
                    break;
                case "--audience" when i + 1 < args.Length:
                    audienceFilter = args[++i];
                    break;
                case "--grant" when i + 1 < args.Length:
                    grantTools = [.. args[++i].Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)];
                    break;
                case "--revoke" when i + 1 < args.Length:
                    revokeTools = [.. args[++i].Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)];
                    break;
                case "--help" or "-h":
                    WriteToolsHelp(writer);
                    return 0;
                default:
                    if (serverNameRaw is null && !args[i].StartsWith('-'))
                        serverNameRaw = args[i];
                    break;
            }
        }

        if (serverNameRaw is null)
        {
            WriteToolsHelp(writer);
            return 1;
        }

        var serverName = new McpServerName(serverNameRaw);

        // Validate audience name if provided
        TrustAudience? targetAudience = null;
        if (audienceFilter is not null)
        {
            if (!SecurityPolicyDefaults.TryParseAudience(audienceFilter, out var parsed))
            {
                writer.WriteLine($"Error: unknown audience '{audienceFilter}'. Use public, team, or personal.");
                return 1;
            }

            targetAudience = parsed;
        }

        if (daemonApi is null)
        {
            writer.WriteLine("Error: daemon API not available. Is the daemon running?");
            return 1;
        }

        // Get discovered tools from daemon
        List<string> discoveredTools;
        try
        {
            discoveredTools = await daemonApi.GetMcpToolNamesAsync(serverName.Value, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            writer.WriteLine("Error: could not reach daemon. Is it running?");
            return 1;
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        if (discoveredTools.Count == 0)
        {
            writer.WriteLine($"No tools discovered for server '{serverName.Value}'.");
            writer.WriteLine("The server may be disconnected or have no tools. Check `netclaw mcp list`.");
            return 1;
        }

        // Load audience profiles for grant status
        var toolConfig = LoadToolConfig(paths);
        var profiles = toolConfig.AudienceProfiles;

        // Surgical grant/revoke
        if (grantTools is not null || revokeTools is not null)
        {
            if (targetAudience is null)
            {
                writer.WriteLine("Error: --grant and --revoke require --audience.");
                return 1;
            }

            return RunToolsGrantRevoke(serverName, targetAudience.Value, discoveredTools,
                grantTools, revokeTools, profiles, paths, writer);
        }

        if (snapshot)
            return RunToolsSnapshot(serverName, targetAudience, discoveredTools, profiles, paths, writer);

        return RunToolsList(serverName, targetAudience, discoveredTools, profiles, writer);
    }

    private static void WriteToolsHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw mcp tools <server> [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --audience <name>     Filter to a specific audience (public, team, personal)");
        writer.WriteLine("  --snapshot            Populate McpServerToolGrants from currently discovered tools");
        writer.WriteLine("  --grant <tools>       Grant comma-separated tools (requires --audience)");
        writer.WriteLine("  --revoke <tools>      Revoke comma-separated tools (requires --audience)");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  netclaw mcp tools memorizer                             Show all tools and grants");
        writer.WriteLine("  netclaw mcp tools memorizer --audience team             Show Team grants only");
        writer.WriteLine("  netclaw mcp tools memorizer --snapshot                  Baseline all audiences");
        writer.WriteLine("  netclaw mcp tools memorizer --snapshot --audience team  Baseline Team only");
        writer.WriteLine("  netclaw mcp tools memorizer --grant search_memories,get --audience team");
        writer.WriteLine("  netclaw mcp tools memorizer --revoke delete,archive_memory --audience personal");
    }

    private static int RunToolsList(
        McpServerName serverName, TrustAudience? audienceFilter, List<string> discoveredTools,
        ToolAudienceProfiles profiles, TextWriter writer)
    {
        writer.WriteLine($"Tools for server '{serverName.Value}' ({discoveredTools.Count} discovered):");
        writer.WriteLine();

        var maxNameLen = Math.Max(discoveredTools.Max(t => t.Length), 4);

        if (audienceFilter is { } single)
        {
            // Single audience view
            var profile = ResolveProfile(single, profiles);
            var label = single.ToString();
            writer.Write("  ");
            writer.Write("Tool".PadRight(maxNameLen + 2));
            writer.WriteLine(label);
            writer.Write("  ");
            writer.WriteLine(new string('\u2500', maxNameLen + 2 + label.Length));

            foreach (var tool in discoveredTools)
            {
                var toolName = new ToolName(tool);
                writer.Write("  ");
                writer.Write(tool.PadRight(maxNameLen + 2));
                writer.WriteLine(FormatGrantStatus(serverName, toolName, profile).TrimEnd());
            }
        }
        else
        {
            // All audiences view
            writer.Write("  ");
            writer.Write("Tool".PadRight(maxNameLen + 2));
            writer.Write("Public   Team     Personal");
            writer.WriteLine();
            writer.Write("  ");
            writer.WriteLine(new string('\u2500', maxNameLen + 2 + 26));

            foreach (var tool in discoveredTools)
            {
                var toolName = new ToolName(tool);
                writer.Write("  ");
                writer.Write(tool.PadRight(maxNameLen + 2));
                writer.Write(FormatGrantStatus(serverName, toolName, profiles.Public));
                writer.Write(FormatGrantStatus(serverName, toolName, profiles.Team));
                writer.Write(FormatGrantStatus(serverName, toolName, profiles.Personal));
                writer.WriteLine();
            }
        }

        writer.WriteLine();
        writer.WriteLine("Edit interactively: netclaw mcp permissions");
        return 0;
    }

    private static string FormatGrantStatus(McpServerName serverName, ToolName toolName, ToolAudienceProfile profile)
    {
        if (profile.McpServerToolGrants is null)
            return "\u2731        "; // ✱ = all (no grants configured)

        if (!profile.McpServerToolGrants.TryGetValue(serverName.Value, out var allowedTools))
            return "\u2731        "; // ✱ = server not in grants, all tools pass

        return allowedTools.Contains(toolName.Value, StringComparer.Ordinal)
            ? "\u2713        " // ✓ = granted
            : "-        ";    // - = blocked
    }

    private static int RunToolsSnapshot(
        McpServerName serverName, TrustAudience? targetAudience, List<string> discoveredTools,
        ToolAudienceProfiles profiles, NetclawPaths paths, TextWriter writer)
    {
        var (config, _) = LoadConfigFiles(paths);
        var toolsSection = GetOrCreateSection(config, "Tools");
        var profilesSection = GetOrCreateSection(toolsSection, "AudienceProfiles");

        var audienceNames = targetAudience switch
        {
            TrustAudience.Public => new[] { "Public" },
            TrustAudience.Team => ["Team"],
            TrustAudience.Personal => ["Personal"],
            _ => ["Public", "Team", "Personal"]
        };

        var updated = 0;
        foreach (var audienceName in audienceNames)
        {
            var profile = audienceName switch
            {
                "Public" => profiles.Public,
                "Team" => profiles.Team,
                _ => profiles.Personal
            };

            var serverAllowed = profile.McpServersMode == ToolProfileMode.All
                || profile.AllowedMcpServers.Contains(serverName.Value, StringComparer.OrdinalIgnoreCase);

            if (!serverAllowed)
            {
                if (targetAudience is not null)
                {
                    writer.WriteLine($"Server '{serverName.Value}' is not allowed by the {audienceName} audience profile.");
                    return 1;
                }

                continue;
            }

            var audienceSection = GetOrCreateSection(profilesSection, audienceName);
            var grants = audienceSection.TryGetValue("McpServerToolGrants", out var existing)
                && existing is Dictionary<string, object> dict
                    ? dict
                    : [];

            grants[serverName.Value] = discoveredTools;
            audienceSection["McpServerToolGrants"] = grants;
            updated++;
        }

        if (updated == 0)
        {
            writer.WriteLine($"Server '{serverName.Value}' is not allowed by any audience profile. Nothing to snapshot.");
            return 1;
        }

        WriteConfigFile(paths.NetclawConfigPath, config);
        writer.WriteLine($"Snapshot complete: {discoveredTools.Count} tools from '{serverName.Value}' written to McpServerToolGrants for {updated} audience profile(s).");
        writer.WriteLine("New tools added by the server will not be exposed until you update the grants.");
        return 0;
    }

    private static int RunToolsGrantRevoke(
        McpServerName serverName, TrustAudience audience, List<string> discoveredTools,
        List<string>? grantTools, List<string>? revokeTools,
        ToolAudienceProfiles profiles, NetclawPaths paths, TextWriter writer)
    {
        var audienceName = audience switch
        {
            TrustAudience.Public => "Public",
            TrustAudience.Team => "Team",
            _ => "Personal"
        };

        var profile = ResolveProfile(audience, profiles);

        // Validate tool names exist on the server
        var unknownGrants = grantTools?.Except(discoveredTools, StringComparer.Ordinal).ToList() ?? [];
        var unknownRevokes = revokeTools?.Except(discoveredTools, StringComparer.Ordinal).ToList() ?? [];
        if (unknownGrants.Count > 0 || unknownRevokes.Count > 0)
        {
            var unknown = unknownGrants.Concat(unknownRevokes).Distinct().ToList();
            writer.WriteLine($"Error: tool(s) not found on server '{serverName.Value}': {string.Join(", ", unknown)}");
            writer.WriteLine("Use `netclaw mcp tools <server>` to see available tools.");
            return 1;
        }

        // Build the updated tool set
        HashSet<string> currentTools;
        if (profile.McpServerToolGrants is { } existing
            && existing.TryGetValue(serverName.Value, out var currentList))
        {
            currentTools = new HashSet<string>(currentList, StringComparer.Ordinal);
        }
        else
        {
            // No grants configured yet — start from all discovered tools
            currentTools = new HashSet<string>(discoveredTools, StringComparer.Ordinal);
        }

        if (grantTools is not null)
            foreach (var tool in grantTools)
                currentTools.Add(tool);

        if (revokeTools is not null)
            foreach (var tool in revokeTools)
                currentTools.Remove(tool);

        // Write to config
        var (config, _) = LoadConfigFiles(paths);
        var toolsSection = GetOrCreateSection(config, "Tools");
        var profilesSection = GetOrCreateSection(toolsSection, "AudienceProfiles");
        var audienceSection = GetOrCreateSection(profilesSection, audienceName);

        var grants = audienceSection.TryGetValue("McpServerToolGrants", out var ex)
            && ex is Dictionary<string, object> dict
                ? dict
                : [];

        grants[serverName.Value] = currentTools.Order(StringComparer.Ordinal).ToList();
        audienceSection["McpServerToolGrants"] = grants;

        WriteConfigFile(paths.NetclawConfigPath, config);

        var changes = new List<string>();
        if (grantTools is { Count: > 0 })
            changes.Add($"granted {grantTools.Count}");
        if (revokeTools is { Count: > 0 })
            changes.Add($"revoked {revokeTools.Count}");

        writer.WriteLine($"Updated {audienceName} profile for '{serverName.Value}': {string.Join(", ", changes)} tool(s). {currentTools.Count} total granted.");
        return 0;
    }

    private static ToolAudienceProfile ResolveProfile(TrustAudience audience, ToolAudienceProfiles profiles)
    {
        return audience switch
        {
            TrustAudience.Public => profiles.Public,
            TrustAudience.Team => profiles.Team,
            _ => profiles.Personal
        };
    }

    private static ToolConfig LoadToolConfig(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath))
            return new ToolConfig();

        try
        {
            var text = File.ReadAllText(paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("Tools", out var toolsSection))
                return new ToolConfig();

            return JsonSerializer.Deserialize<ToolConfig>(toolsSection.GetRawText(), JsonDefaults.EnumAware)
                ?? new ToolConfig();
        }
        catch
        {
            return new ToolConfig();
        }
    }

    private static int WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw mcp <subcommand>");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  add          Add an MCP server profile (fail-closed by default)");
        writer.WriteLine("  auth         Authorize an OAuth-enabled MCP server");
        writer.WriteLine("  list         List configured MCP servers with daemon-reported status");
        writer.WriteLine("  get          Show details for an MCP server");
        writer.WriteLine("  remove       Remove an MCP server profile");
        writer.WriteLine("  enable       Enable a disabled MCP server");
        writer.WriteLine("  disable      Disable an MCP server without removing it");
        writer.WriteLine("  permissions  Interactively edit per-audience tool grants and approval modes (recommended)");
        writer.WriteLine("  tools        Read-only view of per-audience tool grants from the CLI");
        writer.WriteLine();
        writer.WriteLine("Flags for 'add':");
        writer.WriteLine("  --grant-all  CI escape hatch. Skip the empty-grants writes and leave tool");
        writer.WriteLine("               grants null (legacy \"all pass\" behavior). Approval defaults");
        writer.WriteLine("               (Personal=Approval, Team=Approval, Public=Deny) are still written.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  netclaw mcp add --transport stdio memorizer -- npx -y @memorizer/mcp-server");
        writer.WriteLine("  netclaw mcp add --transport http --header \"Authorization: Bearer tok-...\" myapi https://api.example.com/mcp");
        writer.WriteLine("  netclaw mcp add --transport http textforge https://textforge.net/mcp");
        writer.WriteLine("  netclaw mcp add --grant-all --transport stdio trusted -- /usr/local/bin/trusted");
        writer.WriteLine("  netclaw mcp auth forge");
        writer.WriteLine("  netclaw mcp list");
        writer.WriteLine("  netclaw mcp permissions                                    # interactive TUI editor");
        writer.WriteLine("  netclaw mcp tools memorizer                                # read-only CLI view");
        writer.WriteLine("  netclaw mcp tools memorizer --snapshot --audience team");
        writer.WriteLine("  netclaw mcp tools memorizer --grant search_memories,get --audience team");
        return 0;
    }
}
