// -----------------------------------------------------------------------
// <copyright file="AutonomousZoneClampTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Autonomous (non-interactive) sessions are confined to a filesystem zone even
/// under the Personal audience (whose <c>Mode.All</c> would otherwise grant blanket
/// access). The clamp lives at the single <c>ScopedFileAccessPolicy.TryResolvePath</c>
/// seam, so it covers shell (via <c>TryResolveWritePath</c>) and the file tools
/// alike. Interactive sessions are unaffected — the live approval gate is their
/// backstop.
/// </summary>
public sealed class AutonomousZoneClampTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;
    private readonly NetclawPaths _paths;

    public AutonomousZoneClampTests()
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
    public void Autonomous_personal_write_outside_zone_is_denied()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var outside = Path.Combine(_outsideDir, "loot.txt");

        Assert.False(policy.TryResolveWritePath(outside, ctx, out _, out _));
    }

    [Fact]
    public void Autonomous_personal_read_outside_zone_is_denied()
    {
        // The file_read vector: confining only shell would let an injection read
        // arbitrary files via file_read instead. The shared seam closes both.
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var outside = Path.Combine(_outsideDir, "id_rsa");

        Assert.False(policy.TryResolveReadPath(outside, ctx, out _, out _));
    }

    [Fact]
    public void Autonomous_personal_inside_session_and_project_is_allowed()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        Assert.True(policy.TryResolveWritePath(Path.Combine(_sessionDir, "f.txt"), ctx, out _, out var e1), e1);
        Assert.True(policy.TryResolveWritePath(Path.Combine(_projectDir, "f.txt"), ctx, out _, out var e2), e2);
    }

    [Fact]
    public void Interactive_personal_outside_zone_is_unrestricted()
    {
        // Contrast: an interactive Personal session keeps Mode.All blanket access —
        // the human approving in real time is the backstop.
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: false);

        var outside = Path.Combine(_outsideDir, "anything.txt");

        Assert.True(policy.TryResolveWritePath(outside, ctx, out _, out var e), e);
    }

    [Fact]
    public void Unavailable_interactive_capability_enforces_autonomous_clamp()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = TestToolExecutionContext.CreateBound(
            "legacy/s1",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
                ProjectDirectory = _projectDir,
            });

        Assert.False(policy.TryResolveWritePath(
            Path.Join(_outsideDir, "legacy.txt"),
            ctx.Invocation,
            out _,
            out var error));
        Assert.Contains("autonomous session", error);
    }

    [Fact]
    public void Clamp_does_not_widen_autonomous_public()
    {
        // Public is Mode.Roots (session-scoped) — it never reaches the Mode.All
        // clamp, so the zone's project directory does not grant Public project access.
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = Ctx(TrustAudience.Public, autonomous: true);

        Assert.True(policy.TryResolveReadPath(Path.Combine(_sessionDir, "f.txt"), ctx, out _, out _));
        Assert.False(policy.TryResolveReadPath(Path.Combine(_projectDir, "f.txt"), ctx, out _, out _));
    }

    [Fact]
    public void Autonomous_personal_can_write_workspaces_but_not_identity_or_skills()
    {
        // The workspace is the operator's designated writable working area, so an
        // autonomous session may persist cross-run state there (e.g. a dedup file).
        // Skills and identity are system-managed: readable via the global read
        // roots, but never writable by an autonomous session — it must not be able
        // to rewrite its own identity or skills.
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var workspaceFile = Path.Combine(_paths.WorkspacesDirectory, "gotowebinar-last-run.json");
        var identityFile = Path.Combine(_paths.IdentityDirectory, "SOUL.md");
        var skillFile = Path.Combine(_paths.SkillsDirectory, "netclaw-operations", "SKILL.md");

        // Reads reach all three global read roots.
        Assert.True(policy.TryResolveReadPath(workspaceFile, ctx, out _, out var er1), er1);
        Assert.True(policy.TryResolveReadPath(identityFile, ctx, out _, out var er2), er2);

        // Writes reach the workspace but NOT the system-managed identity/skills trees.
        Assert.True(policy.TryResolveWritePath(workspaceFile, ctx, out _, out var ew1), ew1);
        Assert.False(policy.TryResolveWritePath(identityFile, ctx, out _, out _));
        Assert.False(policy.TryResolveWritePath(skillFile, ctx, out _, out _));
    }

    [Fact]
    public void Autonomous_personal_can_write_under_configured_custom_workspaces_dir()
    {
        // Cross-boundary contract: the daemon passes Workspaces:Directory into
        // NetclawPaths(workspacesDirectory:), and the autonomous write zone must
        // honor that configured location — not a hardcoded default — so persisted
        // state lands where the operator pointed it.
        var customWorkspaces = Path.Combine(_dir.Path, "custom-ws");
        Directory.CreateDirectory(customWorkspaces);
        var paths = new NetclawPaths(_dir.Path, customWorkspaces);
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: true);

        var stateFile = Path.Combine(customWorkspaces, "state.json");

        Assert.True(policy.TryResolveWritePath(stateFile, ctx, out _, out var e), e);
    }

    [Fact]
    public void Autonomous_personal_with_empty_zone_fails_closed()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = TestToolExecutionContext.CreateBound("reminder/none", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
        });

        Assert.False(policy.TryResolveWritePath(Path.Join(_outsideDir, "x.txt"), ctx.Invocation, out _, out _));
    }

    [Fact]
    public void Relative_path_uses_existing_project_directory()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = Ctx(TrustAudience.Personal, autonomous: false);

        Assert.True(policy.TryResolveReadPath(
            Path.Join("src", "App.cs"),
            context,
            out var resolved,
            out var error,
            out var failure), error);
        Assert.Equal(Path.GetFullPath(Path.Join(_projectDir, "src", "App.cs")), resolved);
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.None, failure);
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
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);

        Assert.True(policy.TryResolveWritePath(
            "notes/result.md",
            context.Invocation,
            out var resolved,
            out var error,
            out var failure), error);
        Assert.Equal(Path.GetFullPath(Path.Join(_sessionDir, "notes", "result.md")), resolved);
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.None, failure);
    }

    [Fact]
    public void Relative_path_without_project_or_session_returns_correction()
    {
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            InheritedCwd = _projectDir
        });
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);

        Assert.False(policy.TryResolveReadPath(
            "src/App.cs",
            context.Invocation,
            out var resolved,
            out var error,
            out var failure));
        Assert.Empty(resolved);
        Assert.Contains("invalid_context", error, StringComparison.Ordinal);
        Assert.DoesNotContain("set_working_directory", error, StringComparison.Ordinal);
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.MissingBase, failure);
    }

    [Fact]
    public void Relative_traversal_is_canonicalized_before_scope_denial()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = Ctx(TrustAudience.Public, autonomous: true, withProject: false);

        Assert.False(policy.TryResolveReadPath(
            Path.Join("..", "outside.txt"),
            context,
            out var resolved,
            out _,
            out var failure));
        Assert.Equal(Path.GetFullPath(Path.Join(_sessionDir, "..", "outside.txt")), resolved);
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.AccessDenied, failure);
    }

    [Fact]
    public void Absolute_path_retains_existing_resolution()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = Ctx(TrustAudience.Personal, autonomous: false);
        var absolute = Path.GetFullPath(Path.Join(_outsideDir, "file.txt"));

        Assert.True(policy.TryResolveReadPath(
            absolute,
            context,
            out var resolved,
            out var error,
            out var failure), error);
        Assert.Equal(absolute, resolved);
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.None, failure);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Native drive-relative path semantics require Windows.")]
    [SlopwatchSuppress("SW001", "This regression requires native Windows drive-relative and root-relative path semantics.")]
    public void Windows_relative_paths_use_project_but_partial_paths_fail_closed()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = Ctx(TrustAudience.Personal, autonomous: false);

        Assert.True(policy.TryResolveReadPath(
            @"src\App.cs",
            context,
            out var resolved,
            out var error,
            out var failure), error);
        Assert.Equal(Path.GetFullPath(Path.Join(_projectDir, "src", "App.cs")), resolved);
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.None, failure);

        var drive = Path.GetPathRoot(_projectDir)![..2];
        Assert.False(policy.TryResolveReadPath(
            $@"{drive}src\App.cs",
            context,
            out _,
            out _,
            out var driveRelativeFailure));
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.InvalidInput, driveRelativeFailure);

        Assert.False(policy.TryResolveReadPath(
            @"\src\App.cs",
            context,
            out _,
            out _,
            out var rootRelativeFailure));
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.InvalidInput, rootRelativeFailure);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "Directory symlink creation is privilege-gated on Windows.")]
    [SlopwatchSuppress("SW001", "This regression requires native POSIX symbolic-link traversal semantics.")]
    public void Relative_path_through_project_symlink_is_denied()
    {
        var link = Path.Join(_projectDir, "escape");
        Directory.CreateSymbolicLink(link, _outsideDir);
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = Ctx(TrustAudience.Personal, autonomous: true);

        Assert.False(policy.TryResolveReadPath(
            Path.Join("escape", "secret.txt"),
            context,
            out _,
            out _,
            out var failure));
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.AccessDenied, failure);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses native POSIX link semantics.")]
    [SlopwatchSuppress("SW001", "This regression requires native POSIX symbolic-link base semantics.")]
    public void Symlinked_project_base_is_denied()
    {
        var projectLink = Path.Join(_dir.Path, "project-link");
        Directory.CreateSymbolicLink(projectLink, _outsideDir);
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/symlink-project-base",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ProjectDirectory = projectLink
            });

        Assert.False(policy.TryResolveReadPath(
            "secret.txt",
            context.Invocation,
            out _,
            out _,
            out var failure));
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.AccessDenied, failure);
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
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), paths);
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

        Assert.False(policy.TryResolveReadPath(
            "secret.txt",
            context.Invocation,
            out _,
            out _,
            out var failure));
        Assert.Equal(ScopedFileAccessPolicy.PathResolutionFailure.AccessDenied, failure);
    }
}
