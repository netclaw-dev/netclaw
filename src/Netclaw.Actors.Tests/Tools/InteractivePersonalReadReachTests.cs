// -----------------------------------------------------------------------
// <copyright file="InteractivePersonalReadReachTests.cs" company="Petabridge, LLC">
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
/// Interactive Personal-audience sessions get shell-equivalent read/attach
/// reach: out-of-root paths resolve instead of hard-failing, matching the
/// approval-gated shell surface. Autonomous sessions, Team, and Public keep
/// their roots-scoped or fail-closed behavior. Regression guard for
/// netclaw-dev/netclaw#1724.
/// </summary>
public sealed class InteractivePersonalReadReachTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly string _outsideDir;
    private readonly NetclawPaths _paths;

    public InteractivePersonalReadReachTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "sessions", "s1");
        _outsideDir = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(_outsideDir);
        _paths = new NetclawPaths(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private ToolInvocationContext Ctx(TrustAudience audience, bool autonomous)
        => TestToolExecutionContext.CreateBound(
            autonomous ? "reminder/s1" : "signalr/s1",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(!autonomous),
                ProjectDirectory = null,
                ChannelType = autonomous ? "reminder" : "signalr"
            }).Invocation;

    private static ToolConfig BuildPersonalReadRootsConfig(string root)
    {
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = [root]
        };
        return toolConfig;
    }

    public static TheoryData<TrustAudience, bool, bool, bool, bool> ReadReachCases => new()
    {
        // audience, interactive, outsideRoots, hardenedPersonalRoots, expectedAllow
        // Default Personal (Mode.All): blanket interactive grant, autonomous clamp.
        { TrustAudience.Personal, true, false, false, true },
        { TrustAudience.Personal, true, true, false, true },
        { TrustAudience.Personal, false, false, false, true },
        { TrustAudience.Personal, false, true, false, false },
        // Hardened Personal (ReadFiles = Roots = session dir): the #1724 trigger.
        { TrustAudience.Personal, true, false, true, true },
        { TrustAudience.Personal, true, true, true, true },   // NEW: shell-equivalent reach
        { TrustAudience.Personal, false, false, true, true },
        { TrustAudience.Personal, false, true, true, false },
        // Team (Roots): roots-scoped everywhere.
        { TrustAudience.Team, true, false, false, true },
        { TrustAudience.Team, true, true, false, false },
        { TrustAudience.Team, false, false, false, true },
        { TrustAudience.Team, false, true, false, false },
        // Public (session only): never widened.
        { TrustAudience.Public, true, false, false, true },
        { TrustAudience.Public, true, true, false, false },
        { TrustAudience.Public, false, false, false, true },
        { TrustAudience.Public, false, true, false, false },
    };

    [Theory]
    [MemberData(nameof(ReadReachCases))]
    public void Read_reach_matches_expected(
        TrustAudience audience,
        bool interactive,
        bool outsideRoots,
        bool hardenedPersonalRoots,
        bool expectedAllow)
    {
        var config = hardenedPersonalRoots
            ? BuildPersonalReadRootsConfig(_sessionDir)
            : new ToolConfig();
        var policy = new ScopedFileAccessPolicy(config, _paths);
        var ctx = Ctx(audience, autonomous: !interactive);

        var path = outsideRoots
            ? Path.Combine(_outsideDir, "notes.txt")
            : Path.Combine(_sessionDir, "notes.txt");

        var allowed = policy.TryResolveReadPath(path, ctx, out _, out _);

        Assert.Equal(expectedAllow, allowed);
    }

    [Theory]
    [MemberData(nameof(ReadReachCases))]
    public void Attach_reach_matches_expected(
        TrustAudience audience,
        bool interactive,
        bool outsideRoots,
        bool hardenedPersonalRoots,
        bool expectedAllow)
    {
        var config = hardenedPersonalRoots
            ? BuildPersonalReadRootsConfig(_sessionDir)
            : new ToolConfig();
        var policy = new ScopedFileAccessPolicy(config, _paths);
        var ctx = Ctx(audience, autonomous: !interactive);

        var path = outsideRoots
            ? Path.Combine(_outsideDir, "report.png")
            : Path.Combine(_sessionDir, "report.png");

        var allowed = policy.TryResolveAttachPath(path, ctx, out _, out _);

        Assert.Equal(expectedAllow, allowed);
    }

    public static TheoryData<TrustAudience, bool, bool> AttachToolReachCases => new()
    {
        // audience, interactive, expectedAttached
        { TrustAudience.Personal, true, true },
        { TrustAudience.Personal, false, false },
        { TrustAudience.Team, true, false },
        { TrustAudience.Public, true, false },
    };

    [Theory]
    [MemberData(nameof(AttachToolReachCases))]
    public async Task Attach_tool_outside_session_matches_expected(
        TrustAudience audience,
        bool interactive,
        bool expectedAttached)
    {
        var outsideFile = Path.Combine(_outsideDir, "report.png");
        await File.WriteAllBytesAsync(outsideFile, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var context = TestToolExecutionContext.CreateBound(
            interactive ? "signalr/s1" : "reminder/s1",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(interactive),
                ProjectDirectory = null,
                ChannelType = interactive ? "signalr" : "reminder"
            });
        var tool = new AttachFileTool(new ToolConfig(), new NetclawPaths(), new ToolPathPolicy([]));
        var args = ToolInput.Create("Path", outsideFile);

        var result = await tool.ExecuteAsync(args, context.Invocation, CancellationToken.None);

        if (expectedAttached)
        {
            Assert.Contains("File attached", result);
            Assert.Single(context.FileAttachments);
        }
        else
        {
            Assert.Contains("Error", result);
            Assert.Empty(context.FileAttachments);
        }
    }

    [Fact]
    public async Task Attach_tool_denies_control_plane_files_even_with_interactive_reach()
    {
        // Regression (#1724): attach must use the same hard-deny surface
        // as file_read/file_list, so interactive Personal reach cannot ship
        // secrets/keys/db/pid/lock that shell cannot even reference.
        var secretsPath = Path.Combine(_outsideDir, "secrets.json");
        await File.WriteAllTextAsync(secretsPath, """{"apiKey":"top-secret"}""", TestContext.Current.CancellationToken);

        var context = TestToolExecutionContext.CreateBound(
            "signalr/s1",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true),
                ProjectDirectory = null,
                ChannelType = "signalr"
            });
        var tool = new AttachFileTool(new ToolConfig(), new NetclawPaths(), new ToolPathPolicy([secretsPath]));
        var args = ToolInput.Create("Path", secretsPath);

        var result = await tool.ExecuteAsync(args, context.Invocation, CancellationToken.None);

        Assert.Contains("cannot be read", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.FileAttachments);
    }

    [Fact]
    public void Set_working_directory_stays_roots_scoped_for_interactive_personal()
    {
        // Regression (#1724): set_working_directory must NOT inherit
        // shell-equivalent reach — its declaration widens the safe-verb
        // auto-approve zone and feeds project identity files into the prompt.
        var config = BuildPersonalReadRootsConfig(_sessionDir);
        var policy = new ScopedFileAccessPolicy(config, _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: false);

        var outside = Path.Combine(_outsideDir, "notes.txt");

        // Reads resolve (shell-equivalent reach)...
        Assert.True(policy.TryResolveReadPath(outside, ctx, out _, out _));
        // ...but the working-directory declaration stays roots-scoped.
        Assert.False(policy.TryResolveWorkingDirectory(outside, ctx, out _, out _));
    }

    [Fact]
    public void Set_working_directory_stays_roots_scoped_for_default_mode_all()
    {
        // Regression (#1724): the opt-out must also hold for the DEFAULT
        // Personal profile (Mode.All) — the most common configuration. The
        // Mode.All interactive blanket grant must not leak into the
        // working-directory declaration.
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: false);

        var outside = Path.Combine(_outsideDir, "notes.txt");

        // Reads resolve under Mode.All interactive...
        Assert.True(policy.TryResolveReadPath(outside, ctx, out _, out _));
        // ...but the working-directory declaration clamps to the autonomous zone.
        Assert.False(policy.TryResolveWorkingDirectory(outside, ctx, out _, out _));
    }

    public static TheoryData<bool, bool> AttachRootsModeCases => new()
    {
        // interactive, expectedAllow
        { true, true },
        { false, false },
    };

    [Theory]
    [MemberData(nameof(AttachRootsModeCases))]
    public void Attach_reach_roots_mode_matches_expected(bool interactive, bool expectedAllow)
    {
        // Regression (#1724): the new Roots-mode attach branch must actually be
        // exercised — the main matrix hardens only ReadFiles, so its attach rows
        // hit the Mode.All branch. This pins the AccessKind.Attach clause.
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = [_sessionDir]
        };
        config.AudienceProfiles.Personal.AttachFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = [_sessionDir]
        };
        var policy = new ScopedFileAccessPolicy(config, _paths);
        var ctx = Ctx(TrustAudience.Personal, autonomous: !interactive);

        var path = Path.Combine(_outsideDir, "report.png");
        var allowed = policy.TryResolveAttachPath(path, ctx, out _, out _);

        Assert.Equal(expectedAllow, allowed);
    }
}
