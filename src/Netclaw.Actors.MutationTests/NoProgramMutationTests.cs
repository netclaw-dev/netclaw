// -----------------------------------------------------------------------
// <copyright file="NoProgramMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Owner decision (October 2026): a command that runs no program gets no
/// prompt. Only a redirect target that is one plain file qualifies, and an
/// input redirect of such a command must pass the <c>file_read</c> rules.
/// </summary>
public sealed class NoProgramMutationTests : IDisposable
{
    // Bash 5.2 at /bin/bash gives the fresh no-startup state of a production host.
    private static readonly ShellExecutionEnvironment Bash52 =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));

    private const string WorkingDirectory = "/work/project";

    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "netclaw-no-program-mutations", Guid.NewGuid().ToString("N")));
    private readonly SessionStoragePaths _storage;
    private readonly string _readRoot;
    private readonly string _outside;

    public NoProgramMutationTests()
    {
        _paths.EnsureDirectoriesExist();
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")));
        Directory.CreateDirectory(_storage.SessionDirectory.Value);
        // A macOS temporary root has links. The file rules need the physical path.
        FileSystemAuthority.TryResolveLinks(
            Directory.CreateDirectory(Path.Combine(_paths.BasePath, "readable")).FullName, out _readRoot);
        FileSystemAuthority.TryResolveLinks(
            Directory.CreateDirectory(Path.Combine(_paths.BasePath, "outside")).FullName, out _outside);
    }

    // SECURITY: Bash opens a network connection for /dev/tcp and /dev/udp. Only
    // a plain file and /dev/null can skip the prompt.
    [Theory]
    [InlineData(": > /work/project/x.json", true)]
    [InlineData("> /work/project/x.json", true)]
    [InlineData("printf a > /work/project/x.txt", true)]
    [InlineData(": > /dev/null", true)]
    [InlineData(": < /dev/null", true)]
    [InlineData("printf x > /dev/tcp/127.0.0.1/9", false)]
    [InlineData("> /dev/udp/127.0.0.1/9", false)]
    [InlineData(": > /dev/./tcp/127.0.0.1/9", false)]
    [InlineData(": > //dev/tcp/127.0.0.1/9", false)]
    [InlineData(": > /dev/tcp/127.0.0.1/9/..", false)]
    [InlineData(": > /work/project/*.json", false)]
    [InlineData("date > /work/project/x.txt", false)]
    public void Only_a_plain_file_target_runs_no_program(string command, bool runsNoProgram)
    {
        var analysis = new ShellCommandPolicy(Bash52).Analyze(command, WorkingDirectory);

        var occurrence = Assert.Single(analysis.Commands);
        Assert.Equal(runsNoProgram, analysis.RunsNoProgram(occurrence));
    }

    // A data command with a target that is not one plain file is exact: its
    // prompt shows the full command text.
    [Theory]
    [InlineData("printf x > /dev/tcp/127.0.0.1/9")]
    [InlineData(": > /work/project/*.json")]
    public void Data_command_with_another_target_is_exact(string command)
    {
        var analysis = new ShellCommandPolicy(Bash52).Analyze(command, WorkingDirectory);

        Assert.Equal(ShellUnresolvedPart.Command, analysis.GetUnresolvedPart(Assert.Single(analysis.Commands)));
    }

    [Fact]
    public void Input_redirect_outside_the_read_roots_is_denied()
    {
        if (OperatingSystem.IsWindows())
            return;

        var denied = Screen($": < {_outside}/notes.txt");

        Assert.Equal("shell_redirect_read_denied", denied?.DenyReason);
        Assert.Contains($"{_outside}/notes.txt", denied!.DenyMessage, StringComparison.Ordinal);
        Assert.Contains("file_read rules", denied.DenyMessage, StringComparison.Ordinal);
    }

    // Positive controls: a read inside the read roots, and a write target,
    // which the shell trust zone judges, not this read check.
    [Fact]
    public void Read_inside_the_roots_and_a_write_target_pass_the_read_check()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Null(Screen($": < {_readRoot}/notes.txt"));
        Assert.Null(Screen($": > {_outside}/out.txt"));
    }

    // A program never skips its prompt, so this check does not judge its redirects.
    [Fact]
    public void Program_redirect_is_not_judged_by_the_read_check()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Null(Screen($"tee copy.txt < {_outside}/notes.txt"));
    }

    // A target that the parser did not prove denies the call. It never prompts.
    [Fact]
    public void Unresolved_input_redirect_is_denied()
    {
        var candidate = new ApprovalCandidate(": < \"$f\"", Directory: null) { RunsNoProgram = true };
        var parsed = new ShellCommandPolicy(Bash52).Analyze(": < \"$f\"", WorkingDirectory);
        var target = Assert.IsType<FileRedirectAnalysis>(Assert.Single(Assert.Single(parsed.Commands).Redirects)).Target;
        Assert.IsType<ShellValueDomain.Unknown>(target);
        var unresolved = new ShellPolicyResolvedPathFact(
            new ShellPolicySourcePathFact(
                ShellPolicyPathOrigin.Redirect,
                target,
                ShellPathShape.Unknown) { RedirectMode = FileRedirectMode.Input },
            ShellPolicyPathResolutionState.UnknownDynamic,
            []);
        var scope = ShellPolicyPathFacts.ResolveScope(WorkingDirectory, ShellPathStyle.Posix);
        var facts = new ShellPolicyCandidatePathFacts(
            scope,
            new ShellPolicyResolvedPathView(scope, [unresolved]),
            Intent: null);

        var denied = CreatePolicy().ScreenNoProgramRedirects([(candidate, facts)], CreateContext());

        Assert.Equal("shell_redirect_unproved", denied?.DenyReason);
        Assert.Contains("cannot prove the redirect target", denied!.DenyMessage, StringComparison.Ordinal);
    }

    // SECURITY: the trust zone skips a fact that is not known, so this screen
    // must deny it. No parser input reaches this state today.
    [Fact]
    public void Unresolved_output_redirect_is_denied()
    {
        var candidate = new ApprovalCandidate(": > \"$f\"", Directory: null) { RunsNoProgram = true };
        var parsed = new ShellCommandPolicy(Bash52).Analyze(": > \"$f\"", WorkingDirectory);
        var target = Assert.IsType<FileRedirectAnalysis>(Assert.Single(Assert.Single(parsed.Commands).Redirects)).Target;
        var unresolved = new ShellPolicyResolvedPathFact(
            new ShellPolicySourcePathFact(
                ShellPolicyPathOrigin.Redirect,
                target,
                ShellPathShape.Unknown) { RedirectMode = FileRedirectMode.Output },
            ShellPolicyPathResolutionState.UnknownDynamic,
            []);
        var scope = ShellPolicyPathFacts.ResolveScope(WorkingDirectory, ShellPathStyle.Posix);
        var facts = new ShellPolicyCandidatePathFacts(
            scope,
            new ShellPolicyResolvedPathView(scope, [unresolved]),
            Intent: null);

        var denied = CreatePolicy().ScreenNoProgramRedirects([(candidate, facts)], CreateContext());

        Assert.Equal("shell_redirect_unproved", denied?.DenyReason);
        Assert.Contains("cannot prove the redirect target", denied!.DenyMessage, StringComparison.Ordinal);
    }

    // A Deny mode of the file tool denies the redirect, as it denies the tool.
    [Theory]
    [InlineData("file_write", ": > {R}/out.txt")]
    [InlineData("file_read", ": < {R}/notes.txt")]
    public void Denied_file_tool_denies_the_redirect(string tool, string command)
    {
        if (OperatingSystem.IsWindows())
            return;

        var denied = Screen(command.Replace("{R}", _readRoot, StringComparison.Ordinal), tool);

        Assert.Equal("shell_redirect_file_tool_denied", denied?.DenyReason);
        Assert.Contains(tool, denied!.DenyMessage, StringComparison.Ordinal);
    }

    // The other file tool does not decide: a denied file_read leaves a write alone.
    [Fact]
    public void Denied_file_read_does_not_deny_a_write_redirect()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Null(Screen($": > {_readRoot}/out.txt", "file_read"));
        Assert.Null(Screen($": > /dev/null", "file_write"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.BasePath))
            Directory.Delete(_paths.BasePath, recursive: true);
    }

    private ToolAuthorizationDecision? Screen(string command, string? deniedTool = null)
    {
        var policy = CreatePolicy(deniedTool);
        var analysis = policy.ShellCommandPolicy.Analyze(command, _readRoot);
        var approval = policy.ShellApprovalMatcher.AnalyzeInvocation(
            new ToolName(ShellTool.ToolName),
            new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = _readRoot },
            analysis);
        Assert.False(approval.IsMessy);
        var facts = ShellPolicyPathFacts.Create(approval.Candidates, ShellPathStyle.Posix);
        return policy.ScreenNoProgramRedirects(
            approval.Candidates.Select((candidate, index) => (candidate, facts[index])),
            CreateContext());
    }

    private ToolAccessPolicy CreatePolicy(string? deniedTool = null)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        if (deniedTool is not null)
        {
            config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new() { [deniedTool] = ToolApprovalMode.Deny }
            };
        }

        config.AudienceProfiles.GlobalReadRoots = [];
        config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = [_readRoot]
        };
        return new ToolAccessPolicy(
            _paths,
            config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(Bash52),
            new ToolPathPolicy(Bash52, []));
    }

    private ToolExecutionContext CreateContext() => new(
        new ToolRunScope
        {
            Session = new ToolSessionScope.Bound("signalr/no-program-mutation", _storage),
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
            InlineOutputBudget = InlineOutputBudget.Default,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
        },
        ToolExecutionTimeout.Default);
}
