// -----------------------------------------------------------------------
// <copyright file="SmokeMcpPromptSkillTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

[Collection(McpSmokeChildProcessCollection.Name)]
public sealed class SmokeMcpPromptSkillTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dotnet")]
    [InlineData("python")]
    public async Task ManagerDiscoversAndLoadsPromptOverStdio(string serverKind)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var setup = CreateSetup(serverKind);
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = setup.Command,
            Arguments = setup.CommandArguments,
            Enabled = true,
        };

        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke"] = entry },
            new ToolRegistry(),
            output);

        await harness.Manager.StartAsync(cts.Token);

        // Fail with the manager's real connect error instead of an opaque null:
        // StartAsync swallows TimeoutException into an Unreachable status, so a
        // bare null-check hides whether the python stdio handshake timed out.
        harness.AssertConnected("smoke");
        var skill = Assert.IsType<SkillEntry>(harness.SkillRegistry.GetByName(setup.SkillName));
        var source = Assert.IsType<McpPromptSkillSource>(skill.Source);
        Assert.Equal(setup.PromptArgumentNames, source.Arguments.Select(static argument => argument.Name));
        Assert.Contains(setup.IndexSignature,
            harness.SkillIndex.GetContextLayer(TrustAudience.Personal),
            StringComparison.Ordinal);

        var result = await harness.Manager.LoadAsync(
            source,
            setup.PromptArguments,
            TestToolExecutionContext.CreateUnboundWithoutApproval(TrustAudience.Personal).Invocation,
            cts.Token);

        Assert.True(result.Success, result.Error);
        var message = Assert.Single(result.Messages);
        Assert.Equal("user", message.Role);
        Assert.All(setup.ExpectedText,
            expected => Assert.Contains(expected, message.Text, StringComparison.Ordinal));
    }

    private static PromptSmokeSetup CreateSetup(string serverKind)
        => serverKind switch
        {
            "dotnet" => new PromptSmokeSetup(
                "dotnet",
                [SmokeMcpServerLocator.LocateDll()],
                "mcp__smoke__verify-sum",
                ["left", "right"],
                "mcp__smoke__verify-sum <left> <right>",
                new Dictionary<string, string>
                {
                    ["left"] = "20",
                    ["right"] = "22",
                },
                ["SMOKE-MCP-PROMPT-V1", "a=20", "b=22"]),
            "python" => new PromptSmokeSetup(
                "python3",
                [Path.Combine(
                    SmokeMcpServerLocator.LocateRepositoryRoot(),
                    "evals",
                    "fixtures",
                    "mcp",
                    "prompt_server.py")],
                "mcp__smoke__property-analytics",
                ["property"],
                "mcp__smoke__property-analytics <property>",
                new Dictionary<string, string> { ["property"] = "alpha" },
                ["EVAL-MCP-PROMPT-7421", "property alpha"]),
            _ => throw new ArgumentOutOfRangeException(nameof(serverKind), serverKind, null),
        };

    private sealed record PromptSmokeSetup(
        string Command,
        string[] CommandArguments,
        string SkillName,
        string[] PromptArgumentNames,
        string IndexSignature,
        IReadOnlyDictionary<string, string> PromptArguments,
        string[] ExpectedText);
}
