// -----------------------------------------------------------------------
// <copyright file="SpawnAgentToolDescriptionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Executable spec (<c>netclaw-subagents</c>, "The spawn_agent description tells the model not to wait"):
/// <c>spawn_agent</c> returns <c>accepted</c> at once, and the result arrives in a later turn. A model that
/// does not know this waits and polls with <c>sleep</c> and <c>check_agent_run</c>. The tool description
/// reaches the model on every turn, so it must say what to do instead.
/// </summary>
public sealed class SpawnAgentToolDescriptionTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Description_tells_the_model_not_to_wait_and_that_the_result_arrives_in_a_later_turn()
    {
        var description = Tool().Description;

        Assert.Contains("Do not wait or poll for the child.", description, StringComparison.Ordinal);
        Assert.Contains("End the turn.", description, StringComparison.Ordinal);
        Assert.Contains("The result arrives in a later turn.", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_does_not_teach_a_polling_loop()
    {
        var description = Tool().Description;

        Assert.DoesNotContain("sleep", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_agent_run", description, StringComparison.Ordinal);
    }

    private SpawnAgentTool Tool()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        return new SpawnAgentTool(new SubAgentDefinitionRegistry(), spawner: null!, paths);
    }
}
