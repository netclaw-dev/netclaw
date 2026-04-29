// -----------------------------------------------------------------------
// <copyright file="SystemPromptAssemblerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    [Fact]
    public void TryReadProjectIdentityFile_returns_null_for_null_directory()
    {
        var result = FileSystemPromptProvider.TryReadProjectIdentityFile(null);
        Assert.Null(result);
    }

    [Fact]
    public void TryReadProjectIdentityFile_returns_null_for_empty_directory()
    {
        var result = FileSystemPromptProvider.TryReadProjectIdentityFile(string.Empty);
        Assert.Null(result);
    }

    [Fact]
    public void TryReadProjectIdentityFile_returns_null_when_no_candidates_exist()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var result = FileSystemPromptProvider.TryReadProjectIdentityFile(tmpDir);
            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void TryReadProjectIdentityFile_reads_CLAUDE_md()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        File.WriteAllText(Path.Combine(tmpDir, "CLAUDE.md"), "Project instructions here");
        try
        {
            var result = FileSystemPromptProvider.TryReadProjectIdentityFile(tmpDir);
            Assert.Equal("Project instructions here", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void TryReadProjectIdentityFile_prefers_netclaw_AGENTS_over_CLAUDE()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(Path.Combine(tmpDir, ".netclaw"));
        File.WriteAllText(Path.Combine(tmpDir, ".netclaw", "AGENTS.md"), "Netclaw agents");
        File.WriteAllText(Path.Combine(tmpDir, "CLAUDE.md"), "Claude instructions");
        try
        {
            var result = FileSystemPromptProvider.TryReadProjectIdentityFile(tmpDir);
            Assert.Equal("Netclaw agents", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
