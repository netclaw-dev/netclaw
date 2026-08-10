// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxTreeIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Security;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Smoke tests confirming the ShellSyntaxTree contract Netclaw consumes is
/// stable across package upgrades. These are integration-level — they
/// exercise the live package without mocks — so an unexpected package
/// behavior change fails CI loudly before it surfaces in the gate evaluator.
///
/// When the gate evaluator lands, the parser-version-bump CI gate (task
/// 14.7 of approval-policy-trust-zones) runs the entire ShellSyntaxTree
/// corpus through Netclaw's live matcher; these tests are the smaller
/// per-PR canary that catches contract regressions earlier.
/// </summary>
public sealed class ShellSyntaxTreeIntegrationTests
{
    [Fact]
    public void Parser_resolves_through_DI_registration()
    {
        var services = new ServiceCollection();
        services.AddShellParser();

        using var provider = services.BuildServiceProvider();
        var parser = provider.GetRequiredService<IShellParser>();

        var result = parser.Parse("git status");
        Assert.False(result.IsUnparseable);
        Assert.Equal("git status", Assert.Single(result.Commands).Clause.Verb.Joined);
    }

    [Fact]
    public void Explicit_environment_DI_registration_uses_selected_power_shell_dialect()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var services = new ServiceCollection();
        services.AddShellParser(environment);

        using var provider = services.BuildServiceProvider();
        var parser = provider.GetRequiredService<IShellParser>();

        var result = parser.Parse("Get-ChildItem && Get-Content .\\input.txt");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Commands.Count);
    }

    [Fact]
    public void Simple_verb_produces_single_clause()
    {
        var parser = new BashParser();

        var result = parser.Parse("ls -la /tmp");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);

        var clause = result.Clauses[0];
        Assert.Equal(CompoundOperator.None, clause.Operator);
        Assert.Equal("ls", clause.Verb.Joined);
        Assert.Contains(clause.Args, a => a.Raw == "-la" && a.IsFlag);
        Assert.Contains(clause.Args, a => a.Raw == "/tmp" && a.IsPath);

        var occurrence = Assert.Single(result.Commands);
        Assert.Same(clause, occurrence.Clause);
        Assert.Equal(CommandOccurrenceRole.Ordinary, occurrence.ImmediateRole);
        Assert.True(occurrence.IsComplete);
    }

    [Fact]
    public void Verb_chain_extracts_greedily_through_verb_like_tokens()
    {
        // ShellSyntaxTree 0.1.4-alpha (issue #27): the parser extends the
        // verb chain through every "verb-like" token until it hits a flag
        // or a path. So `git push origin main` produces a four-token verb
        // chain rather than collapsing to `git push` via a fixed BashArity
        // table — which is the behavior Netclaw wants for narrow
        // auto-proposed verb patterns (`git push origin main *` is safer
        // to approve than `git push *`).
        var parser = new BashParser();

        var result = parser.Parse("git push origin main");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
        Assert.Equal("git push origin main", result.Clauses[0].Verb.Joined);
    }

    [Fact]
    public void Verb_chain_stops_at_flag_token()
    {
        // `-s` is a flag, so verb-chain extraction must halt before it.
        var parser = new BashParser();

        var result = parser.Parse("git status -s");

        Assert.False(result.IsUnparseable);
        Assert.Equal("git status", result.Clauses[0].Verb.Joined);
        Assert.Contains(result.Clauses[0].Args, a => a.Raw == "-s");
    }

    [Fact]
    public void Verb_chain_stops_at_path_token()
    {
        // A token containing `/` or `.` is path-like, not verb-like, so
        // verb-chain extraction must halt before it.
        var parser = new BashParser();

        var dotted = parser.Parse("cat file.txt");
        Assert.Equal("cat", dotted.Clauses[0].Verb.Joined);

        var slashed = parser.Parse("dotnet test /home/user/repos/Foo");
        Assert.Equal("dotnet test", slashed.Clauses[0].Verb.Joined);
    }

    [Fact]
    public void Multi_token_cli_subcommand_extracts_full_chain()
    {
        // Regression for ShellSyntaxTree #27 — production hit on
        // `git worktree list`. The pre-fix parser stopped at `git
        // worktree`, which propagated to a verb-chain mismatch between
        // approval-prompt time and retry time and surfaced as
        // "I encountered an error executing a tool". Same heuristic now
        // also handles non-git CLIs (`freshdesk ticket list`,
        // `kubectl get pods`, etc.) without per-CLI tables.
        var parser = new BashParser();

        Assert.Equal(
            "git worktree list",
            parser.Parse("git worktree list").Clauses[0].Verb.Joined);
        Assert.Equal(
            "freshdesk ticket list",
            parser.Parse("freshdesk ticket list").Clauses[0].Verb.Joined);
    }

    [Fact]
    public void Compound_with_andif_produces_multiple_clauses()
    {
        var parser = new BashParser();

        var result = parser.Parse("cd /repo && git status");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.Equal("cd", result.Clauses[0].Verb.Joined);
        Assert.Equal("git status", result.Clauses[1].Verb.Joined);
    }

    [Fact]
    public void Cd_in_compound_attributes_target_to_subsequent_clauses()
    {
        // The whole point of consuming ShellSyntaxTree: cd-in-compound
        // propagation lands on subsequent clauses so Netclaw's zone gate
        // sees /repo as a path the second clause operates on.
        var parser = new BashParser();

        var result = parser.Parse("cd /repo && cat file.txt");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);

        var secondClause = result.Clauses[1];
        Assert.Contains(secondClause.Args,
            a => a.IsCwdAttribution && a.Resolved == "/repo");
    }

    [Fact]
    public void Unparseable_input_sets_flag_without_throwing()
    {
        var parser = new BashParser();

        var result = parser.Parse("echo \"unbalanced");

        Assert.True(result.IsUnparseable);
        Assert.False(string.IsNullOrEmpty(result.UnparseableReason));
    }

    [Fact]
    public void Unknown_variable_state_is_unparseable()
    {
        // Netclaw cannot prove the inherited Bash variable attributes.
        // Alpha.1 rejects the full command before a nameref can hide
        // execution inside the path expression.
        var parser = new BashParser();

        var result = parser.Parse("rm $UNRESOLVED/foo");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Clauses);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Command_string_wrapper_marks_inner_clause()
    {
        var parser = new BashParser();

        var result = parser.Parse("bash -c \"git status\"");

        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.True(clause.IsCommandStringWrapped);
        Assert.Equal("git status", clause.Verb.Joined);
        Assert.Null(clause.Verb.CanonicalVerb);
    }

    [Fact]
    public void Leading_line_comment_is_stripped_from_clause_extraction()
    {
        // Regression test for ShellSyntaxTree #25 — bash line comments
        // (# starting a token, runs to end-of-line) must not appear as
        // verb-chain content. Pre-fix this surfaced as approval prompts
        // saying "Approve `# Get` in ..." and persistence-versus-recheck
        // verb-set mismatches that broke ApprovedSession on commented
        // commands. Fixed in ShellSyntaxTree 0.1.3-alpha.
        var parser = new BashParser();

        var result = parser.Parse(
            "# fetch the latest\ngit pull origin main");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
        Assert.Equal("git pull origin main", result.Clauses[0].Verb.Joined);
    }

    [Fact]
    public void Hash_inside_double_quotes_is_not_a_comment()
    {
        // Per POSIX, # is only a comment when starting a word AND outside
        // quotes. echo "hash is #1234" should produce one verb (echo)
        // with one literal arg containing the hash sign.
        var parser = new BashParser();

        var result = parser.Parse("echo \"hash is #1234\"");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
        Assert.Equal("echo", result.Clauses[0].Verb.Joined);
        Assert.Contains(result.Clauses[0].Args, a => a.Raw.Contains("#1234"));
    }

    [Fact]
    public void Bare_newline_separates_clauses_as_sequence()
    {
        // ShellSyntaxTree 0.1.5-beta (SPEC §4): a bare newline outside
        // quotes/heredocs/continuations is a clause separator equivalent to
        // `;`, and the following clause gets CompoundOperator.Sequence.
        // Netclaw's approval gate relies on this so a multi-line command
        // decomposes into one approval unit per statement rather than
        // collapsing two verbs into a single garbled unit.
        var parser = new BashParser();

        var result = parser.Parse("git fetch\ngit status");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal("git fetch", result.Clauses[0].Verb.Joined);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
        Assert.Equal("git status", result.Clauses[1].Verb.Joined);
    }

    [Fact]
    public void Consecutive_newlines_collapse_without_empty_clauses()
    {
        // Blank lines, leading/trailing newlines, and newlines after a
        // compound operator collapse — they must not produce empty clauses
        // that would surface as blank approval units.
        var parser = new BashParser();

        var result = parser.Parse("\necho a\n\n\necho b\n");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal("echo a", result.Clauses[0].Verb.Joined);
        Assert.Equal("echo b", result.Clauses[1].Verb.Joined);
    }

    [Fact]
    public void Heredoc_followed_by_command_parses_as_two_clauses()
    {
        // The newline after a heredoc terminator separates the heredoc body
        // from the next command; newlines *inside* the heredoc do not.
        var parser = new BashParser();

        var result = parser.Parse("cat <<EOF\nbody line\nEOF\necho after");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal("cat", result.Clauses[0].Verb.Joined);
        Assert.Equal("echo after", result.Clauses[1].Verb.Joined);
    }

    [Fact]
    public void Static_descriptor_redirect_has_explicit_non_path_facts()
    {
        var parser = new BashParser();

        var result = parser.Parse("dotnet test 2>&1");

        Assert.False(result.IsUnparseable);
        var redirect = Assert.Single(Assert.Single(result.Commands).Redirects);
        Assert.Equal(RedirectSourceKind.Descriptor, redirect.Source.Kind);
        Assert.Equal(2, redirect.Source.Descriptor);
        Assert.Equal(RedirectOperation.DescriptorDuplicate, redirect.Operation);
        Assert.Equal(1, redirect.TargetDescriptor);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Control_flow_keyword_opening_newline_clause_is_unparseable()
    {
        // A control-flow keyword that opens a newline-separated clause makes
        // the whole parse unparseable (control flow is unsupported in v0.1).
        // The Bash analysis maps that to an empty candidate
        // list, so the approval gate fails closed to a Once/Deny prompt.
        var parser = new BashParser();

        var result = parser.Parse("echo hi\nfor x in 1 2 3; do echo $x; done");

        Assert.True(result.IsUnparseable);
        Assert.False(string.IsNullOrEmpty(result.UnparseableReason));
    }
}
