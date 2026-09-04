// -----------------------------------------------------------------------
// <copyright file="UnattendedPathAccessTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using static Netclaw.Actors.Tests.Tools.PathAccessDecisionAssertions;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Unattended sessions are confined to trusted roots even
/// under the Personal audience (whose <c>Mode.All</c> would otherwise grant blanket
/// access). The decision lives in <see cref="PathAccessPolicy"/>, so it covers
/// shell and structured file tools alike. Interactive Personal sessions retain
/// their broader filesystem reach because the live approval gate is available.
/// </summary>
public sealed class UnattendedPathAccessTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;
    private readonly NetclawPaths _paths;

    public UnattendedPathAccessTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "sessions", "s1");
        _projectDir = Path.Combine(_dir.Path, "projects", "p1");
        _outsideDir = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_outsideDir);
        _paths = new NetclawPaths(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private ToolInvocationContext Ctx(TrustAudience audience, bool autonomous, bool withProject = true)
        => TestToolExecutionContext.CreateBound(
            autonomous ? "reminder/s1" : "signalr/s1",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(!autonomous),
                ProjectDirectory = withProject ? _projectDir : null,
                ChannelType = autonomous ? "reminder" : "signalr"
            }).Invocation;

    [Fact]
    public void Unattended_personal_write_outside_trusted_roots_is_denied()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var outside = Path.Combine(_outsideDir, "loot.txt");

        AssertDenied(
            policy.Evaluate(outside, ctx, PathAccessPolicy.FileOperation.Write),
            Path.GetFullPath(outside));
    }

    [Fact]
    public void Unattended_personal_read_outside_trusted_roots_is_denied()
    {
        // The file_read vector: confining only shell would let an injection read
        // arbitrary files via file_read instead. The shared seam closes both.
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var outside = Path.Combine(_outsideDir, "id_rsa");

        AssertDenied(
            policy.Evaluate(outside, ctx, PathAccessPolicy.FileOperation.Read),
            Path.GetFullPath(outside));
    }

    [Fact]
    public void Unattended_personal_inside_session_and_project_is_allowed()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var sessionFile = Path.Combine(_sessionDir, "f.txt");
        var projectFile = Path.Combine(_projectDir, "f.txt");
        AssertAllowed(
            policy.Evaluate(sessionFile, ctx, PathAccessPolicy.FileOperation.Write),
            sessionFile);
        AssertAllowed(
            policy.Evaluate(projectFile, ctx, PathAccessPolicy.FileOperation.Write),
            projectFile);
    }

    [Fact]
    public void Interactive_personal_outside_trusted_roots_is_unrestricted()
    {
        // Contrast: an interactive Personal session keeps Mode.All blanket access —
        // the human approving in real time is the backstop.
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: false);

        var outside = Path.Combine(_outsideDir, "anything.txt");

        AssertAllowed(
            policy.Evaluate(outside, ctx, PathAccessPolicy.FileOperation.Write),
            outside);
    }

    [Fact]
    public void Unavailable_interactive_capability_enforces_unattended_path_policy()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = TestToolExecutionContext.CreateBound(
            "legacy/s1",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
                ProjectDirectory = _projectDir,
            });

        var outside = Path.Join(_outsideDir, "legacy.txt");
        var decision = policy.Evaluate(
            outside,
            ctx.Invocation,
            PathAccessPolicy.FileOperation.Write);

        AssertDenied(decision, Path.GetFullPath(outside));
        Assert.Contains("unattended session", decision.Error);
    }

    [Fact]
    public void Unattended_path_policy_does_not_widen_public_access()
    {
        // Public is Mode.Roots (session-scoped) — it never reaches the Mode.All
        // Mode.All branch, so the project directory does not grant Public project access.
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Public, autonomous: true);

        var sessionFile = Path.Combine(_sessionDir, "f.txt");
        var projectFile = Path.Combine(_projectDir, "f.txt");
        AssertAllowed(
            policy.Evaluate(sessionFile, ctx, PathAccessPolicy.FileOperation.Read),
            sessionFile);
        AssertDenied(
            policy.Evaluate(projectFile, ctx, PathAccessPolicy.FileOperation.Read),
            Path.GetFullPath(projectFile));
    }

    [Fact]
    public void Unattended_personal_can_write_workspaces_but_not_identity_or_skills()
    {
        // The workspace is the operator's designated writable working area, so an
        // autonomous session may persist cross-run state there (e.g. a dedup file).
        // Skills and identity are system-managed: readable via the global read
        // roots, but never writable by an autonomous session — it must not be able
        // to rewrite its own identity or skills.
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var workspaceFile = Path.Combine(_paths.WorkspacesDirectory, "gotowebinar-last-run.json");
        var identityFile = Path.Combine(_paths.IdentityDirectory, "SOUL.md");
        var skillFile = Path.Combine(_paths.SkillsDirectory, "netclaw-operations", "SKILL.md");

        // Reads reach all three global read roots.
        AssertAllowed(
            policy.Evaluate(workspaceFile, ctx, PathAccessPolicy.FileOperation.Read),
            workspaceFile);
        AssertAllowed(
            policy.Evaluate(identityFile, ctx, PathAccessPolicy.FileOperation.Read),
            identityFile);

        // Writes reach the workspace but NOT the system-managed identity/skills trees.
        AssertAllowed(
            policy.Evaluate(workspaceFile, ctx, PathAccessPolicy.FileOperation.Write),
            workspaceFile);
        AssertDenied(
            policy.Evaluate(identityFile, ctx, PathAccessPolicy.FileOperation.Write),
            Path.GetFullPath(identityFile));
        AssertDenied(
            policy.Evaluate(skillFile, ctx, PathAccessPolicy.FileOperation.Write),
            Path.GetFullPath(skillFile));
    }

    [Fact]
    public void Unattended_personal_can_write_under_configured_custom_workspaces_dir()
    {
        // Cross-boundary contract: the daemon passes Workspaces:Directory into
        // NetclawPaths(workspacesDirectory:), and unattended path authorization must
        // honor that configured location — not a hardcoded default — so persisted
        // state lands where the operator pointed it.
        var customWorkspaces = Path.Combine(_dir.Path, "custom-ws");
        Directory.CreateDirectory(customWorkspaces);
        var paths = new NetclawPaths(_dir.Path, customWorkspaces);
        var policy = new PathAccessPolicy(new ToolConfig(), paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var stateFile = Path.Combine(customWorkspaces, "state.json");

        AssertAllowed(
            policy.Evaluate(stateFile, ctx, PathAccessPolicy.FileOperation.Write),
            stateFile);
    }

    [Fact]
    public void Unattended_personal_without_trusted_roots_fails_closed()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = TestToolExecutionContext.CreateBound("reminder/none", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
        });

        var outside = Path.Join(_outsideDir, "x.txt");
        AssertDenied(
            policy.Evaluate(outside, ctx.Invocation, PathAccessPolicy.FileOperation.Write),
            Path.GetFullPath(outside));
    }

    [Fact]
    public void Relative_path_uses_existing_project_directory()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = Ctx(TrustAudience.Personal, autonomous: false);

        var requestedPath = Path.Join("src", "App.cs");
        var expectedPath = Path.GetFullPath(Path.Join(_projectDir, "src", "App.cs"));
        var decision = policy.Evaluate(
            requestedPath,
            context,
            PathAccessPolicy.FileOperation.Read);

        AssertAllowed(decision, expectedPath);
    }

    [Fact]
    public void Relative_path_falls_back_to_session_when_project_is_stale()
    {
        var missingProject = Path.Join(_dir.Path, "moved-project");
        var context = TestToolExecutionContext.CreateBound(
            "signalr/stale-project",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true),
                ProjectDirectory = missingProject
            });
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));

        var requestedPath = "notes/result.md";
        var expectedPath = Path.GetFullPath(Path.Join(_sessionDir, "notes", "result.md"));
        var decision = policy.Evaluate(
            requestedPath,
            context.Invocation,
            PathAccessPolicy.FileOperation.Write);

        AssertAllowed(decision, expectedPath);
    }

    [Fact]
    public void Relative_path_without_project_or_session_returns_correction()
    {
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            InheritedCwd = _projectDir
        });
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));

        var decision = policy.Evaluate(
            "src/App.cs",
            context.Invocation,
            PathAccessPolicy.FileOperation.Read);

        AssertDenied(decision, string.Empty, PathAccessPolicy.PathAccessFailure.MissingBase);
        Assert.Contains("invalid_context", decision.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("set_working_directory", decision.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_traversal_into_shared_sessions_root_is_allowed()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = Ctx(TrustAudience.Public, autonomous: true, withProject: false);

        var requestedPath = Path.Join("..", "outside.txt");
        var expectedPath = Path.GetFullPath(Path.Join(_sessionDir, "..", "outside.txt"));
        var decision = policy.Evaluate(
            requestedPath,
            context,
            PathAccessPolicy.FileOperation.Read);

        AssertAllowed(decision, expectedPath);
    }

    [Fact]
    public void Relative_traversal_beyond_shared_sessions_root_is_denied()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = Ctx(TrustAudience.Public, autonomous: true, withProject: false);

        var requestedPath = Path.Join("..", "..", "outside.txt");
        var expectedPath = Path.GetFullPath(Path.Join(_sessionDir, "..", "..", "outside.txt"));
        var decision = policy.Evaluate(
            requestedPath,
            context,
            PathAccessPolicy.FileOperation.Read);

        AssertDenied(decision, expectedPath);
    }

    [Fact]
    public void Absolute_path_retains_existing_resolution()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = Ctx(TrustAudience.Personal, autonomous: false);
        var absolute = Path.GetFullPath(Path.Join(_outsideDir, "file.txt"));

        var decision = policy.Evaluate(
            absolute,
            context,
            PathAccessPolicy.FileOperation.Read);

        AssertAllowed(decision, absolute);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Native drive-relative path semantics require Windows.")]
    [SlopwatchSuppress("SW001", "This regression requires native Windows drive-relative and root-relative path semantics.")]
    public void Windows_relative_paths_use_project_but_partial_paths_fail_closed()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = Ctx(TrustAudience.Personal, autonomous: false);

        var relativeDecision = policy.Evaluate(
            @"src\App.cs",
            context,
            PathAccessPolicy.FileOperation.Read);
        AssertAllowed(
            relativeDecision,
            Path.GetFullPath(Path.Join(_projectDir, "src", "App.cs")));

        var drive = Path.GetPathRoot(_projectDir)![..2];
        var driveRelativeDecision = policy.Evaluate(
            $@"{drive}src\App.cs",
            context,
            PathAccessPolicy.FileOperation.Read);
        AssertDenied(
            driveRelativeDecision,
            string.Empty,
            PathAccessPolicy.PathAccessFailure.InvalidInput);

        var rootRelativeDecision = policy.Evaluate(
            @"\src\App.cs",
            context,
            PathAccessPolicy.FileOperation.Read);
        AssertDenied(
            rootRelativeDecision,
            string.Empty,
            PathAccessPolicy.PathAccessFailure.InvalidInput);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "Directory symlink creation is privilege-gated on Windows.")]
    [SlopwatchSuppress("SW001", "This regression requires native POSIX symbolic-link traversal semantics.")]
    public void Relative_path_through_project_symlink_is_denied()
    {
        var link = Path.Join(_projectDir, "escape");
        Directory.CreateSymbolicLink(link, _outsideDir);
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = Ctx(TrustAudience.Personal, autonomous: true);

        var requestedPath = Path.Join("escape", "secret.txt");
        var decision = policy.Evaluate(
            requestedPath,
            context,
            PathAccessPolicy.FileOperation.Read);

        AssertDenied(decision, string.Empty);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses native POSIX link semantics.")]
    [SlopwatchSuppress("SW001", "This regression requires native POSIX symbolic-link base semantics.")]
    public void Symlinked_project_base_is_denied()
    {
        var projectLink = Path.Join(_dir.Path, "project-link");
        Directory.CreateSymbolicLink(projectLink, _outsideDir);
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = TestToolExecutionContext.CreateBound(
            "signalr/symlink-project-base",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ProjectDirectory = projectLink
            });

        var decision = policy.Evaluate(
            "secret.txt",
            context.Invocation,
            PathAccessPolicy.FileOperation.Read);

        AssertDenied(decision, string.Empty);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses native POSIX link semantics.")]
    [SlopwatchSuppress("SW001", "This regression requires native POSIX ancestor-link semantics.")]
    public void Posix_project_base_with_link_ancestor_is_denied()
    {
        AssertProjectBaseWithLinkAncestorIsDenied(autonomous: true);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses native POSIX ancestor-link semantics.")]
    [SlopwatchSuppress("SW001", "This regression requires native POSIX ancestor-link semantics.")]
    public void Interactive_project_base_with_link_ancestor_is_denied()
    {
        AssertProjectBaseWithLinkAncestorIsDenied(autonomous: false);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "This case uses native Windows junction semantics.")]
    [SlopwatchSuppress("SW001", "This regression requires native Windows ancestor-link semantics.")]
    public void Windows_project_base_with_link_ancestor_is_denied()
    {
        AssertProjectBaseWithLinkAncestorIsDenied(autonomous: true);
    }

    private void AssertProjectBaseWithLinkAncestorIsDenied(bool autonomous)
    {
        var workspaces = Path.Join(_dir.Path, "owned-workspaces");
        var target = Path.Join(_dir.Path, "linked-target");
        var project = Path.Join(target, "project");
        Directory.CreateDirectory(workspaces);
        Directory.CreateDirectory(project);

        var link = Path.Join(workspaces, "linked-parent");
        Directory.CreateSymbolicLink(link, target);
        var linkedProject = Path.Join(link, "project");
        var paths = new NetclawPaths(_dir.Path, workspaces);
        var policy = new PathAccessPolicy(new ToolConfig(), paths, new ToolPathPolicy([]));
        var context = TestToolExecutionContext.CreateBound(
            "reminder/ancestor-link-project",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = autonomous
                    ? new InteractiveApprovalCapability.Unavailable()
                    : new InteractiveApprovalCapability.Available(new TestParentApprovalBridge()),
                ProjectDirectory = linkedProject
            });

        var decision = policy.Evaluate(
            "secret.txt",
            context.Invocation,
            PathAccessPolicy.FileOperation.Read);

        AssertDenied(decision, string.Empty);
    }
}
