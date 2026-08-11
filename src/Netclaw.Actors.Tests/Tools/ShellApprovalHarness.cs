// -----------------------------------------------------------------------
// <copyright file="ShellApprovalHarness.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

internal sealed record ObservedApproval(
    ToolAuthorizationOutcome Outcome,
    ToolAllowReason? AllowReason,
    string? DenyReason,
    IReadOnlyList<string> CandidateVerbs,
    bool? IsMessy,
    IReadOnlyList<string> ApprovalMatches);

internal sealed class ShellApprovalHarness : IAsyncDisposable
{
    private const string InvocationSessionId = "signalr/approval-matrix";
    private const string OtherSessionId = "signalr/other-session";

    private readonly string _rootDirectory;
    private readonly string _projectDirectory;
    private readonly string _externalDirectory;
    private readonly IActorRef _approvalActor;
    private readonly FunctionCallContent _toolCall;
    private readonly ToolExecutionContext _context;
    private readonly DispatchingToolExecutor _executor;

    private ShellApprovalHarness(
        string rootDirectory,
        string projectDirectory,
        string externalDirectory,
        IActorRef approvalActor,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        DispatchingToolExecutor executor,
        CountingApprovalService approvalService)
    {
        _rootDirectory = rootDirectory;
        _projectDirectory = projectDirectory;
        _externalDirectory = externalDirectory;
        _approvalActor = approvalActor;
        _toolCall = toolCall;
        _context = context;
        _executor = executor;
        ApprovalService = approvalService;
    }

    public CountingApprovalService ApprovalService { get; }

    public static async Task<ShellApprovalHarness> CreateAsync(
        ShellApprovalCase testCase,
        ActorSystem actorSystem,
        CancellationToken ct)
    {
        var rootDirectory = Path.Combine(
            CanonicalTemporaryDirectory(),
            "netclaw-approval-matrix",
            Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(rootDirectory, "project");
        var sessionDirectory = Path.Combine(rootDirectory, "session");
        var externalDirectory = Path.Combine(rootDirectory, "external");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(sessionDirectory);
        Directory.CreateDirectory(externalDirectory);

        var environment = testCase.Invocation.CreateEnvironment();
        var approvalProjectDirectory = projectDirectory;
        var approvalSessionDirectory = sessionDirectory;
        var approvalExternalDirectory = externalDirectory;
        if (environment.PathStyle == ShellPathStyle.Windows)
        {
            var windowsRoot = $"C:/netclaw-approval-matrix/{Guid.NewGuid():N}";
            approvalProjectDirectory = $"{windowsRoot}/project";
            approvalSessionDirectory = $"{windowsRoot}/session";
            approvalExternalDirectory = $"{windowsRoot}/external";
        }

        var store = new ToolApprovalStore(Path.Combine(rootDirectory, "tool-approvals.json"));
        var approvalActor = CreateApprovalActor(actorSystem, store);
        var approvalService = CreateApprovalService(approvalActor);

        var persistentSeeds = testCase.Approvals.Seeds
            .Where(seed => seed.Source == ApprovalSeedSource.Persistent)
            .ToList();
        foreach (var seed in persistentSeeds)
        {
            await approvalService.RecordApprovalAsync(
                (ToolApprovalSessionId)"seed/persistent",
                seed.Audience,
                new ToolName(ShellTool.ToolName),
                [seed.Pattern],
                persistent: true,
                ResolveDirectory(
                    seed.Directory,
                    approvalProjectDirectory,
                    approvalSessionDirectory,
                    approvalExternalDirectory),
                ct);
        }

        if (persistentSeeds.Count > 0)
        {
            await approvalActor.GracefulStop(TimeSpan.FromSeconds(5));
            approvalActor = CreateApprovalActor(actorSystem, store);
            approvalService = CreateApprovalService(approvalActor);
        }

        foreach (var seed in testCase.Approvals.Seeds.Where(seed => seed.Source == ApprovalSeedSource.Session))
        {
            await approvalService.RecordApprovalAsync(
                (ToolApprovalSessionId)ResolveSession(seed.Session),
                seed.Audience,
                new ToolName(ShellTool.ToolName),
                [seed.Pattern],
                persistent: false,
                cwd: null,
                ct);
        }

        var countingApprovalService = new CountingApprovalService(approvalService);
        var config = CreateConfig();
        var commandPolicy = new ShellCommandPolicy(environment);
        var deniedPaths = environment.Platform == ShellPlatform.Windows
            ? new[] { @"C:\protected\config" }
            : [];
        var pathPolicy = new ToolPathPolicy(environment, deniedPaths);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            pathPolicy,
            commandPolicy);

        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            shellCommandPolicy: commandPolicy,
            toolPathPolicy: pathPolicy,
            shellTrustZonePolicy: new ShellTrustZonePolicy(
                config,
                new NetclawPaths(rootDirectory, Path.Combine(rootDirectory, "workspaces"))),
            safeVerbs: SafeVerbLoader.Load(environment.Platform == ShellPlatform.Windows));
        var executor = new DispatchingToolExecutor(registry, policy, countingApprovalService);

        var workingDirectory = ResolveDirectory(
            testCase.Invocation.WorkingDirectory,
            approvalProjectDirectory,
            approvalSessionDirectory,
            approvalExternalDirectory);
        var arguments = workingDirectory is null
            ? ToolInput.Create("Command", testCase.Invocation.Command)
            : ToolInput.Create(
                "Command", testCase.Invocation.Command,
                "WorkingDirectory", workingDirectory);
        var toolCall = new FunctionCallContent(testCase.Id, ShellTool.ToolName, arguments);
        var context = TestToolExecutionContext.CreateBound(
            InvocationSessionId,
            approvalSessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = testCase.Invocation.Audience,
                ProjectDirectory = approvalProjectDirectory,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(testCase.Invocation.Interactive)
            });

        return new ShellApprovalHarness(
            rootDirectory,
            projectDirectory,
            externalDirectory,
            approvalActor,
            toolCall,
            context,
            executor,
            countingApprovalService);
    }

    public async Task<ObservedApproval> EvaluateAsync(CancellationToken ct)
    {
        var decision = await _executor.EvaluateAuthorizationAsync(_toolCall, _context, ct);
        var approvalContext = decision.ApprovalContext;

        return new ObservedApproval(
            decision.Outcome,
            decision.AllowReason,
            decision.DenyReason,
            approvalContext?.CandidateVerbs ?? [],
            approvalContext?.IsMessy,
            decision.ApprovalMatches
                .Select(match => $"{match.Source}:{match.Pattern}")
                .ToList());
    }

    public Task<ToolAuthorizationDecision> EvaluateDecisionAsync(CancellationToken ct)
        => _executor.EvaluateAuthorizationAsync(_toolCall, _context, ct);

    public void SeedOneTimeApproval(ToolApprovalContext approvalContext)
        => _context.Approval.SeedOneTimeApproval(
            _toolCall.Name,
            OneTimeApprovalKeys.Create(approvalContext));

    public void ReplaceProjectDirectoryWithExternalSymlink(string relativeDirectory)
    {
        var path = Path.Combine(_projectDirectory, relativeDirectory);
        Directory.CreateDirectory(path);
        Directory.Delete(path);
        Directory.CreateSymbolicLink(path, _externalDirectory);
    }

    public void CreateProjectDirectory(string relativeDirectory)
        => Directory.CreateDirectory(Path.Combine(_projectDirectory, relativeDirectory));

    public void CreateProjectFileSymlinkToExternalFile(string relativePath)
    {
        var externalFile = Path.Combine(_externalDirectory, "secret.txt");
        File.WriteAllText(externalFile, "synthetic test data");
        File.CreateSymbolicLink(Path.Combine(_projectDirectory, relativePath), externalFile);
    }

    public async ValueTask DisposeAsync()
    {
        await _approvalActor.GracefulStop(TimeSpan.FromSeconds(5));
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);
    }

    private static ToolConfig CreateConfig()
        => new()
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(DeploymentPosture.Personal)
        };

    private static string CanonicalTemporaryDirectory()
    {
        var fullPath = Path.GetFullPath(Path.GetTempPath());
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The temporary directory has no path root.");
        var current = pathRoot;
        var relative = fullPath[pathRoot.Length..];
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            current = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? candidate;
        }

        return current;
    }

    private static IActorRef CreateApprovalActor(ActorSystem actorSystem, ToolApprovalStore store)
        => actorSystem.ActorOf(
            ToolApprovalActor.CreateProps(store),
            $"approval-matrix-{Guid.NewGuid():N}");

    private static AkkaToolApprovalService CreateApprovalService(IActorRef actor)
        => new(new StubRequiredActor(actor));

    private static string ResolveSession(ApprovalSessionShape session)
        => session switch
        {
            ApprovalSessionShape.Invocation => InvocationSessionId,
            ApprovalSessionShape.Other => OtherSessionId,
            _ => throw new ArgumentOutOfRangeException(nameof(session), session, "Unknown approval session shape.")
        };

    private static string? ResolveDirectory(
        ApprovalDirectoryShape directory,
        string projectDirectory,
        string sessionDirectory,
        string externalDirectory)
        => directory switch
        {
            ApprovalDirectoryShape.None => null,
            ApprovalDirectoryShape.Project => projectDirectory,
            ApprovalDirectoryShape.Session => sessionDirectory,
            ApprovalDirectoryShape.External => externalDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(directory), directory, "Unknown approval directory shape.")
        };

    private sealed class StubRequiredActor(IActorRef actor) : IRequiredActor<ToolApprovalActorKey>
    {
        public IActorRef ActorRef => actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(actor);
    }
}

internal sealed class CountingApprovalService(IToolApprovalService inner) : IToolApprovalService
{
    private int _checkCount;

    public int CheckCount => Volatile.Read(ref _checkCount);

    public async Task<ToolApprovalCheckResult> CheckApprovalAsync(
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _checkCount);
        return await inner.CheckApprovalAsync(sessionId, audience, toolName, candidates, cwd, ct);
    }

    public Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        string? cwd,
        CancellationToken ct = default)
        => inner.GetUnapprovedPatternsAsync(sessionId, audience, toolName, patterns, cwd, ct);

    public Task RecordApprovalAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        string? cwd,
        CancellationToken ct = default)
        => inner.RecordApprovalAsync(sessionId, audience, toolName, patterns, persistent, cwd, ct);
}

public sealed class ShellApprovalMatrixFixture : IAsyncLifetime
{
    public ActorSystem ActorSystem { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        ActorSystem = ActorSystem.Create($"shell-approval-matrix-{Guid.NewGuid():N}");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await ActorSystem.Terminate();
    }
}

[CollectionDefinition(Name)]
public sealed class ShellApprovalMatrixCollection : ICollectionFixture<ShellApprovalMatrixFixture>
{
    public const string Name = "Shell approval matrix";
}
