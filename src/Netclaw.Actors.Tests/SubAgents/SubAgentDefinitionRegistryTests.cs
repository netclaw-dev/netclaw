// -----------------------------------------------------------------------
// <copyright file="SubAgentDefinitionRegistryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public class SubAgentDefinitionRegistryTests
{
    private static SubAgentProfile CreateProfile(
        string name = "test-agent",
        SubAgentVisibility visibility = SubAgentVisibility.UserFacing)
    {
        return new SubAgentProfile
        {
            Name = name,
            Description = "Test agent",
            SystemPrompt = "You are a test agent.",
            ToolNames = ["web_search", "file_read"],
            Visibility = visibility
        };
    }

    [Fact]
    public void Register_adds_profile()
    {
        var registry = new SubAgentDefinitionRegistry();
        var profile = CreateProfile();

        Assert.True(registry.Register(profile));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_rejects_duplicate_name()
    {
        var registry = new SubAgentDefinitionRegistry();
        var profile1 = CreateProfile("agent-a");
        var profile2 = CreateProfile("agent-a");

        Assert.True(registry.Register(profile1));
        Assert.False(registry.Register(profile2));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_is_case_insensitive()
    {
        var registry = new SubAgentDefinitionRegistry();
        Assert.True(registry.Register(CreateProfile("Agent-A")));
        Assert.False(registry.Register(CreateProfile("agent-a")));
    }

    [Fact]
    public void TryGetByName_returns_profile()
    {
        var registry = new SubAgentDefinitionRegistry();
        var profile = CreateProfile("my-agent");
        registry.Register(profile);

        var result = registry.TryGetByName("my-agent");
        Assert.NotNull(result);
        Assert.Equal("my-agent", result.Name);
    }

    [Fact]
    public void TryGetByName_is_case_insensitive()
    {
        var registry = new SubAgentDefinitionRegistry();
        registry.Register(CreateProfile("My-Agent"));

        Assert.NotNull(registry.TryGetByName("my-agent"));
        Assert.NotNull(registry.TryGetByName("MY-AGENT"));
    }

    [Fact]
    public void TryGetByName_returns_null_for_unknown()
    {
        var registry = new SubAgentDefinitionRegistry();
        Assert.Null(registry.TryGetByName("nonexistent"));
    }

    [Fact]
    public void GetUserFacing_excludes_internal_agents()
    {
        var registry = new SubAgentDefinitionRegistry();
        registry.Register(CreateProfile("user-agent", SubAgentVisibility.UserFacing));
        registry.Register(CreateProfile("internal-agent", SubAgentVisibility.Internal));

        var userFacing = registry.GetUserFacing();
        Assert.Single(userFacing);
        Assert.Equal("user-agent", userFacing[0].Name);
    }

    [Fact]
    public void GetAll_includes_both_visibilities()
    {
        var registry = new SubAgentDefinitionRegistry();
        registry.Register(CreateProfile("user-agent", SubAgentVisibility.UserFacing));
        registry.Register(CreateProfile("internal-agent", SubAgentVisibility.Internal));

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetUserFacing_returns_sorted_by_name()
    {
        var registry = new SubAgentDefinitionRegistry();
        registry.Register(CreateProfile("zebra-agent"));
        registry.Register(CreateProfile("alpha-agent"));

        var userFacing = registry.GetUserFacing();
        Assert.Equal("alpha-agent", userFacing[0].Name);
        Assert.Equal("zebra-agent", userFacing[1].Name);
    }
}
