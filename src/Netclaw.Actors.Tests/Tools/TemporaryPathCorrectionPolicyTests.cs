// -----------------------------------------------------------------------
// <copyright file="TemporaryPathCorrectionPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class TemporaryPathCorrectionPolicyTests
{
    private const string PosixTemp = "/tmp";
    private const string PosixSession = "/home/user/.netclaw/sessions/example";
    private const string WindowsTemp = "C:\\Users\\user\\AppData\\Local\\Temp";
    private const string WindowsSession = "C:\\Users\\user\\.netclaw\\sessions\\example";
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsPosix => !OperatingSystem.IsWindows();
    public static bool IsWindows => OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "This test requires a POSIX storage path and Bash temporary path semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX host path semantics.")]
    public void Explicit_posix_temp_cwd_returns_managed_temporary_correction()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            PosixSession,
            explicitWorkingDirectory: PosixTemp);

        var correction = Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(decision.AgentCorrection);
        Assert.Equal(Path.Combine(PosixSession, "tmp", "parent"), correction.Target.ManagedTemporaryDirectory);
        Assert.Equal(PosixTemp, correction.Target.PlatformTemporaryRoot);
        Assert.IsNotType<ToolCorrection.ProjectDirectorySuggested>(decision.AgentCorrection);
    }

    [SlopwatchSuppress("SW001", "This test requires a POSIX storage path and Bash temporary path semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX host path semantics.")]
    public void Platform_temp_alias_maps_to_canonical_target()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "cat /tmp/result.log",
            PosixSession,
            explicitWorkingDirectory: PosixTemp,
            inspector: new TestPathInspector(resolvedRoot: "/private/tmp"));

        var correction = Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(decision.AgentCorrection);
        Assert.Equal("/private/tmp", correction.Target.PlatformTemporaryRoot);
    }

    [SlopwatchSuppress("SW001", "This theory requires a POSIX storage path and Bash temporary path semantics.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX host path semantics.")]
    [InlineData("cd /tmp && gh api repos/example/project > /tmp/result.log")]
    [InlineData("command cd /tmp && gh api repos/example/project > /tmp/result.log")]
    [InlineData("builtin cd /tmp && gh api repos/example/project > /tmp/result.log")]
    public void Parser_owned_bash_temp_transition_returns_managed_temporary_correction(
        string command)
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            command,
            PosixSession);

        Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(decision.AgentCorrection);
        Assert.IsNotType<ToolCorrection.ProjectDirectorySuggested>(decision.AgentCorrection);
    }

    [SlopwatchSuppress("SW001", "This test requires a POSIX storage path and Bash temporary path semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX host path semantics.")]
    public void Additional_posix_temp_alias_maps_to_its_own_canonical_root()
    {
        const string runtimeTemp = "/var/folders/example/T";
        var decision = Evaluate(
            BashEnvironment(),
            runtimeTemp,
            "cat /tmp/result.log",
            PosixSession,
            explicitWorkingDirectory: PosixTemp,
            inspector: new MappedPathInspector(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [runtimeTemp] = runtimeTemp,
                    [PosixTemp] = "/private/tmp"
                }),
            additionalTemporaryRoots: [PosixTemp]);

        var correction = Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(
            decision.AgentCorrection);
        Assert.Equal("/private/tmp", correction.Target.PlatformTemporaryRoot);
    }

    [Fact]
    public void Additional_posix_temp_alias_maps_descendants_to_its_canonical_root()
    {
        const string runtimeTemp = "/var/folders/example/T";
        var inspector = new MappedPathInspector(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [runtimeTemp] = runtimeTemp,
                [PosixTemp] = "/private/tmp"
            });
        var policy = new TemporaryPathCorrectionPolicy(
            BashEnvironment(),
            runtimeTemp,
            inspector,
            [PosixTemp]);

        Assert.True(policy.IsEligiblePlatformTemporaryPath("/tmp/work/result.log"));
        Assert.True(policy.IsEligiblePlatformTemporaryPath("/private/tmp/work/result.log"));
        Assert.False(policy.IsEligiblePlatformTemporaryPath("/var/external/result.log"));
    }

    [SlopwatchSuppress("SW001", "This test requires the native macOS temporary path alias configuration.")]
    [Fact(SkipUnless = nameof(IsMacOS), Skip = "This case uses native macOS temporary path aliases.")]
    public void MacOS_factory_recognizes_the_conventional_posix_temp_alias()
    {
        var policy = TemporaryPathCorrectionPolicy.Create(BashEnvironment());

        Assert.True(policy.IsPlatformTemporaryRoot(PosixTemp));
    }

    [SlopwatchSuppress("SW001", "This test requires a Windows storage path and PowerShell temporary path semantics.")]
    [Fact(SkipUnless = nameof(IsWindows), Skip = "This case uses native Windows path semantics.")]
    public void Native_windows_explicit_temp_cwd_returns_managed_temporary_correction()
    {
        var decision = Evaluate(
            PowerShellEnvironment(),
            WindowsTemp,
            "Get-Content result.log",
            WindowsSession,
            explicitWorkingDirectory: WindowsTemp);

        var correction = Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(decision.AgentCorrection);
        Assert.Equal(Path.Combine(WindowsSession, "tmp", "parent"), correction.Target.ManagedTemporaryDirectory);
    }

    [SlopwatchSuppress("SW001", "This test requires native Windows case rules for temporary paths.")]
    [Fact(SkipUnless = nameof(IsWindows), Skip = "This case uses native Windows path semantics.")]
    public void Native_windows_temp_comparison_uses_windows_case_rules()
    {
        var decision = Evaluate(
            PowerShellEnvironment(),
            WindowsTemp,
            "Get-Content result.log",
            WindowsSession,
            explicitWorkingDirectory: WindowsTemp.ToUpperInvariant());

        Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(decision.AgentCorrection);
    }

    [Theory]
    [InlineData("cd \"$TMPDIR\" && gh api repos/example/project")]
    [InlineData("cd /tmp && \"$tool\"")]
    [InlineData("cd /tmp && head result.log; \"$tool\"")]
    [InlineData("cd /tmp && gh api repos/example/project > \"$target\"")]
    [InlineData("cd /tmp && cat /etc/passwd")]
    public void Dynamic_or_external_bash_scope_keeps_normal_approval(string command)
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            command,
            PosixSession);

        Assert.Null(decision.AgentCorrection);
    }

    [Fact]
    public void Hard_deny_keeps_precedence_over_temp_correction()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "rm -rf /",
            PosixSession,
            explicitWorkingDirectory: PosixTemp);

        Assert.False(decision.Allowed);
        Assert.Null(decision.AgentCorrection);
        Assert.StartsWith("hard_deny_", decision.DenyReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Protected_path_keeps_precedence_over_temp_correction()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "cat /tmp/protected/result.log",
            PosixSession,
            explicitWorkingDirectory: PosixTemp,
            deniedPaths: ["/tmp/protected"]);

        Assert.False(decision.Allowed);
        Assert.Null(decision.AgentCorrection);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
    }

    [Fact]
    public void Inherited_temp_project_does_not_create_authored_intent()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            PosixSession,
            projectDirectory: PosixTemp);

        Assert.Null(decision.AgentCorrection);
    }

    [Fact]
    public void Headless_temp_call_keeps_noninteractive_policy_result()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            PosixSession,
            explicitWorkingDirectory: PosixTemp,
            interactive: false);

        Assert.Null(decision.AgentCorrection);
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public void Non_personal_temp_call_retains_existing_shell_boundary(TrustAudience audience)
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            PosixSession,
            explicitWorkingDirectory: PosixTemp,
            audience: audience);

        Assert.Null(decision.AgentCorrection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/session")]
    public void Invalid_storage_envelope_is_rejected_before_correction(string sessionDirectory)
    {
        Assert.Throws<ArgumentException>(() => new SessionStorageEnvelopeRoot(sessionDirectory));
    }

    [SlopwatchSuppress("SW001", "This test requires a POSIX storage path and Bash temporary path semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX host path semantics.")]
    public void Fresh_session_scope_does_not_need_to_exist()
    {
        var sessionDirectory = $"/home/user/.netclaw/sessions/{Guid.NewGuid():N}";
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            sessionDirectory,
            explicitWorkingDirectory: PosixTemp);

        Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(decision.AgentCorrection);
    }

    [Fact]
    public void PowerShell_causal_location_remains_ineligible()
    {
        var decision = Evaluate(
            PowerShellEnvironment(),
            WindowsTemp,
            "Set-Location $env:TEMP; Get-Content result.log",
            WindowsSession);

        Assert.Null(decision.AgentCorrection);
    }

    [Fact]
    public void Link_inspection_failure_suppresses_correction()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "cat /tmp/result.log",
            PosixSession,
            explicitWorkingDirectory: PosixTemp,
            inspector: new TestPathInspector(failDescendantInspection: true));

        Assert.Null(decision.AgentCorrection);
    }

    [Fact]
    public void Windows_reparse_inspection_failure_suppresses_correction()
    {
        var decision = Evaluate(
            PowerShellEnvironment(),
            WindowsTemp,
            "Get-Content escape\\result.log",
            WindowsSession,
            explicitWorkingDirectory: WindowsTemp,
            inspector: new TestPathInspector(failDescendantInspection: true));

        Assert.Null(decision.AgentCorrection);
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic link traversal semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX symbolic link semantics.")]
    public void Host_inspector_rejects_posix_symlink_descendant()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"netclaw-managed-temporary-policy-{Guid.NewGuid():N}");
        var outside = Path.Combine(testRoot, "outside");
        var link = Path.Combine(testRoot, "link");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            Assert.False(HostPlatformTemporaryPathInspector.Instance.HasNoLinkEscape(
                testRoot,
                Path.Combine(link, "result.log"),
                ShellPathStyle.Posix));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside);
            Directory.Delete(testRoot);
        }
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic link root semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX symbolic link semantics.")]
    public void Host_inspector_resolves_symlink_in_temporary_root_parent()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"netclaw-temp-root-{Guid.NewGuid():N}");
        var realParent = Path.Combine(testRoot, "real-parent");
        var realTemp = Path.Combine(realParent, "temp");
        var aliasParent = Path.Combine(testRoot, "alias-parent");
        Directory.CreateDirectory(realTemp);
        Directory.CreateSymbolicLink(aliasParent, realParent);

        try
        {
            Assert.True(HostPlatformTemporaryPathInspector.Instance.TryResolveRoot(
                Path.Combine(aliasParent, "temp"),
                ShellPathStyle.Posix,
                out var resolved));
            Assert.Equal(realTemp, resolved);
        }
        finally
        {
            Directory.Delete(aliasParent);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic link alias semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case uses POSIX symbolic link semantics.")]
    public void Canonical_platform_temp_alias_can_recommend_managed_temporary_for_redirect()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"netclaw-temp-alias-{Guid.NewGuid():N}");
        var realTemp = Path.Combine(testRoot, "real-temp");
        var aliasTemp = Path.Combine(testRoot, "alias-temp");
        Directory.CreateDirectory(realTemp);
        Directory.CreateSymbolicLink(aliasTemp, realTemp);

        try
        {
            var output = Path.Combine(aliasTemp, "result.log");
            var externalFirst = Evaluate(
                BashEnvironment(),
                aliasTemp,
                $"head '{PosixSession}/prior.log'; cd '{aliasTemp}' && " +
                $"gh api repos/example/project > '{output}'",
                PosixSession,
                inspector: HostPlatformTemporaryPathInspector.Instance);
            Assert.Null(externalFirst.AgentCorrection);

            var decision = Evaluate(
                BashEnvironment(),
                aliasTemp,
                $"cd '{aliasTemp}' && gh api repos/example/project > '{output}'",
                PosixSession,
                inspector: HostPlatformTemporaryPathInspector.Instance);

            var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
            var correction = Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(decision.AgentCorrection);
            Assert.Equal(realTemp, correction.Target.PlatformTemporaryRoot);
        }
        finally
        {
            Directory.Delete(aliasTemp);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static ToolAuthorizationDecision Evaluate(
        ShellExecutionEnvironment environment,
        string tempRoot,
        string command,
        string sessionDirectory,
        string? explicitWorkingDirectory = null,
        string? projectDirectory = null,
        bool interactive = true,
        TrustAudience audience = TrustAudience.Personal,
        IPlatformTemporaryPathInspector? inspector = null,
        IReadOnlyList<string>? deniedPaths = null,
        IReadOnlyList<string>? additionalTemporaryRoots = null)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var profile = audience switch
        {
            TrustAudience.Public => config.AudienceProfiles.Public,
            TrustAudience.Team => config.AudienceProfiles.Team,
            TrustAudience.Personal => config.AudienceProfiles.Personal,
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, null)
        };
        profile.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ShellTool.ToolName] = ToolApprovalMode.Approval
            }
        };
        var commandPolicy = new ShellCommandPolicy(environment);
        var pathPolicy = new ToolPathPolicy(environment, deniedPaths ?? []);
        var pathInspector = inspector ?? new TestPathInspector();
        var tempPolicy = additionalTemporaryRoots is null
            ? new TemporaryPathCorrectionPolicy(environment, tempRoot, pathInspector)
            : new TemporaryPathCorrectionPolicy(
                environment,
                tempRoot,
                pathInspector,
                additionalTemporaryRoots);
        var policy = new ToolAccessPolicy(
            new NetclawPaths(),
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                audience,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy,
            tempPolicy,
            safeVerbs: SafeVerbList.FromVerbs(
                environment.Grammar == ShellGrammar.Bash
                    ? ApprovalShell.Bash
                    : ApprovalShell.PowerShell,
                ["head", "cat"]));
        var shellTool = new ShellTool(config, pathPolicy, commandPolicy);
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(sessionDirectory)));
        var context = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/example",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                ProjectDirectory = projectDirectory,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(interactive)
            });
        var arguments = explicitWorkingDirectory is null
            ? ToolInput.Create("Command", command)
            : ToolInput.Create(
                "Command", command,
                "WorkingDirectory", explicitWorkingDirectory);

        return policy.AuthorizeInvocation(shellTool, context, arguments);
    }

    private static ShellExecutionEnvironment BashEnvironment()
        => ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);

    private static ShellExecutionEnvironment PowerShellEnvironment()
        => ShellExecutionEnvironment.CreatePowerShell(
            "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);

    private sealed class TestPathInspector : IPlatformTemporaryPathInspector
    {
        private readonly bool _failDescendantInspection;
        private readonly string? _resolvedRoot;

        internal TestPathInspector(
            bool failDescendantInspection = false,
            string? resolvedRoot = null)
        {
            _failDescendantInspection = failDescendantInspection;
            _resolvedRoot = resolvedRoot;
        }

        public bool TryResolveRoot(
            string path,
            ShellPathStyle pathStyle,
            out string resolvedRoot)
        {
            var root = _resolvedRoot ?? path;
            return ShellPathRules.TryNormalize(root, pathStyle, out resolvedRoot);
        }

        public bool HasNoLinkEscape(string root, string path, ShellPathStyle pathStyle)
            => !_failDescendantInspection;

        public bool SupportsPathInspection(ShellPathStyle pathStyle)
            => true;
    }

    private sealed class MappedPathInspector(
        IReadOnlyDictionary<string, string> roots) : IPlatformTemporaryPathInspector
    {
        public bool TryResolveRoot(
            string path,
            ShellPathStyle pathStyle,
            out string resolvedRoot)
        {
            resolvedRoot = string.Empty;
            return roots.TryGetValue(path, out var mapped)
                   && ShellPathRules.TryNormalize(mapped, pathStyle, out resolvedRoot);
        }

        public bool HasNoLinkEscape(string root, string path, ShellPathStyle pathStyle)
            => ShellPathRules.IsWithinRoot(path, root, pathStyle);

        public bool SupportsPathInspection(ShellPathStyle pathStyle)
            => true;
    }
}
