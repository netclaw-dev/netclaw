// -----------------------------------------------------------------------
// <copyright file="PersonalReadReachTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using ShellSyntaxTree;
using static Netclaw.Actors.Tests.Tools.PathAccessDecisionAssertions;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Issue #2554, Fix A: for the Personal audience only, the read and attach reach
/// of a file tool is at least the write reach of the same profile. The write
/// reach is the shell trust zone. Protected paths still win, a project
/// declaration stays confined, and Team and Public do not change.
/// </summary>
public sealed class PersonalReadReachTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly string _readRoot;
    private readonly string _writeRoot;
    private readonly string _outsideDir;
    private readonly NetclawPaths _paths;

    public PersonalReadReachTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "sessions", "s1");
        _readRoot = Path.Combine(_dir.Path, "read-root");
        _writeRoot = Path.Combine(_dir.Path, "write-root");
        _outsideDir = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(_readRoot);
        Directory.CreateDirectory(_writeRoot);
        Directory.CreateDirectory(_outsideDir);
        _paths = new NetclawPaths(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    public enum WriteShape
    {
        /// <summary>WriteFiles mode All.</summary>
        All,

        /// <summary>WriteFiles mode Roots with the write root.</summary>
        Roots,

        /// <summary>WriteFiles mode None.</summary>
        None,

        /// <summary>WriteFiles mode Roots with the read root only (read covers write).</summary>
        SameAsRead
    }

    public enum Target
    {
        ReadRoot,
        WriteRoot,
        Outside
    }

    // audience, write shape, target, attended, expected allow (Read and Attach alike)
    public static TheoryData<TrustAudience, WriteShape, Target, bool, bool> ReachCases => new()
    {
        // Personal: the read reach follows the write reach (Fix A), attended or not (D2).
        { TrustAudience.Personal, WriteShape.All, Target.Outside, true, true },
        { TrustAudience.Personal, WriteShape.All, Target.Outside, false, true },
        { TrustAudience.Personal, WriteShape.Roots, Target.WriteRoot, true, true },
        { TrustAudience.Personal, WriteShape.Roots, Target.WriteRoot, false, true },
        { TrustAudience.Personal, WriteShape.Roots, Target.ReadRoot, true, true },
        // Negative: a path outside both the read and the write roots stays denied.
        { TrustAudience.Personal, WriteShape.Roots, Target.Outside, true, false },
        { TrustAudience.Personal, WriteShape.None, Target.Outside, true, false },
        { TrustAudience.Personal, WriteShape.SameAsRead, Target.Outside, true, false },
        { TrustAudience.Personal, WriteShape.None, Target.ReadRoot, true, true },
        // Team and Public: an explicit read profile stays authoritative. A
        // write-only folder stays write-only.
        { TrustAudience.Team, WriteShape.Roots, Target.WriteRoot, true, false },
        { TrustAudience.Team, WriteShape.Roots, Target.WriteRoot, false, false },
        { TrustAudience.Team, WriteShape.All, Target.Outside, true, false },
        { TrustAudience.Team, WriteShape.Roots, Target.ReadRoot, true, true },
        { TrustAudience.Public, WriteShape.Roots, Target.WriteRoot, true, false },
        { TrustAudience.Public, WriteShape.All, Target.Outside, true, false },
    };

    [Theory]
    [MemberData(nameof(ReachCases))]
    public void Read_and_attach_reach_follow_the_personal_write_profile(
        TrustAudience audience,
        WriteShape write,
        Target target,
        bool attended,
        bool expectedAllow)
    {
        AssertReach(audience, write, target, attended, expectedAllow, PathAccessPolicy.FileOperation.Read);
        AssertReach(audience, write, target, attended, expectedAllow, PathAccessPolicy.FileOperation.Attach);
    }

    [Fact]
    public void Write_reach_does_not_change()
    {
        // Fix A widens Read and Attach only. A Write decision keeps the write profile.
        var policy = CreatePolicy(TrustAudience.Personal, WriteShape.Roots);
        var path = Path.Combine(_readRoot, "notes.txt");

        AssertDenied(
            policy.Evaluate(path, Context(TrustAudience.Personal, attended: true), PathAccessPolicy.FileOperation.Write),
            path);
    }

    [Fact]
    public void Protected_path_stays_read_denied_for_personal_write_all()
    {
        var secrets = Path.Combine(_outsideDir, "secrets.json");
        File.WriteAllText(secrets, """{"apiKey":"top-secret"}""");
        var policy = CreatePolicy(TrustAudience.Personal, WriteShape.All, deniedPaths: [secrets]);

        var decision = policy.Evaluate(secrets, Context(TrustAudience.Personal, attended: true), PathAccessPolicy.FileOperation.Read);

        var denied = Assert.IsType<PathAccessPolicy.PathAccessDecision.Denied>(decision);
        Assert.Contains("cannot be read", denied.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SlopwatchSuppress("SW001", "This test needs POSIX symbolic link semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX symbolic links.")]
    public void Protected_path_stays_read_denied_through_a_linked_folder()
    {
        var keys = Path.Combine(_outsideDir, "keys");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, "id_ed25519"), "private");
        var link = Path.Combine(_outsideDir, "linked-keys");
        Directory.CreateSymbolicLink(link, keys);
        var policy = CreatePolicy(TrustAudience.Personal, WriteShape.All, deniedPaths: [keys]);
        var path = Path.Combine(link, "id_ed25519");

        AssertDenied(
            policy.Evaluate(path, Context(TrustAudience.Personal, attended: true), PathAccessPolicy.FileOperation.Read),
            path);
    }

    [Theory]
    [InlineData(WriteShape.All)]
    [InlineData(WriteShape.Roots)]
    public void Project_declaration_stays_confined(WriteShape write)
    {
        // #1770 review: a wider declaration widens the reviewed-safe zone and
        // loads a planted AGENTS.md. The declaration never gets the wider reach.
        var policy = CreatePolicy(TrustAudience.Personal, write);
        var target = write == WriteShape.All ? _outsideDir : _writeRoot;

        AssertDenied(
            policy.Evaluate(target, Context(TrustAudience.Personal, attended: true), PathAccessPolicy.FileOperation.DeclareProjectScope),
            target);
    }

    [Theory]
    [InlineData(TrustAudience.Personal, true)]
    [InlineData(TrustAudience.Team, false)]
    public void Trusted_read_and_attach_roots_add_the_write_roots_only_for_personal(
        TrustAudience audience,
        bool expectWriteRoot)
    {
        var policy = CreatePolicy(audience, WriteShape.Roots);
        var context = Context(audience, attended: true);

        var read = policy.GetTrustedRoots(context, PathAccessPolicy.FileOperation.Read);
        Assert.Contains(_readRoot, read);
        Assert.Equal(expectWriteRoot, read.Contains(_writeRoot));
        Assert.Equal(expectWriteRoot, policy.GetTrustedRoots(context, PathAccessPolicy.FileOperation.Attach).Contains(_writeRoot));
    }

    [Fact]
    public void Trusted_read_roots_for_personal_write_all_equal_a_read_all_profile()
    {
        // The prompt and IsReadableByAudience must agree with Evaluate. A write
        // All profile reads like a read All profile.
        var narrowed = CreatePolicy(TrustAudience.Personal, WriteShape.All);
        var reference = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = Context(TrustAudience.Personal, attended: true);

        Assert.Equal(
            reference.GetTrustedRoots(context, PathAccessPolicy.FileOperation.Read),
            narrowed.GetTrustedRoots(context, PathAccessPolicy.FileOperation.Read));
    }

    [Fact]
    public async Task File_read_tool_reads_a_file_that_the_personal_shell_can_write()
    {
        // The #2554 symptom: the shell writes to /tmp, then file_read refuses it.
        var file = Path.Combine(_outsideDir, "inline_01.txt");
        await File.WriteAllTextAsync(file, "frame-one", TestContext.Current.CancellationToken);
        var config = CreateConfig(TrustAudience.Personal, WriteShape.All);
        var tool = new FileReadTool(config, _paths, new ToolPathPolicy([]));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", file),
            Context(TrustAudience.Personal, attended: true),
            TestContext.Current.CancellationToken);

        Assert.Contains("frame-one", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Reviewed_shell_read_follows_the_personal_write_profile()
    {
        // IsReadableByAudience uses Evaluate(Read). A reviewed read phrase such
        // as `cat <outside>/x` then needs no prompt, as with the default profile.
        var policy = CreatePolicy(TrustAudience.Personal, WriteShape.All);
        var path = Path.Combine(_outsideDir, "x.log");
        var style = OperatingSystem.IsWindows() ? ShellPathStyle.Windows : ShellPathStyle.Posix;

        Assert.IsType<PathAccessPolicy.PathAccessDecision.Allowed>(
            policy.EvaluateReviewedShellPath(path, Context(TrustAudience.Personal, attended: true), style));
    }

    /// <summary>
    /// Cross-boundary round trip: the live netclaw.json shape of issue #2554
    /// binds on top of the Personal posture defaults (an explicit key replaces
    /// the default), and the runtime path decision then follows Fix A.
    /// </summary>
    [Fact]
    public void Live_config_shape_binds_and_reads_a_path_that_the_shell_can_write()
    {
        var configPath = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText(
            configPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal", "ShellExecutionMode": "HostAllowed" },
              "Tools": {
                "ShellMode": "HostAllowed",
                "AudienceProfiles": {
                  "Personal": {
                    "ReadFiles": { "Mode": "Roots", "Roots": ["~/repositories", "~/.netclaw"] },
                    "WriteFiles": { "Mode": "All" },
                    "AttachFiles": { "Mode": "Roots", "Roots": ["~/repositories", "~/.netclaw"] }
                  },
                  "Team": {
                    "ReadFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
                    "WriteFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
                    "AttachFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] }
                  }
                }
              }
            }
            """);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();
        var tools = PolicyConfiguration.Bind(configuration).Tools;

        // The bound shape is the live shape: the explicit read key won over the posture default.
        Assert.Equal(ToolFilesystemMode.Roots, tools.AudienceProfiles.Personal.ReadFiles.Mode);
        Assert.Equal(ToolFilesystemMode.All, tools.AudienceProfiles.Personal.WriteFiles.Mode);
        Assert.Equal(ToolFilesystemMode.Roots, tools.AudienceProfiles.Personal.AttachFiles.Mode);

        var secrets = Path.Combine(_outsideDir, "secrets.json");
        var policy = new PathAccessPolicy(tools, _paths, new ToolPathPolicy([secrets]));
        var render = Path.Combine(_outsideDir, "gifcheck", "inline_01.png");

        AssertAllowed(
            policy.Evaluate(render, Context(TrustAudience.Personal, attended: true), PathAccessPolicy.FileOperation.Read),
            render);
        AssertAllowed(
            policy.Evaluate(render, Context(TrustAudience.Personal, attended: false), PathAccessPolicy.FileOperation.Attach),
            render);
        AssertDenied(
            policy.Evaluate(secrets, Context(TrustAudience.Personal, attended: true), PathAccessPolicy.FileOperation.Read),
            secrets);
        AssertDenied(
            policy.Evaluate(render, Context(TrustAudience.Team, attended: true), PathAccessPolicy.FileOperation.Read),
            render);
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    private void AssertReach(
        TrustAudience audience,
        WriteShape write,
        Target target,
        bool attended,
        bool expectedAllow,
        PathAccessPolicy.FileOperation operation)
    {
        var policy = CreatePolicy(audience, write);
        var directory = target switch
        {
            Target.ReadRoot => _readRoot,
            Target.WriteRoot => _writeRoot,
            _ => _outsideDir
        };
        var path = Path.Combine(directory, "notes.txt");

        var decision = policy.Evaluate(path, Context(audience, attended), operation);

        if (expectedAllow)
            AssertAllowed(decision, path);
        else
            AssertDenied(decision, path);
    }

    private PathAccessPolicy CreatePolicy(
        TrustAudience audience,
        WriteShape write,
        IReadOnlyList<string>? deniedPaths = null)
        => new(CreateConfig(audience, write), _paths, new ToolPathPolicy(deniedPaths ?? []));

    // Read and attach profiles are Roots [read-root]. The write profile varies.
    // Team and Public cannot set Mode=All in a validated config, but the runtime
    // decision must not widen their reads when an operator sets it anyway.
    private ToolConfig CreateConfig(TrustAudience audience, WriteShape write)
    {
        var config = new ToolConfig();
        var profile = audience switch
        {
            TrustAudience.Personal => config.AudienceProfiles.Personal,
            TrustAudience.Team => config.AudienceProfiles.Team,
            _ => config.AudienceProfiles.Public
        };
        profile.ReadFiles = Roots(_readRoot);
        profile.AttachFiles = Roots(_readRoot);
        profile.WriteFiles = write switch
        {
            WriteShape.All => new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All },
            WriteShape.Roots => Roots(_writeRoot),
            WriteShape.SameAsRead => Roots(_readRoot),
            _ => new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.None }
        };
        return config;
    }

    private static ToolFilesystemAccessProfile Roots(string root)
        => new() { Mode = ToolFilesystemMode.Roots, Roots = [root] };

    private ToolInvocationContext Context(TrustAudience audience, bool attended)
        => TestToolExecutionContext.CreateBound(
            attended ? "signalr/s1" : "reminder/s1",
            _sessionDir,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(attended),
                ProjectDirectory = null,
                ChannelType = attended ? "signalr" : "reminder"
            }).Invocation;
}
