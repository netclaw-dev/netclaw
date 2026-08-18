// -----------------------------------------------------------------------
// <copyright file="McpCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();

    public McpCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Add_StdioServer_WritesConfig()
    {
        var args = new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("McpServers", out var servers));
        Assert.True(servers.TryGetProperty("memorizer", out var entry));
        Assert.Equal("stdio", entry.GetProperty("Transport").GetString());
        Assert.Equal("npx", entry.GetProperty("Command").GetString());
    }

    [Fact]
    public async Task Add_HttpServer_WritesConfig()
    {
        var args = new[] { "mcp", "add", "--transport", "http", "textforge", "https://textforge.net/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("McpServers", out var servers));
        Assert.True(servers.TryGetProperty("textforge", out var entry));
        Assert.Equal("http", entry.GetProperty("Transport").GetString());
        Assert.Equal("https://textforge.net/mcp", entry.GetProperty("Url").GetString());
    }

    [Fact]
    public async Task Add_WithEnvVar_WritesSecretsFile()
    {
        var args = new[] { "mcp", "add", "--transport", "stdio", "--env", "API_KEY=secret123", "myserver", "--", "dotnet", "run" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        // Check secrets.json has the env var
        Assert.True(File.Exists(_paths.SecretsPath));
        var secrets = ReadConfigFile(_paths.SecretsPath);
        Assert.True(secrets.RootElement.TryGetProperty("McpServers", out var mcpSecrets));
        Assert.True(mcpSecrets.TryGetProperty("myserver", out var serverSecrets));
        Assert.True(serverSecrets.TryGetProperty("EnvironmentVariables", out var envVars));
        var encrypted = envVars.GetProperty("API_KEY").GetString();
        Assert.StartsWith("ENC:", encrypted);

        // Loader should transparently decrypt encrypted values
        var loaded = McpCommand.LoadMcpServers(_paths);
        Assert.Equal("secret123", loaded["myserver"].EnvironmentVariables?["API_KEY"].Value);
    }

    [Fact]
    public async Task Add_WithHeader_WritesSecretsFile()
    {
        var args = new[] { "mcp", "add", "--transport", "http", "--header", "Authorization: Bearer tok-123", "myapi", "https://api.example.com/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var secrets = ReadConfigFile(_paths.SecretsPath);
        Assert.True(secrets.RootElement.TryGetProperty("McpServers", out var mcpSecrets));
        Assert.True(mcpSecrets.TryGetProperty("myapi", out var serverSecrets));
        Assert.True(serverSecrets.TryGetProperty("Headers", out var headers));
        var encrypted = headers.GetProperty("Authorization").GetString();
        Assert.StartsWith("ENC:", encrypted);

        // Loader should transparently decrypt encrypted values
        var loaded = McpCommand.LoadMcpServers(_paths);
        Assert.Equal("Bearer tok-123", loaded["myapi"].Headers?["Authorization"].Value);
    }

    // ── Fail-closed defaults for new MCP servers ──

    [Fact]
    public async Task Add_WritesEmptyGrantsAndApprovalDefaultsAcrossAudiences()
    {
        var args = new[] { "mcp", "add", "--transport", "http", "notion", "https://mcp.notion.com/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var profiles = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles");

        // Personal: no per-tool grants (all tools pass), Auto approval
        var personal = profiles.GetProperty("Personal");
        var hasPersonalGrants = personal.TryGetProperty("McpServerToolGrants", out var pg)
            && pg.ValueKind == JsonValueKind.Object
            && pg.TryGetProperty("notion", out _);
        Assert.False(hasPersonalGrants);
        Assert.Equal("Auto", personal
            .GetProperty("ApprovalPolicy")
            .GetProperty("McpServerDefaults")
            .GetProperty("notion")
            .GetString());

        // Team/Public: empty grants, Approval/Deny
        foreach (var audience in new[] { "Team", "Public" })
        {
            var profile = profiles.GetProperty(audience);
            var grants = profile.GetProperty("McpServerToolGrants").GetProperty("notion");
            Assert.Equal(JsonValueKind.Array, grants.ValueKind);
            Assert.Equal(0, grants.GetArrayLength());

            var approvalMode = profile
                .GetProperty("ApprovalPolicy")
                .GetProperty("McpServerDefaults")
                .GetProperty("notion")
                .GetString();

            var expected = audience == "Public" ? "Deny" : "Approval";
            Assert.Equal(expected, approvalMode);
        }

        var output = _output.ToString();
        Assert.Contains("Personal grants all tools", output);
        Assert.Contains("netclaw mcp permissions", output);
        Assert.Contains("Personal=Auto", output);
        Assert.Contains("Public=Deny", output);
    }

    [Fact]
    public async Task Add_WithGrantAll_SkipsGrantsButWritesApprovalDefaults()
    {
        var args = new[] { "mcp", "add", "--grant-all", "--transport", "stdio", "trusted", "--", "/usr/local/bin/trusted" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var profiles = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles");

        foreach (var audience in new[] { "Personal", "Team", "Public" })
        {
            var profile = profiles.GetProperty(audience);

            // No grants written — either missing section or missing key.
            var hasGrants = profile.TryGetProperty("McpServerToolGrants", out var grants)
                && grants.ValueKind == JsonValueKind.Object
                && grants.TryGetProperty("trusted", out _);
            Assert.False(hasGrants);

            var approvalMode = profile
                .GetProperty("ApprovalPolicy")
                .GetProperty("McpServerDefaults")
                .GetProperty("trusted")
                .GetString();
            var expected = audience switch
            {
                "Personal" => "Auto",
                "Public" => "Deny",
                _ => "Approval"
            };
            Assert.Equal(expected, approvalMode);
        }

        Assert.Contains("legacy null-grants behavior", _output.ToString());
    }

    [Fact]
    public async Task Add_DoesNotMutateExistingServers()
    {
        // Pre-populate an existing server manually, with null grants.
        var initial = new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["McpServers"] = new Dictionary<string, object>
            {
                ["old-server"] = new Dictionary<string, object>
                {
                    ["Transport"] = "stdio",
                    ["Command"] = "npx",
                    ["Enabled"] = true
                }
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.NetclawConfigPath)!);
        File.WriteAllText(_paths.NetclawConfigPath, JsonSerializer.Serialize(initial));

        var args = new[] { "mcp", "add", "--transport", "http", "new-server", "https://new.example/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var profiles = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles");

        // Personal: no per-tool grants written for new-server (all tools pass)
        var personalProfile = profiles.GetProperty("Personal");
        var hasPersonalGrants = personalProfile.TryGetProperty("McpServerToolGrants", out var personalGrants)
            && personalGrants.ValueKind == JsonValueKind.Object
            && personalGrants.TryGetProperty("new-server", out _);
        Assert.False(hasPersonalGrants);
        var personalApproval = personalProfile
            .GetProperty("ApprovalPolicy")
            .GetProperty("McpServerDefaults");
        Assert.False(personalApproval.TryGetProperty("old-server", out _));
        Assert.True(personalApproval.TryGetProperty("new-server", out _));

        // Team/Public: empty grants written, old-server untouched
        foreach (var audience in new[] { "Team", "Public" })
        {
            var profile = profiles.GetProperty(audience);

            Assert.True(profile.TryGetProperty("McpServerToolGrants", out var grants));
            Assert.False(grants.TryGetProperty("old-server", out _));
            Assert.True(grants.TryGetProperty("new-server", out _));

            var approvalDefaults = profile
                .GetProperty("ApprovalPolicy")
                .GetProperty("McpServerDefaults");
            Assert.False(approvalDefaults.TryGetProperty("old-server", out _));
            Assert.True(approvalDefaults.TryGetProperty("new-server", out _));
        }
    }

    [Fact]
    public async Task Add_CreatesApprovalPolicySectionWhenMissing()
    {
        var initial = new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Tools"] = new Dictionary<string, object>
            {
                ["AudienceProfiles"] = new Dictionary<string, object>
                {
                    ["Personal"] = new Dictionary<string, object>
                    {
                        // No ApprovalPolicy section yet.
                        ["McpServersMode"] = "All"
                    }
                }
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.NetclawConfigPath)!);
        File.WriteAllText(_paths.NetclawConfigPath, JsonSerializer.Serialize(initial));

        var args = new[] { "mcp", "add", "--transport", "http", "notion", "https://mcp.notion.com/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var personal = doc.RootElement
            .GetProperty("Tools")
            .GetProperty("AudienceProfiles")
            .GetProperty("Personal");

        Assert.True(personal.TryGetProperty("ApprovalPolicy", out var approvalPolicy));
        Assert.Equal(
            "Auto",
            approvalPolicy.GetProperty("McpServerDefaults").GetProperty("notion").GetString());

        // Pre-existing property should still be there.
        Assert.Equal("All", personal.GetProperty("McpServersMode").GetString());
    }

    // ── Add-time unconditional OAuth hint ──
    //
    // The daemon owns OAuth discovery (RFC 9728/8414, via McpOAuthClientRegistrar).
    // The CLI does not probe the endpoint; it prints an unconditional hint for any
    // HTTP/SSE server added without an explicit Authorization header.

    [Theory]
    [InlineData("stdio")]
    [InlineData("http-with-header")]
    public async Task Add_DoesNotPrintOAuthHint_ForStdioOrExplicitAuthorizationHeader(string scenario)
    {
        var args = scenario is "stdio"
            ? new[] { "mcp", "add", "--transport", "stdio", "local", "--", "npx", "-y", "@local/mcp" }
            : new[] { "mcp", "add", "--transport", "http", "--header", "Authorization: Bearer test-token", "myapi", "https://api.example.com/mcp" };

        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var output = _output.ToString();
        Assert.DoesNotContain("Next steps:", output);
        Assert.DoesNotContain("netclaw mcp auth", output);
        Assert.Contains("Next: run `netclaw mcp permissions`", output);
    }

    [Fact]
    public async Task Add_HttpServerWithoutAuthorizationHeader_PrintsUnconditionalAuthHint()
    {
        var args = new[] { "mcp", "add", "--transport", "http", "plain", "https://plain.example/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var output = _output.ToString();
        Assert.Contains("Next steps:", output);
        Assert.Contains("If this server requires OAuth, authorize first: netclaw mcp auth plain", output);
        Assert.Contains("Then grant tools: netclaw mcp permissions", output);
    }

    [Fact]
    public async Task Add_WithAuthFlag_NoDaemon_PrintsFallbackHint()
    {
        var args = new[] { "mcp", "add", "--auth", "--transport", "http", "notion", "https://mcp.notion.com/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var output = _output.ToString();
        Assert.Contains("Next steps:", output);
        Assert.Contains("authorize first: netclaw mcp auth notion", output);
        Assert.Contains("--auth: daemon API not available. Run `netclaw mcp auth notion` once the daemon is running.", output);
    }

    [Fact]
    public async Task Add_WithAuthFlag_DaemonRejects_PropagatesAuthErrorForAddedServer()
    {
        var args = new[] { "mcp", "add", "--auth", "--transport", "http", "notion", "https://mcp.notion.com/mcp" };
        var daemonApi = CreateDaemonApi(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/mcp/oauth/start/notion" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var exitCode = await McpCommand.RunAsync(
            args, _paths, daemonApi, output: _output);

        // The auth flow must target the added server ('notion'), not the '--auth'
        // flag position — a wrong name would print "MCP server '--auth' not found."
        Assert.Equal(1, exitCode);
        Assert.Contains("HTTP 403 Forbidden", _output.ToString());
        Assert.Contains("notion", _output.ToString());
    }

    [Fact]
    public async Task Add_WithAuthFlag_OnStdio_Ignored()
    {
        var args = new[] { "mcp", "add", "--auth", "--transport", "stdio", "local", "--", "npx", "-y", "@local/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var output = _output.ToString();
        Assert.Contains("--auth ignored: OAuth is only for HTTP/SSE servers.", output);
        Assert.Contains("netclaw mcp permissions", output);
    }

    [Fact]
    public async Task List_NoServers_ShowsEmptyMessage()
    {
        await McpCommand.RunAsync(["mcp", "list"], _paths, output: _output);

        Assert.Contains("No MCP servers configured", _output.ToString());
    }

    [Fact]
    public async Task List_ShowsConfiguredServersWithDaemonReportedStatus()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp"],
            _paths, output: _output);

        var daemonApi = CreateDaemonApi(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/mcp/statuses" => FakeHttpMessageHandler.JsonResponse(new
            {
                memorizer = new
                {
                    state = "Connected",
                    toolCount = 4,
                    error = (string?)null,
                }
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var listOutput = new StringWriter();
        var exitCode = await McpCommand.RunAsync(["mcp", "list"], _paths, daemonApi, listOutput);
        var output = listOutput.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("memorizer", output);
        Assert.Contains("stdio", output);
        Assert.Contains("connected (4 tools)", output);
    }

    [Fact]
    public async Task List_WithoutDaemon_ShowsExplicitUnavailableStatus()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp"],
            _paths, output: _output);

        var listOutput = new StringWriter();
        var exitCode = await McpCommand.RunAsync(["mcp", "list"], _paths, output: listOutput);
        var output = listOutput.ToString();

        Assert.Equal(1, exitCode);
        Assert.Contains("Live MCP status unavailable", output);
        Assert.Contains("status unavailable", output);
        Assert.DoesNotContain("unreachable", output);
    }

    [Fact]
    public async Task List_WhenDaemonDoesNotTrackServer_ShowsRestartHint()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "textforge", "https://textforge.net/mcp"],
            _paths, output: _output);

        var daemonApi = CreateDaemonApi(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/mcp/statuses" => FakeHttpMessageHandler.JsonResponse(new { }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var listOutput = new StringWriter();
        var exitCode = await McpCommand.RunAsync(["mcp", "list"], _paths, daemonApi, listOutput);
        var output = listOutput.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("status unavailable", output);
        Assert.Contains("restart daemon to load this config", output);
    }

    [Fact]
    public async Task List_DisabledServer_ShowsDisabledStatus()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp"],
            _paths, output: _output);
        await McpCommand.RunAsync(
            ["mcp", "disable", "memorizer"], _paths, output: _output);

        var listOutput = new StringWriter();
        await McpCommand.RunAsync(["mcp", "list"], _paths, output: listOutput);
        var output = listOutput.ToString();

        Assert.Contains("memorizer", output);
        Assert.Contains("disabled", output);
    }

    [Fact]
    public async Task Get_ShowsServerDetails()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp"],
            _paths, output: _output);

        var getOutput = new StringWriter();
        await McpCommand.RunAsync(["mcp", "get", "memorizer"], _paths, output: getOutput);
        var output = getOutput.ToString();

        Assert.Contains("Name:", output);
        Assert.Contains("memorizer", output);
        Assert.Contains("Transport:", output);
        Assert.Contains("stdio", output);
    }

    [Fact]
    public async Task Get_NotFound_ReturnsError()
    {
        var exitCode = await McpCommand.RunAsync(
            ["mcp", "get", "nonexistent"], _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Remove_DeletesFromConfig()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp"],
            _paths, output: _output);

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "remove", "memorizer"], _paths, output: _output);

        Assert.Equal(0, exitCode);

        // Verify it's gone
        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.Empty(servers);
    }

    [Fact]
    public async Task Remove_NotFound_ReturnsError()
    {
        var exitCode = await McpCommand.RunAsync(
            ["mcp", "remove", "nonexistent"], _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Disable_TogglesEnabled()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp"],
            _paths, output: _output);

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "disable", "memorizer"], _paths, output: _output);

        Assert.Equal(0, exitCode);

        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.False(servers["memorizer"].Enabled);
    }

    [Fact]
    public async Task Enable_TogglesEnabled()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp"],
            _paths, output: _output);
        await McpCommand.RunAsync(
            ["mcp", "disable", "memorizer"], _paths, output: _output);

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "enable", "memorizer"], _paths, output: _output);

        Assert.Equal(0, exitCode);

        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.True(servers["memorizer"].Enabled);
    }

    [Fact]
    public async Task ReadPasteRedirectAsync_InvalidUrl_AllowsRetryUntilSuccess()
    {
        var lines = new Queue<string?>([
            "not-a-url",
            "http://127.0.0.1:5199/api/mcp/oauth/callback?code=auth-code&state=flow-state&iss=https%3A%2F%2Fauth.example"
        ]);

        var submissions = 0;
        var output = new StringWriter();

        var result = await McpCommand.ReadPasteRedirectAsync(
            output,
            _ => Task.FromResult(lines.Dequeue()),
            (code, state, iss, _) =>
            {
                submissions++;
                Assert.Equal("auth-code", code);
                Assert.Equal("flow-state", state);
                // The MCP SDK validates iss per RFC 9207. Dropping it here makes every
                // headless authorization fail against a server that advertises it.
                Assert.Equal("https://auth.example", iss);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, submissions);
        Assert.Contains("Invalid redirect URL", output.ToString());
    }

    [Fact]
    public async Task ReadPasteRedirectAsync_RejectedRedirect_AllowsRetry()
    {
        var lines = new Queue<string?>([
            "http://127.0.0.1:5199/api/mcp/oauth/callback?code=bad-code&state=flow-state",
            "http://127.0.0.1:5199/api/mcp/oauth/callback?code=good-code&state=flow-state"
        ]);

        var submissions = 0;
        var output = new StringWriter();

        var result = await McpCommand.ReadPasteRedirectAsync(
            output,
            _ => Task.FromResult(lines.Dequeue()),
            (code, _, _, _) =>
            {
                submissions++;
                var status = code == "bad-code"
                    ? HttpStatusCode.BadRequest
                    : HttpStatusCode.OK;
                return Task.FromResult(new HttpResponseMessage(status));
            },
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, submissions);
        Assert.Contains("Redirect URL was rejected", output.ToString());
    }

    [Fact]
    public void TryEmitOsc52Copy_StringWriter_ReturnsFalse()
    {
        var output = new StringWriter();

        var copied = McpCommand.TryEmitOsc52Copy(output, "https://example.com/oauth");

        Assert.False(copied);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Auth_EmptyErrorBodyFallsBackToHttpStatusAndReason()
    {
        await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "oauth", "https://mcp.example/mcp"],
            _paths,
            output: _output);
        var daemonApi = CreateDaemonApi(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/mcp/oauth/start/oauth" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(["mcp", "auth", "oauth"], _paths, daemonApi, output);

        Assert.Equal(1, exitCode);
        Assert.Contains("HTTP 403 Forbidden", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Error: \n", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMcpError_MalformedBodyFallsBackToHttpStatusAndReason()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "text/plain"),
        };

        var message = await McpCommand.ReadMcpErrorAsync(response);

        Assert.Equal("HTTP 502 Bad Gateway", message);
    }

    [Fact]
    public async Task Tools_Revoke_AllMcpServersMode_WritesDenyOverrideNotGrantAllowlist()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
        { "configVersion": 1, "Tools": { "AudienceProfiles": { "Personal": { "McpServersMode": "All" } } } }
        """);
        var daemonApi = ToolsDaemonApi("dropbox", "copy", "delete");

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "tools", "dropbox", "--revoke", "delete", "--audience", "personal"],
            _paths, daemonApi, _output);

        Assert.Equal(0, exitCode);
        using var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var personal = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles").GetProperty("Personal");
        Assert.Equal(
            "Deny",
            personal.GetProperty("ApprovalPolicy").GetProperty("ToolOverrides").GetProperty("dropbox/delete").GetString());
        Assert.False(personal.TryGetProperty("McpServerToolGrants", out _));
    }

    [Fact]
    public async Task Tools_Grant_AllMcpServersMode_ClearsDenyOverride()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
        {
          "configVersion": 1,
          "Tools": { "AudienceProfiles": { "Personal": {
            "McpServersMode": "All",
            "ApprovalPolicy": { "ToolOverrides": { "dropbox/delete": "Deny" } }
          } } }
        }
        """);
        var daemonApi = ToolsDaemonApi("dropbox", "copy", "delete");

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "tools", "dropbox", "--grant", "delete", "--audience", "personal"],
            _paths, daemonApi, _output);

        Assert.Equal(0, exitCode);
        using var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var overrides = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles")
            .GetProperty("Personal").GetProperty("ApprovalPolicy").GetProperty("ToolOverrides");
        Assert.False(overrides.TryGetProperty("dropbox/delete", out _));
    }

    [Fact]
    public async Task Tools_Grant_AllMcpServersMode_ClearsAliasDenyOverride()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
        {
          "configVersion": 1,
          "Tools": { "AudienceProfiles": { "Personal": {
            "McpServersMode": "All",
            "ApprovalPolicy": { "ToolOverrides": { "dropbox__delete": "Deny" } }
          } } }
        }
        """);
        var daemonApi = ToolsDaemonApi("dropbox", "copy", "delete");

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "tools", "dropbox", "--grant", "delete", "--audience", "personal"],
            _paths, daemonApi, _output);

        Assert.Equal(0, exitCode);
        using var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var overrides = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles")
            .GetProperty("Personal").GetProperty("ApprovalPolicy").GetProperty("ToolOverrides");
        Assert.False(overrides.TryGetProperty("dropbox/delete", out _));
        Assert.False(overrides.TryGetProperty("dropbox__delete", out _));
    }

    [Fact]
    public async Task Tools_Grant_AllMcpServersMode_PreservesApprovalOverride()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
        {
          "configVersion": 1,
          "Tools": { "AudienceProfiles": { "Personal": {
            "McpServersMode": "All",
            "ApprovalPolicy": { "ToolOverrides": { "dropbox/copy": "Approval" } }
          } } }
        }
        """);
        var daemonApi = ToolsDaemonApi("dropbox", "copy", "delete");

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "tools", "dropbox", "--grant", "copy", "--audience", "personal"],
            _paths, daemonApi, _output);

        Assert.Equal(0, exitCode);
        using var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var overrides = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles")
            .GetProperty("Personal").GetProperty("ApprovalPolicy").GetProperty("ToolOverrides");
        Assert.Equal("Approval", overrides.GetProperty("dropbox/copy").GetString());
    }

    [Fact]
    public async Task Tools_Snapshot_AllMcpServersMode_IsRejected()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
        { "configVersion": 1, "Tools": { "AudienceProfiles": { "Personal": { "McpServersMode": "All" } } } }
        """);
        var daemonApi = ToolsDaemonApi("dropbox", "copy", "delete");

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "tools", "dropbox", "--snapshot", "--audience", "personal"],
            _paths, daemonApi, _output);

        Assert.Equal(1, exitCode);
        Assert.Contains("All MCP server mode", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_Grant_AllMcpServersMode_OverServerDefaultDeny_WritesApprovalOverride()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
        {
          "configVersion": 1,
          "Tools": { "AudienceProfiles": { "Personal": {
            "McpServersMode": "All",
            "ApprovalPolicy": { "McpServerDefaults": { "dropbox": "Deny" } }
          } } }
        }
        """);
        var daemonApi = ToolsDaemonApi("dropbox", "copy", "delete");

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "tools", "dropbox", "--grant", "copy", "--audience", "personal"],
            _paths, daemonApi, _output);

        Assert.Equal(0, exitCode);
        using var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var overrides = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles")
            .GetProperty("Personal").GetProperty("ApprovalPolicy").GetProperty("ToolOverrides");
        Assert.Equal("Approval", overrides.GetProperty("dropbox/copy").GetString());
    }

    private static DaemonApi ToolsDaemonApi(string serverName, params string[] tools)
    {
        var body = JsonSerializer.Serialize(tools);
        return CreateDaemonApi(request => request.RequestUri!.AbsolutePath == $"/api/mcp/tools/{serverName}"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static JsonDocument ReadConfigFile(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-daemon-api-test-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();

        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, paths);
    }

}
