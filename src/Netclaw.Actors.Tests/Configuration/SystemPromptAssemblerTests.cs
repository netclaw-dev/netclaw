using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Configuration;

public class SystemPromptAssemblerTests
{
    [Fact]
    public void Assemble_all_layers_present_concatenates_with_separator()
    {
        var result = SystemPromptAssembler.Assemble(
            personality: "You are a helpful assistant.",
            instructions: "Follow these rules.",
            userPreferences: "Owner prefers concise answers.",
            projectAgents: "This project uses C#.");

        Assert.Contains("You are a helpful assistant.", result);
        Assert.Contains("Follow these rules.", result);
        Assert.Contains("Owner prefers concise answers.", result);
        Assert.Contains("This project uses C#.", result);
    }

    [Fact]
    public void Assemble_missing_personality_skips_layer()
    {
        var result = SystemPromptAssembler.Assemble(
            personality: null,
            instructions: "Follow these rules.",
            userPreferences: "Owner prefers concise answers.");

        Assert.DoesNotContain("personality", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Follow these rules.", result);
        Assert.Contains("Owner prefers concise answers.", result);
    }

    [Fact]
    public void Assemble_missing_instructions_skips_layer()
    {
        var result = SystemPromptAssembler.Assemble(
            personality: "You are helpful.",
            instructions: null,
            userPreferences: "Owner likes detail.");

        Assert.Contains("You are helpful.", result);
        Assert.Contains("Owner likes detail.", result);
    }

    [Fact]
    public void Assemble_missing_user_preferences_skips_layer()
    {
        var result = SystemPromptAssembler.Assemble(
            personality: "You are helpful.",
            instructions: "Be concise.",
            userPreferences: null);

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
            personality: "   ",
            instructions: "\n\t",
            userPreferences: "");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Assemble_trims_layer_content()
    {
        var result = SystemPromptAssembler.Assemble(
            personality: "  You are helpful.  \n",
            instructions: "\nBe concise.\n");

        Assert.Equal("You are helpful.\n\nBe concise.", result);
    }

    [Fact]
    public void Assemble_project_agents_overlay_appended_last()
    {
        var result = SystemPromptAssembler.Assemble(
            personality: "Base personality.",
            projectAgents: "Project overlay.");

        // Project overlay appears after personality
        var personalityIndex = result.IndexOf("Base personality.", StringComparison.Ordinal);
        var overlayIndex = result.IndexOf("Project overlay.", StringComparison.Ordinal);
        Assert.True(overlayIndex > personalityIndex);
    }

    [Fact]
    public void Assemble_single_layer_returns_without_separator()
    {
        var result = SystemPromptAssembler.Assemble(personality: "Just me.");

        Assert.Equal("Just me.", result);
        Assert.DoesNotContain("\n\n", result);
    }
}
