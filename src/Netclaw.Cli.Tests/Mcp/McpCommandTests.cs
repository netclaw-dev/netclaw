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
        Assert.Equal("secret123", loaded["myserver"].EnvironmentVariables?["API_KEY"]);
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
        Assert.Equal("Bearer tok-123", loaded["myapi"].Headers?["Authorization"]);
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

        foreach (var audience in new[] { "Personal", "Team", "Public" })
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
        Assert.Contains("0 tools granted", output);
        Assert.Contains("netclaw mcp permissions", output);
        Assert.Contains("Personal=Approval", output);
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
            var expected = audience == "Public" ? "Deny" : "Approval";
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

        foreach (var audience in new[] { "Personal", "Team", "Public" })
        {
            var profile = profiles.GetProperty(audience);

            // old-server MUST NOT have defaults written.
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
            "Approval",
            approvalPolicy.GetProperty("McpServerDefaults").GetProperty("notion").GetString());

        // Pre-existing property should still be there.
        Assert.Equal("All", personal.GetProperty("McpServersMode").GetString());
    }

    [Fact]
    public async Task List_NoServers_ShowsEmptyMessage()
    {
        await McpCommand.RunAsync(new[] { "mcp", "list" }, _paths, output: _output);

        Assert.Contains("No MCP servers configured", _output.ToString());
    }

    [Fact]
    public async Task List_ShowsConfiguredServersWithDaemonReportedStatus()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths, output: _output);

        var daemonApi = CreateDaemonApi(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/mcp/statuses" => JsonResponse(new
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
        var exitCode = await McpCommand.RunAsync(new[] { "mcp", "list" }, _paths, daemonApi, listOutput);
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
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths, output: _output);

        var listOutput = new StringWriter();
        var exitCode = await McpCommand.RunAsync(new[] { "mcp", "list" }, _paths, output: listOutput);
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
            new[] { "mcp", "add", "--transport", "http", "textforge", "https://textforge.net/mcp" },
            _paths, output: _output);

        var daemonApi = CreateDaemonApi(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/mcp/statuses" => JsonResponse(new { }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var listOutput = new StringWriter();
        var exitCode = await McpCommand.RunAsync(new[] { "mcp", "list" }, _paths, daemonApi, listOutput);
        var output = listOutput.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("status unavailable", output);
        Assert.Contains("restart daemon to load this config", output);
    }

    [Fact]
    public async Task List_DisabledServer_ShowsDisabledStatus()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths, output: _output);
        await McpCommand.RunAsync(
            new[] { "mcp", "disable", "memorizer" }, _paths, output: _output);

        var listOutput = new StringWriter();
        await McpCommand.RunAsync(new[] { "mcp", "list" }, _paths, output: listOutput);
        var output = listOutput.ToString();

        Assert.Contains("memorizer", output);
        Assert.Contains("disabled", output);
    }

    [Fact]
    public async Task Get_ShowsServerDetails()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths, output: _output);

        var getOutput = new StringWriter();
        await McpCommand.RunAsync(new[] { "mcp", "get", "memorizer" }, _paths, output: getOutput);
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
            new[] { "mcp", "get", "nonexistent" }, _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Remove_DeletesFromConfig()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths, output: _output);

        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "remove", "memorizer" }, _paths, output: _output);

        Assert.Equal(0, exitCode);

        // Verify it's gone
        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.Empty(servers);
    }

    [Fact]
    public async Task Remove_NotFound_ReturnsError()
    {
        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "remove", "nonexistent" }, _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Disable_TogglesEnabled()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths, output: _output);

        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "disable", "memorizer" }, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.False(servers["memorizer"].Enabled);
    }

    [Fact]
    public async Task Enable_TogglesEnabled()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths, output: _output);
        await McpCommand.RunAsync(
            new[] { "mcp", "disable", "memorizer" }, _paths, output: _output);

        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "enable", "memorizer" }, _paths, output: _output);

        Assert.Equal(0, exitCode);

        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.True(servers["memorizer"].Enabled);
    }

    [Fact]
    public async Task ReadPasteRedirectAsync_InvalidUrl_AllowsRetryUntilSuccess()
    {
        var lines = new Queue<string?>([
            "not-a-url",
            "http://127.0.0.1:5199/api/mcp/oauth/callback?code=auth-code&state=flow-state"
        ]);

        var submissions = 0;
        var output = new StringWriter();

        var result = await McpCommand.ReadPasteRedirectAsync(
            output,
            _ => Task.FromResult(lines.Dequeue()),
            (code, state, _) =>
            {
                submissions++;
                Assert.Equal("auth-code", code);
                Assert.Equal("flow-state", state);
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
            (code, _, _) =>
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

    private static JsonDocument ReadConfigFile(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-daemon-api-test-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();

        return new DaemonApi(new StubHttpClientFactory(handler), configuration, paths);
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(_handler));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
