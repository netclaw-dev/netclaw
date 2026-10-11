// -----------------------------------------------------------------------
// <copyright file="McpGrantAllowListTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

/// <summary>
/// <c>mcp tools --grant</c> allows the server for the audience, and what the audience can then
/// call is decided by the production tool access policy over the config file the command wrote.
/// </summary>
public sealed class McpGrantAllowListTests : IDisposable
{
    private static readonly string[] ServerTools = ["add", "echo", "process-info"];

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();

    public McpGrantAllowListTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    private void WriteTeam(string teamBody, string? approval = "\"ApprovalPolicy\": { \"McpServerDefaults\": { \"dropbox\": \"Approval\" } }")
    {
        var parts = new[] { teamBody, approval }.Where(p => !string.IsNullOrWhiteSpace(p));
        File.WriteAllText(_paths.NetclawConfigPath,
            $$"""{ "configVersion": 1, "Tools": { "AudienceProfiles": { "Team": { {{string.Join(",", parts)}} } } } }""");
    }

    private Task<int> RunAsync(params string[] args)
        => McpCommand.RunAsync(["mcp", "tools", "dropbox", .. args], _paths, ToolsDaemonApi(), _output);

    private DaemonApi ToolsDaemonApi()
    {
        var body = JsonSerializer.Serialize(ServerTools);
        var factory = new FakeHttpClientFactory(request => request.RequestUri!.AbsolutePath == "/api/mcp/tools/dropbox"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var paths = new NetclawPaths(Path.Combine(_dir.Path, Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        return new DaemonApi(factory, new ConfigurationBuilder().Build(), paths);
    }

    // The tools the audience can call after the command, by the policy the daemon builds from the file.
    private List<string> Callable(TrustAudience audience)
    {
        var config = ConfigFileHelper.LoadToolConfig(_paths);
        var policy = new ToolAccessPolicy(
            new NetclawPaths(), config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal, ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var context = TestToolExecutionContext.CreateBound("slack/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = audience,
            Boundary = audience == TrustAudience.Team ? TrustBoundary.Team : TrustBoundary.TrustedInstance,
            ChannelType = "slack"
        }).Invocation;

        return ServerTools
            .Where(tool => policy.IsToolExposed(
                new McpToolAdapter(AIFunctionFactory.Create(() => "ok", tool), "dropbox", tool), context))
            .ToList();
    }

    [Fact]
    public async Task Grant_OverAnOldCliEntryThatListsEveryTool_ExposesOnlyTheNamedTool()
    {
        // A 0.27.1 `--grant add` on a server the audience did not allow wrote every tool.
        WriteTeam("\"McpServerToolGrants\": { \"dropbox\": [ \"add\", \"echo\", \"process-info\" ] }");
        Assert.Empty(Callable(TrustAudience.Team));

        Assert.Equal(0, await RunAsync("--grant", "echo", "--audience", "team"));

        Assert.Equal(["echo"], Callable(TrustAudience.Team));
        var output = _output.ToString();
        Assert.Contains("Also allowed server 'dropbox' for Team.", output);
        Assert.Contains("Dropped stale grants from before the server was allowed: add, process-info.", output);
        Assert.Contains("Team can now call: echo (mode: Approval)", output);
    }

    [Fact]
    public async Task Grant_OverAPartialStaleEntry_ExposesOnlyTheNamedTool()
    {
        WriteTeam("\"McpServerToolGrants\": { \"dropbox\": [ \"echo\", \"process-info\" ] }");

        Assert.Equal(0, await RunAsync("--grant", "add", "--audience", "team"));

        Assert.Equal(["add"], Callable(TrustAudience.Team));
        Assert.Contains("Dropped stale grants from before the server was allowed: echo, process-info.", _output.ToString());
    }

    [Fact]
    public async Task Grant_NamingATheStaleToolAgain_KeepsItAndDropsNothing()
    {
        WriteTeam("\"McpServerToolGrants\": { \"dropbox\": [ \"echo\" ] }");

        Assert.Equal(0, await RunAsync("--grant", "echo", "--audience", "team"));

        Assert.Equal(["echo"], Callable(TrustAudience.Team));
        Assert.DoesNotContain("Dropped", _output.ToString());
    }

    [Fact]
    public async Task Grant_OnANewServerWithNoEntry_ExposesOnlyTheNamedTool()
    {
        WriteTeam("");

        Assert.Equal(0, await RunAsync("--grant", "echo,add", "--audience", "team"));

        Assert.Equal(["add", "echo"], Callable(TrustAudience.Team));
    }

    [Fact]
    public async Task Revoke_OnAServerTheAudienceDoesNotAllow_ChangesNothingAndSaysSo()
    {
        WriteTeam("\"McpServerToolGrants\": { \"dropbox\": [] }");
        var before = File.ReadAllText(_paths.NetclawConfigPath);

        var exitCode = await RunAsync("--revoke", "add", "--audience", "team");

        Assert.Equal(1, exitCode);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Contains("Server 'dropbox' is not allowed by the Team audience profile. Nothing changed.", _output.ToString());
    }

    [Fact]
    public async Task Revoke_OnAnAllowedServer_NeverTouchesTheAllowList()
    {
        WriteTeam("\"AllowedMcpServers\": [ \"dropbox\" ], \"McpServerToolGrants\": { \"dropbox\": [ \"add\", \"echo\" ] }");

        Assert.Equal(0, await RunAsync("--revoke", "add", "--audience", "team"));

        Assert.Equal(["echo"], Callable(TrustAudience.Team));
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var team = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles").GetProperty("Team");
        Assert.Equal(1, team.GetProperty("AllowedMcpServers").GetArrayLength());
    }

    [Fact]
    public async Task Grant_KeepsOtherServersInTheAllowListAndTheirGrants()
    {
        WriteTeam("\"AllowedMcpServers\": [ \"other\" ], \"McpServerToolGrants\": { \"other\": [ \"read\" ], \"dropbox\": [] }");

        Assert.Equal(0, await RunAsync("--grant", "add", "--audience", "team"));

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var team = doc.RootElement.GetProperty("Tools").GetProperty("AudienceProfiles").GetProperty("Team");
        Assert.Equal(["other", "dropbox"], team.GetProperty("AllowedMcpServers").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(["read"], team.GetProperty("McpServerToolGrants").GetProperty("other").EnumerateArray().Select(e => e.GetString()).ToList());
    }

    [Fact]
    public async Task Grant_ToPublic_WhenItsApprovalPolicyDeniesTheServer_SaysTheToolCannotBeCalled()
    {
        // What `mcp add` writes for Public.
        File.WriteAllText(_paths.NetclawConfigPath, """
        { "configVersion": 1, "Tools": { "AudienceProfiles": { "Public": {
          "McpServerToolGrants": { "dropbox": [] },
          "ApprovalPolicy": { "McpServerDefaults": { "dropbox": "Deny" } }
        } } } }
        """);

        var exitCode = await RunAsync("--grant", "add", "--audience", "public");

        Assert.Equal(0, exitCode);
        Assert.Empty(Callable(TrustAudience.Public));
        var output = _output.ToString();
        Assert.Contains("Granted, but the Public approval policy denies add, so it cannot be called.", output);
        Assert.Contains("ApprovalPolicy.McpServerDefaults.dropbox", output);
        Assert.DoesNotContain("Public can now call", output);
    }

    [Fact]
    public async Task Grant_OnAServerWithNoApprovalDefault_ReportsTheToolRunsWithoutAsking()
    {
        WriteTeam("", approval: null);

        Assert.Equal(0, await RunAsync("--grant", "add", "--audience", "team"));

        Assert.Contains("Team can now call: add (mode: Auto)", _output.ToString());
    }

    [Fact]
    public async Task Grant_WithLowerCaseKeysAtEveryLevel_WritesNoSecondSpelling()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
        { "configVersion": 1, "tools": { "audienceProfiles": { "team": {
          "allowedMcpServers": [ "other" ],
          "mcpServerToolGrants": { "dropbox": [ "echo" ] }
        } } } }
        """);

        Assert.Equal(0, await RunAsync("--grant", "add", "--audience", "team"));

        // Every later command loads the file, and a duplicate key stops the load.
        var config = ConfigFileHelper.LoadToolConfig(_paths);
        Assert.Equal(["other", "dropbox"], config.AudienceProfiles.Team.AllowedMcpServers);
        Assert.Equal(["add"], config.AudienceProfiles.Team.McpServerToolGrants!["dropbox"]);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        JsonElement level = doc.RootElement;
        foreach (var key in new[] { "Tools", "AudienceProfiles", "Team" })
        {
            Assert.Single(level.EnumerateObject(), p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            level = level.EnumerateObject().Single(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        Assert.Single(level.EnumerateObject(), p => string.Equals(p.Name, "AllowedMcpServers", StringComparison.OrdinalIgnoreCase));
        Assert.Single(level.EnumerateObject(), p => string.Equals(p.Name, "McpServerToolGrants", StringComparison.OrdinalIgnoreCase));
    }
}
