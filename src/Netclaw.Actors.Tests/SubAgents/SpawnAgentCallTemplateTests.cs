// -----------------------------------------------------------------------
// <copyright file="SpawnAgentCallTemplateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// The subagent discovery context layer goes to the model on each turn. It shows
/// a complete <c>spawn_agent</c> call template. The <c>spawn_agent</c> tool is
/// deferred, so the model often reads this template before it loads the schema.
/// In production sessions, the model copied the template and omitted
/// <c>_rationale</c>. The runtime then rejected the full first batch of
/// <c>spawn_agent</c> calls. A template must name each argument that the
/// schema requires.
/// </summary>
public sealed class SpawnAgentCallTemplateTests
{
    private static readonly Regex CallTemplate = new(@"spawn_agent\((?<args>[^)]*)\)", RegexOptions.CultureInvariant);
    private static readonly Regex ArgumentName = new(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Discovery_call_template_names_every_required_spawn_agent_argument(int agentCount)
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var required = RequiredSpawnAgentArguments(paths);
        Assert.Contains("_rationale", required);

        var index = SubAgentDiscoveryContextLayer.BuildIndex(Profiles(agentCount), paths.AgentsDirectory);

        var templates = CallTemplate.Matches(index).Select(static m => m.Value).ToList();
        Assert.NotEmpty(templates);

        foreach (var template in templates)
        {
            var named = ArgumentName.Matches(template)
                .Select(static m => m.Groups["name"].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = required.Where(name => !named.Contains(name)).ToList();

            Assert.True(missing.Count == 0,
                $"The discovery call template {template} does not name the required spawn_agent "
                + $"argument(s): {string.Join(", ", missing)}. The model copies this template, so a "
                + "first spawn_agent call fails with the required _rationale error.");
        }
    }

    private static IReadOnlyList<string> RequiredSpawnAgentArguments(NetclawPaths paths)
    {
        var registry = new ToolRegistry();
        var policy = new ToolAccessPolicy(paths,
            new ToolConfig(),
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));
        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new FakeChatClient()),
            registry,
            policy,
            approvalService: null,
            new StaticSystemPromptProvider("Test parent."),
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance,
            new SessionConfig());
        var tool = new SpawnAgentTool(new SubAgentDefinitionRegistry(), spawner, paths);

        return tool.ParameterSchema.GetProperty("required")
            .EnumerateArray()
            .Select(static e => e.GetString()!)
            .ToList();
    }

    private static IReadOnlyList<SubAgentProfile> Profiles(int count)
        => Enumerable.Range(0, count)
            .Select(static i => new SubAgentProfile
            {
                Name = $"analyst-{i}",
                Description = "Analyze code.",
                SystemPrompt = "You analyze code.",
                ToolNames = [],
                Visibility = SubAgentVisibility.UserFacing
            })
            .ToList();
}
