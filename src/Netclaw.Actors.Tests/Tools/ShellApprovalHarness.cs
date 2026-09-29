// -----------------------------------------------------------------------
// <copyright file="ShellApprovalHarness.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

// The approval contract tests observe authorization through the test-owned
// types in this file. This file is the only test file that names production
// decision types. It maps each decision to an observation, so a later change
// to the production types changes this file and not the contract tests.

internal enum ApprovalOutcome
{
    Allowed,
    RequiresApproval,
    RequiresAgentCorrection,
    Denied
}

internal enum ApprovalAllowReason
{
    PolicyAuto,
    BackgroundJobLifecycle,
    ReviewedSafePolicy,
    ApprovalExemptShellCandidates,
    StoredApproval,
    OneTimeApproval
}

internal enum ApprovalCorrection
{
    ManagedTemporaryDirectory,
    NativeTool,
    ProjectDirectory,
    ShellWorkingDirectory
}

/// <summary>
/// Names the approval option keys that a prompt can offer. The values are the
/// keys that the approval protocol sends to a channel.
/// </summary>
internal static class ObservedOptionKeys
{
    public const string ApproveOnce = ApprovalOptionKeys.ApproveOnce;
    public const string ApproveSession = ApprovalOptionKeys.ApproveSession;
    public const string ApproveAlways = ApprovalOptionKeys.ApproveAlways;
    public const string ApproveRepository = ApprovalOptionKeys.ApproveRepository;
    public const string ApproveEverywhere = ApprovalOptionKeys.ApproveEverywhere;
    public const string ApproveAssignmentSessionV1 = ApprovalOptionKeys.ApproveAssignmentSessionV1;
    public const string ApproveAssignmentAlwaysV1 = ApprovalOptionKeys.ApproveAssignmentAlwaysV1;
    public const string Deny = ApprovalOptionKeys.Deny;
}

internal sealed record ApprovalPromptObservation(
    IReadOnlyList<string> CandidateVerbs,
    bool IsMessy,
    IReadOnlyList<string> OptionKeys)
{
    /// <summary>The command text that a channel shows to the operator.</summary>
    public string DisplayText { get; init; } = string.Empty;

    /// <summary>The button labels, in the same order as <see cref="OptionKeys"/>.</summary>
    public IReadOnlyList<string> OptionLabels { get; init; } = [];

    /// <summary>The directory of each approval candidate, or null when a candidate uses the prompt directory.</summary>
    public IReadOnlyList<string?>? CandidateDirectories { get; init; }

    /// <summary>The resolved working directory of the prompt.</summary>
    public string? Cwd { get; init; }

    /// <summary>The keys that a "Once" answer stores for the retry of this prompt.</summary>
    public IReadOnlyList<string> OneTimeApprovalKeys { get; init; } = [];
}

internal sealed record ApprovalObservation(
    ApprovalOutcome Outcome,
    ApprovalAllowReason? AllowReason,
    string? DenyReason,
    ApprovalCorrection? AgentCorrection,
    ApprovalPromptObservation? Prompt,
    int ApprovalChecks,
    IReadOnlyList<string> ApprovalMatches,
    IReadOnlyList<string> TraceRows,
    IReadOnlyList<(int CandidateId, string Coverage)> CandidateCoverage)
{
    /// <summary>True when the decision asks the operator for consent.</summary>
    public bool NeedsApproval { get; init; }

    /// <summary>The agent-facing text of a denial, when the policy supplies one.</summary>
    public string? DenyMessage { get; init; }

    /// <summary>The directory or tool name that a correction suggests.</summary>
    public string? AgentCorrectionTarget { get; init; }

    /// <summary>The platform temporary root that a managed temporary correction replaces.</summary>
    public string? PlatformTemporaryRoot { get; init; }
}

/// <summary>
/// Describes one tool call that the executor ran or refused.
/// </summary>
internal sealed record ToolRunObservation(
    ApprovalOutcome Outcome,
    string? DenyReason,
    string? AgentResult,
    string? Output);

/// <summary>
/// Signals that the executor refused a call because the agent must correct it.
/// </summary>
internal sealed class ShellApprovalCorrectionRequiredException(Exception inner)
    : Exception("The executor requires an agent correction.", inner);

/// <summary>
/// Supplies operator policy inputs that the production registration reads.
/// </summary>
internal sealed record ShellApprovalHarnessPolicy
{
    /// <summary>The content of the operator hard-deny override file, or null for no file.</summary>
    public string? HardDenyOverridesJson { get; init; }

    /// <summary>The <c>Tools.HardDenyPatterns</c> configuration value.</summary>
    public IReadOnlyList<string> HardDenyPatterns { get; init; } = [];

    /// <summary>The Personal audience approval overrides, keyed by tool or tool category.</summary>
    public IReadOnlyDictionary<string, ToolApprovalMode> PersonalApprovalOverrides { get; init; }
        = new Dictionary<string, ToolApprovalMode>();

    /// <summary>Changes other <c>Tools</c> configuration values before the registration reads them.</summary>
    public Action<ToolConfig>? ConfigureTools { get; init; }

    /// <summary>A custom workspaces directory, as <c>NetclawPaths</c> accepts from the operator.</summary>
    public string? WorkspacesDirectory { get; init; }

    /// <summary>True when the tool call runs without a bound session and without a project.</summary>
    public bool Sessionless { get; init; }
}

internal sealed record ShellApprovalHarnessScope(
    string ProjectDirectory,
    string SessionDirectory,
    string InvocationSessionId,
    IReadOnlyList<string> OneTimeApprovalKeys)
{
    internal string? RepositoryGrantWorktree { get; init; }
}

/// <summary>
/// Builds the tool authorization system through the daemon's production
/// registration and observes one tool call at a time.
/// </summary>
internal sealed class ShellApprovalHarness : IAsyncDisposable
{
    private const string InvocationSessionId = "signalr/approval-matrix";
    private const string OtherSessionId = "signalr/other-session";

    private readonly string _rootDirectory;
    private readonly string _projectDirectory;
    private readonly string _externalDirectory;
    private readonly ServiceProvider _services;
    private readonly StubRequiredActor _approvalActor;
    private readonly FunctionCallContent _toolCall;
    private readonly ToolExecutionContext _context;
    private readonly DispatchingToolExecutor _executor;
    private readonly ToolRegistry _registry;

    private ShellApprovalHarness(
        string rootDirectory,
        string projectDirectory,
        string externalDirectory,
        NetclawPaths paths,
        string approvalProjectDirectory,
        string approvalSessionDirectory,
        ServiceProvider services,
        StubRequiredActor approvalActor,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        DispatchingToolExecutor executor,
        ToolRegistry registry,
        CountingApprovalService approvalService)
    {
        _rootDirectory = rootDirectory;
        _projectDirectory = projectDirectory;
        _externalDirectory = externalDirectory;
        Paths = paths;
        ProjectDirectory = approvalProjectDirectory;
        SessionDirectory = approvalSessionDirectory;
        _services = services;
        _approvalActor = approvalActor;
        _toolCall = toolCall;
        _context = context;
        _executor = executor;
        _registry = registry;
        ApprovalService = approvalService;
    }

    public CountingApprovalService ApprovalService { get; }

    /// <summary>The Netclaw home layout that the production registration protects.</summary>
    public NetclawPaths Paths { get; }

    /// <summary>The project directory of the tool execution context.</summary>
    public string ProjectDirectory { get; }

    /// <summary>The session directory of the tool execution context.</summary>
    public string SessionDirectory { get; }

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
        IReadOnlyList<string>? deniedPaths = null,
        ToolApprovalMode? shellApprovalMode = null,
        ShellApprovalHarnessPolicy? policy = null)
    {
        var rootDirectory = Path.Combine(
            CanonicalTemporaryDirectory(),
            "netclaw-approval-matrix",
            Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(rootDirectory, "project");
        var sessionDirectory = Path.Combine(rootDirectory, "session");
        var externalDirectory = Path.Combine(rootDirectory, "workspaces", "external");
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
            approvalExternalDirectory = $"{windowsRoot}/workspaces/external";
        }

        // The external directory stays outside every trusted root, the
        // workspaces root included. A case in that directory therefore cannot
        // declare it as a project.
        var paths = new NetclawPaths(
            Path.Combine(rootDirectory, "netclaw"),
            policy?.WorkspacesDirectory ?? Path.Combine(rootDirectory, "netclaw", "workspaces"));
        if (policy?.HardDenyOverridesJson is { } hardDenyOverrides)
        {
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllTextAsync(paths.HardDenyOverridesPath, hardDenyOverrides, ct);
        }

        var services = CreateServices(
            paths,
            environment,
            CreateConfig(shellApprovalMode, policy),
            CreateProtectedPathPolicy(paths, environment, deniedPaths),
            safeVerbs ?? SafeVerbLoader.Load(environment.Platform == ShellPlatform.Windows),
            timeProvider ?? TimeProvider.System,
            actorSystem,
            out var approvalActor);
        var provider = services.BuildServiceProvider();

        var approvalShell = environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        var persistentSeeds = approvals.Seeds
            .Where(seed => seed.Source == ApprovalSeedSource.Persistent)
            .ToList();
        // Seed persistent grants SYNCHRONOUSLY into the store, before the actor
        // exists. The actor re-loads the store fresh on each read, so a direct
        // write is behaviorally identical to routing through the actor's
        // RecordStructuredToolApproval handler. Critically, this moves the
        // blocking WriteThrough + Flush(flushToDisk: true) fsync OFF the actor's
        // 5s Ask deadline: it now runs on this test thread with no wall-clock
        // budget, so disk latency (Windows CI Defender scanning each new
        // tool-approvals.json in a fresh %TEMP% tree, full-suite parallel load)
        // can no longer expire the Ask and surface as an AskTimeoutException on
        // whichever test's seed lands in the contended window.
        var store = provider.GetRequiredService<ToolApprovalStore>();
        foreach (var audienceGroup in persistentSeeds.GroupBy(seed => seed.Audience))
        {
            store.TryAddApprovals(
                audienceGroup.Key,
                ShellTool.ToolName,
                audienceGroup
                    .Select(seed => CreatePersistentEntry(
                        seed.Directory == ApprovalDirectoryShape.Repository
                            ? CreateRepositoryGrant(
                                seed.Pattern,
                                approvalShell,
                                scope?.RepositoryGrantWorktree ?? approvalProjectDirectory)
                            : CreateGrant(seed.Pattern, approvalShell, ResolveDirectory(
                                seed.Directory,
                                approvalProjectDirectory,
                                approvalSessionDirectory,
                                approvalExternalDirectory))))
                    .ToList());
        }

        var approvalService = provider.GetRequiredService<CountingApprovalService>();
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

        var executor = (DispatchingToolExecutor)provider.GetRequiredService<IToolExecutor>();
        var workingDirectory = ResolveDirectory(
            invocation.WorkingDirectory,
            approvalProjectDirectory,
            approvalSessionDirectory,
            approvalExternalDirectory);
        var toolCall = CreateShellCall(caseId, invocation.Command, workingDirectory);
        var contextOptions = new TestToolExecutionContextOptions
        {
            Audience = invocation.Audience,
            ProjectDirectory = policy?.Sessionless == true ? null : approvalProjectDirectory,
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(invocation.Interactive)
        };
        var context = policy?.Sessionless == true
            ? TestToolExecutionContext.CreateUnbound(contextOptions)
            : TestToolExecutionContext.CreateBound(
                scope?.InvocationSessionId ?? InvocationSessionId,
                approvalSessionDirectory,
                contextOptions);
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
            paths,
            approvalProjectDirectory,
            approvalSessionDirectory,
            provider,
            approvalActor,
            toolCall,
            context,
            executor,
            provider.GetRequiredService<ToolRegistry>(),
            approvalService);
    }

    // Uses the daemon registration. The harness adds only the host facts that
    // the daemon supplies before the registration runs, the approval actor
    // that Akka hosting starts, and a counting decorator on the approval service.
    private static ServiceCollection CreateServices(
        NetclawPaths paths,
        ShellExecutionEnvironment environment,
        ToolConfig config,
        ToolPathPolicy toolPathPolicy,
        SafeVerbList safeVerbs,
        TimeProvider timeProvider,
        ActorSystem actorSystem,
        out StubRequiredActor approvalActor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AddProductionToolAuthorization(services, paths, environment, config, toolPathPolicy, safeVerbs, timeProvider);

        var stub = new StubRequiredActor(actorSystem);
        approvalActor = stub;
        services.AddSingleton<IRequiredActor<ToolApprovalActorKey>>(sp =>
        {
            stub.Start(sp.GetRequiredService<ToolApprovalStore>());
            return stub;
        });

        var production = services.Single(descriptor => descriptor.ServiceType == typeof(IToolApprovalService));
        var implementation = production.ImplementationType
            ?? throw new InvalidOperationException("The approval service registration has no implementation type.");
        services.Remove(production);
        services.AddSingleton(implementation);
        services.AddSingleton(sp => new CountingApprovalService(
            (IToolApprovalService)sp.GetRequiredService(implementation)));
        services.AddSingleton<IToolApprovalService>(sp => sp.GetRequiredService<CountingApprovalService>());
        return services;
    }

    /// <summary>
    /// Registers the tool authorization system and the tool executor with the
    /// daemon registration methods, for a Personal deployment on the given host shell.
    /// </summary>
    internal static void AddProductionToolAuthorization(
        IServiceCollection services,
        NetclawPaths paths,
        ShellExecutionEnvironment environment,
        ToolConfig config)
        => AddProductionToolAuthorization(
            services,
            paths,
            environment,
            config,
            DaemonToolPathPolicyFactory.Create(paths, environment),
            SafeVerbLoader.Load(environment.Platform == ShellPlatform.Windows),
            TimeProvider.System);

    private static void AddProductionToolAuthorization(
        IServiceCollection services,
        NetclawPaths paths,
        ShellExecutionEnvironment environment,
        ToolConfig config,
        ToolPathPolicy toolPathPolicy,
        SafeVerbList safeVerbs,
        TimeProvider timeProvider)
    {
        // The daemon registers its host shell before the tool registration runs.
        services.AddSingleton(environment);
        var policy = services.AddDaemonToolAuthorization(
            paths,
            environment,
            config,
            SecurityPolicyDefaults.Resolve(new SecurityPolicyConfig
            {
                DeploymentPosture = DeploymentPosture.Personal,
                StrictDefaults = false
            }),
            toolPathPolicy,
            safeVerbs,
            FeatureGates.AllEnabled,
            timeProvider);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(policy);
        services.AddDaemonToolExecutor(registry, policy);
    }

    // The daemon protects the control plane of its Netclaw home. A Windows host
    // case runs on any host, so it uses one Windows protected path instead.
    // A case that names its own protected paths uses only those paths.
    private static ToolPathPolicy CreateProtectedPathPolicy(
        NetclawPaths paths,
        ShellExecutionEnvironment environment,
        IReadOnlyList<string>? deniedPaths)
    {
        if (deniedPaths is not null)
            return new ToolPathPolicy(environment, deniedPaths);

        return environment.Platform == ShellPlatform.Windows
            ? new ToolPathPolicy(environment, [@"C:\protected\config"])
            : DaemonToolPathPolicyFactory.Create(paths, environment);
    }

    private static FunctionCallContent CreateShellCall(
        string callId,
        string command,
        string? workingDirectory)
    {
        var arguments = workingDirectory is null
            ? ToolInput.Create("Command", command)
            : ToolInput.Create(
                "Command", command,
                "WorkingDirectory", workingDirectory);
        return new FunctionCallContent(callId, ShellTool.ToolName, arguments);
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

    private static ToolApprovalGrant CreateRepositoryGrant(
        string pattern,
        ApprovalShell shell,
        string worktree)
    {
        if (!GitRepositoryApprovalScope.TryResolve(worktree, out var scope))
            throw new InvalidOperationException("The test repository worktree is not registered.");

        return CreateGrant(pattern, shell, directory: null) with
        {
            Repository = scope!.CommonDirectory,
            RepositoryWorktree = worktree,
        };
    }

    // Mirrors ToolApprovalActor.TryCreateEntries for the persistent-shell path.
    // Replicating this small slice lets the harness seed the store directly in
    // CreateAsync without routing through the actor's 5s-Ask persistence write
    // (the Windows CI flake). Only the shell cases the harness seeds are handled;
    // the actor's non-shell and session-entry branches are not reachable here.
    private static ApprovalEntry CreatePersistentEntry(ToolApprovalGrant grant)
    {
        if (grant.Candidate.Shell is not { } shell ||
            grant.Candidate.VerbTokens is not { } tokens)
        {
            throw new InvalidOperationException("Persistent shell seed lacks shell/verb tokens.");
        }

        if (grant.Repository is not null)
        {
            if (grant.RepositoryWorktree is null
                || !GitRepositoryApprovalScope.TryResolveCandidate(
                    grant.Candidate.Directory,
                    grant.RepositoryWorktree,
                    out var scope)
                || !ToolApprovalEntryComparer.Equals(scope!.CommonDirectory, grant.Repository)
                || !PathUtility.AreEquivalentPaths(scope.WorktreeRoot, grant.RepositoryWorktree))
            {
                throw new InvalidOperationException("Repository grant scope is invalid.");
            }

            return ApprovalEntry.CreateRepositoryTokenPrefix(shell, tokens, grant.Repository);
        }

        return ApprovalEntry.CreateTokenPrefix(shell, tokens, grant.Directory);
    }

    public async Task<ApprovalObservation> EvaluateAsync(CancellationToken ct)
    {
        var decision = await _executor.EvaluateAuthorizationAsync(_toolCall, _context, ct);
        return Observe(decision, ApprovalService.CheckCount);
    }

    /// <summary>Evaluates another shell command in the same session, project, and approval state.</summary>
    public Task<ApprovalObservation> EvaluateShellAsync(
        string command,
        CancellationToken ct,
        string? workingDirectory = null)
        => EvaluateToolAsync(CreateShellCall(_toolCall.CallId, command, workingDirectory), ct);

    /// <summary>Evaluates a call of any registered tool in the same session, project, and approval state.</summary>
    public Task<ApprovalObservation> EvaluateToolAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        CancellationToken ct)
        => EvaluateToolAsync(new FunctionCallContent(_toolCall.CallId, toolName, arguments), ct);

    private async Task<ApprovalObservation> EvaluateToolAsync(FunctionCallContent call, CancellationToken ct)
    {
        var before = ApprovalService.CheckCount;
        var decision = await _executor.EvaluateAuthorizationAsync(call, _context, ct);
        return Observe(decision, ApprovalService.CheckCount - before);
    }

    /// <summary>Runs a call of any registered tool through the executor and observes the result.</summary>
    public async Task<ToolRunObservation> RunToolAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var runArguments = new Dictionary<string, object?>(arguments);
        runArguments.TryAdd("_rationale", "Observe the approval contract.");
        try
        {
            var output = await _executor.ExecuteAsync(
                new FunctionCallContent(_toolCall.CallId, toolName, runArguments),
                _context,
                ct);
            return new ToolRunObservation(ApprovalOutcome.Allowed, null, null, output);
        }
        catch (ToolAccessDeniedException denied)
        {
            return new ToolRunObservation(ApprovalOutcome.Denied, denied.DenyReason, denied.ToAgentResult(), null);
        }
        catch (ToolApprovalRequiredException)
        {
            return new ToolRunObservation(ApprovalOutcome.RequiresApproval, null, null, null);
        }
        catch (ToolCorrectionRequiredException)
        {
            return new ToolRunObservation(ApprovalOutcome.RequiresAgentCorrection, null, null, null);
        }
    }

    /// <summary>
    /// Adds one MCP server tool to the live registry, as the daemon MCP client
    /// manager does after it connects. The tool returns a fixed marker.
    /// </summary>
    /// <returns>The registered tool name.</returns>
    public string RegisterMcpTool(string serverName, string toolName)
    {
        var tool = new McpToolAdapter(
            AIFunctionFactory.Create(() => "mcp-tool-ran", toolName),
            serverName,
            toolName);
        _registry.Register(tool);
        return tool.Name;
    }

    /// <summary>Runs another shell command through the executor.</summary>
    public Task<ToolRunObservation> RunShellAsync(string command, CancellationToken ct)
        => RunToolAsync(ShellTool.ToolName, ToolInput.Create("Command", command), ct);

    private static ApprovalObservation Observe(
        ToolAuthorizationDecision decision,
        int approvalChecks)
    {
        var approvalContext = decision.ApprovalContext;

        return new ApprovalObservation(
            MapOutcome(decision.Outcome),
            decision.AllowReason is { } reason ? MapAllowReason(reason) : null,
            decision.DenyReason,
            MapCorrection(decision.AgentCorrection),
            approvalContext is null ? null : ObservePrompt(approvalContext),
            approvalChecks,
            decision.ApprovalMatches
                .Select(match => $"{match.Source}:{match.Pattern}")
                .ToList(),
            decision.ShellPolicyTrace.Rows.Select(FormatTraceRow).ToList(),
            decision.ShellPolicyTrace.Rows
                .Where(row => row.CandidateId is not null && row.Coverage is not null)
                .Select(row => (
                    row.CandidateId!.Value.Value,
                    row.Coverage!.Value.ToString()))
                .ToList())
        {
            NeedsApproval = decision.NeedsApproval,
            DenyMessage = decision.DenyMessage,
            AgentCorrectionTarget = decision.AgentCorrection switch
            {
                ToolCorrection.ShellWorkingDirectorySuggested suggestion => suggestion.Directory,
                ToolCorrection.ProjectDirectorySuggested suggestion => suggestion.Directory,
                ToolCorrection.NativeToolSuggested suggestion => suggestion.ToolName.Value,
                ToolCorrection.ManagedTemporaryDirectorySuggested suggestion => suggestion.Target.ManagedTemporaryDirectory,
                _ => null
            },
            PlatformTemporaryRoot = decision.AgentCorrection is ToolCorrection.ManagedTemporaryDirectorySuggested temporary
                ? temporary.Target.PlatformTemporaryRoot
                : null
        };
    }

    private static ApprovalPromptObservation ObservePrompt(ToolApprovalContext approvalContext)
        => new(
            approvalContext.CandidateVerbs,
            approvalContext.IsMessy,
            approvalContext.Options.Select(option => option.Key.Value).ToList())
        {
            DisplayText = approvalContext.DisplayText,
            OptionLabels = approvalContext.Options.Select(option => option.Label).ToList(),
            CandidateDirectories = approvalContext.Candidates?
                .Select(candidate => candidate.Directory)
                .ToList(),
            Cwd = approvalContext.Cwd,
            OneTimeApprovalKeys = OneTimeApprovalKeys.Create(approvalContext)
        };

    internal static ApprovalOutcome ObserveOutcome(
        ToolAuthorizationDecision decision)
        => MapOutcome(decision.Outcome);

    private static ApprovalOutcome MapOutcome(ToolAuthorizationOutcome outcome)
        => outcome switch
        {
            ToolAuthorizationOutcome.Allowed => ApprovalOutcome.Allowed,
            ToolAuthorizationOutcome.RequiresApproval => ApprovalOutcome.RequiresApproval,
            ToolAuthorizationOutcome.RequiresAgentCorrection => ApprovalOutcome.RequiresAgentCorrection,
            ToolAuthorizationOutcome.Denied => ApprovalOutcome.Denied,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown authorization outcome.")
        };

    private static ApprovalAllowReason MapAllowReason(ToolAllowReason reason)
        => reason switch
        {
            ToolAllowReason.PolicyAuto => ApprovalAllowReason.PolicyAuto,
            ToolAllowReason.BackgroundJobLifecycle => ApprovalAllowReason.BackgroundJobLifecycle,
            ToolAllowReason.ReviewedSafePolicy => ApprovalAllowReason.ReviewedSafePolicy,
            ToolAllowReason.ApprovalExemptShellCandidates => ApprovalAllowReason.ApprovalExemptShellCandidates,
            ToolAllowReason.StoredApproval => ApprovalAllowReason.StoredApproval,
            ToolAllowReason.OneTimeApproval => ApprovalAllowReason.OneTimeApproval,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown allow reason.")
        };

    private static ApprovalCorrection? MapCorrection(ToolCorrection? correction)
        => correction switch
        {
            null => null,
            ToolCorrection.ManagedTemporaryDirectorySuggested => ApprovalCorrection.ManagedTemporaryDirectory,
            ToolCorrection.NativeToolSuggested => ApprovalCorrection.NativeTool,
            ToolCorrection.ProjectDirectorySuggested => ApprovalCorrection.ProjectDirectory,
            ToolCorrection.ShellWorkingDirectorySuggested => ApprovalCorrection.ShellWorkingDirectory,
            _ => throw new ArgumentOutOfRangeException(
                nameof(correction), correction, "Unknown approval correction.")
        };

    private static string FormatTraceRow(ShellPolicyTraceRow row)
        => string.Join(
            '|',
            row.Stage,
            row.CandidateId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.ExecutableBasename ?? string.Empty,
            row.Outcome,
            row.Reason,
            row.Coverage?.ToString() ?? string.Empty,
            row.ScopeRelation,
            row.GrantTimestamp?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);

    public Task<ToolAuthorizationDecision> EvaluateDecisionAsync(CancellationToken ct)
        => _executor.EvaluateAuthorizationAsync(_toolCall, _context, ct);

    public async Task<string> ExecuteAsync(CancellationToken ct)
    {
        var arguments = new Dictionary<string, object?>(
            _toolCall.Arguments ?? new Dictionary<string, object?>())
        {
            ["_rationale"] = "Verify that directory advice stops this shell call."
        };
        try
        {
            return await _executor.ExecuteAsync(
                new FunctionCallContent(_toolCall.CallId, _toolCall.Name, arguments),
                _context,
                ct);
        }
        catch (ToolCorrectionRequiredException correction)
        {
            throw new ShellApprovalCorrectionRequiredException(correction);
        }
    }

    public void SeedOneTimeApproval(ToolApprovalContext approvalContext)
        => _context.Approval.SeedOneTimeApproval(
            _toolCall.Name,
            OneTimeApprovalKeys.Create(approvalContext));

    /// <summary>Stores a "Once" answer for the prompt that an earlier evaluation observed.</summary>
    public void SeedOneTimeApproval(ApprovalPromptObservation prompt)
        => _context.Approval.SeedOneTimeApproval(
            _toolCall.Name,
            prompt.OneTimeApprovalKeys);

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
        if (_approvalActor.StartedActor is { } actor)
            await actor.GracefulStop(TimeSpan.FromSeconds(15));
        await _services.DisposeAsync();
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);
    }

    private static ToolConfig CreateConfig(
        ToolApprovalMode? shellApprovalMode,
        ShellApprovalHarnessPolicy? policy)
    {
        var config = new ToolConfig
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(DeploymentPosture.Personal)
        };

        if (shellApprovalMode is { } mode)
        {
            config.AudienceProfiles.Personal.ApprovalPolicy!.ToolOverrides[ShellTool.ToolName] = mode;
        }

        if (policy is not null)
        {
            config.HardDenyPatterns = [.. policy.HardDenyPatterns];
            foreach (var (key, value) in policy.PersonalApprovalOverrides)
                config.AudienceProfiles.Personal.ApprovalPolicy!.ToolOverrides[key] = value;
            policy.ConfigureTools?.Invoke(config);
        }

        return config;
    }

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
            ApprovalDirectoryShape.ProjectChild => Path.Combine(projectDirectory, "sub"),
            ApprovalDirectoryShape.Session => sessionDirectory,
            ApprovalDirectoryShape.External => externalDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(directory), directory, "Unknown approval directory shape.")
        };

    // Stands in for the Akka hosting registry. The daemon starts the approval
    // actor from the DI store; this stub starts it from the same store.
    private sealed class StubRequiredActor(ActorSystem actorSystem) : IRequiredActor<ToolApprovalActorKey>
    {
        private IActorRef? _actor;

        public IActorRef? StartedActor => _actor;

        public IActorRef ActorRef => _actor
            ?? throw new InvalidOperationException("The approval actor has not started.");

        public void Start(ToolApprovalStore store)
            => _actor ??= actorSystem.ActorOf(
                ToolApprovalActor.CreateProps(store),
                $"approval-matrix-{Guid.NewGuid():N}");

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActorRef);
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
