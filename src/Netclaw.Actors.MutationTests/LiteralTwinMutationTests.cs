// -----------------------------------------------------------------------
// <copyright file="LiteralTwinMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Owner decision F1: the strictest twin result decides a call. One denied twin
/// denies it, and the candidates of every twin replace the candidates of their
/// source command, so each twin needs its own coverage.
/// </summary>
public sealed class LiteralTwinMutationTests : IDisposable
{
    private const string OwnerLoop = "for n in 8250 8244; do gh api repos/o/r/issues/$n; done; git status";

    // Bash 5.2 at /bin/bash gives the fresh no-startup state that twins need.
    private static readonly ShellExecutionEnvironment Bash52 =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));

    // The Bash environment reads POSIX paths on every host. A Windows temporary
    // path is not a POSIX path, so the parser gives it no twins.
    private const string WorkingDirectory = "/work/project";

    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "netclaw-literal-twin-mutations", Guid.NewGuid().ToString("N")));
    private readonly SessionStoragePaths _storage;

    public LiteralTwinMutationTests()
    {
        _paths.EnsureDirectoriesExist();
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")));
        Directory.CreateDirectory(_storage.SessionDirectory.Value);
    }

    [Fact]
    public void Twin_candidates_replace_only_their_source_command()
    {
        var policy = new ShellCommandPolicy(Bash52);
        var matcher = new ShellApprovalMatcher(Bash52);
        var analysis = policy.Analyze(OwnerLoop, WorkingDirectory);
        Assert.True(BashLiteralTwinSlices.TryCreate(analysis, policy, matcher, out var twins));

        var applied = twins.Apply(CommandCandidates(matcher, analysis));

        var twinCommands = twins.Slices
            .Select(static slice => slice.Analysis.Commands[0])
            .ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.Equal(2, twinCommands.Count);
        Assert.Equal(2, applied.Candidates.Count(candidate => twinCommands.Contains(candidate.SourceOccurrence!)));
        Assert.DoesNotContain(applied.Candidates, candidate => ReferenceEquals(candidate.SourceOccurrence, analysis.Commands[0]));
        Assert.Contains(applied.Candidates, candidate => ReferenceEquals(candidate.SourceOccurrence, analysis.Commands[1]));
        Assert.Equal(3, applied.Candidates.Count);
    }

    // An unresolved source has no candidates to replace. It keeps its one exact answer.
    [Fact]
    public void Unresolved_approval_keeps_its_exact_answer()
    {
        var policy = new ShellCommandPolicy(Bash52);
        var matcher = new ShellApprovalMatcher(Bash52);
        var analysis = policy.Analyze(OwnerLoop, WorkingDirectory);
        Assert.True(BashLiteralTwinSlices.TryCreate(analysis, policy, matcher, out var twins));
        var unresolved = matcher.AnalyzeInvocation(ShellToolName, Arguments(OwnerLoop), analysis);
        Assert.True(unresolved.IsMessy);

        Assert.Same(unresolved, twins.Apply(unresolved));
    }

    // SECURITY: twins that cannot find their source command would get no check.
    [Fact]
    public void Approval_of_another_analysis_fails_loudly()
    {
        var policy = new ShellCommandPolicy(Bash52);
        var matcher = new ShellApprovalMatcher(Bash52);
        var analysis = policy.Analyze(OwnerLoop, WorkingDirectory);
        Assert.True(BashLiteralTwinSlices.TryCreate(analysis, policy, matcher, out var twins));
        var foreign = CommandCandidates(matcher, policy.Analyze(OwnerLoop, WorkingDirectory));

        var failure = Assert.Throws<InvalidOperationException>(() => twins.Apply(foreign));
        Assert.Equal("A command with literal twins has no candidate in the approval.", failure.Message);
    }

    // The hard denial of the first twin decides before any host path check of
    // a later twin, so the result is the same on every host. A screen that
    // returned the result of an allowed twin would allow the call.
    [Fact]
    public void One_denied_twin_denies_the_call()
    {
        var access = CreatePolicy(new ShellCommandPolicy(Bash52, ["git fetch"]));

        var denied = Screen(access, "for v in fetch status; do git $v; done", CreateContext());

        Assert.Equal("hard_deny_custom_deny", denied?.DenyReason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.BasePath))
            Directory.Delete(_paths.BasePath, recursive: true);
    }

    private static readonly ToolName ShellToolName = new(ShellTool.ToolName);

    private ToolAuthorizationDecision? Screen(ToolAccessPolicy access, string command, ToolExecutionContext context)
    {
        var analysis = access.ShellCommandPolicy.Analyze(command, WorkingDirectory);
        Assert.True(access.TryProjectLiteralTwins(analysis, out var twins));
        Assert.Equal(2, twins.Slices.Count());
        return access.ScreenLiteralTwins(twins, context);
    }

    private ShellApprovalAnalysis CommandCandidates(ShellApprovalMatcher matcher, ShellCommandAnalysis analysis)
        => ToolAccessPolicy.WithCommandCandidates(
            matcher.AnalyzeInvocation(ShellToolName, Arguments(analysis.Source), analysis));

    private Dictionary<string, object?> Arguments(string command) => new()
    {
        ["Command"] = command,
        ["WorkingDirectory"] = WorkingDirectory
    };

    private ToolAccessPolicy CreatePolicy(ShellCommandPolicy commandPolicy)
        => new(
            _paths,
            new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            commandPolicy,
            new ToolPathPolicy(Bash52, []));

    private ToolExecutionContext CreateContext() => new(
        new ToolRunScope
        {
            Session = new ToolSessionScope.Bound("signalr/literal-twin-mutation", _storage),
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
            InlineOutputBudget = InlineOutputBudget.Default,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
        },
        ToolExecutionTimeout.Default);
}
