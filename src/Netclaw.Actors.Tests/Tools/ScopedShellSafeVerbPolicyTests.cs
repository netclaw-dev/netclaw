// -----------------------------------------------------------------------
// <copyright file="ScopedShellSafeVerbPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ScopedShellSafeVerbPolicyTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _sessionDir;
    private readonly string _outsideDir;

    public ScopedShellSafeVerbPolicyTests()
    {
        _projectDir = CreateTempDir("project");
        _sessionDir = CreateTempDir("session");
        _outsideDir = CreateTempDir("outside");
    }

    public void Dispose()
    {
        SafeDelete(_projectDir);
        SafeDelete(_sessionDir);
        SafeDelete(_outsideDir);
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netclaw-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDelete(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort — a leftover temp tree from a failed
            // test run is acceptable, a test crash from a permissions glitch
            // during teardown is not. Log so an investigator finding the
            // leftover tree can correlate it back to a specific test run.
            System.Diagnostics.Debug.WriteLine($"SafeDelete failed for '{path}': {ex.Message}");
        }
    }

    private static SafeVerbList VerbList(params string[] verbs)
        => SafeVerbList.FromVerbs(ApprovalShell.Bash, verbs);

    private static ApprovalCandidate Candidate(
        string verb,
        string? directory = null,
        ApprovalShell shell = ApprovalShell.Bash)
    {
        if (shell != ApprovalShell.Bash)
        {
            return new ApprovalCandidate(verb, directory)
            {
                Shell = shell,
                VerbTokens = Array.AsReadOnly(
                    verb.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            };
        }

        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var parsed = Assert.Single(matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = verb,
                ["WorkingDirectory"] = "/"
            }));
        return parsed with { Directory = directory };
    }

    private static IReadOnlyList<ApprovalCandidate> Candidates(params string[] verbs)
        => verbs.Select(verb => Candidate(verb)).ToList();

    private static bool ShortCircuits(
        ScopedShellSafeVerbPolicy policy,
        string verb,
        string? cwd,
        ToolInvocationContext context) =>
        AllShortCircuit(policy, [Candidate(verb)], cwd, context);

    private static bool AllShortCircuit(
        ScopedShellSafeVerbPolicy policy,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        ToolInvocationContext context)
    {
        if (candidates.Count == 0)
            return false;

        return candidates.All(candidate => policy.ShortCircuits(candidate, cwd, context));
    }

    private ToolInvocationContext PersonalContext(string? projectDir = null, string? sessionDir = null)
        => TestToolExecutionContext.CreateBound("session-1", sessionDir ?? _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = projectDir
        }).Invocation;

    private ToolInvocationContext PublicContext(string? projectDir = null)
        => TestToolExecutionContext.CreateBound("session-1", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Public,
            ProjectDirectory = projectDir
        }).Invocation;

    [Fact]
    public void Safe_verb_in_project_directory_short_circuits()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(ShortCircuits(policy, "grep", _projectDir, ctx));
    }

    [Fact]
    public void Safe_verb_in_session_directory_short_circuits()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("cat"));
        var ctx = PersonalContext();

        Assert.True(ShortCircuits(policy, "cat", _sessionDir, ctx));
    }

    [Fact]
    public void Safe_verb_outside_safe_spaces_falls_through_to_prompt()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "grep", _outsideDir, ctx));
    }

    [Fact]
    public void Mutating_verb_in_safe_space_falls_through_to_prompt()
    {
        // The verb list deliberately omits "git push"; even with cwd inside
        // the safe space the policy refuses the short-circuit.
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git status", "git log"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "git push", _projectDir, ctx));
    }

    [Fact]
    public void Public_audience_does_not_get_project_directory_safe_space()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        // Public has project_dir set (somehow), but it should be ignored.
        var ctx = PublicContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "grep", _projectDir, ctx));
        // Session_dir still works for Public.
        Assert.True(ShortCircuits(policy, "grep", _sessionDir, ctx));
    }

    [Fact]
    public void Symlink_segment_in_cwd_breaks_short_circuit()
    {
        // Skip on Windows where directory symlink creation is privilege-gated.
        if (OperatingSystem.IsWindows())
            return;

        var leakTarget = CreateTempDir("leak-target");
        var symlinkPath = Path.Combine(_projectDir, "leak");
        try
        {
            Directory.CreateSymbolicLink(symlinkPath, leakTarget);

            var policy = new ScopedShellSafeVerbPolicy(VerbList("cat"));
            var ctx = PersonalContext(projectDir: _projectDir);

            Assert.False(ShortCircuits(policy, "cat", symlinkPath, ctx));
        }
        finally
        {
            SafeDelete(leakTarget);
        }
    }

    [Fact]
    public void All_short_circuit_returns_false_when_any_verb_is_unsafe()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep", "cat"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(policy, Candidates("grep", "git push"), _projectDir, ctx));
    }

    [Fact]
    public void All_short_circuit_returns_true_when_every_verb_is_safe_and_in_space()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep", "cat", "wc"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(AllShortCircuit(policy, Candidates("grep", "cat", "wc"), _projectDir, ctx));
    }

    [Fact]
    public void Empty_candidate_list_does_not_short_circuit()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(policy, [], _projectDir, ctx));
    }

    [Fact]
    public void Null_cwd_does_not_short_circuit()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "grep", null, ctx));
    }

    [Fact]
    public void Newly_added_read_only_verb_short_circuits_in_safe_space()
    {
        // Mirrors the reviewed catalog: a read-only system verb and a
        // read-only gh query short-circuit inside a trusted
        // zone and still prompt outside one. The bundled list's membership of
        // these verbs is verified separately by SafeVerbLoaderTests.
        var policy = new ScopedShellSafeVerbPolicy(VerbList("whoami", "gh run list"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(ShortCircuits(policy, "whoami", _sessionDir, ctx));
        Assert.True(ShortCircuits(policy, "gh run list", _projectDir, ctx));
        Assert.False(ShortCircuits(policy, "whoami", _outsideDir, ctx));
    }

    [Fact]
    public void New_safe_verb_chained_with_mutating_verb_still_prompts()
    {
        // The all-clauses-safe conjunction holds: `whoami` is safe but a
        // compound that also runs the unlisted `git push` must still prompt.
        var policy = new ScopedShellSafeVerbPolicy(VerbList("whoami"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(
            policy,
            Candidates("whoami", "git push origin main"),
            _projectDir,
            ctx));
    }

    [Fact]
    public void Reviewed_phrase_matches_a_longer_canonical_token_chain()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git ls-tree"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(AllShortCircuit(
            policy,
            [Candidate("git ls-tree feature", _projectDir)],
            _projectDir,
            ctx));
    }

    [Theory]
    [InlineData("git -c include.path={0}/config status")]
    [InlineData("git --no-pager status")]
    public void Argument_before_reviewed_phrase_stays_strict(string commandTemplate)
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git status"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var command = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            commandTemplate,
            _outsideDir);
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = _projectDir
            });

        Assert.False(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Theory]
    [InlineData("grep -f /external/patterns ./data.txt", "grep")]
    [InlineData("wc --files0-from=/external/list", "wc")]
    [InlineData("du --exclude-from=/external/patterns ./data", "du")]
    [InlineData("realpath --relative-to=/external ./data", "realpath")]
    public void Path_shaped_option_operand_outside_safe_root_stays_strict(
        string command,
        string phrase)
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList(phrase));
        var ctx = PersonalContext(projectDir: _projectDir);
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = _projectDir
            });

        Assert.False(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Path_shaped_option_operand_under_safe_root_remains_eligible()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var command = "grep -f ./patterns ./data.txt";
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = _projectDir
            });

        Assert.True(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Path_shaped_data_under_safe_root_does_not_create_new_authority()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("gh run list"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "gh run list --repo example/project",
                ["WorkingDirectory"] = _projectDir
            });

        Assert.True(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void PowerShell_compatibility_paths_use_posix_host_roots()
    {
        if (OperatingSystem.IsWindows())
            return;

        var policy = new ScopedShellSafeVerbPolicy(
            SafeVerbList.FromVerbs(ApprovalShell.PowerShell, ["Get-ChildItem"]));
        var ctx = PersonalContext(projectDir: _projectDir);
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                "C:\\PowerShell\\pwsh.exe",
                PwshDialect.PowerShell7));
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "Get-ChildItem -LiteralPath .\\data.txt",
                ["WorkingDirectory"] = _projectDir
            });

        Assert.True(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Prefix_collision_does_not_match_reviewed_phrase()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git ls-tree"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(
            policy,
            [Candidate("git ls-treex feature", _projectDir)],
            _projectDir,
            ctx));
    }

    [Fact]
    public void Candidate_without_canonical_tokens_stays_strict()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidate = new ApprovalCandidate("head", _projectDir);

        Assert.False(AllShortCircuit(policy, [candidate], _projectDir, ctx));
    }

    [Fact]
    public void Candidate_from_another_shell_stays_strict()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("Get-Content"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(
            policy,
            [Candidate("Get-Content", _projectDir, ApprovalShell.PowerShell)],
            _projectDir,
            ctx));
    }

    [Fact]
    public void Dotted_symlink_directory_does_not_short_circuit()
    {
        if (OperatingSystem.IsWindows())
            return;

        var target = CreateTempDir("dotted-target");
        var link = Path.Combine(_projectDir, "service.repo");
        try
        {
            Directory.CreateSymbolicLink(link, target);
            var policy = new ScopedShellSafeVerbPolicy(VerbList("find"));
            var ctx = PersonalContext(projectDir: _projectDir);
            var candidate = Candidate("find", link);

            Assert.False(AllShortCircuit(policy, [candidate], _projectDir, ctx));
        }
        finally
        {
            SafeDelete(target);
        }
    }

    [Fact]
    public void Candidate_path_outside_safe_spaces_falls_through_to_prompt()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("cat"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[] { Candidate("cat", _outsideDir) };

        Assert.False(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Reviewed_safe_work_under_cwd_can_request_project_declaration()
    {
        var nested = Path.Combine(_outsideDir, "src");
        Directory.CreateDirectory(nested);
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head", "wc"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[]
        {
            Candidate("head", nested),
            Candidate("wc", _outsideDir)
        };

        Assert.True(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Already_declared_project_scope_does_not_request_another_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _outsideDir);
        var candidates = new[] { Candidate("head", _outsideDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Unsafe_work_cannot_request_project_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[]
        {
            Candidate("head", _outsideDir),
            Candidate("rm", _outsideDir)
        };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Explicit_path_outside_cwd_cannot_request_project_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[] { Candidate("head", _projectDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Public_session_cannot_request_project_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PublicContext(projectDir: _projectDir);
        var candidates = new[] { Candidate("head", _outsideDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }
}
