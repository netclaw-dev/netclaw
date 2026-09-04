// -----------------------------------------------------------------------
// <copyright file="PersonalFileAccessPolicyTests.cs" company="Petabridge, LLC">
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
/// Verifies that file profiles, rather than shell or approval capability,
/// decide Personal, Team, and Public read and attach reach.
/// </summary>
public sealed class PersonalFileAccessPolicyTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly string _outsideDir;
    private readonly NetclawPaths _paths;

    public PersonalFileAccessPolicyTests()
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

    private static ToolConfig BuildPersonalRootsConfig(string root)
    {
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = [root]
        };
        toolConfig.AudienceProfiles.Personal.AttachFiles = new ToolFilesystemAccessProfile
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
        // Explicit Personal roots remain authoritative in every run scope.
        { TrustAudience.Personal, true, false, true, true },
        { TrustAudience.Personal, true, true, true, false },
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
            ? BuildPersonalRootsConfig(_sessionDir)
            : new ToolConfig();
        var policy = new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
        var ctx = Ctx(audience, autonomous: !interactive);

        var path = outsideRoots
            ? Path.Combine(_outsideDir, "notes.txt")
            : Path.Combine(_sessionDir, "notes.txt");

        var decision = policy.Evaluate(path, ctx, PathAccessPolicy.FileOperation.Read);

        Assert.Equal(expectedAllow, decision.Allowed);
        Assert.Equal(Path.GetFullPath(path), decision.CanonicalPath);
        Assert.Equal(expectedAllow, string.IsNullOrEmpty(decision.Error));
        Assert.Equal(
            expectedAllow ? null : PathAccessPolicy.PathAccessFailure.AccessDenied,
            decision.Failure);
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
            ? BuildPersonalRootsConfig(_sessionDir)
            : new ToolConfig();
        var policy = new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
        var ctx = Ctx(audience, autonomous: !interactive);

        var path = outsideRoots
            ? Path.Combine(_outsideDir, "report.png")
            : Path.Combine(_sessionDir, "report.png");

        var decision = policy.Evaluate(path, ctx, PathAccessPolicy.FileOperation.Attach);

        Assert.Equal(expectedAllow, decision.Allowed);
        Assert.Equal(Path.GetFullPath(path), decision.CanonicalPath);
        Assert.Equal(expectedAllow, string.IsNullOrEmpty(decision.Error));
        Assert.Equal(
            expectedAllow ? null : PathAccessPolicy.PathAccessFailure.AccessDenied,
            decision.Failure);
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
    public void Explicit_personal_read_roots_apply_to_reads_and_project_declarations()
    {
        var config = BuildPersonalRootsConfig(_sessionDir);
        var policy = new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: false);

        var outside = Path.Combine(_outsideDir, "notes.txt");

        AssertDenied(
            policy.Evaluate(outside, ctx, PathAccessPolicy.FileOperation.Read),
            Path.GetFullPath(outside));
        AssertDenied(
            policy.Evaluate(outside, ctx, PathAccessPolicy.FileOperation.DeclareProjectScope),
            Path.GetFullPath(outside));
    }

    [Fact]
    public void Set_working_directory_stays_roots_scoped_for_default_mode_all()
    {
        // Regression (#1724): the opt-out must also hold for the DEFAULT
        // Personal profile (Mode.All) — the most common configuration. The
        // Mode.All interactive blanket grant must not leak into the
        // working-directory declaration.
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: false);

        var outside = Path.Combine(_outsideDir, "notes.txt");

        // Mode.All grants file reads independently of shell policy.
        AssertAllowed(
            policy.Evaluate(outside, ctx, PathAccessPolicy.FileOperation.Read),
            outside);
        // ...but declaring project scope still requires an allowed path access decision.
        AssertDenied(
            policy.Evaluate(outside, ctx, PathAccessPolicy.FileOperation.DeclareProjectScope),
            Path.GetFullPath(outside));
    }

    public static TheoryData<bool, bool> AttachRootsModeCases => new()
    {
        // interactive, expectedAllow
        { true, false },
        { false, false },
    };

    [Theory]
    [MemberData(nameof(AttachRootsModeCases))]
    public void Attach_reach_roots_mode_matches_expected(bool interactive, bool expectedAllow)
    {
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
        var policy = new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
        var ctx = Ctx(TrustAudience.Personal, autonomous: !interactive);

        var path = Path.Combine(_outsideDir, "report.png");
        var decision = policy.Evaluate(path, ctx, PathAccessPolicy.FileOperation.Attach);

        Assert.Equal(expectedAllow, decision.Allowed);
        Assert.Equal(Path.GetFullPath(path), decision.CanonicalPath);
        Assert.Equal(expectedAllow, string.IsNullOrEmpty(decision.Error));
        Assert.Equal(
            expectedAllow ? null : PathAccessPolicy.PathAccessFailure.AccessDenied,
            decision.Failure);
    }
}
