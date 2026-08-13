// -----------------------------------------------------------------------
// <copyright file="PlatformTemporaryScopePolicyTests.cs" company="Petabridge, LLC">
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

public sealed class PlatformTemporaryScopePolicyTests
{
    private const string PosixTemp = "/tmp";
    private const string PosixSession = "/home/user/.netclaw/sessions/example";
    private const string WindowsTemp = "C:\\Users\\user\\AppData\\Local\\Temp";
    private const string WindowsSession = "C:\\Users\\user\\.netclaw\\sessions\\example";

    [Fact]
    public void Explicit_posix_temp_cwd_returns_private_scratch_correction()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            PosixSession,
            explicitWorkingDirectory: PosixTemp);

        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        var correction = Assert.IsType<ToolAgentCorrection.SessionScratchSuggested>(context.AgentCorrection);
        Assert.Equal(PosixSession, correction.SessionDirectory);
        Assert.Equal(PosixTemp, correction.TemporaryRoot);
        Assert.Null(context.SuggestedProjectDirectory);
    }

    [Fact]
    public void Platform_temp_alias_maps_to_canonical_target()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "cat /tmp/result.log",
            PosixSession,
            explicitWorkingDirectory: PosixTemp,
            inspector: new TestPathInspector(resolvedRoot: "/private/tmp"));

        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        var correction = Assert.IsType<ToolAgentCorrection.SessionScratchSuggested>(context.AgentCorrection);
        Assert.Equal("/private/tmp", correction.TemporaryRoot);
    }

    [Fact]
    public void Static_bash_temp_transition_returns_private_scratch_correction()
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "cd /tmp && gh api repos/example/project > /tmp/result.log",
            PosixSession);

        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.IsType<ToolAgentCorrection.SessionScratchSuggested>(context.AgentCorrection);
        Assert.Null(context.SuggestedProjectDirectory);
    }

    [Fact]
    public void Native_windows_explicit_temp_cwd_returns_private_scratch_correction()
    {
        var decision = Evaluate(
            PowerShellEnvironment(),
            WindowsTemp,
            "Get-Content result.log",
            WindowsSession,
            explicitWorkingDirectory: WindowsTemp);

        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        var correction = Assert.IsType<ToolAgentCorrection.SessionScratchSuggested>(context.AgentCorrection);
        Assert.Equal(ApprovalShell.PowerShell, correction.Shell);
        Assert.Equal(WindowsSession, correction.SessionDirectory);
    }

    [Fact]
    public void Native_windows_temp_comparison_uses_windows_case_rules()
    {
        var decision = Evaluate(
            PowerShellEnvironment(),
            WindowsTemp,
            "Get-Content result.log",
            WindowsSession,
            explicitWorkingDirectory: WindowsTemp.ToUpperInvariant());

        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.IsType<ToolAgentCorrection.SessionScratchSuggested>(context.AgentCorrection);
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

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
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
        Assert.Null(decision.ApprovalContext?.AgentCorrection);
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
        Assert.Null(decision.ApprovalContext?.AgentCorrection);
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

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
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

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
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

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/session")]
    public void Invalid_session_scope_suppresses_correction(string sessionDirectory)
    {
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            sessionDirectory,
            explicitWorkingDirectory: PosixTemp);

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
    }

    [Fact]
    public void Fresh_session_scope_does_not_need_to_exist()
    {
        var sessionDirectory = $"/home/user/.netclaw/sessions/{Guid.NewGuid():N}";
        var decision = Evaluate(
            BashEnvironment(),
            PosixTemp,
            "gh api repos/example/project",
            sessionDirectory,
            explicitWorkingDirectory: PosixTemp);

        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.IsType<ToolAgentCorrection.SessionScratchSuggested>(context.AgentCorrection);
    }

    [Fact]
    public void PowerShell_causal_location_remains_ineligible()
    {
        var decision = Evaluate(
            PowerShellEnvironment(),
            WindowsTemp,
            "Set-Location $env:TEMP; Get-Content result.log",
            WindowsSession);

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
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

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
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

        Assert.Null(decision.ApprovalContext?.AgentCorrection);
    }

    [Fact]
    public void Host_inspector_rejects_posix_symlink_descendant()
    {
        if (OperatingSystem.IsWindows())
            return;

        var testRoot = Path.Combine(Path.GetTempPath(), $"netclaw-scratch-policy-{Guid.NewGuid():N}");
        var outside = Path.Combine(testRoot, "outside");
        var link = Path.Combine(testRoot, "link");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            Assert.False(HostPlatformTemporaryPathInspector.Instance.IsSafeDescendant(
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

    [Fact]
    public void Host_inspector_resolves_symlink_in_temporary_root_parent()
    {
        if (OperatingSystem.IsWindows())
            return;

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

    [Fact]
    public void Canonical_platform_temp_alias_can_recommend_scratch_for_redirect()
    {
        if (OperatingSystem.IsWindows())
            return;

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
            Assert.Null(externalFirst.ApprovalContext?.AgentCorrection);

            var decision = Evaluate(
                BashEnvironment(),
                aliasTemp,
                $"cd '{aliasTemp}' && gh api repos/example/project > '{output}'",
                PosixSession,
                inspector: HostPlatformTemporaryPathInspector.Instance);

            var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
            var correction = Assert.IsType<ToolAgentCorrection.SessionScratchSuggested>(context.AgentCorrection);
            Assert.Equal(realTemp, correction.TemporaryRoot);
        }
        finally
        {
            Directory.Delete(aliasTemp);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static ToolAccessDecision Evaluate(
        ShellExecutionEnvironment environment,
        string tempRoot,
        string command,
        string sessionDirectory,
        string? explicitWorkingDirectory = null,
        string? projectDirectory = null,
        bool interactive = true,
        TrustAudience audience = TrustAudience.Personal,
        IPlatformTemporaryPathInspector? inspector = null,
        IReadOnlyList<string>? deniedPaths = null)
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
        var tempPolicy = new PlatformTemporaryScopePolicy(
            environment,
            tempRoot,
            inspector ?? new TestPathInspector());
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                audience,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy,
            tempPolicy,
            safeVerbs: SafeVerbList.FromVerbs(["head", "cat"]));
        var shellTool = new ShellTool(config, pathPolicy, commandPolicy);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/example",
            sessionDirectory,
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

        public bool IsSafeDescendant(string root, string path, ShellPathStyle pathStyle)
            => !_failDescendantInspection;

        public bool ContainsInvalidPathState(string path, ShellPathStyle pathStyle)
            => false;
    }
}
