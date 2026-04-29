// -----------------------------------------------------------------------
// <copyright file="SystemPromptAssemblerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SystemPromptAssemblerTests
{
    [Fact]
    public void Assemble_includes_project_instructions_when_provided()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "You are helpful.",
            projectInstructions: "# Project Rules\nUse tabs.");

        Assert.Contains("You are helpful.", result);
        Assert.Contains("# Project Rules", result);
    }

    [Fact]
    public void Assemble_omits_project_instructions_when_null()
    {
        var result = SystemPromptAssembler.Assemble(
            soul: "You are helpful.",
            projectInstructions: null);

        Assert.Contains("You are helpful.", result);
        Assert.DoesNotContain("Project", result);
    }

    [Fact]
    public void Assemble_returns_only_project_instructions_when_other_layers_null()
    {
        var result = SystemPromptAssembler.Assemble(
            projectInstructions: "# Project Rules");

        Assert.Equal("# Project Rules", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryReadProjectIdentityFile_returns_null_for_null_or_empty_directory(string? directory)
    {
        var result = FileSystemPromptProvider.TryReadProjectIdentityFile(directory);
        Assert.Null(result);
    }

    [Fact]
    public void TryReadProjectIdentityFile_returns_null_when_no_candidates_exist()
    {
        using var dir = new DisposableTempDir();
        var result = FileSystemPromptProvider.TryReadProjectIdentityFile(dir.Path);
        Assert.Null(result);
    }

    [Fact]
    public void TryReadProjectIdentityFile_reads_CLAUDE_md()
    {
        using var dir = new DisposableTempDir();
        File.WriteAllText(Path.Combine(dir.Path, "CLAUDE.md"), "Project instructions here");

        var result = FileSystemPromptProvider.TryReadProjectIdentityFile(dir.Path);
        Assert.Equal("Project instructions here", result);
    }

    [Fact]
    public void TryReadProjectIdentityFile_prefers_netclaw_AGENTS_over_CLAUDE()
    {
        using var dir = new DisposableTempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, ".netclaw"));
        File.WriteAllText(Path.Combine(dir.Path, ".netclaw", "AGENTS.md"), "Netclaw agents");
        File.WriteAllText(Path.Combine(dir.Path, "CLAUDE.md"), "Claude instructions");

        var result = FileSystemPromptProvider.TryReadProjectIdentityFile(dir.Path);
        Assert.Equal("Netclaw agents", result);
    }
}
