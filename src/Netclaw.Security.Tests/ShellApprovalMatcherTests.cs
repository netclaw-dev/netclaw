// -----------------------------------------------------------------------
// <copyright file="ShellApprovalMatcherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellApprovalMatcherTests
{
    private readonly ShellApprovalMatcher _matcher = new(
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    private static Dictionary<string, object?> Args(string command, string workingDirectory)
        => new()
        {
            ["Command"] = command,
            ["WorkingDirectory"] = workingDirectory
        };

    private static ApprovalEntry Verb(string verb) => new(verb) { Directory = null };
    private static ApprovalEntry InDir(string verb, string dir) => new(verb) { Directory = dir };

    /// <summary>
    /// xunit.v3 <c>SkipUnless</c> hook for tests that require the POSIX
    /// filesystem in addition to the matcher's explicit Bash environment.
    /// </summary>
    public static bool IsPosix => !OperatingSystem.IsWindows();
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory]
    [InlineData("pwsh -NoProfile -Command 'git status'", "pwsh")]
    [InlineData("powershell.exe -File script.ps1", "powershell.exe")]
    public void Bash_matcher_keeps_power_shell_as_one_external_approval_unit(
        string command,
        string expectedVerb)
    {
        var analysis = _matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(command));

        Assert.False(analysis.IsMessy);
        Assert.Equal(expectedVerb, Assert.Single(analysis.Candidates).Verb);
    }

    [Fact]
    public void Bash_github_diagnostic_with_exit_status_is_reusable()
    {
        const string command =
            "gh run view 123456 --repo example/project --log-failed --verbose 2>&1 "
            + "| head -200; echo \"---EXIT $?---\"";

        var analysis = _matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(command, "/work"));

        Assert.False(analysis.IsMessy);
        Assert.Equal(
            ["gh run view", "head", "echo"],
            analysis.Candidates.Select(static candidate => candidate.Verb));
    }

    [Theory]
    [InlineData("head -c 20 /tmp/work/site.css | xxd | head -3")]
    [InlineData("rg -rn \"operation failed\" src/ tests/ | head -20; echo \"---\"; rg -rln \"upload\" src/ | head -20")]
    [InlineData("netclaw mcp --help 2>&1 | head -50")]
    [InlineData("find /work/project -iname \"*Command*\" -o -iname \"*Add*\" 2>/dev/null | head; echo \"---\"; rg -rn \"transport http|--transport\" /work/project --include=\"*.cs\" -l 2>/dev/null | head")]
    [InlineData("for u in /api/first /api/second; do echo \"=== $u ===\"; curl -sS -m 10 \"$u\" | head -c 1500; echo; done")]
    [InlineData("cd /work/project && git status --short 2>&1 | head; echo \"---branch---\"; git branch --show-current 2>&1; echo \"---remotes---\"; git remote -v 2>&1 | head -4; echo \"---recent---\"; git log --oneline -3 2>&1")]
    [InlineData("~/.dotnet/dotnet test tests/Project.Tests/Project.Tests.csproj --filter \"FullyQualifiedName~SchemaTests\" --nologo 2>&1 | tail -30")]
    [InlineData("docker run --rm -v tools:/tools --entrypoint sh ruby:3.1 -c 'find /tools -maxdepth 2 -type f | head'")]
    [InlineData("docker run --rm --user root -v tools:/workbench/tools -v /tmp/site:/workbench/site -w /workbench/site --entrypoint bash image:tag -c 'bundle exec jekyll build | head'")]
    public void Bash_live_read_and_diagnostic_shapes_are_reusable(string command)
    {
        var analysis = _matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(command, "/work/project"));

        Assert.False(analysis.IsMessy);
        Assert.NotEmpty(analysis.Candidates);
        Assert.All(analysis.Candidates, static candidate =>
        {
            Assert.Equal(ApprovalShell.Bash, candidate.Shell);
            Assert.NotNull(candidate.VerbTokens);
            Assert.NotEmpty(candidate.VerbTokens);
        });
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Bash_finite_filesystem_loop_uses_bounded_path_scopes()
    {
        const string command =
            "for f in src/A.cs src/B.cs; do cat /work/$f; done";
        var arguments = Args(command, "/work");

        var analysis = _matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            arguments);

        Assert.False(analysis.IsMessy);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal("cat", candidate.Verb);
        Assert.Equal("/work", candidate.Directory);
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            arguments,
            [InDir("cat", "/work")],
            cwd: "/work"));
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Bash_finite_filesystem_loop_keeps_external_scopes_exact()
    {
        const string command =
            "for f in /work/A.cs /work2/B.cs; do cat \"$f\"; done";

        var analysis = _matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(command, "/work"));

        Assert.False(analysis.IsMessy);
        Assert.Equal(
            ["/work/A.cs", "/work2/B.cs"],
            analysis.Candidates.Select(static candidate => candidate.Directory));
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Bash_finite_filesystem_loop_rejects_a_symlink_scope()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"netclaw-authored-loop-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var externalDirectory = Path.Combine(root, "external");
        var externalFile = Path.Combine(externalDirectory, "secret.txt");
        var link = Path.Combine(projectDirectory, "link.txt");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(externalFile, "secret");
        File.CreateSymbolicLink(link, externalFile);

        try
        {
            var arguments = Args(
                "for f in link.txt safe.txt; do cat \"$f\"; done",
                projectDirectory);

            Assert.True(_matcher.IsMessy(
                new ToolName("shell_execute"),
                arguments));
            Assert.Empty(_matcher.ExtractCandidates(
                new ToolName("shell_execute"),
                arguments));
        }
        finally
        {
            File.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Power_shell_matcher_uses_the_native_power_shell_grammar()
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));

        var analysis = matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args("Get-ChildItem -Path . -Filter *.cs", @"C:\work"));

        Assert.False(analysis.IsMessy);
        Assert.Equal("Get-ChildItem", Assert.Single(analysis.Candidates).Verb);
    }

    [Theory]
    [InlineData(@"Set-Location C:\workspace\service.repo", "Set-Location")]
    [InlineData(@"cd C:\workspace\service.repo", "Set-Location")]
    [InlineData(@"Push-Location C:\workspace\service.repo", "Push-Location")]
    public void Power_shell_location_command_preserves_dotted_directory(
        string command,
        string expectedVerb)
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));

        var candidate = Assert.Single(matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(command, @"C:\workspace")));

        Assert.Equal(expectedVerb, candidate.Verb);
        Assert.Equal("C:/workspace/service.repo", candidate.Directory);
    }

    [Theory]
    [InlineData(@"Get-Content Env:\Path")]
    [InlineData(@"Get-Item HKLM:\Software\Vendor")]
    [InlineData(@"Remove-Item CustomDrive:\target")]
    [InlineData(@"Write-Output Env:\Path")]
    [InlineData(@"Get-Item Registry::HKEY_LOCAL_MACHINE\Software")]
    [InlineData(@"Get-Item Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\Software")]
    public void Power_shell_provider_drives_do_not_create_persistent_candidates(string command)
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));

        var analysis = matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(command, @"C:\work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Patterns);
        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public void Power_shell_file_system_provider_keeps_directory_scope()
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));

        var analysis = matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(@"Get-Content 'FileSystem::C:\work\input.txt'", @"C:\work"));

        Assert.False(analysis.IsMessy);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal("Get-Content", candidate.Verb);
        Assert.Equal(OperatingSystem.IsWindows() ? @"C:\work" : "C:/work", candidate.Directory);
    }

    [Fact]
    public void Power_shell_redirect_uses_the_environment_path_style()
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));

        var analysis = matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(@"Get-Content .\input.txt > .\output.txt", @"C:\work"));

        Assert.False(analysis.IsMessy);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal("Get-Content", candidate.Verb);
        Assert.Equal("C:/work", candidate.Directory);
    }

    [Fact]
    public void Power_shell_redirect_does_not_use_the_posix_null_device_exception()
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));

        var analysis = matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args("Get-Content .\\input.txt > /dev/null", @"C:\work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public void Dialect_change_reparses_before_candidates_can_match_a_grant()
    {
        const string command = @"Get-ChildItem && Get-Content .\input.txt";
        var powerShell7 = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));
        var windowsPowerShell = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                PwshDialect.WindowsPowerShell51));

        var acceptedDialect = powerShell7.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(command, @"C:\work"));
        var changedDialect = windowsPowerShell.AnalyzeInvocation(
            new ToolName("shell_execute"),
            Args(command, @"C:\work"));

        Assert.False(acceptedDialect.IsMessy);
        Assert.Equal(2, acceptedDialect.Candidates.Count);
        Assert.True(changedDialect.IsMessy);
        Assert.Empty(changedDialect.Patterns);
        Assert.Empty(changedDialect.Candidates);
    }

    [Fact]
    public void ExtractPatterns_simple_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Single(patterns);
        Assert.Equal("git push origin main", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_compound_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"));
        Assert.Equal(3, patterns.Count);
        Assert.Contains("git add .", patterns);
        Assert.Contains("git commit -m fix", patterns);
        Assert.Contains("git push", patterns);
    }

    [Fact]
    public void ExtractPatterns_recurses_into_bash_c_wrapper()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("bash -c \"git push --force\""));

        Assert.Single(patterns);
        Assert.Equal("git push --force", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_empty_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args(""));
        Assert.Empty(patterns);
    }

    [Fact]
    public void ExtractCandidateVerbs_collapses_to_verb_chains_only()
    {
        // Pure verb chains, no normalized commands or directory roots — the
        // v2 matcher leaves the directory half of approval pairs to the cwd.
        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"));
        Assert.Equal(3, verbs.Count);
        Assert.Contains("git add", verbs);
        Assert.Contains("git commit", verbs);
        Assert.Contains("git push", verbs);
    }

    [Fact]
    public void ExtractCandidateVerbs_emits_command_head_only()
    {
        // v2.1 path-extraction: verb chain is the command head only.
        // The path argument is captured separately on
        // ExtractCandidates(...).Directory; see
        // ShellApprovalMatcherPathExtractionTests for the full coverage.
        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("cat /home/user/.netclaw/logs/crash.log"));
        Assert.Single(verbs);
        Assert.Equal("cat", verbs[0]);
    }

    // ---- Call-specific value normalization (digit-bearing tokens) ----
    // ShellSyntaxTree's greedy verb walk (SPEC §6.1) folds lowercase-leading
    // value tokens into the verb chain (`git tag v0.4.2`, `git show aa211dcb`)
    // but stops at digit-leading ones (`0.4.2` lands in Args, verb stays
    // `git tag`). Both are call-specific values, so the matcher trims trailing
    // digit-bearing non-flag, non-path tokens off the chain on the gate path
    // (ExtractCandidateVerbs / IsApproved) AND the persisted-pattern path
    // (ExtractPatterns), so the same intent yields one stable verb. Regression
    // for the v0.4.2-vs-0.4.2 approval divergence.

    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    [InlineData("git tag v0.4.2")]
    [InlineData("git tag 0.4.2")]
    [InlineData("git tag 1.0.0-beta.3")]
    [InlineData("git tag v2.0")]
    public void ExtractCandidateVerbs_strips_trailing_value_token(string command)
    {
        var verbs = _matcher.ExtractCandidateVerbs(new ToolName("shell_execute"), Args(command));
        Assert.Single(verbs);
        Assert.Equal("git tag", verbs[0]);
    }

    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    [InlineData("git tag v0.4.2")]
    [InlineData("git tag 0.4.2")]
    public void ExtractPatterns_strips_trailing_value_token(string command)
    {
        // The persisted grant (ExtractPatterns) must normalize to the same
        // `git tag` the gate compares, so a freshly-granted version generalizes
        // across versions instead of pinning to the one that was approved.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args(command));
        Assert.Single(patterns);
        Assert.Equal("git tag", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_digit_bearing_operand_terminates_pattern()
    {
        // Digit-bearing operands (`test123`) are call-specific values and
        // terminate the pattern; flags before the value are retained.
        var patterns = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("docker run --name test123 --port=8080"));
        Assert.Single(patterns);
        Assert.Equal("docker run --name", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void IsApproved_git_tag_grant_matches_both_version_forms()
    {
        // The exact production scenario: a standing `git tag` (anywhere) grant
        // auto-approved `git tag 0.4.2` but `git tag v0.4.2` re-prompted,
        // because the `v`-prefixed version folded into the verb chain.
        var approved = new[] { Verb("git tag") };

        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"), Args("git tag v0.4.2"), approved, cwd: "/repo"));
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"), Args("git tag 0.4.2"), approved, cwd: "/repo"));
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void IsApproved_tag_then_push_compound_matches_standing_grants()
    {
        // Full session command: `git tag <v> && git push origin <v>` under
        // standing `git tag` + `git push origin` grants.
        var approved = new[] { Verb("git tag"), Verb("git push origin") };

        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("git tag v0.4.2 && git push origin v0.4.2"),
            approved,
            cwd: "/repo"));
    }

    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    [InlineData("git checkout v2", "git checkout")]               // digit-bearing ref is a value
    [InlineData("git show 1234abcd", "git show")]                 // digit-leading SHA lands in Args
    [InlineData("git show aa211dcb", "git show")]                 // alpha-leading SHA folds into chain, then trims
    [InlineData("git log v0.4.1..dev", "git log")]                // range ref is a value
    [InlineData("git push origin main", "git push origin main")]  // all-alpha operands are unclassifiable by shape -> preserved
    [InlineData("aws s3 ls", "aws s3 ls")]                        // mid-chain digit token is not trailing -> untouched
    public void ExtractCandidateVerbs_trims_digit_bearing_tokens_trailing_only(string command, string expected)
    {
        var verbs = _matcher.ExtractCandidateVerbs(new ToolName("shell_execute"), Args(command));
        Assert.Single(verbs);
        Assert.Equal(expected, verbs[0]);
    }

    [Fact]
    public void IsApproved_global_wildcard_matches_anywhere()
    {
        var approved = new[] { Verb("git push"), Verb("git add"), Verb("git commit") };
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"),
            approved,
            cwd: "/anywhere"));
    }

    [Fact]
    public void IsApproved_one_verb_unapproved_returns_false()
    {
        var approved = new[] { Verb("git add"), Verb("git push") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"),
            approved,
            cwd: null));
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_matches_when_cwd_is_under_directory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(tempRoot, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            // Use a non-path-aware verb so the candidate stays a pure verb
            // chain ("git status"); path-aware verbs (cat, grep, etc.) append
            // their first positional argument which would not match a bare
            // verb in the approved entry.
            var approved = new[] { InDir("git status", tempRoot) };
            Assert.True(_matcher.IsApproved(
                new ToolName("shell_execute"),
                Args("git status"),
                approved,
                cwd: sub));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_does_not_match_when_cwd_is_outside()
    {
        var approved = new[] { InDir("grep", "/home/user/repos/foo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("grep error file.log"),
            approved,
            cwd: "/etc"));
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_requires_concrete_cwd()
    {
        var approved = new[] { InDir("grep", "/home/user/repos/foo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("grep error file.log"),
            approved,
            cwd: null));
    }

    [Fact]
    public void IsApproved_recurses_into_bash_c_wrapper()
    {
        var approved = new[] { Verb("git push") };
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("bash -c \"git push --force\""),
            approved,
            cwd: null));
    }

    [Fact]
    public void FormatForDisplay_returns_command()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Equal("git push origin main", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_multiline_quoted_arg_terminates_pattern_at_flag()
    {
        // Issue #1402: the multi-line message body is call-specific content,
        // not approvable intent — the stored pattern stops at the flag so a
        // later invocation with a different body still matches.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("freshdesk ticket reply --message \"Hi,\nWe've rolled out a fix. Please verify.\""));

        Assert.Single(patterns);
        Assert.Equal("freshdesk ticket reply --message", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_multiline_quoted_arg_after_digit_id_terminates_at_id()
    {
        // The digit-bearing 605 terminates the walk before --message is
        // reached (IsCallSpecificValueToken), so the multi-line blob never
        // enters the pattern — pins issue #1402's exact command shape.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("freshdesk ticket reply 605 --message \"Hi,\nWe've rolled out a fix. Please verify.\""));

        Assert.Single(patterns);
        Assert.Equal("freshdesk ticket reply", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_summarizes_multiline_quoted_arg()
    {
        // Issue #1402: channel renderers embed DisplayText in single-line
        // code fences — the multi-line body renders as a size summary.
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("freshdesk ticket reply 605 --message \"Hi,\nWe've rolled out a fix. Please verify.\""));

        Assert.DoesNotContain('\n', display);
        Assert.Equal("freshdesk ticket reply 605 --message (2 lines, 42 chars)", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_renders_newline_separated_statements_with_explicit_separator()
    {
        // Bare-newline statement separators render as "; " so the one-line
        // display doesn't visually merge two statements into one command.
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("git fetch\ngit status"));

        Assert.Equal("git fetch; git status", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_heredoc_falls_back_to_flattened_raw_command()
    {
        // The compatibility redirect cannot preserve the v0.3 heredoc facts.
        // The raw fallback keeps the full body visible to the approver.
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("bash <<EOF\nrm -rf /tmp/x\nEOF"));

        Assert.DoesNotContain('\n', display);
        Assert.Equal("bash <<EOF ⏎ rm -rf /tmp/x ⏎ EOF", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_here_string_keeps_authored_operator()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("cat <<< \"alpha\nbeta\""));

        Assert.DoesNotContain('\n', display);
        Assert.Equal("cat <<< \"alpha ⏎ beta\"", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_heredoc_keeps_following_command_boundary()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("cat <<'EOF'\nbody\nEOF\ngit push"));

        Assert.DoesNotContain('\n', display);
        Assert.Equal("cat <<'EOF' ⏎ body ⏎ EOF ⏎ git push", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_dynamic_redirect_target_is_not_persistable()
    {
        var toolName = new ToolName("shell_execute");
        var arguments = Args("echo hi >> \"$LOGDIR\nfile\"");

        Assert.Empty(_matcher.ExtractPatterns(toolName, arguments));
        Assert.True(_matcher.IsMessy(toolName, arguments));
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_carriage_return_arg_terminates_pattern_at_flag()
    {
        // A lone CR (no LF) is a line break too: in a terminal-rendered
        // prompt it returns the cursor to column 0, so it must terminate
        // the pattern walk exactly like LF.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("freshdesk ticket reply --message \"Hi,\rEvil\""));

        Assert.Single(patterns);
        Assert.DoesNotContain('\r', patterns[0]);
        Assert.Equal("freshdesk ticket reply --message", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_single_line_quoted_free_text_terminates_pattern_at_flag()
    {
        // Issue #1406: a single-line quoted commit message is call-specific
        // free text, not approvable intent. The stored pattern stops at the
        // flag so a later commit with a different message still matches.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git commit -m \"fix the bug\""));

        Assert.Single(patterns);
        Assert.Equal("git commit -m", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_single_line_quoted_body_drops_from_pattern()
    {
        // Issue #1406: the ticket body is a single-line quoted operand with
        // internal whitespace, so it drops before it inflates the pattern.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("freshdesk ticket reply --message \"Single line body\""));

        Assert.Single(patterns);
        Assert.Equal("freshdesk ticket reply --message", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_single_word_quoted_arg_is_kept()
    {
        // A single-word quoted arg has no internal whitespace, so it stays in
        // the pattern and normalizes the same as its unquoted form — the drop
        // rule targets only multi-word quoted free text.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git commit -m \"fix\""));

        Assert.Single(patterns);
        Assert.Equal("git commit -m fix", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_quoted_glob_without_internal_whitespace_is_kept()
    {
        // `"*.cs"` is quoted but has no internal whitespace, so the drop rule
        // leaves it in the pattern — only whitespace-bearing free text drops.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("find . -name \"*.cs\"", "/srv/project"));

        Assert.Single(patterns);
        Assert.Contains("-name", patterns[0]);
        Assert.Contains("*.cs", patterns[0]);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractCandidates_quoted_path_with_space_keeps_directory_scope()
    {
        // Security: the drop rule shapes only the stored pattern. A quoted
        // path with a space is authorization state — ExtractCandidates still
        // scopes the candidate to the file's parent directory.
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("cat \"my file.txt\"", "/srv/project"));

        Assert.Contains(candidates, c => c.Verb == "cat" && c.Directory == "/srv/project");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractCandidates_quoted_free_text_before_path_keeps_path_scope()
    {
        // The quoted search pattern `"foo bar"` is free text and never becomes
        // a scope, while the trailing path operand still scopes the candidate.
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("grep \"foo bar\" ./notes.txt", "/srv/project"));

        Assert.Contains(candidates, c => c.Verb == "grep" && c.Directory == "/srv/project");
        Assert.DoesNotContain(candidates, c => c.Directory is not null && c.Directory.Contains("foo"));
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_single_line_quoted_free_text_shows_full_command()
    {
        // The drop rule is pattern-only: a single-line command has no line
        // break, so the operator still sees the full message verbatim in the
        // approval prompt. Only the stored pattern omits the body.
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("git commit -m \"fix the bug\""));

        Assert.Equal("git commit -m \"fix the bug\"", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_carriage_return_arg_is_summarized()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("freshdesk ticket reply --message \"Hi,\rEvil\""));

        Assert.DoesNotContain('\r', display);
        Assert.DoesNotContain('\n', display);
        Assert.Equal("freshdesk ticket reply --message (2 lines, 8 chars)", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_unparseable_multiline_redirect_flattens_raw_command()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("echo hi >> \"$LOGDIR\nfile\""));

        Assert.DoesNotContain('\n', display);
        Assert.Equal("echo hi >> \"$LOGDIR file\"", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_subshell_falls_back_to_flattened_raw_command()
    {
        // Subshell grouping doesn't survive the parser's flat clause list —
        // a reconstruction would misstate which statements the pipe applies
        // to. The fallback keeps the parens the user typed.
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("(git fetch\ngit status) | tee log"));

        Assert.DoesNotContain('\n', display);
        Assert.Equal("(git fetch git status) | tee log", display);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void FormatForDisplay_subshell_with_multiline_arg_stays_single_line()
    {
        // Regression guard: the clause after a closed subshell carries
        // CompoundOperator.None, which previously threw inside the display
        // walk and silently fell back to the unsummarized raw command.
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"),
            Args("(echo hi)\nfreshdesk ticket reply --message \"Hi,\nbody\""));

        Assert.DoesNotContain('\n', display);
        Assert.Equal("(echo hi) freshdesk ticket reply --message \"Hi, body\"", display);
    }

    [Fact]
    public void IsMessy_true_for_bash_control_flow()
    {
        Assert.True(_matcher.IsMessy(
            new ToolName("shell_execute"),
            Args("for pid in $(pgrep netclawd); do echo $pid; done")));
    }

    [Fact]
    public void IsMessy_false_for_well_formed_compound()
    {
        Assert.False(_matcher.IsMessy(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push")));
    }

    [Fact]
    public void IsApproved_returns_false_for_messy_command_even_with_global_wildcards()
    {
        // Even if every conceivable verb is approved, a messy command never
        // auto-runs: the matcher cannot extract verb chains to evaluate, and
        // the prompt must offer Once/Deny only.
        var approved = new[] { Verb("for"), Verb("do"), Verb("done"), Verb("echo"), Verb("printf") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("for x in $(printf '1 2 3'); do echo \"$x\"; done"),
            approved,
            cwd: null));
    }

    // ---------------------------------------------------------------- integer positional arguments
    // Issue #1331: bare integers (ticket IDs, port numbers, timeouts) must NOT
    // be baked into the approval verb chain. Every unique integer previously
    // created a distinct approval entry, forcing users to approve each one
    // separately. The AST correctly strips integers from VerbChain (IsVerbLikeToken
    // requires [a-z] start), but the display/pattern extraction path uses raw
    // whitespace tokenization — which MUST also exclude bare integers.

    [Fact]
    public void ExtractPatterns_strips_bare_integer_positional_arguments()
    {
        // This test uses POSIX filesystem semantics and a Linux Bash environment.
        // Windows skips with a pass to keep the test active for Slopwatch.
        if (OperatingSystem.IsWindows()) return;

        // The approval pattern for `freshdesk ticket get 123` should be
        // `freshdesk ticket get` — NOT `freshdesk ticket get 123`.
        var patterns = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("freshdesk ticket get 123"));

        Assert.Single(patterns);
        Assert.Equal("freshdesk ticket get", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_tolerates_different_integer_values()
    {
        // Two invocations with different integers should produce the same
        // pattern — approval granted for one integer-valued command should
        // cover all integer values of the same verb chain.
        if (OperatingSystem.IsWindows()) return;

        var patterns1 = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("nc host 8080"));
        var patterns2 = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("nc host 9090"));

        Assert.Single(patterns1);
        Assert.Single(patterns2);
        Assert.Equal(patterns1[0], patterns2[0]);
        Assert.Equal("nc host", patterns1[0]);
    }

    [Fact]
    public void ExtractPatterns_strips_timeout_integer()
    {
        // `timeout 30 curl` — the 30 is a timeout value, not a verb component.
        if (OperatingSystem.IsWindows()) return;

        var patterns = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("timeout 30 curl http://example.com"));

        Assert.Single(patterns);
        Assert.Equal("timeout", patterns[0]);
    }

    [Fact]
    public void ExtractCandidateVerbs_strips_bare_integer_positional_arguments()
    {
        // Candidate verbs must NOT include bare integer arguments.
        if (OperatingSystem.IsWindows()) return;

        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("freshdesk ticket get 123"));

        Assert.Single(verbs);
        Assert.Equal("freshdesk ticket get", verbs[0]);
        Assert.DoesNotContain("123", verbs);
    }
}

/// <summary>
/// Path-extraction-aware matcher tests. The v2.1 design moves path arguments
/// out of the verb chain and into the candidate's directory half so future
/// calls in the same tree match a single persisted entry.
/// </summary>
public sealed class ShellApprovalMatcherPathExtractionTests
{
    private readonly ShellApprovalMatcher _matcher = new(
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    private static Dictionary<string, object?> Args(string command, string workingDirectory)
        => new()
        {
            ["Command"] = command,
            ["WorkingDirectory"] = workingDirectory
        };

    public static TheoryData<string, string, string[]> ParserPathScopeCases => new()
    {
        {
            "curl --data=@request.json https://example.invalid/api",
            "curl",
            ["project"]
        },
        {
            "curl --data=@{external}/request.json https://example.invalid/api",
            "curl",
            ["external"]
        },
        {
            "curl -D ./headers.txt --data=@{external}/request.json https://example.invalid/api",
            "curl",
            ["project", "external"]
        },
        {
            "curl -D {external}/headers.txt --data=@request.json https://example.invalid/api",
            "curl",
            ["external", "project"]
        },
        {
            "curl -D ./headers.txt --data=@request.json https://example.invalid/api",
            "curl",
            ["project"]
        },
        {
            "curl --data=@{external}/request.json https://example.invalid/api > ./response.json",
            "curl",
            ["external", "project"]
        },
        {
            "curl --data=@$REQUEST_FILE https://example.invalid/api",
            "curl",
            []
        },
        {
            "cat \"{external}/secret.txt\"",
            "cat",
            ["external"]
        },
        {
            "cat safe/../../external/secret.txt",
            "cat",
            ["external"]
        }
    };

    public static TheoryData<string, string> StaticGlobScopeCases => new()
    {
        { "ls *.txt", "project" },
        { "cat src/*.cs", "project/src" },
        { "rm {external}/*.bak", "external" },
        { "curl --data=@payloads/*.json https://example.invalid/api", "project/payloads" }
    };

    public static TheoryData<string> UnsafeGlobScopeCases => new()
    {
        { "cat */../../secret.txt" },
        { "cat artifacts/*/secret.txt" },
        { "rm /tmp/*/../../etc/*.bak" }
    };

    public static TheoryData<string, string> SymlinkLeafGlobCases => new()
    {
        { "cat artifacts/*.txt", "leak.txt" },
        { "cat artifacts/?.txt", "😀.txt" },
        { "cat artifacts/\\.*", ".leak" }
    };

    /// <summary>
    /// Directory-listing globs: a trailing slash restricts the wildcard to
    /// directories (<c>foo/*/</c>) but adds no descendant path segment — every
    /// match is still a direct child of the covering directory <c>foo</c>. These
    /// MUST resolve to that covering directory and stay persistable, exactly like
    /// the leaf glob <c>foo/*</c>. Regression for the 0.25.3 change that swept the
    /// directory-listing idiom into the one-shot-only "complex command" bucket
    /// (the <c>ls -d .../immovlan/*/ | xargs -n1 basename</c> report).
    /// </summary>
    public static TheoryData<string, string> DirectoryOnlyTrailingSlashGlobCases => new()
    {
        { "ls -d artifacts/*/", "artifacts" },
        { "ls artifacts/*/", "artifacts" },
        { "ls -d workspaces/immovlan/*/", "workspaces/immovlan" }
    };

    /// <summary>
    /// A trailing slash relaxes ONLY the directory-listing case (<c>foo/*/</c>).
    /// A glob with a real path segment after the wildcard still hides the matched
    /// segment's identity — a symlink or traversal the covering directory cannot
    /// bound — so it MUST stay one-shot even when it also ends in a slash. Guards
    /// the fix against over-reaching past a single trailing slash.
    /// </summary>
    public static TheoryData<string> TrailingSlashWithRealSegmentStaysMessyCases => new()
    {
        { "cat artifacts/*/deeper/" },
        { "ls artifacts/*/*/" }
    };

    /// <summary>
    /// xunit.v3 <c>SkipUnless</c> hook for tests that require POSIX paths and
    /// filesystem behavior. Native PowerShell cases have a separate matrix.
    /// </summary>
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static string CanonicalTemporaryDirectory()
    {
        var fullPath = Path.GetFullPath(Path.GetTempPath());
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The temporary directory has no path root.");
        var current = pathRoot;
        var relative = fullPath[pathRoot.Length..];
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            current = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? candidate;
        }

        return current;
    }

    [SlopwatchSuppress("SW001", "This theory verifies Bash parser path scopes, which do not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [MemberData(nameof(ParserPathScopeCases))]
    public void ExtractCandidates_uses_all_parser_path_scopes(
        string commandTemplate,
        string expectedVerb,
        string[] expectedScopeNames)
    {
        var root = Path.Combine(CanonicalTemporaryDirectory(), $"netclaw-path-scopes-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var externalDirectory = Path.Combine(root, "external");
        var command = commandTemplate.Replace(
            "{external}",
            externalDirectory,
            StringComparison.Ordinal);

        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(command, projectDirectory));
        var expectedDirectories = expectedScopeNames
            .Select(scope => scope == "project" ? projectDirectory : externalDirectory)
            .Order(StringComparer.Ordinal)
            .ToList();
        var actualDirectories = candidates
            .Select(candidate => candidate.Directory!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.All(candidates, candidate => Assert.Equal(expectedVerb, candidate.Verb));
        Assert.Equal(expectedDirectories, actualDirectories);
    }

    [SlopwatchSuppress("SW001", "This theory verifies Bash glob scopes, which do not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [MemberData(nameof(StaticGlobScopeCases))]
    public void ExtractCandidates_uses_static_glob_covering_directory(
        string commandTemplate,
        string expectedScope)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-glob-scopes-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var externalDirectory = Path.Combine(root, "external");
        var command = commandTemplate.Replace(
            "{external}",
            externalDirectory,
            StringComparison.Ordinal);
        var expectedDirectory = expectedScope switch
        {
            "project" => projectDirectory,
            "project/src" => Path.Combine(projectDirectory, "src"),
            "project/payloads" => Path.Combine(projectDirectory, "payloads"),
            "external" => externalDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedScope), expectedScope, "Unknown test scope.")
        };

        var candidate = Assert.Single(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(command, projectDirectory)));

        Assert.Equal(expectedDirectory, candidate.Directory);
        Assert.False(_matcher.IsMessy(new ToolName("shell_execute"), Args(command, projectDirectory)));
    }

    [Fact]
    public void ExtractCandidates_uses_declared_posix_scope_for_relative_glob()
    {
        var arguments = Args("du -sh ./*", "/work");
        var candidate = Assert.Single(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            arguments));

        Assert.Equal("du", candidate.Verb);
        Assert.Equal("/work", candidate.Directory);
        Assert.False(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
    }

    [Theory]
    [InlineData(ShellPathStyle.Posix, "./bad\0/*", "/work")]
    [InlineData(ShellPathStyle.Windows, "bad\0\\*", @"C:\work")]
    public void Declared_path_resolution_rejects_control_characters(
        ShellPathStyle pathStyle,
        string path,
        string resolutionBase)
    {
        Assert.False(ShellPathRules.TryResolve(
            path,
            resolutionBase,
            pathStyle,
            out _));
    }

    [Fact]
    public void ExtractCandidates_rejects_control_character_in_relative_glob()
    {
        var arguments = Args("du -sh \"./bad\0/*\"", "/work");

        Assert.Empty(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            arguments));
        Assert.True(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
    }

    [Fact]
    public void PowerShell_candidates_reject_control_character_in_glob()
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7));
        var arguments = Args(
            "Get-ChildItem 'C:\\work\\bad\0\\*'",
            @"C:\work");

        Assert.Empty(matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            arguments));
        Assert.True(matcher.IsMessy(new ToolName("shell_execute"), arguments));
    }

    [SlopwatchSuppress("SW001", "This theory verifies Bash glob scopes, which do not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [MemberData(nameof(UnsafeGlobScopeCases))]
    public void Directory_segment_glob_fails_closed(string command)
    {
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            $"netclaw-unsafe-glob-{Guid.NewGuid():N}");
        var arguments = Args(command, projectDirectory);

        Assert.Empty(_matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));
        Assert.True(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
    }

    [SlopwatchSuppress("SW001", "This test verifies Bash symlink glob behavior, which does not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [MemberData(nameof(SymlinkLeafGlobCases))]
    public void Leaf_glob_in_directory_with_symlink_fails_closed(string command, string linkName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-glob-symlink-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var artifactsDirectory = Path.Combine(projectDirectory, "artifacts");
        var externalDirectory = Path.Combine(root, "external");
        var externalFile = Path.Combine(externalDirectory, "secret.txt");
        var link = Path.Combine(artifactsDirectory, linkName);
        Directory.CreateDirectory(artifactsDirectory);
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(externalFile, "secret");
        File.CreateSymbolicLink(link, externalFile);

        try
        {
            var arguments = Args(command, projectDirectory);

            Assert.Empty(_matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));
            Assert.True(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This theory verifies Bash directory-glob scopes, which do not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [MemberData(nameof(DirectoryOnlyTrailingSlashGlobCases))]
    public void ExtractCandidates_trailing_slash_directory_glob_resolves_covering_directory(
        string command,
        string expectedRelativeScope)
    {
        // The directory-listing idiom `foo/*/` must scope to the covering
        // directory `foo` and stay persistable — not degrade to a one-shot
        // "complex command". Currently fails (the trailing slash trips the
        // descendant-scope guard); passes once `foo/*/` normalizes to `foo/*`.
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            $"netclaw-trailing-slash-glob-{Guid.NewGuid():N}");
        var expectedDirectory = expectedRelativeScope
            .Split('/')
            .Aggregate(projectDirectory, Path.Combine);
        var arguments = Args(command, projectDirectory);

        var candidate = Assert.Single(
            _matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));

        Assert.Equal("ls", candidate.Verb);
        Assert.Equal(expectedDirectory, candidate.Directory);
        Assert.False(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
    }

    [SlopwatchSuppress("SW001", "This theory verifies Bash glob scopes, which do not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [MemberData(nameof(TrailingSlashWithRealSegmentStaysMessyCases))]
    public void Trailing_slash_does_not_rescue_real_descendant_segment(string command)
    {
        // A trailing slash after a real intermediate segment (`foo/*/deeper/`)
        // or a second wildcard (`foo/*/*/`) must NOT be mistaken for the benign
        // directory-listing case — the matched segment is still unbounded, so
        // these stay one-shot both before and after the fix.
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            $"netclaw-trailing-descendant-{Guid.NewGuid():N}");
        var arguments = Args(command, projectDirectory);

        Assert.Empty(_matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));
        Assert.True(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
    }

    [SlopwatchSuppress("SW001", "This test verifies Bash symlink glob behavior, which does not apply to the Windows shell parser.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Trailing_slash_directory_glob_with_symlink_child_fails_closed()
    {
        // `foo/*/` reduces to the covering directory `foo`. The symlink scan of
        // `foo` must still fail the command closed — the trailing-slash
        // relaxation must not remove the symlink protection a leaf glob already
        // enforces. A fix that skips the covering-directory scan for `foo/*/`
        // would surface a candidate here and flip IsMessy to false.
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-trailing-symlink-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var artifactsDirectory = Path.Combine(projectDirectory, "artifacts");
        var externalDirectory = Path.Combine(root, "external");
        var link = Path.Combine(artifactsDirectory, "escape");
        Directory.CreateDirectory(artifactsDirectory);
        Directory.CreateDirectory(externalDirectory);
        Directory.CreateSymbolicLink(link, externalDirectory);

        try
        {
            var arguments = Args("ls -d artifacts/*/", projectDirectory);

            Assert.Empty(_matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));
            Assert.True(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This test verifies Bash symlink path behavior, which does not apply to the Windows shell parser.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_keeps_ambiguous_path_when_symlink_can_escape_cwd()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-path-symlink-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var externalDirectory = Path.Combine(root, "external");
        var linkDirectory = Path.Combine(projectDirectory, "link");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(externalDirectory);
        Directory.CreateSymbolicLink(linkDirectory, externalDirectory);

        try
        {
            var candidate = Assert.Single(_matcher.ExtractCandidates(
                new ToolName("shell_execute"),
                Args("cat link/secret.txt", projectDirectory)));

            Assert.Equal("cat", candidate.Verb);
            Assert.Equal(linkDirectory, candidate.Directory);
        }
        finally
        {
            Directory.Delete(linkDirectory);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_strips_path_from_verb()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("find /home/user -name X"));

        var c = Assert.Single(candidates);
        Assert.Equal("find", c.Verb);
        Assert.Equal("/home/user", c.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_applies_file_parent_rule()
    {
        // `cat ~/.bashrc` → directory is the basename's parent (the home
        // directory itself). The matcher canonicalizes via the parser's
        // Resolved field, so the result is the absolute home directory
        // rather than the raw `~` prefix — needed so this candidate
        // compares string-equal with cwd-attributed directories from
        // other clauses in a compound (see the
        // ExtractCandidates_normalizes_tilde_cd... test).
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("cat ~/.bashrc"));

        var c = Assert.Single(candidates);
        Assert.Equal("cat", c.Verb);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            c.Directory);
    }

    [Fact]
    public void ExtractCandidates_no_path_returns_null_directory()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("git status"));

        var c = Assert.Single(candidates);
        Assert.Equal("git status", c.Verb);
        Assert.Null(c.Directory);
    }

    [Fact]
    public void ExtractCandidates_compound_command_extracts_per_clause()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("ls /repo && git status"));

        Assert.Equal(2, candidates.Count);
        Assert.Equal("ls", candidates[0].Verb);
        Assert.Equal("/repo", candidates[0].Directory);
        Assert.Equal("git status", candidates[1].Verb);
        Assert.Null(candidates[1].Directory);
    }

    [Fact]
    public void Matches_when_candidate_path_under_entry_directory()
    {
        // Folder-scoped trust compounds: an entry on /home/petabridge
        // covers any candidate whose path is under it.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/petabridge/.netclaw",
            cwd: null,
            approvedEntries: [new ApprovalEntry("find") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Matches_when_candidate_path_equals_entry_directory()
    {
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/petabridge",
            cwd: null,
            approvedEntries: [new ApprovalEntry("find") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Rejects_when_candidate_path_outside_entry_directory()
    {
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/other",
            cwd: null,
            approvedEntries: [new ApprovalEntry("find") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Falls_back_to_cwd_when_candidate_path_is_null()
    {
        // No path argument on the candidate — cwd is the effective directory.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "git status",
            candidateDirectory: null,
            cwd: "/home/petabridge/.netclaw",
            approvedEntries: [new ApprovalEntry("git status") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Null_directory_entry_matches_any_candidate()
    {
        // Global wildcard ignores both candidate path and cwd.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "freshdesk",
            candidateDirectory: null,
            cwd: null,
            approvedEntries: [new ApprovalEntry("freshdesk") { Directory = null }]));
    }

    [Fact]
    public void Null_directory_entry_matches_subagent_with_null_cwd_for_netclaw_stats()
    {
        // Regression: a sub-agent that inherits no cwd (parent had none either)
        // invokes `netclaw stats`. The persisted global grant in
        // tool-approvals.json must still auto-approve. Bound to a real verb
        // from the original bug report so a future refactor that re-orders the
        // matcher loop trips this test specifically.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "netclaw stats",
            candidateDirectory: null,
            cwd: null,
            approvedEntries: [new ApprovalEntry("netclaw stats") { Directory = null }]));
    }

    [Fact]
    public void Null_directory_entry_wins_over_folder_scoped_entry_with_null_cwd()
    {
        // Spec scenario "Global grant precedence over folder-scoped grants":
        // when both grants exist for the same verb and the candidate has no
        // cwd, the global grant must still win even though the folder-scoped
        // grant gets skipped.
        ApprovalEntry[] entries =
        [
            new ApprovalEntry("dotnet") { Directory = "/home/user/repos/foo/" },
            new ApprovalEntry("dotnet") { Directory = null },
        ];

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "dotnet",
            candidateDirectory: null,
            cwd: null,
            approvedEntries: entries));
    }

    [Fact]
    public void IsPureSideEffect_skips_echo_without_redirect()
    {
        Assert.True(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("echo", Directory: null)));
    }

    [Fact]
    public void IsPureSideEffect_does_not_skip_echo_with_redirect_target()
    {
        // echo X > /tmp/log gets /tmp as its directory via the path-arg
        // scan, which means it's no longer "pure" side effect.
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("echo", Directory: "/tmp")));
    }

    [Fact]
    public void IsPureSideEffect_does_not_skip_action_verbs()
    {
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("find", Directory: null)));
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("git push", Directory: null)));
    }

    [Fact]
    public void ExtractCandidates_caps_echo_at_one_token()
    {
        // Without the SingleTokenSideEffectVerbs cap, the verb-chain
        // extractor would capture `echo hello` as a 2-token verb (since
        // `hello` neither starts with `-` nor matches LooksLikeArgument)
        // and the side-effect skip list would not match. Aaron's real
        // dogfood case used `echo "---REMOTE-INFO---"` which already
        // breaks at the leading `-` — but operators routinely run
        // `echo hello`-shape commands in build scripts.
        // (`echo done` would be the more obvious example but `done` is
        // a bash control-flow keyword and triggers IsMessyCompoundCommand,
        // which returns zero candidates.)
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?> { ["Command"] = "echo hello" });

        var c = Assert.Single(candidates);
        Assert.Equal("echo", c.Verb);
        Assert.True(ApprovalPatternMatching.IsPureSideEffect(c));
    }

    [Fact]
    public void ExtractCandidates_keeps_parser_tokens_when_legacy_verb_is_shortened()
    {
        var candidate = Assert.Single(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?> { ["Command"] = "whoami user" }));

        Assert.Equal("whoami", candidate.Verb);
        Assert.Equal(["whoami", "user"], candidate.VerbTokens);
        Assert.Equal(ApprovalShell.Bash, candidate.Shell);
    }

    [Fact]
    public void ExtractCandidates_keeps_distinct_occurrences_with_one_legacy_projection()
    {
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "whoami user; whoami admin"
            });

        Assert.Collection(
            candidates,
            first =>
            {
                Assert.Equal("whoami", first.Verb);
                Assert.Equal(["whoami", "user"], first.VerbTokens);
            },
            second =>
            {
                Assert.Equal("whoami", second.Verb);
                Assert.Equal(["whoami", "admin"], second.VerbTokens);
            });
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_extracts_cd_target_as_directory()
    {
        // Production case: `cd /repo && git remote -v`. The header /
        // persistence layer needs the cd target as the candidate's
        // directory so the prompt shows the meaningful trust scope rather
        // than the per-session ephemeral session_dir, AND so ApprovedSession
        // / ApprovedAlways grants for the verb actually persist (the
        // persistence guard at LlmSessionActor.PersistApprovalCandidatesAsync
        // drops candidates whose effective directory resolves to session_dir
        // — when git remote inherited session_dir as its fallback cwd, that
        // guard silently dropped the grant and the retry threw
        // ToolApprovalRequiredException).
        //
        // ShellSyntaxTree 0.1.4-alpha attributes the cd target as a
        // synthetic IsCwdAttribution arg on every clause that follows
        // a `cd` in the compound; ExtractCandidates surfaces that into
        // the candidate's Directory so the verb's effective directory
        // matches the actual filesystem location the shell will run it in.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /home/user/repos/example && git remote -v"
            });

        Assert.Contains(candidates,
            c => c.Verb == "cd"
              && c.Directory == "/home/user/repos/example");
        Assert.Contains(candidates,
            c => c.Verb == "git remote"
              && c.Directory == "/home/user/repos/example");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_preserves_dotted_cd_target_as_directory()
    {
        var candidate = Assert.Single(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /workspace/service.repo"
            }));

        Assert.Equal("cd", candidate.Verb);
        Assert.Equal("/workspace/service.repo", candidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_find_dot_preserves_dotted_working_directory()
    {
        var candidate = Assert.Single(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "find . -maxdepth 1 -type f",
                ["WorkingDirectory"] = "/workspace/service.repo"
            }));

        Assert.Equal("find", candidate.Verb);
        Assert.Equal("/workspace/service.repo", candidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_file_operand_in_dotted_directory_uses_parent_directory()
    {
        var candidate = Assert.Single(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cat /workspace/service.repo/readme.md"
            }));

        Assert.Equal("cat", candidate.Verb);
        Assert.Equal("/workspace/service.repo", candidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_propagates_cd_target_to_subsequent_clauses_with_no_path_arg()
    {
        // Production repro of the retry-after-approval failure on
        // `cd ~/repos && git checkout -b feature/foo`. The git checkout
        // clause has no anchored path arg of its own (feature/foo has a
        // slash but isn't an anchored path token), so before the
        // BashParser rewrite its candidate ended up
        // (git checkout, null) → effective directory fell back to
        // session_dir at persistence time → the session-scratch guard
        // dropped the grant → retry threw ToolApprovalRequiredException.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /home/user/repos/foo && git checkout -b feature/freshdesk-cli-skill"
            });

        Assert.Contains(candidates,
            c => c.Verb == "cd" && c.Directory == "/home/user/repos/foo");
        Assert.Contains(candidates,
            c => c.Verb == "git checkout" && c.Directory == "/home/user/repos/foo");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_tracks_latest_cd_through_multiple_hops()
    {
        // cd /a && cd /b && grep ... — grep inherits /b (the latest cd),
        // not /a. Mirrors the BashParser's state-machine semantics for
        // cd-in-compound; pinning here so a parser regression that
        // forgets cwd updates breaks Netclaw CI before it surfaces in
        // production.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /a && cd /b && pwd"
            });

        Assert.Contains(candidates, c => c.Verb == "cd" && c.Directory == "/a");
        Assert.Contains(candidates, c => c.Verb == "cd" && c.Directory == "/b");
        Assert.Contains(candidates, c => c.Verb == "pwd" && c.Directory == "/b");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Occurrence_extraction_rebases_unknown_path_only_for_explicit_intent_scope()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(
            "cd /tmp && inspect; head result.log",
            "/work");
        var occurrence = Assert.Single(
            analysis.Commands,
            command => command.Clause.Verb.Tokens is ["head"]);
        var matcher = new ShellApprovalMatcher(environment);

        Assert.Null(matcher.ExtractCandidatesForOccurrence(
            occurrence,
            "/tmp",
            resolveUnknownPathsFromEffectiveValues: false));

        var candidate = Assert.Single(matcher.ExtractCandidatesForOccurrence(
            occurrence,
            "/tmp",
            resolveUnknownPathsFromEffectiveValues: true)!);
        Assert.Equal("head", candidate.Verb);
        Assert.Equal("/tmp", candidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_recurses_into_bash_dash_c_with_cd_attribution_intact()
    {
        // bash -c "cd /repo && git push" — the parser flattens the
        // inner command and propagates the cd target onto git push.
        // Recursion is the parser's responsibility; the matcher just
        // reads what it produces.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "bash -c \"cd /repo && git push\""
            });

        Assert.Contains(candidates, c => c.Verb == "git push" && c.Directory == "/repo");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_bundled_wrapper_inherits_outer_proven_cwd()
    {
        var candidate = Assert.Single(_matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(
                "cd /tmp && bash -lc \"cat relative.txt\"",
                "/work")),
            candidate => candidate.Verb == "cat");

        Assert.Equal("/tmp", candidate.Directory);
    }

    [SlopwatchSuppress("SW001", "This theory verifies Bash wrapper approval scopes, which do not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [InlineData("sudo bash -lc \"git status\"", "sudo bash", "/work")]
    [InlineData("sudo /bin/bash -lc \"git status\"", "sudo", "/bin/bash")]
    [InlineData("env bash -lc \"git status\"", "env bash", "/work")]
    [InlineData("env /bin/bash -lc \"git status\"", "env", "/bin/bash")]
    [InlineData("nohup bash -lc \"git status\"", "nohup bash", "/work")]
    [InlineData("timeout 5 bash -lc \"git status\"", "timeout", "/work")]
    [InlineData("nice -n 5 bash -lc \"git status\"", "nice", "/work")]
    public void ExtractCandidates_retains_prefix_executable(
        string command,
        string expectedPrefix,
        string expectedDirectory)
    {
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(command, "/work"));

        Assert.Contains(candidates, candidate =>
            candidate.Verb == expectedPrefix && candidate.Directory == expectedDirectory);
        Assert.Contains(candidates, candidate =>
            candidate.Verb == "git status" && candidate.Directory == "/work");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Redirect_to_symlink_target_fails_closed()
    {
        var root = Path.Combine(CanonicalTemporaryDirectory(), $"netclaw-redirect-symlink-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var externalDirectory = Path.Combine(root, "external");
        var externalFile = Path.Combine(externalDirectory, "result.log");
        var redirectTarget = Path.Combine(projectDirectory, "result.log");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(externalFile, "external");
        File.CreateSymbolicLink(redirectTarget, externalFile);

        try
        {
            var arguments = Args("git status > result.log", projectDirectory);

            Assert.Empty(_matcher.ExtractCandidates(
                new ToolName("shell_execute"),
                arguments));
            Assert.True(_matcher.IsMessy(
                new ToolName("shell_execute"),
                arguments));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_prefers_explicit_path_arg_over_cd_attribution()
    {
        // When a clause has its own anchored path argument, that wins —
        // the candidate is approved for the specific path it operates
        // on, not for the broader cd target. Approving
        // `dotnet test /home/foo` shouldn't accidentally grant dotnet
        // test access to all of /tmp just because the operator happened
        // to be cd'd there.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /tmp && dotnet test /home/foo"
            });

        Assert.Contains(candidates,
            c => c.Verb == "dotnet test" && c.Directory == "/home/foo");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_side_effect_verbs_do_not_inherit_cd_attribution()
    {
        // echo / printf / true / false write to stdout and ignore cwd,
        // so cd attribution must NOT attach to them — both because the
        // attribution is semantically meaningless for these verbs and
        // because ApprovalPatternMatching.IsPureSideEffect treats them
        // as an unconditional pass when Directory is null. A redirect
        // produces an additional directory candidate.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /tmp && echo \"done\""
            });

        Assert.Contains(candidates, c => c.Verb == "echo" && c.Directory == null);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_normalizes_tilde_cd_to_absolute_path_so_clauses_share_one_directory()
    {
        // Production header bug: prompt for `cd ~/x && git checkout -b f`
        // displayed "Saved for this chat: cd, git checkout in 2
        // directories" — cd's candidate kept the raw `~/...` form while
        // git checkout's inherited directory was already absolute (the
        // parser's IsCwdAttribution.Resolved is always pre-expanded).
        // Both should canonicalize to the same absolute path so the
        // distinct-directory counter in the prompt header reads 1.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, "repos", "example");

        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd ~/repos/example && git checkout -b feature/foo"
            });

        Assert.Single(
            candidates
                .Where(c => !string.IsNullOrEmpty(c.Directory))
                .Select(c => c.Directory)
                .Distinct(StringComparer.Ordinal));

        Assert.Contains(candidates, c => c.Verb == "cd" && c.Directory == expected);
        Assert.Contains(candidates, c => c.Verb == "git checkout" && c.Directory == expected);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_checks_each_clause_in_one_pipe_approval_unit()
    {
        // The prompt keeps a pipeline in one approval unit. Authorization
        // still checks each clause so an unsafe tail cannot hide.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cat /etc/hosts | wc -l"
            });

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate =>
            candidate.Verb == "cat" && candidate.Directory == "/etc/hosts");
        Assert.Contains(candidates, candidate =>
            candidate.Verb == "wc" && candidate.Directory is null);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_uses_redirect_target_and_invocation_working_directory()
    {
        var workingDirectory = Path.Combine(CanonicalTemporaryDirectory(), $"netclaw-redirect-{Guid.NewGuid():N}");
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "echo hello > result.txt",
                ["WorkingDirectory"] = workingDirectory
            });

        Assert.Contains(candidates, candidate =>
            candidate.Verb == "echo" && candidate.Directory == workingDirectory);
    }

    [SlopwatchSuppress("SW001", "This theory verifies POSIX null device behavior, which does not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [InlineData("ls -la 2>/dev/null", "ls")]
    [InlineData("ls -la 2>/dev/./null", "ls")]
    [InlineData("cat </dev/null", "cat")]
    public void ExtractCandidates_ignores_resolved_posix_null_device_redirect(
        string command,
        string expectedVerb)
    {
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(command));

        var candidate = Assert.Single(candidates);
        Assert.Equal(expectedVerb, candidate.Verb);
        Assert.Null(candidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_uses_invocation_directory_after_null_device_redirect()
    {
        const string workingDirectory = "/home/user/repos/demo";
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args("tmux ls 2>/dev/null", workingDirectory));

        var candidate = Assert.Single(candidates);
        Assert.Equal("tmux ls", candidate.Verb);
        Assert.Equal(workingDirectory, candidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_keeps_other_redirect_scope_after_null_device_redirect()
    {
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(
                "tmux ls 2>/dev/null >/netclaw-approval-external/netclaw-output.txt",
                "/work"));

        var candidate = Assert.Single(candidates);
        Assert.Equal("tmux ls", candidate.Verb);
        Assert.Equal("/netclaw-approval-external", candidate.Directory);
    }

    [SlopwatchSuppress("SW001", "This theory verifies POSIX null device lookalikes, which do not apply to the Windows shell parser.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    [InlineData("ls -la 2>/dev/nul")]
    [InlineData("ls -la 2>/dev/null.backup")]
    public void ExtractCandidates_does_not_generalize_posix_null_device_exception(string command)
    {
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Args(command));

        var candidate = Assert.Single(candidates);
        Assert.Equal("ls", candidate.Verb);
        Assert.Equal("/dev", candidate.Directory);
    }

    [SlopwatchSuppress("SW001", "This test verifies POSIX symlink redirect behavior, which does not apply to the Windows shell parser.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_rejects_redirect_through_symlink_directory()
    {
        var root = Path.Combine(
            CanonicalTemporaryDirectory(),
            $"netclaw-redirect-parent-symlink-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(root, "project");
        var externalDirectory = Path.Combine(root, "external");
        var redirectDirectory = Path.Combine(workingDirectory, "output");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(externalDirectory);
        Directory.CreateSymbolicLink(redirectDirectory, externalDirectory);

        try
        {
            var arguments = Args("echo hello > output/result.txt", workingDirectory);

            Assert.Empty(_matcher.ExtractCandidates(
                new ToolName("shell_execute"),
                arguments));
            Assert.True(_matcher.IsMessy(
                new ToolName("shell_execute"),
                arguments));
        }
        finally
        {
            Directory.Delete(redirectDirectory);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_echo_text_question_mark_is_not_a_glob_scope()
    {
        // Regression for #1795: `echo "---try /stats?format=json---"` contains
        // a `?`, which the parser classifies as a Glob token. The matcher then
        // derives a covering directory from the static prefix (`---try`) —
        // but the `?` is URL query syntax inside echo text, not a glob
        // pattern, and `---try` is not a real directory. echo is a
        // stdout-only side-effect verb, so the candidate must carry
        // Directory == null (matching `echo "done"`). The phantom scope is
        // what inflated the approval header to "Approve in 2 directories?".
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "echo \"---try /stats?format=json---\"",
                ["WorkingDirectory"] = "/home/user/repos/demo"
            });

        var echoCandidate = Assert.Single(candidates);
        Assert.Equal("echo", echoCandidate.Verb);
        Assert.Null(echoCandidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_bare_numeric_operand_uses_command_cwd()
    {
        // The #1795 guard removes the false `/cwd/2000` path scope.
        // The v0.3 occurrence still supplies the command cwd for a scoped grant.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "head -c 2000",
                ["WorkingDirectory"] = "/home/user/repos/demo"
            });

        var headCandidate = Assert.Single(candidates);
        Assert.Equal("head", headCandidate.Verb);
        Assert.Equal("/home/user/repos/demo", headCandidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_printf_format_operand_is_not_a_path_scope()
    {
        // Regression for #1795: printf is a stdout-only side-effect verb. Its
        // format string and value operands are literal text, not paths. The
        // candidate must carry Directory == null.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "printf \"%d\" 5",
                ["WorkingDirectory"] = "/home/user/repos/demo"
            });

        var printfCandidate = Assert.Single(candidates);
        Assert.Equal("printf", printfCandidate.Verb);
        Assert.Null(printfCandidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_echo_glob_char_text_is_not_a_scope()
    {
        // Regression for #1795: `echo "a?b"` contains a `?`, which the parser
        // classifies as a Glob token. echo is a side-effect verb, so no
        // arg-derived scope forms. The candidate must carry Directory == null.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "echo \"a?b\"",
                ["WorkingDirectory"] = "/home/user/repos/demo"
            });

        var echoCandidate = Assert.Single(candidates);
        Assert.Equal("echo", echoCandidate.Verb);
        Assert.Null(echoCandidate.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_bare_numeric_flag_value_uses_command_cwd()
    {
        // The #1795 guard removes the false `/cwd/20` path scope.
        // The v0.3 occurrence still supplies the command cwd for a scoped grant.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "head -n 20",
                ["WorkingDirectory"] = "/home/user/repos/demo"
            });

        var headCandidate = Assert.Single(candidates);
        Assert.Equal("head", headCandidate.Verb);
        Assert.Equal("/home/user/repos/demo", headCandidate.Directory);
    }

    [SlopwatchSuppress("SW001", "This test verifies Bash symlink path behavior, which does not apply to the Windows shell parser.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void IsApproved_numeric_token_that_names_an_escaping_symlink_still_prompts()
    {
        // Security regression for the #1795 numeric guard. `cat 2000` where
        // `2000` is a symlink out of the granted tree must NOT auto-approve.
        // The guard drops a numeric operand only when no filesystem entry
        // exists at its path. A real symlink named `2000` stays a path arg, so
        // its scope survives and the symlink-segment check in
        // MatchesShellApproval refuses the folder grant. A purely syntactic
        // guard would drop `2000`, collapse the scope to the cwd, skip the
        // symlink check, and auto-approve a read outside the tree.
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-numeric-symlink-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var externalDirectory = Path.Combine(root, "external");
        var externalSecret = Path.Combine(externalDirectory, "secret.txt");
        var link = Path.Combine(projectDirectory, "2000");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(externalSecret, "secret");
        File.CreateSymbolicLink(link, externalSecret);

        try
        {
            var approved = new[] { new ApprovalEntry("cat") { Directory = projectDirectory } };
            Assert.False(_matcher.IsApproved(
                new ToolName("shell_execute"),
                Args("cat 2000", projectDirectory),
                approved,
                cwd: projectDirectory));
        }
        finally
        {
            File.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void IsApproved_numeric_token_that_names_a_real_in_tree_directory_is_covered_by_grant()
    {
        // Complement to the symlink case. `cat 2000` where `2000` is a real
        // directory inside the granted tree keeps its scope and is covered by
        // the folder grant. This proves the existence gate does not over-block
        // a legitimate in-tree entry, and that the numeric token stays a path
        // when a real object exists.
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-numeric-dir-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var numericDirectory = Path.Combine(projectDirectory, "2000");
        Directory.CreateDirectory(numericDirectory);

        try
        {
            var approved = new[] { new ApprovalEntry("cat") { Directory = projectDirectory } };
            Assert.True(_matcher.IsApproved(
                new ToolName("shell_execute"),
                Args("cat 2000", projectDirectory),
                approved,
                cwd: projectDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsApproved_treats_side_effect_candidates_as_authorized()
    {
        // Regression: when a compound command contains both action verbs and
        // pure side-effect clauses (echo "==="), persistence skips the echo
        // but the matcher historically did not. Result: after the user
        // clicked Always anywhere, the action verbs were stored but echo
        // wasn't, so the retry's authorization check saw echo as unapproved
        // and threw ToolApprovalRequiredException — which escaped the
        // already-active approval-pause catch and failed the turn.
        // This test asserts IsApproved skips side-effect candidates the
        // same way persistence does.
        var approvedEntries = new[]
        {
            new ApprovalEntry("cd") { Directory = null },
            new ApprovalEntry("git status") { Directory = null },
            new ApprovalEntry("git remote") { Directory = null }
            // No echo entry — exactly what the side-effect skip produces.
        };

        var compound =
            "cd ~/repo && git status && echo \"---\" && git remote -v && echo \"finished\"";
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            new Dictionary<string, object?> { ["Command"] = compound },
            approvedEntries,
            cwd: null));
    }
}

/// <summary>
/// Supplies source-level Slopwatch suppressions without a runtime package dependency.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class SlopwatchSuppressAttribute(string ruleId, string reason) : Attribute
{
    public string RuleId { get; } = ruleId;

    public string Reason { get; } = reason;
}

public sealed class DefaultApprovalMatcherTests
{
    private readonly DefaultApprovalMatcher _matcher = DefaultApprovalMatcher.Instance;

    private static ApprovalEntry Verb(string verb) => new(verb) { Directory = null };

    [Fact]
    public void ExtractPatterns_returns_tool_name()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("mcp:memorizer:store"), null);
        Assert.Single(patterns);
        Assert.Equal("mcp:memorizer:store", patterns[0]);
    }

    [Fact]
    public void IsApproved_matches_exact_tool_name()
    {
        Assert.True(_matcher.IsApproved(
            new ToolName("mcp:memorizer:store"),
            null,
            [Verb("mcp:memorizer:store")],
            cwd: null));
    }

    [Fact]
    public void IsApproved_no_match()
    {
        Assert.False(_matcher.IsApproved(
            new ToolName("mcp:memorizer:store"),
            null,
            [Verb("mcp:memorizer:get")],
            cwd: null));
    }
}

public sealed class McpApprovalMatcherTests
{
    private readonly McpApprovalMatcher _matcher = McpApprovalMatcher.Instance;

    [Fact]
    public void FormatForDisplay_without_arguments_returns_tool_name()
    {
        Assert.Equal(
            "Dropbox/upload",
            _matcher.FormatForDisplay(new ToolName("Dropbox/upload"), null));
    }

    [Fact]
    public void FormatForDisplay_prioritizes_location_values_and_summarizes_large_strings()
    {
        var content = string.Join('\n', Enumerable.Repeat("memorizer payload line", 2_000));
        var display = _matcher.FormatForDisplay(
            new ToolName("Dropbox/upload"),
            new Dictionary<string, object?>
            {
                ["contents"] = content,
                ["overwrite"] = true,
                ["source_path"] = "/home/operator/reports/quarterly-results.pdf",
                ["destination_directory"] = "/Finance/Board/2026/Q3"
            });

        Assert.Contains("source_path=\"/home/operator/reports/quarterly-results.pdf\"", display);
        Assert.Contains("destination_directory=\"/Finance/Board/2026/Q3\"", display);
        Assert.Contains($"contents=({content.Length} chars, 2000 lines)", display);
        Assert.DoesNotContain("memorizer payload line", display);
        Assert.True(
            display.IndexOf("destination_directory", StringComparison.Ordinal)
            < display.IndexOf("contents", StringComparison.Ordinal));
        Assert.True(
            display.IndexOf("source_path", StringComparison.Ordinal)
            < display.IndexOf("contents", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatForDisplay_handles_JsonElement_scalars_and_small_structures()
    {
        using var json = JsonDocument.Parse(
            """{"mode":"replace","options":{"notify":true,"retries":3},"tags":["finance","board"]}""");
        var root = json.RootElement;

        var display = _matcher.FormatForDisplay(
            new ToolName("Dropbox/upload"),
            new Dictionary<string, object?>
            {
                ["mode"] = root.GetProperty("mode").Clone(),
                ["options"] = root.GetProperty("options").Clone(),
                ["tags"] = root.GetProperty("tags").Clone()
            });

        Assert.Contains("mode=\"replace\"", display);
        Assert.Contains("options={\"notify\":true,\"retries\":3}", display);
        Assert.Contains("tags=[\"finance\",\"board\"]", display);
        Assert.DoesNotContain('\n', display);
    }

    [Fact]
    public void FormatForDisplay_redacts_top_level_nested_and_token_shaped_secrets()
    {
        using var json = JsonDocument.Parse(
            """{"password":"nested-password","safe":"visible"}""");
        const string providerToken = "sk-1234567890abcdef";

        var display = _matcher.FormatForDisplay(
            new ToolName("service/create"),
            new Dictionary<string, object?>
            {
                ["access_token"] = "plain-access-token",
                ["options"] = json.RootElement.Clone(),
                ["reference"] = providerToken
            });

        Assert.DoesNotContain("plain-access-token", display);
        Assert.DoesNotContain("nested-password", display);
        Assert.DoesNotContain(providerToken, display);
        Assert.Contains("***REDACTED***", display);
        Assert.Contains("visible", display);
    }

    [Fact]
    public void FormatForDisplay_preserves_both_ends_of_long_locator()
    {
        var path = "/source/" + new string('a', 1_100) + "/quarterly-results.pdf";

        var display = _matcher.FormatForDisplay(
            new ToolName("Dropbox/upload"),
            new Dictionary<string, object?> { ["source_path"] = path });

        Assert.Contains("/source/", display);
        Assert.Contains("quarterly-results.pdf", display);
        Assert.Contains($"[{path.Length} chars]", display);
    }

    [Fact]
    public void FormatForDisplay_redacts_uri_credentials_query_and_fragment()
    {
        const string signedUrl = "https://operator:password@example.com/reports/q3.pdf?signature=opaque-secret&expires=123#access-token";

        var display = _matcher.FormatForDisplay(
            new ToolName("Dropbox/upload"),
            new Dictionary<string, object?> { ["callback"] = signedUrl });

        Assert.Contains("https://example.com/reports/q3.pdf", display);
        Assert.DoesNotContain("operator", display);
        Assert.DoesNotContain("password", display);
        Assert.DoesNotContain("opaque-secret", display);
        Assert.DoesNotContain("access-token", display);
        Assert.Contains("REDACTED", display);
    }

    [Fact]
    public void FormatForDisplay_escapes_untrusted_argument_names_and_inline_code_breakouts()
    {
        var display = _matcher.FormatForDisplay(
            new ToolName("service/invoke"),
            new Dictionary<string, object?>
            {
                ["safe\n```\u202E**Approve**"] = "value`spoof"
            });

        Assert.DoesNotContain('\n', display);
        Assert.DoesNotContain('`', display);
        Assert.DoesNotContain('\u202E', display);
        Assert.Contains("\\u000A", display);
        Assert.Contains("\\u0060", display);
        Assert.Contains("\\u202E", display);
    }

    [Fact]
    public void FormatForDisplay_does_not_infer_value_sensitivity_from_argument_names()
    {
        var longValue = new string('x', 2_000);
        var display = _matcher.FormatForDisplay(
            new ToolName("service/invoke"),
            new Dictionary<string, object?>
            {
                ["file_contents"] = longValue,
                ["source_code"] = longValue,
                ["target_payload"] = longValue
            });

        Assert.Equal(3, display.Split("2000 chars", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(new string('x', 50), display);
    }

    [Fact]
    public void FormatForDisplay_bounds_argument_and_collection_work()
    {
        using var json = JsonDocument.Parse(
            $"[{string.Join(',', Enumerable.Range(0, 1_000))}]");
        var arguments = Enumerable.Range(0, 1_000)
            .ToDictionary(i => $"argument_{i:D4}", i => (object?)json.RootElement.Clone());

        var display = _matcher.FormatForDisplay(new ToolName("service/invoke"), arguments);

        Assert.True(display.Length <= 1_600);
        Assert.Contains("12+ items", display);
        Assert.Contains("arguments", display);
        Assert.DoesNotContain("argument_0024", display);
    }

    [Fact]
    public void FormatForDisplay_bounds_oversized_names_and_redacts_their_values()
    {
        var oversizedKey = new string('k', 10_000);
        var display = _matcher.FormatForDisplay(
            new ToolName($"service/{new string('t', 10_000)}`\nspoof"),
            new Dictionary<string, object?> { [oversizedKey] = "must-not-appear" });

        Assert.True(display.Length <= 1_600);
        Assert.DoesNotContain("must-not-appear", display);
        Assert.DoesNotContain('`', display);
        Assert.DoesNotContain('\n', display);
        Assert.Contains("10015 chars", display);
        Assert.Contains("10000\\u0020chars", display);
        Assert.Contains("REDACTED", display);
    }

    [Fact]
    public void FormatForDisplay_reports_unserializable_value_without_throwing()
    {
        var value = new Func<int>(() => 42);

        var display = _matcher.FormatForDisplay(
            new ToolName("service/invoke"),
            new Dictionary<string, object?> { ["callback"] = value });

        Assert.Contains("value unavailable", display);
        Assert.DoesNotContain("42", display);
    }
}
