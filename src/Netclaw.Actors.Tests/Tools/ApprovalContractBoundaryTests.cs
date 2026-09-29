// -----------------------------------------------------------------------
// <copyright file="ApprovalContractBoundaryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Boundary cases for approval rules that only internal unit tests covered
/// before. Each case goes through the production registration and the tool
/// executor. The cases describe current behavior. They do not add rows to the
/// frozen case catalog.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ApprovalContractBoundaryTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        string caseId,
        ShellApprovalInvocation? invocation = null,
        ApprovalState? approvals = null,
        ShellApprovalHarnessPolicy? policy = null,
        ShellApprovalHarnessScope? scope = null)
        => ShellApprovalHarness.CreateAsync(
            caseId,
            invocation ?? new ShellApprovalInvocation("true"),
            approvals ?? Approvals.None,
            fixture.ActorSystem,
            Ct,
            scope: scope,
            policy: policy);

    // ── 1. Operator hard-deny overrides and configured deny patterns ──
    // Old coverage: HardDenyOverridesLoaderTests, HardDenyRuleTests,
    // ShellCommandPolicyOverrideTests.

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Operator_hard_deny_override_denies_a_matching_verb_chain()
    {
        await using var harness = await CreateHarnessAsync(
            "hard-deny-override-verb",
            policy: new ShellApprovalHarnessPolicy
            {
                HardDenyOverridesJson = """
                    [ { "verb": ["terraform", "destroy"], "reason": "no infrastructure teardown" } ]
                    """
            });

        foreach (var command in new[] { "terraform destroy -auto-approve", "git status && terraform destroy" })
        {
            var denied = await harness.EvaluateShellAsync(command, Ct);
            Assert.Equal(ApprovalOutcome.Denied, denied.Outcome);
            Assert.Equal("hard_deny_custom_deny", denied.DenyReason);
        }

        // The verb chain must start a clause. The rule text is not a substring match.
        foreach (var command in new[] { "terraform plan", "echo terraform destroy" })
        {
            var other = await harness.EvaluateShellAsync(command, Ct);
            Assert.NotEqual(ApprovalOutcome.Denied, other.Outcome);
        }
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Operator_hard_deny_path_constraint_matches_only_the_first_positional_path()
    {
        await using var harness = await CreateHarnessAsync(
            "hard-deny-override-path",
            policy: new ShellApprovalHarnessPolicy
            {
                HardDenyOverridesJson = """
                    [ { "verb": ["terraform", "destroy"], "firstPath": { "oneOf": ["prod"] }, "reason": "no prod" } ]
                    """
            });

        foreach (var command in new[] { "terraform destroy prod", "terraform destroy -auto-approve prod/" })
        {
            var denied = await harness.EvaluateShellAsync(command, Ct);
            Assert.Equal(ApprovalOutcome.Denied, denied.Outcome);
            Assert.Equal("hard_deny_custom_deny", denied.DenyReason);
        }

        foreach (var command in new[] { "terraform destroy staging", "terraform destroy staging prod" })
        {
            var other = await harness.EvaluateShellAsync(command, Ct);
            Assert.NotEqual(ApprovalOutcome.Denied, other.Outcome);
        }
    }

    [Theory]
    [InlineData("{not json", "malformed JSON")]
    [InlineData("""[ { "reason": "no match shape" } ]""", "rule at index 0")]
    [InlineData("""[ { "verb": ["bad"], "rawText": "bad", "reason": "two shapes" } ]""", "rule at index 0")]
    public async Task Invalid_hard_deny_override_file_stops_the_registration(string content, string expectedText)
    {
        // The daemon refuses to start. It never drops an operator rule without a message.
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CreateHarnessAsync(
            "hard-deny-override-invalid",
            policy: new ShellApprovalHarnessPolicy { HardDenyOverridesJson = content }));

        Assert.Contains(expectedText, error.Message);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Configured_hard_deny_pattern_denies_a_leading_verb_chain()
    {
        await using var harness = await CreateHarnessAsync(
            "hard-deny-configured-pattern",
            policy: new ShellApprovalHarnessPolicy { HardDenyPatterns = ["terraform destroy"] });

        var denied = await harness.EvaluateShellAsync("terraform destroy workspace", Ct);
        Assert.Equal(ApprovalOutcome.Denied, denied.Outcome);
        Assert.Equal("hard_deny_custom_deny", denied.DenyReason);

        foreach (var command in new[] { "terraform destroyer", "echo terraform destroy" })
        {
            var other = await harness.EvaluateShellAsync(command, Ct);
            Assert.NotEqual(ApprovalOutcome.Denied, other.Outcome);
        }
    }

    // ── 2. Protected paths ──
    // Old coverage: ToolPathPolicyTests (control-plane files, synced skill
    // scripts, link escalation).

    public static TheoryData<string> ControlPlaneFiles =>
    [
        "sqlite-database",
        "pid-file",
        "lock-file",
        "restart-manifest",
    ];

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(ControlPlaneFiles))]
    public async Task Shell_reference_to_a_control_plane_file_is_denied(string file)
    {
        await using var harness = await CreateHarnessAsync($"control-plane-{file}");
        var path = file switch
        {
            "sqlite-database" => harness.Paths.SqliteDbPath,
            "pid-file" => harness.Paths.PidFilePath,
            "lock-file" => harness.Paths.LockFilePath,
            "restart-manifest" => harness.Paths.RestartManifestPath,
            _ => throw new ArgumentOutOfRangeException(nameof(file), file, "Unknown control-plane file.")
        };

        var observed = await harness.EvaluateShellAsync($"cat '{path}'", Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_references_protected_path", observed.DenyReason);
        Assert.Equal(0, observed.ApprovalChecks);
    }

    [SlopwatchSuppress("SW001", "The case creates a POSIX symbolic link.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case creates a POSIX symbolic link.")]
    public async Task Link_into_a_protected_directory_is_denied()
    {
        await using var harness = await CreateHarnessAsync(
            "protected-link",
            approvals: Approvals.PersistentAnywhere("cat"));
        Directory.CreateDirectory(harness.Paths.KeysDirectory);
        await File.WriteAllTextAsync(Path.Combine(harness.Paths.KeysDirectory, "k1"), "synthetic key", Ct);
        Directory.CreateSymbolicLink(
            Path.Combine(harness.ProjectDirectory, "leak"),
            harness.Paths.KeysDirectory);

        var observed = await harness.EvaluateShellAsync("cat leak/k1", Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_references_protected_path", observed.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Synced_skill_script_can_run_but_its_directory_stays_write_protected()
    {
        await using var harness = await CreateHarnessAsync("synced-skill-script");
        var tools = Path.Combine(harness.Paths.SystemSkillsDirectory, "netclaw-operations", "tools");
        Directory.CreateDirectory(tools);
        var script = Path.Combine(tools, "check");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho ok\n", Ct);

        var run = await harness.EvaluateShellAsync($"'{script}'", Ct);
        var write = await harness.EvaluateShellAsync($"touch '{Path.Combine(tools, "added")}'", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, run.Outcome);
        Assert.Equal(ApprovalOutcome.Denied, write.Outcome);
    }

    // ── 3. Unattended and Public path authority for file tools ──
    // Old coverage: UnattendedPathAccessTests, PublicAudienceFileAccessPolicyTests.

    private const string UnattendedRootsMessage = "Error: unattended session may only access files inside trusted roots.";

    [Fact]
    public async Task Unattended_personal_file_tools_stay_inside_trusted_roots()
    {
        await using var harness = await CreateHarnessAsync(
            "unattended-file-roots",
            new ShellApprovalInvocation("true", Interactive: false));
        var outside = Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "workspaces", "external", "notes.txt");
        await File.WriteAllTextAsync(outside, "outside data", Ct);

        var identity = await harness.EvaluateToolAsync(
            "file_write",
            ToolInput.Create("Path", Path.Combine(harness.Paths.IdentityDirectory, "SOUL.md"), "Content", "x"),
            Ct);
        var skills = await harness.EvaluateToolAsync(
            "file_write",
            ToolInput.Create("Path", Path.Combine(harness.Paths.SkillsDirectory, "added", "SKILL.md"), "Content", "x"),
            Ct);
        var read = await harness.EvaluateToolAsync("file_read", ToolInput.Create("Path", outside), Ct);
        var session = await harness.EvaluateToolAsync(
            "file_write",
            ToolInput.Create("Path", Path.Combine(harness.SessionDirectory, "notes.txt"), "Content", "x"),
            Ct);

        foreach (var denied in new[] { identity, skills, read })
        {
            Assert.Equal(ApprovalOutcome.Denied, denied.Outcome);
            Assert.Equal("path_access_denied", denied.DenyReason);
            Assert.Equal(UnattendedRootsMessage, denied.DenyMessage);
        }

        Assert.Equal(ApprovalOutcome.Allowed, session.Outcome);
    }

    [Fact]
    public async Task Public_file_reads_exclude_global_roots_and_hide_root_paths()
    {
        await using var harness = await CreateHarnessAsync(
            "public-file-roots",
            new ShellApprovalInvocation("true", Audience: TrustAudience.Public));
        var skill = Path.Combine(harness.Paths.SkillsDirectory, "shared", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        await File.WriteAllTextAsync(skill, "skill data", Ct);
        var sessionFile = Path.Combine(harness.SessionDirectory, "notes.txt");
        await File.WriteAllTextAsync(sessionFile, "session data", Ct);

        var global = await harness.EvaluateToolAsync("file_read", ToolInput.Create("Path", skill), Ct);
        var write = await harness.EvaluateToolAsync(
            "file_write",
            ToolInput.Create("Path", sessionFile, "Content", "x"),
            Ct);
        var session = await harness.EvaluateToolAsync("file_read", ToolInput.Create("Path", sessionFile), Ct);

        Assert.Equal(ApprovalOutcome.Denied, global.Outcome);
        Assert.Equal("path_access_denied", global.DenyReason);
        Assert.Equal(
            "Error: Public trust context may only access files inside the current session directory.",
            global.DenyMessage);
        Assert.Equal(ApprovalOutcome.Denied, write.Outcome);
        Assert.Equal("tool_not_allowed_for_audience_profile", write.DenyReason);
        Assert.Equal(ApprovalOutcome.Allowed, session.Outcome);
    }

    // ── 4. Decision trace redaction and bounds ──
    // Old coverage: DispatchingToolExecutorTests (ShellPolicyDecisionTraceBuilder.SanitizeText).

    public static TheoryData<string, string> TraceExecutables => new()
    {
        { "ghp_12345678901234567890", "***REDACTED***" },
        { "/opt/tools/ghp_12345678901234567890", "***REDACTED***" },
        { new string('a', 200), new string('a', 128) },
        { new string('a', 600), "***REDACTED***" },
    };

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(TraceExecutables))]
    public async Task Decision_trace_redacts_and_bounds_executable_names(string executable, string expected)
    {
        await using var harness = await CreateHarnessAsync("trace-redaction");

        var observed = await harness.EvaluateShellAsync($"{executable} status", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        var basenames = observed.TraceRows
            .Select(row => row.Split('|')[2])
            .Where(name => name.Length > 0)
            .ToList();
        Assert.NotEmpty(basenames);
        Assert.All(basenames, name => Assert.Equal(expected, name));
    }

    // ── 5. Approval prompt display text ──
    // Old coverage: ShellApprovalMatcherTests (McpApprovalMatcher.FormatForDisplay,
    // ShellApprovalMatcher.FormatForDisplay).

    [Fact]
    public async Task Mcp_approval_prompt_redacts_secrets_and_escapes_markup()
    {
        await using var harness = await CreateHarnessAsync(
            "mcp-display",
            policy: new ShellApprovalHarnessPolicy
            {
                PersonalApprovalOverrides = new Dictionary<string, ToolApprovalMode>
                {
                    ["reports/fetch_report"] = ToolApprovalMode.Approval
                }
            });
        var toolName = harness.RegisterMcpTool("reports", "fetch_report");

        var observed = await harness.EvaluateToolAsync(
            toolName,
            new Dictionary<string, object?>
            {
                ["url"] = "https://operator:password@example.com/reports/q3.pdf?signature=opaque-secret&expires=123#access-token",
                ["access_token"] = "plain-access-token",
                ["reference"] = "sk-1234567890abcdef",
                ["safe\n```\u202E**Approve**"] = "value`spoof",
            },
            Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        var display = observed.Prompt!.DisplayText;
        Assert.Contains("https://example.com/reports/q3.pdf", display);
        Assert.Contains("REDACTED", display);
        foreach (var secret in new[] { "operator", "password", "opaque-secret", "access-token", "plain-access-token", "sk-1234567890abcdef" })
            Assert.DoesNotContain(secret, display, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", display, StringComparison.Ordinal);
        Assert.DoesNotContain("`", display, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", display, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Shell_approval_prompt_summarizes_a_multi_line_argument()
    {
        await using var harness = await CreateHarnessAsync("shell-display");

        var observed = await harness.EvaluateShellAsync(
            "freshdesk ticket reply 605 --message \"Hi,\nWe've rolled out a fix. Please verify.\"",
            Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal("freshdesk ticket reply 605 --message (2 lines, 42 chars)", observed.Prompt!.DisplayText);
    }

    // ── 7. Legacy tokenizer inputs to hard deny and protected paths ──
    // Old coverage: ShellTokenizerTests. A background "&" leaves the structural
    // analysis unresolved, so these checks use the legacy tokenizer.

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("netclaw daemon stop &", "hard_deny_self_destructive")]
    [InlineData("'netclaw' daemon stop &", "hard_deny_self_destructive")]
    [InlineData("bash -c \"kill -9 1\" &", "hard_deny_self_destructive")]
    [InlineData("sudo rm -rf / &", "hard_deny_privilege_escalation")]
    [InlineData("cat ../netclaw/config/notes.txt &", "shell_references_protected_path")]
    public async Task Unresolved_command_still_meets_hard_deny_and_protected_paths(string command, string reason)
    {
        await using var harness = await CreateHarnessAsync("legacy-tokenizer");

        var observed = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal(reason, observed.DenyReason);
    }

    // ── 8. Approval pattern v3 ──
    // Old coverage: ApprovalPatternV3Tests, ShellApprovalMatcherTests ExtractPatterns_*.

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("git tag v0.4.2", "git tag")]
    [InlineData("git tag 0.4.2", "git tag")]
    [InlineData("freshdesk ticket get 123", "freshdesk ticket get")]
    [InlineData("git log v0.4.1..dev", "git log")]
    public async Task Prompt_candidate_drops_trailing_version_and_number_operands(string command, string candidate)
    {
        await using var harness = await CreateHarnessAsync("pattern-v3");

        var observed = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal([candidate], observed.Prompt!.CandidateVerbs);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Verb_grant_covers_a_later_version_operand()
    {
        await using var harness = await CreateHarnessAsync(
            "pattern-v3-grant",
            approvals: Approvals.PersistentAnywhere("git tag"));

        var observed = await harness.EvaluateShellAsync("git tag v0.5.0", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, observed.AllowReason);
    }

    // ── 11. Assignment digest identity ──
    // Old coverage: ShellAssignmentDigestTests, ToolApprovalGateTests
    // One_time_keys_bind_the_exact_assignment_constraint.

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Assignment_prompt_offers_only_assignment_scoped_keys()
    {
        // Bash 5.2 has a known initial state, so the parser resolves the assignment.
        await using var harness = await CreateHarnessAsync(
            "assignment-keys",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52));

        var observed = await harness.EvaluateShellAsync("FOO=1 make build", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal(["make build"], observed.Prompt!.CandidateVerbs);
        Assert.Contains(ObservedOptionKeys.ApproveAssignmentSessionV1, observed.Prompt.OptionKeys);
        Assert.Contains(ObservedOptionKeys.ApproveAssignmentAlwaysV1, observed.Prompt.OptionKeys);
        foreach (var legacy in new[]
                 {
                     ObservedOptionKeys.ApproveSession,
                     ObservedOptionKeys.ApproveAlways,
                     ObservedOptionKeys.ApproveRepository,
                     ObservedOptionKeys.ApproveEverywhere
                 })
        {
            Assert.DoesNotContain(legacy, observed.Prompt.OptionKeys);
        }
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task One_time_approval_binds_the_exact_assignment_value()
    {
        await using var harness = await CreateHarnessAsync(
            "assignment-one-time",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52));
        var prompt = await harness.EvaluateShellAsync("FOO=1 make build", Ct);
        harness.SeedOneTimeApproval(prompt.Prompt!);

        var otherValue = await harness.EvaluateShellAsync("FOO=2 make build", Ct);
        var sameValue = await harness.EvaluateShellAsync("FOO=1 make build", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, otherValue.Outcome);
        Assert.Equal(ApprovalOutcome.Allowed, sameValue.Outcome);
        Assert.Equal(ApprovalAllowReason.OneTimeApproval, sameValue.AllowReason);
    }

    // ── 13. Approval option labels and button caps ──
    // Old coverage: ToolApprovalGateTests Narrow_shell_context_* and
    // Shell_command_with_long_directory_path_keeps_labels_within_button_caps.

    private static readonly string[] StandardLabels =
        ["Once", "This chat", "Always here", "Always anywhere", "Deny"];

    private static readonly string[] LabelsWithoutFolderScope =
        ["Once", "This chat", "Always anywhere", "Deny"];

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Prompt_labels_are_fixed_and_omit_folder_scope_near_a_root_or_in_session_storage()
    {
        await using var harness = await CreateHarnessAsync("option-labels");
        var deep = Path.Combine(
            harness.ProjectDirectory,
            string.Join('/', Enumerable.Repeat("a-long-directory-name-for-the-button-label-check", 6)));
        Directory.CreateDirectory(deep);

        var project = await harness.EvaluateShellAsync("git push", Ct, harness.ProjectDirectory);
        var longPath = await harness.EvaluateShellAsync("git push", Ct, deep);
        var nearRoot = await harness.EvaluateShellAsync("git push", Ct, "/etc");
        var session = await harness.EvaluateShellAsync("git push", Ct, harness.SessionDirectory);

        Assert.Equal(StandardLabels, project.Prompt!.OptionLabels);
        Assert.Equal(StandardLabels, longPath.Prompt!.OptionLabels);
        Assert.Contains(ObservedOptionKeys.ApproveAlways, project.Prompt.OptionKeys);
        Assert.Equal(LabelsWithoutFolderScope, nearRoot.Prompt!.OptionLabels);
        Assert.DoesNotContain(ObservedOptionKeys.ApproveAlways, nearRoot.Prompt.OptionKeys);
        Assert.Equal(LabelsWithoutFolderScope, session.Prompt!.OptionLabels);
        Assert.DoesNotContain(ObservedOptionKeys.ApproveAlways, session.Prompt.OptionKeys);
    }

    // ── 14. Platform temporary roots ──
    // Old coverage: TemporaryPathCorrectionPolicyTests.

    [SlopwatchSuppress("SW001", "The POSIX temporary root is /tmp.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The POSIX temporary root is /tmp.")]
    public async Task Platform_temporary_root_gets_managed_directory_advice_unless_a_link_escapes()
    {
        await using var harness = await CreateHarnessAsync("platform-temporary-root");
        var canonicalTemporaryRoot = new DirectoryInfo("/tmp").ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? "/tmp";
        var probe = Directory.CreateDirectory(Path.Combine("/tmp", $"netclaw-temp-root-{Guid.NewGuid():N}"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(probe.FullName, "escape"), harness.ProjectDirectory);

            var advised = await harness.EvaluateShellAsync("cd /tmp && touch marker.txt", Ct);
            var escaped = await harness.EvaluateShellAsync(
                $"cd /tmp && touch {probe.FullName}/escape/marker.txt",
                Ct);

            Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, advised.Outcome);
            Assert.Equal(ApprovalCorrection.ManagedTemporaryDirectory, advised.AgentCorrection);
            Assert.Equal(canonicalTemporaryRoot, advised.PlatformTemporaryRoot);
            Assert.NotEqual(ApprovalCorrection.ManagedTemporaryDirectory, escaped.AgentCorrection);
        }
        finally
        {
            probe.Delete(recursive: true);
        }
    }

    // ── 15. Native-tool correction ──
    // Old coverage: DispatchingToolExecutorTests (NativeToolShellCorrectionDetector.Detect).

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("file_read --path notes.txt", "file_read")]
    [InlineData("echo ignored | file_read --path notes.txt", "file_read")]
    [InlineData("file_write --path first && file_read --path second", "file_write")]
    [InlineData("FILE_READ --path notes.txt", null)]
    [InlineData("./file_read --path notes.txt", null)]
    [InlineData("file_reed --path notes.txt", null)]
    public async Task Shell_call_of_a_native_tool_name_gets_native_tool_advice(string command, string? tool)
    {
        await using var harness = await CreateHarnessAsync("native-tool-advice");

        var observed = await harness.EvaluateShellAsync(command, Ct);

        if (tool is null)
        {
            Assert.NotEqual(ApprovalCorrection.NativeTool, observed.AgentCorrection);
            return;
        }

        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, observed.Outcome);
        Assert.Equal(ApprovalCorrection.NativeTool, observed.AgentCorrection);
        Assert.Equal(tool, observed.AgentCorrectionTarget);
        Assert.Equal(0, observed.ApprovalChecks);
    }

    // ── 9. Git repository identity ──
    // Old coverage: GitRepositoryApprovalScopeTests.

    [SlopwatchSuppress("SW001", "The Git layout uses POSIX paths and the git CLI.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Git layout uses POSIX paths and the git CLI.")]
    public async Task Repository_grant_does_not_cover_a_worktree_whose_registration_points_elsewhere()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-reverse-gitdir-");
        try
        {
            var main = Path.Combine(root.FullName, "main");
            var worktreeA = Path.Combine(root.FullName, "wa");
            var worktreeB = Path.Combine(root.FullName, "wb");
            Directory.CreateDirectory(main);
            await ApprovalTestGit.CreateRepositoryAsync(main);
            await ApprovalTestGit.RunAsync(main, "worktree", "add", "--quiet", "--detach", worktreeA, "HEAD");
            await ApprovalTestGit.RunAsync(main, "worktree", "add", "--quiet", "--detach", worktreeB, "HEAD");

            await using var valid = await CreateRepositoryHarnessAsync("reverse-gitdir-valid", worktreeA, main);
            var allowed = await valid.EvaluateShellAsync("touch marker.txt", Ct, worktreeA);
            Assert.Equal(ApprovalOutcome.Allowed, allowed.Outcome);
            Assert.Equal(ApprovalAllowReason.StoredApproval, allowed.AllowReason);

            // The admin entry of worktree A now names the .git file of worktree B.
            await File.WriteAllTextAsync(
                Path.Combine(main, ".git", "worktrees", "wa", "gitdir"),
                Path.Combine(worktreeB, ".git") + "\n",
                Ct);

            await using var moved = await CreateRepositoryHarnessAsync("reverse-gitdir-moved", worktreeA, main);
            var observed = await moved.EvaluateShellAsync("touch marker.txt", Ct, worktreeA);

            Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
            Assert.DoesNotContain(ObservedOptionKeys.ApproveRepository, observed.Prompt!.OptionKeys);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "The Git layout uses POSIX paths and the git CLI.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Git layout uses POSIX paths and the git CLI.")]
    public async Task Repository_grant_does_not_cover_a_separate_git_directory_checkout()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-separate-gitdir-");
        try
        {
            var granted = Path.Combine(root.FullName, "granted");
            var checkout = Path.Combine(root.FullName, "checkout");
            Directory.CreateDirectory(granted);
            await ApprovalTestGit.CreateRepositoryAsync(granted);
            await ApprovalTestGit.RunAsync(
                root.FullName,
                "init",
                "--quiet",
                "--separate-git-dir",
                Path.Combine(root.FullName, "metadata"),
                checkout);

            await using var harness = await CreateRepositoryHarnessAsync("separate-gitdir", checkout, granted);
            var observed = await harness.EvaluateShellAsync("touch marker.txt", Ct, checkout);

            Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
            Assert.DoesNotContain(ObservedOptionKeys.ApproveRepository, observed.Prompt!.OptionKeys);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private Task<ShellApprovalHarness> CreateRepositoryHarnessAsync(
        string caseId,
        string project,
        string grantWorktree)
        => CreateHarnessAsync(
            caseId,
            approvals: Approvals.PersistentRepository("touch"),
            scope: new ShellApprovalHarnessScope(
                project,
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(project)!, "session")).FullName,
                $"signalr/{caseId}",
                [])
            {
                RepositoryGrantWorktree = grantWorktree
            });

    // ── 10. Bash startup and loader overrides at launch ──
    // Old coverage: ShellExecutionEnvironmentTests, ShellAssignmentMutationTests.
    //
    // This case changes the environment of the test process for the duration
    // of one launch. The names are probes without effect on other processes:
    // the startup file only exports a variable, and no loader reads the
    // LD_/DYLD_ probe names.
    [SlopwatchSuppress("SW001", "The launch runs /bin/bash, which exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The launch runs /bin/bash.")]
    public async Task Shell_launch_removes_bash_startup_and_loader_overrides()
    {
        await using var harness = await CreateHarnessAsync(
            "launch-environment",
            new ShellApprovalInvocation("printenv"),
            Approvals.PersistentAnywhere("printenv"));
        var startupFile = Path.Combine(harness.ProjectDirectory, "startup.sh");
        await File.WriteAllTextAsync(startupFile, "export NETCLAW_BASH_ENV_PROBE=probe-bash-env\n", Ct);
        var probes = new Dictionary<string, string>
        {
            ["NETCLAW_LAUNCH_PROBE"] = "probe-kept",
            ["BASH_ENV"] = startupFile,
            ["LD_NETCLAW_PROBE"] = "probe-ld",
            ["DYLD_NETCLAW_PROBE"] = "probe-dyld",
        };

        ToolRunObservation run;
        try
        {
            foreach (var (name, value) in probes)
                Environment.SetEnvironmentVariable(name, value);
            run = await harness.RunShellAsync(
                "printenv NETCLAW_LAUNCH_PROBE NETCLAW_BASH_ENV_PROBE LD_NETCLAW_PROBE DYLD_NETCLAW_PROBE",
                Ct);
        }
        finally
        {
            foreach (var name in probes.Keys)
                Environment.SetEnvironmentVariable(name, null);
        }

        Assert.Equal(ApprovalOutcome.Allowed, run.Outcome);
        Assert.Contains("probe-kept", run.Output);
        Assert.DoesNotContain("probe-bash-env", run.Output);
        Assert.DoesNotContain("probe-ld", run.Output);
        Assert.DoesNotContain("probe-dyld", run.Output);
    }
}

/// <summary>
/// Creates Git repositories with the git CLI for approval boundary cases.
/// </summary>
internal static class ApprovalTestGit
{
    public static async Task CreateRepositoryAsync(string directory)
    {
        await RunAsync(directory, "init", "--quiet");
        await RunAsync(directory, "config", "user.name", "Netclaw Test");
        await RunAsync(directory, "config", "user.email", "netclaw@example.invalid");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "tracked.txt"),
            "test",
            TestContext.Current.CancellationToken);
        await RunAsync(directory, "add", "tracked.txt");
        await RunAsync(directory, "commit", "--quiet", "-m", "initial");
    }

    public static async Task RunAsync(string workingDirectory, params string[] arguments)
    {
        var ct = TestContext.Current.CancellationToken;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
        var standardError = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.True(
            process.ExitCode == 0,
            $"git failed: {await standardOutput}\n{await standardError}");
    }
}
