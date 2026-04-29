// -----------------------------------------------------------------------
// <copyright file="SystemPromptAssemblerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Configuration;

public class SystemPromptAssemblerTests
{
    [Fact]
    public void Assemble_all_layers_present_concatenates_with_separator()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "You are a helpful assistant.",
            agents: "Follow these rules.",
            tooling: "Docker is available.");

        Assert.Contains("You are a helpful assistant.", result);
        Assert.Contains("Follow these rules.", result);
        Assert.Contains("Docker is available.", result);
    }

    [Fact]
    public void Assemble_missing_soul_skips_layer()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: null,
            agents: "Follow these rules.",
            tooling: "Docker is available.");

        Assert.DoesNotContain("soul", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Follow these rules.", result);
        Assert.Contains("Docker is available.", result);
    }

    [Fact]
    public void Assemble_missing_agents_skips_layer()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "You are helpful.",
            agents: null,
            tooling: "kubectl available.");

        Assert.Contains("You are helpful.", result);
        Assert.Contains("kubectl available.", result);
    }

    [Fact]
    public void Assemble_missing_tooling_skips_layer()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "You are helpful.",
            agents: "Be concise.",
            tooling: null);

        Assert.Contains("You are helpful.", result);
        Assert.Contains("Be concise.", result);
    }

    [Fact]
    public void Assemble_all_layers_missing_returns_empty()
    {
        var result = SystemPromptAssembler.Assemble();

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Assemble_whitespace_only_layers_treated_as_missing()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "   ",
            agents: "\n\t",
            tooling: "");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Assemble_trims_layer_content()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "  You are helpful.  \n",
            agents: "\nBe concise.\n");

        Assert.Equal("You are helpful.\n\nBe concise.", result);
    }

    [Fact]
    public void Assemble_layers_in_correct_order()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "Soul content.",
            agents: "Agents content.",
            tooling: "Tooling content.");

        var soulIndex = result.IndexOf("Soul content.", StringComparison.Ordinal);
        var agentsIndex = result.IndexOf("Agents content.", StringComparison.Ordinal);
        var toolingIndex = result.IndexOf("Tooling content.", StringComparison.Ordinal);
        Assert.True(soulIndex < agentsIndex);
        Assert.True(agentsIndex < toolingIndex);
    }

    [Fact]
    public void Assemble_single_layer_returns_without_separator()
    {
        var result = SystemPromptAssembler.Assemble(soul: "Just me.");

        Assert.Equal("Just me.", result);
        Assert.DoesNotContain("\n\n", result);
    }

    [Fact]
    public void AssembleLegacy_maps_old_names_to_new()
    {
        var result = SystemPromptAssembler.AssembleLegacy(
            personality: "Old personality.",
            instructions: "Old instructions.",
            userPreferences: "Old prefs.");

        Assert.Contains("Old personality.", result);
        Assert.Contains("Old instructions.", result);
        Assert.Contains("Old prefs.", result);
    }
}
