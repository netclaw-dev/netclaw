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

    private ToolExecutionContext Ctx(TrustAudience audience, bool autonomous, bool withProject = true)
        => new(autonomous ? "reminder/s1" : "signalr/s1", _sessionDir)
        {
            Audience = audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            SupportsInteractiveApproval = !autonomous,
            ProjectDirectory = withProject ? _projectDir : null,
            ChannelType = autonomous ? "reminder" : "signalr"
        };

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
        var ctx = new ToolExecutionContext("reminder/none", sessionDirectory: null)
        {
            Audience = TrustAudience.Personal,
            SupportsInteractiveApproval = false
        };

        Assert.False(policy.TryResolveWritePath(Path.Combine(_outsideDir, "x.txt"), ctx, out _, out _));
    }
}
