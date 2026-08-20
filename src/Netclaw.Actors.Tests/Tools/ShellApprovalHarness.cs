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

internal sealed record ShellApprovalHarnessScope(
    string ProjectDirectory,
    string SessionDirectory,
    string InvocationSessionId,
    IReadOnlyList<string> OneTimeApprovalKeys);

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

    public static Task<ShellApprovalHarness> CreateAsync(
        ShellApprovalCase testCase,
        ActorSystem actorSystem,
        CancellationToken ct)
        => CreateAsync(
            testCase.Id,
            testCase.Invocation,
            testCase.Approvals,
            actorSystem,
            ct);

    internal static async Task<ShellApprovalHarness> CreateAsync(
        string caseId,
        ShellApprovalInvocation invocation,
        ApprovalState approvals,
        ActorSystem actorSystem,
        CancellationToken ct,
        TimeProvider? timeProvider = null,
        ShellApprovalHarnessScope? scope = null,
        SafeVerbList? safeVerbs = null,
        IReadOnlyList<string>? deniedPaths = null)
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

        var environment = invocation.CreateEnvironment();
        var approvalProjectDirectory = scope?.ProjectDirectory ?? projectDirectory;
        var approvalSessionDirectory = scope?.SessionDirectory ?? sessionDirectory;
        var approvalExternalDirectory = externalDirectory;
        if (environment.PathStyle == ShellPathStyle.Windows && scope is null)
        {
            var windowsRoot = $"C:/netclaw-approval-matrix/{Guid.NewGuid():N}";
            approvalProjectDirectory = $"{windowsRoot}/project";
            approvalSessionDirectory = $"{windowsRoot}/session";
            approvalExternalDirectory = $"{windowsRoot}/external";
        }

        var approvalShell = environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        var store = new ToolApprovalStore(
            Path.Combine(rootDirectory, "tool-approvals.json"),
            timeProvider,
            migrationContext: new ApprovalStoreMigrationContext(approvalShell),
            lockTimeout: TimeSpan.Zero);
        var approvalActor = CreateApprovalActor(actorSystem, store);
        var approvalService = CreateApprovalService(approvalActor);

        var persistentSeeds = approvals.Seeds
            .Where(seed => seed.Source == ApprovalSeedSource.Persistent)
            .ToList();
        // Seed all persistent grants in ONE Ask per audience instead of one Ask
        // per grant. The actor's RecordStructuredToolApproval handler persists a
        // whole grant list in a single locked, atomic write (ToolApprovalStore.AddApprovals
        // -> one SaveLocked), so the resulting store state is equivalent to N sequential seed
        // messages (the only divergence is per-entry CreatedAt stamping under a real clock,
        // which is unobservable here: fixture tests run on a frozen FakeTimeProvider and no
        // harness test asserts seed timestamps). It removes N-1 synchronous WriteThrough +
        // Flush(flushToDisk: true) file
        // rewrites from the test's critical path: the Ask deadline is a hard 5s wall clock,
        // and under full-suite parallel load on Windows CI (Defender scanning each new
        // tool-approvals.json in a fresh %TEMP% tree) per-write latency of the heaviest case
        // (D10, 5 seeds) occasionally exceeded it. Grouping by audience preserves the
        // per-seed audience semantics for every harness caller.
        foreach (var audienceGroup in persistentSeeds.GroupBy(seed => seed.Audience))
        {
            await approvalService.RecordApprovalCandidatesAsync(
                (ToolApprovalSessionId)"seed/persistent",
                audienceGroup.Key,
                new ToolName(ShellTool.ToolName),
                audienceGroup
                    .Select(seed => CreateGrant(seed.Pattern, approvalShell, ResolveDirectory(
                        seed.Directory,
                        approvalProjectDirectory,
                        approvalSessionDirectory,
                        approvalExternalDirectory)))
                    .ToList(),
                persistent: true,
                ct);
        }

        if (persistentSeeds.Count > 0)
        {
            // The stop waits for a persistence flush and the actor teardown.
            // The budget bounds a multi-hop shutdown under a starved CI
            // scheduler. It does not measure correctness. Every shell-approval
            // test goes through this shared harness, so a short budget makes a
            // whole suite flake at once.
            await approvalActor.GracefulStop(TimeSpan.FromSeconds(15));
            approvalActor = CreateApprovalActor(actorSystem, store);
            approvalService = CreateApprovalService(approvalActor);
        }

        foreach (var seed in approvals.Seeds.Where(seed => seed.Source == ApprovalSeedSource.Session))
        {
            await approvalService.RecordApprovalCandidatesAsync(
                (ToolApprovalSessionId)ResolveSession(
                    seed.Session,
                    scope?.InvocationSessionId ?? InvocationSessionId),
                seed.Audience,
                new ToolName(ShellTool.ToolName),
                [CreateGrant(seed.Pattern, approvalShell, directory: null)],
                persistent: false,
                ct);
        }

        var countingApprovalService = new CountingApprovalService(approvalService);
        var config = CreateConfig();
        var commandPolicy = new ShellCommandPolicy(environment);
        var effectiveDeniedPaths = deniedPaths ?? (environment.Platform == ShellPlatform.Windows
            ? [@"C:\protected\config"]
            : []);
        var pathPolicy = new ToolPathPolicy(environment, effectiveDeniedPaths);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            pathPolicy,
            commandPolicy,
            toolAccessPolicy: TestToolAccessPolicy.Create(config, commandPolicy, pathPolicy));

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
            safeVerbs: safeVerbs ?? SafeVerbLoader.Load(environment.Platform == ShellPlatform.Windows));
        var executor = new DispatchingToolExecutor(registry, policy, countingApprovalService);

        var workingDirectory = ResolveDirectory(
            invocation.WorkingDirectory,
            approvalProjectDirectory,
            approvalSessionDirectory,
            approvalExternalDirectory);
        var arguments = workingDirectory is null
            ? ToolInput.Create("Command", invocation.Command)
            : ToolInput.Create(
                "Command", invocation.Command,
                "WorkingDirectory", workingDirectory);
        var toolCall = new FunctionCallContent(caseId, ShellTool.ToolName, arguments);
        var context = TestToolExecutionContext.CreateBound(
            scope?.InvocationSessionId ?? InvocationSessionId,
            approvalSessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = invocation.Audience,
                ProjectDirectory = approvalProjectDirectory,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(invocation.Interactive)
            });
        if (scope?.OneTimeApprovalKeys is { Count: > 0 } oneTimeApprovalKeys)
        {
            context.Approval.SeedOneTimeApproval(
                ShellTool.ToolName,
                oneTimeApprovalKeys);
        }

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

    private static ToolApprovalGrant CreateGrant(
        string pattern,
        ApprovalShell shell,
        string? directory)
    {
        var tokens = Array.AsReadOnly(
            pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return new ToolApprovalGrant(
            new ApprovalCandidate(pattern, Directory: null)
            {
                Shell = shell,
                VerbTokens = tokens,
            },
            directory);
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

    public void CreateProjectFileSymlink(string linkPath, string targetPath)
    {
        File.WriteAllText(Path.Combine(_projectDirectory, targetPath), "synthetic test data");
        File.CreateSymbolicLink(
            Path.Combine(_projectDirectory, linkPath),
            Path.Combine(_projectDirectory, targetPath));
    }

    public async ValueTask DisposeAsync()
    {
        // Same reason as the seed-phase stop above: the budget bounds a
        // multi-hop teardown under a starved CI scheduler, not correctness.
        await _approvalActor.GracefulStop(TimeSpan.FromSeconds(15));
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
        => new(new StubRequiredActor(actor), TestShellEnvironment.Current);

    private static string ResolveSession(
        ApprovalSessionShape session,
        string invocationSessionId)
        => session switch
        {
            ApprovalSessionShape.Invocation => invocationSessionId,
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

internal sealed class CountingApprovalService(IToolApprovalService inner) :
    IToolApprovalService,
    IStructuredToolApprovalService,
    IShellApprovalMatchService
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

    public async Task<ShellApprovalMatchResult> MatchShellCandidatesAsync(
        ShellApprovalMatchRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _checkCount);
        return await ((IShellApprovalMatchService)inner).MatchShellCandidatesAsync(
            request,
            cancellationToken);
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

    public Task RecordApprovalCandidatesAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ToolApprovalGrant> grants,
        bool persistent,
        CancellationToken ct = default)
        => ((IStructuredToolApprovalService)inner).RecordApprovalCandidatesAsync(
            sessionId,
            audience,
            toolName,
            grants,
            persistent,
            ct);
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
