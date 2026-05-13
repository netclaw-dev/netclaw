// -----------------------------------------------------------------------
// <copyright file="ScopedShellSafeVerbPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
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

    private ToolExecutionContext PersonalContext(string? projectDir = null, string? sessionDir = null)
        => new("session-1", sessionDir ?? _sessionDir)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            ProjectDirectory = projectDir
        };

    private ToolExecutionContext PublicContext(string? projectDir = null)
        => new("session-1", _sessionDir)
        {
            Audience = TrustAudience.Public.ToWireValue(),
            ProjectDirectory = projectDir
        };

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

        Assert.False(policy.AllShortCircuit(["grep", "git push"], _projectDir, ctx));
    }

    [Fact]
    public void All_short_circuit_returns_true_when_every_verb_is_safe_and_in_space()
    {
        var policy = new ScopedShellSafeVerbPolicy(VerbList("grep", "cat", "wc"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(policy.AllShortCircuit(["grep", "cat", "wc"], _projectDir, ctx));
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
}
