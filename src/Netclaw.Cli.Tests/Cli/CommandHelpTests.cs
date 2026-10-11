// -----------------------------------------------------------------------
// <copyright file="CommandHelpTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Approvals;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Memory;
using Netclaw.Cli.Model;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Reminder;
using Netclaw.Cli.Secrets;
using Netclaw.Cli.Skills;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// <c>&lt;group&gt; &lt;subcommand&gt; --help</c> must print usage and stop. A dispatcher that
/// checks only the subcommand slot reads the help token as a name or a flag, and can run the
/// subcommand on it (<c>mcp add --help</c> could create a server called <c>--help</c>).
/// </summary>
public sealed class CommandHelpTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public CommandHelpTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> Subcommands() =>
    [
        "mcp add", "mcp auth", "mcp list", "mcp get", "mcp remove", "mcp enable", "mcp disable",
        "provider list", "provider add", "provider rename", "provider remove",
        "model list", "model set", "model discover", "model clear",
        "approvals list", "approvals revoke", "approvals trust-verb",
        "skill list", "skill sync", "skill show", "skill validate", "skill remove", "skill issues",
        "skill search", "skill source list", "skill source add", "skill source remove",
        "skill source enable", "skill source disable",
        "memory backfill-embeddings",
        "webhooks list", "webhooks show", "webhooks set", "webhooks delete", "webhooks validate",
        "reminder list", "reminder create", "reminder cancel", "reminder delete", "reminder disable",
        "reminder enable", "reminder import", "reminder validate", "reminder show", "reminder history",
        "reminder status", "reminder run",
        "secrets set", "secrets add",
    ];

    [Theory]
    [MemberData(nameof(Subcommands))]
    public async Task HelpFlag_AfterSubcommand_PrintsUsageAndChangesNothing(string commandLine)
    {
        foreach (var flag in new[] { "--help", "-h" })
        {
            string[] args = [.. commandLine.Split(' '), flag];
            var before = Snapshot();
            using var output = new StringWriter();

            var exitCode = await RunAsync(args, output);

            Assert.Equal(0, exitCode);
            Assert.StartsWith($"Usage: netclaw {args[0]}", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(before, Snapshot());
        }
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("help")]
    public async Task McpAdd_NewServerNamedLikeAHelpRequest_IsRejected(string name)
    {
        using var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", name, "https://mcp.example.test/mcp"],
            _paths,
            output: output);

        Assert.Equal(1, exitCode);
        Assert.Contains("is not a valid server name", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(_paths.NetclawConfigPath));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-x")]
    [InlineData("help")]
    public async Task McpExistingServerNamedLikeAHelpRequest_CanStillBeDisabledEnabledAndRemoved(string name)
    {
        // 0.27.1 could create all three, so they must stay reachable. After "--" the name is
        // never read as a flag.
        File.WriteAllText(
            _paths.NetclawConfigPath,
            new System.Text.Json.Nodes.JsonObject
            {
                ["McpServers"] = new System.Text.Json.Nodes.JsonObject
                {
                    [name] = new System.Text.Json.Nodes.JsonObject { ["Transport"] = "stdio", ["Command"] = "npx" },
                    ["keep"] = new System.Text.Json.Nodes.JsonObject { ["Transport"] = "stdio", ["Command"] = "npx" },
                },
            }.ToJsonString());

        foreach (var (verb, expected) in new[] { ("disable", "Disabled"), ("enable", "Enabled"), ("remove", "Removed") })
        {
            using var output = new StringWriter();
            Assert.Equal(0, await McpCommand.RunAsync(["mcp", verb, "--", name], _paths, output: output));
            Assert.StartsWith($"{expected} MCP server '{name}'", output.ToString(), StringComparison.Ordinal);
        }

        var config = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.Contains("\"keep\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{name}\"", config, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-x")]
    public async Task McpRemove_ExistingServerNamedLikeAFlag_IsRemovedWithoutTheSeparator(string name)
    {
        File.WriteAllText(
            _paths.NetclawConfigPath,
            new System.Text.Json.Nodes.JsonObject
            {
                ["McpServers"] = new System.Text.Json.Nodes.JsonObject
                {
                    [name] = new System.Text.Json.Nodes.JsonObject { ["Transport"] = "stdio", ["Command"] = "npx" },
                },
            }.ToJsonString());
        using var output = new StringWriter();

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", name], _paths, output: output));

        Assert.DoesNotContain($"\"{name}\"", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remove", "-x")]
    [InlineData("disable", "-x")]
    public async Task McpUnknownServerNamedLikeAFlag_IsNotFoundNotCreated(string verb, string name)
    {
        using var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(["mcp", verb, "--", name], _paths, output: output);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpRemove_HelpFlagBesideAnExistingName_PrintsUsageAndRemovesNothing()
    {
        File.WriteAllText(
            _paths.NetclawConfigPath,
            """{"McpServers":{"keep":{"Transport":"stdio","Command":"npx"}}}""");
        var before = Snapshot();
        using var output = new StringWriter();

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", "keep", "--help"], _paths, output: output));

        Assert.StartsWith("Usage: netclaw mcp", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task McpAdd_HelpAsAnOperand_IsNotAHelpRequest()
    {
        using var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "add", "-t", "stdio", "srv", "npx", "help"], _paths, output: output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Added MCP server 'srv'", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsSet_KeyNamedHelp_StoresTheSecret()
    {
        using var output = new StringWriter();

        Assert.Equal(0, SecretsCommand.Run(["secrets", "set", "help", "v"], _paths, output));

        Assert.Contains("help", File.ReadAllText(_paths.SecretsPath), StringComparison.Ordinal);
    }

    public static TheoryData<string> BareHelpOperands() =>
    [
        "approvals trust-verb help",
        "approvals revoke --tool help --all",
        "skill show help",
        "skill search help",
        "skill source add help --path /tmp",
        "provider remove help",
    ];

    [Theory]
    [MemberData(nameof(BareHelpOperands))]
    public async Task BareHelp_AsAnOperand_IsNotAHelpRequest(string commandLine)
    {
        string[] args = commandLine.Split(' ');
        using var help = new StringWriter();
        await RunAsync([args[0], "--help"], help);
        using var output = new StringWriter();

        await RunAsync(args, output);

        Assert.NotEqual(help.ToString(), output.ToString());
    }

    [Fact]
    public async Task McpAdd_ArgumentsAfterDoubleDash_AreNotHelpRequests()
    {
        using var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "tool", "--", "server", "--help"],
            _paths,
            output: output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Added MCP server 'tool'", output.ToString(), StringComparison.Ordinal);
    }

    private Task<int> RunAsync(string[] args, StringWriter output)
    {
        using var error = new StringWriter();
        var configuration = new ConfigurationBuilder().Build();
        return args[0] switch
        {
            "mcp" => McpCommand.RunAsync(args, _paths, output: output),
            "provider" => ProviderCommand.RunAsync(args, _paths, output: output),
            "model" => ModelCommand.RunAsync(args, _paths, output: output),
            "approvals" => ApprovalsCommand.RunAsync(
                new CliContext(_paths, TimeProvider.System, TextReader.Null, output, error), args),
            "skill" => SkillCommand.RunAsync(args, _paths, output: output),
            "memory" => MemoryCommand.RunAsync(args, _paths, configuration, output, error),
            "webhooks" => WebhooksCommand.RunAsync(args, _paths, output, error: error),
            "reminder" => ReminderCommand.RunAsync(args, daemonApi: null, output, error),
            "secrets" => Task.FromResult(SecretsCommand.Run(args, _paths, output)),
            _ => throw new ArgumentOutOfRangeException(nameof(args), args[0], "Unknown command group."),
        };
    }

    private string Snapshot() => string.Join(
        "\n",
        Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => $"{file}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"));
}
