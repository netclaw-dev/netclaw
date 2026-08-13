// -----------------------------------------------------------------------
// <copyright file="ScopedShellSafeVerbPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
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
        => SafeVerbList.FromVerbs(verbs);

    private static IReadOnlyList<ApprovalCandidate> Candidates(params string[] verbs)
        => verbs.Select(verb => new ApprovalCandidate(verb, Directory: null)).ToList();

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

        Assert.True(policy.ShortCircuitsApproval("grep", _projectDir, ctx));
    }

    [Fact]
    public void Safe_verb_in_session_directory_short_circuits()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("cat"));
        var ctx = PersonalContext();

        Assert.True(policy.ShortCircuitsApproval("cat", _sessionDir, ctx));
    }

    [Fact]
    public void Safe_verb_outside_safe_spaces_falls_through_to_prompt()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(policy.ShortCircuitsApproval("grep", _outsideDir, ctx));
    }

    [Fact]
    public void Mutating_verb_in_safe_space_falls_through_to_prompt()
    {
        // The verb list deliberately omits "git push"; even with cwd inside
        // the safe space the policy refuses the short-circuit.
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git status", "git log"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(policy.ShortCircuitsApproval("git push", _projectDir, ctx));
    }

    [Fact]
    public void Public_audience_does_not_get_project_directory_safe_space()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        // Public has project_dir set (somehow), but it should be ignored.
        var ctx = PublicContext(projectDir: _projectDir);

        Assert.False(policy.ShortCircuitsApproval("grep", _projectDir, ctx));
        // Session_dir still works for Public.
        Assert.True(policy.ShortCircuitsApproval("grep", _sessionDir, ctx));
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

            Assert.False(policy.ShortCircuitsApproval("cat", symlinkPath, ctx));
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

        Assert.False(policy.AllShortCircuit(Candidates("grep", "git push"), _projectDir, ctx));
    }

    [Fact]
    public void All_short_circuit_returns_true_when_every_verb_is_safe_and_in_space()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep", "cat", "wc"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(policy.AllShortCircuit(Candidates("grep", "cat", "wc"), _projectDir, ctx));
    }

    [Fact]
    public void Empty_candidate_list_does_not_short_circuit()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(policy.AllShortCircuit([], _projectDir, ctx));
    }

    [Fact]
    public void Null_cwd_does_not_short_circuit()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(policy.ShortCircuitsApproval("grep", null, ctx));
    }

    [Fact]
    public void Newly_added_read_only_verb_short_circuits_in_safe_space()
    {
        // Mirrors the safe-verb expansion: a read-only system verb (date) and
        // a read-only gh query (gh pr view) short-circuit inside a trusted
        // zone and still prompt outside one. The bundled list's membership of
        // these verbs is verified separately by SafeVerbLoaderTests.
        var policy = new ScopedShellSafeVerbPolicy(VerbList("date", "gh pr view"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(policy.ShortCircuitsApproval("date", _sessionDir, ctx));
        Assert.True(policy.ShortCircuitsApproval("gh pr view", _projectDir, ctx));
        Assert.False(policy.ShortCircuitsApproval("date", _outsideDir, ctx));
    }

    [Fact]
    public void New_safe_verb_chained_with_mutating_verb_still_prompts()
    {
        // The all-clauses-safe conjunction holds: `date` is safe but a
        // compound that also runs the unlisted `git push` must still prompt.
        var policy = new ScopedShellSafeVerbPolicy(VerbList("date"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(policy.AllShortCircuit(Candidates("date", "git push origin main"), _projectDir, ctx));
    }

    [Fact]
    public void Git_ls_tree_operand_normalizes_to_read_only_verb()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git ls-tree"));
        var candidate = new ApprovalCandidate("git ls-tree feature", _projectDir)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = Array.AsReadOnly(["git", "ls-tree", "feature"]),
        };

        var normalized = policy.NormalizeCandidate(candidate);

        Assert.Equal("git ls-tree", normalized.Verb);
        Assert.Equal(_projectDir, normalized.Directory);
        Assert.Equal(["git", "ls-tree", "feature"], normalized.VerbTokens);
        Assert.Equal(ApprovalShell.Bash, normalized.Shell);
    }

    [Fact]
    public void Git_subcommand_without_an_operand_rule_stays_exact()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git remote"));
        var candidate = new ApprovalCandidate("git remote add", _projectDir);

        Assert.Equal(candidate, policy.NormalizeCandidate(candidate));
    }

    [Fact]
    public void Same_length_safe_verb_does_not_normalize_to_git_ls_tree()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git ls-tree", "gh run list"));
        var candidate = new ApprovalCandidate("gh run list feature", _projectDir);

        Assert.Equal(candidate, policy.NormalizeCandidate(candidate));
    }

    [Fact]
    public void Git_ls_tree_normalization_requires_safe_list_membership()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("git status"));
        var candidate = new ApprovalCandidate("git ls-tree feature", _projectDir);

        Assert.Equal(candidate, policy.NormalizeCandidate(candidate));
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
            var candidate = new ApprovalCandidate("find", link);

            Assert.False(policy.AllShortCircuit([candidate], _projectDir, ctx));
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
        var candidates = new[] { new ApprovalCandidate("cat", _outsideDir) };

        Assert.False(policy.AllShortCircuit(candidates, _projectDir, ctx));
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
            new ApprovalCandidate("head", nested),
            new ApprovalCandidate("wc", _outsideDir)
        };

        Assert.True(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Already_declared_project_scope_does_not_request_another_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _outsideDir);
        var candidates = new[] { new ApprovalCandidate("head", _outsideDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Unsafe_work_cannot_request_project_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[]
        {
            new ApprovalCandidate("head", _outsideDir),
            new ApprovalCandidate("rm", _outsideDir)
        };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Explicit_path_outside_cwd_cannot_request_project_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[] { new ApprovalCandidate("head", _projectDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Public_session_cannot_request_project_declaration()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("head"));
        var ctx = PublicContext(projectDir: _projectDir);
        var candidates = new[] { new ApprovalCandidate("head", _outsideDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }
}
