// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Resolves child tools and authority before the session accepts a durable start.
/// The session owns the <see cref="SubAgentActor"/> and its terminal result.
/// The public foreground adapter remains available for explicit foreground callers.
/// Singleton — registered in DI.
/// </summary>
public sealed class SubAgentSpawner
{
    private static readonly StringComparer FilePathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;


    private readonly IChatClientProvider _chatClientProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly SessionConfig _sessionConfig;
    private readonly ILogger<SubAgentSpawner> _logger;
    private readonly IWorkingContextSnapshotProvider _workingContextSnapshots;

    // The process-wide daily-stats sink, handed to each spawned SubAgentActor so its
    // LLM calls are billed to `netclaw stats`. Nullable to match the rest of the stats
    // wiring (a host without the daemon stats backend is a real runtime state); DI
    // injects the registered singleton in production.
    private readonly Telemetry.ISessionMetrics? _sessionMetrics;

    public SubAgentSpawner(
        IChatClientProvider chatClientProvider,
        ToolRegistry toolRegistry,
        ToolAccessPolicy toolAccessPolicy,
        IToolApprovalService? approvalService,
        ISystemPromptProvider promptProvider,
        IWorkingContextSnapshotProvider workingContextSnapshots,
        ILogger<SubAgentSpawner> logger,
        SessionConfig sessionConfig,
        SubAgentConfig? subAgentConfig = null,
        Telemetry.ISessionMetrics? sessionMetrics = null)
    {
        _chatClientProvider = chatClientProvider;
        _toolRegistry = toolRegistry;
        _toolAccessPolicy = toolAccessPolicy;
        _approvalService = approvalService;
        _promptProvider = promptProvider;
        _workingContextSnapshots = workingContextSnapshots;
        _subAgentConfig = subAgentConfig ?? new SubAgentConfig();
        _sessionConfig = sessionConfig;
        _logger = logger;
        _sessionMetrics = sessionMetrics;
    }

    /// <summary>
    /// Spawn a subagent as a child of the owning session, execute the task, and return the result.
    /// The subagent is created via <see cref="ToolInvocationContext.SpawnChildActor"/> which is
    /// wired by <c>LlmSessionActor</c> to <c>Context.ActorOf</c>. If no spawn factory is
    /// available (e.g., in tests or standalone mode), returns a failure result.
    /// Reports start/complete notifications through the per-call output sink.
    /// </summary>
    public async Task<SubAgentResult> SpawnAsync(
        SubAgentProfile profile,
        string task,
        string? runtimeContext,
        ToolInvocationContext context,
        CancellationToken ct = default,
        string? systemPromptOverlay = null,
        ChannelWriter<ToolActivityUpdate>? activitySink = null)
    {
        var result = await SpawnRunAsync(profile, task, runtimeContext, context, ct, systemPromptOverlay, activitySink)
            .ConfigureAwait(false);
        return result.ToProtocolResult();
    }

    internal async Task<ChildRunAcceptance> StartRunAsync(
        SubAgentProfile profile, string task, string? runtimeContext,
        ToolInvocationContext context, CancellationToken ct, string? systemPromptOverlay)
    {
        var prepared = await PrepareRunAsync(profile, task, runtimeContext, context, ct, systemPromptOverlay);
        var spawn = context.SpawnChildActor
            ?? throw new InvalidOperationException("A child start requires an owning session.");
        return (ChildRunAcceptance)await spawn(prepared, prepared.ActorName, ct);
    }

    internal async Task<EnrichedChildRunResult> SpawnRunAsync(
        SubAgentProfile profile, string task, string? runtimeContext,
        ToolInvocationContext context, CancellationToken ct, string? systemPromptOverlay,
        ChannelWriter<ToolActivityUpdate>? activitySink)
    {
        IActorRef? child = null;
        PreparedChildStart? prepared = null;
        try
        {
            prepared = await PrepareRunAsync(profile, task, runtimeContext, context, ct, systemPromptOverlay);
            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = prepared.RunId, AgentName = profile.Name, IsStarted = true, ToolCount = prepared.ToolCount
            });
            var spawn = context.SpawnChildActor
                ?? throw new InvalidOperationException("A child start requires an owning session.");
            child = (IActorRef)await spawn(prepared.Props, prepared.ActorName, ct);
            var result = await child.Ask<SubAgentResult>(prepared.Execution with
            {
                Cancellation = ct,
                ActivitySink = activitySink
            }, Timeout.InfiniteTimeSpan, ct);
            result = await EnrichWorkingContextResultAsync(result,
                prepared.Execution.Scope.InitialWorkingSnapshot, context.Audience, ct);
            result = result with { RunId = prepared.RunId, ScopeId = prepared.Execution.Scope.ScopeId };
            ReportCompletion(result);
            if (result.Completion is ChildRunCompletion.Completed or ChildRunCompletion.Partial)
                return new EnrichedChildRunResult.SuccessfulRun(result,
                    new EnrichedChildRunResult.RunLocations(prepared.Storage.LogPath, prepared.Storage.ArtifactDirectory));
            return new EnrichedChildRunResult.OtherRun(result);
        }
        catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
        {
            if (prepared is not null)
            {
                if (child is null)
                    SubAgentSpawnBreadcrumbs.ChildSpawnFailed(_logger, context, profile.Name, prepared.RunId, error);
                else
                    SubAgentSpawnBreadcrumbs.RunFailed(_logger, context, profile.Name, prepared.RunId, error);
            }
            if (child is not null)
                TryStopSubAgent(child);
            var result = new SubAgentResult
            {
                AgentName = new AgentName(profile.Name), RunId = prepared?.RunId,
                ScopeId = prepared?.Execution.Scope.ScopeId,
                Completion = error is OperationCanceledException
                    ? new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent)
                    : new ChildRunCompletion.Failed(error is ChildStartRejectedException rejected
                        ? rejected.Reason : SubAgentOutcomeReason.SpawnError),
                Output = error.Message
            };
            if (prepared is not null)
                ReportCompletion(result);
            return new EnrichedChildRunResult.OtherRun(result);
        }
        finally
        {
            activitySink?.TryComplete();
        }

        void ReportCompletion(SubAgentResult result)
        {
            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = result.RunId ?? throw new InvalidOperationException("A child completion requires its run ID."),
                AgentName = profile.Name, IsStarted = false,
                Success = result.Success, Outcome = result.Outcome, OutcomeReason = result.OutcomeReason,
                Findings = result.Findings, WorkingContext = result.WorkingContext
            });
        }
    }

    private sealed class ChildStartRejectedException(SubAgentOutcomeReason reason, string message)
        : InvalidOperationException(message)
    {
        public SubAgentOutcomeReason Reason { get; } = reason;
    }

    private async Task<PreparedChildStart> PrepareRunAsync(
        SubAgentProfile profile, string task, string? runtimeContext,
        ToolInvocationContext context, CancellationToken ct, string? systemPromptOverlay)
    {
        SubAgentSpawnBreadcrumbs.SpawnRequested(_logger, context, profile.Name, task.Length);
        if (context.SpawnChildActor is null)
        {
            SubAgentSpawnBreadcrumbs.NoSessionContext(_logger, context, profile.Name);
            throw new ChildStartRejectedException(SubAgentOutcomeReason.SpawnUnavailable, "A child start requires an owning session.");
        }
        var exposure = ResolveTools(context);
        if (exposure.Tools.Count == 0)
        {
            SubAgentSpawnBreadcrumbs.NoToolsAvailable(_logger, context, profile.Name);
            throw new ChildStartRejectedException(SubAgentOutcomeReason.NoToolsAvailable,
                "No child tools are available under the parent audience policy.");
        }
        var definition = new SubAgentDefinition
        {
            Name = new AgentName(profile.Name),
            SystemPrompt = AppendSystemPromptOverlay(profile.SystemPrompt, systemPromptOverlay),
            Tools = exposure.Tools,
            ModelRole = profile.ModelRole,
            EmitStructuredFindings = profile.EmitStructuredFindings,
            ProjectInstructions = ResolveProjectInstructions(context),
            OperatingRules = ResolveOperatingRules(context)
        };

        var runId = SubAgentRunId.New();

        var chatClient = _chatClientProvider.GetClient(definition.ModelRole);
        // Inter-delta budget: session-wide, so every subagent gets the same value.
        var subAgentTimeout = _sessionConfig.FirstTokenTimeout;
        var prefillTimeout = TimeSpan.FromSeconds(
            profile.PrefillTimeoutSeconds ?? _subAgentConfig.PrefillTimeoutSeconds);
        var noProgressTimeout = TimeSpan.FromSeconds(_subAgentConfig.NoProgressTimeoutSeconds);
        var subAgentScopeId = !string.IsNullOrWhiteSpace(context.SessionId)
            ? $"{context.SessionId}/subagent/{definition.Name}/{runId}"
            : $"subagent/{definition.Name}/{runId}";
        var scopeId = new SubAgentScopeId(subAgentScopeId);
        var parentStorage = context.SessionStorage
            ?? throw new InvalidOperationException("A subagent run requires resolved session storage.");
        var childStorage = parentStorage.ForChild(runId, scopeId);
        CreateChildLogTarget(childStorage.LogPath.Value);
        var parentWorkingContext = new WorkingContext
        {
            ProjectDirectory = context.ProjectDirectory,
            RecentFiles = [.. context.RecentFiles.Take(WorkingContext.MaxRecentFiles)]
        };
        var initialWorkingSnapshot = await _workingContextSnapshots.CreateAsync(
            parentWorkingContext, context.Audience, ct).ConfigureAwait(false);
        // A transport can own an approval channel without being able to service
        // interactive prompts. Fork only the admitted capability, never bridge
        // presence by itself.
        var interactiveApproval = context.RunScope.InteractiveApproval;
        var childScope = new ChildRunScope
        {
            ScopeId = scopeId,
            Authority = new ToolRunScope
            {
                Session = new ToolSessionScope.Bound(scopeId.Value, childStorage),
                Audience = context.Audience,
                InlineOutputBudget = InlineOutputBudget.Default,
                Boundary = context.Boundary,
                ChannelType = context.ChannelType,
                DefaultDeliveryTarget = context.DefaultDeliveryTarget,
                RequestedDeliveryTarget = context.RequestedDeliveryTarget,
                ModelInputModalities = context.ModelInputModalities,
                InteractiveApproval = interactiveApproval,
                ProjectDirectory = context.ProjectDirectory,
                InheritedCwd = context.ResolveShellCwd(null),
                RecentFiles = context.RecentFiles,
            },
            InitialWorkingSnapshot = initialWorkingSnapshot
        };

        var props = SubAgentActor.CreatePropsWithProjectInstructionProvider(
            definition,
            chatClient,
            _toolAccessPolicy,
            _promptProvider,
            _approvalService,
            _sessionMetrics,
            exposure.CoreToolNames,
            _logger);
        var actorName = $"subagent-{definition.Name}-{runId}";
        var digestInput = System.Text.Json.JsonSerializer.Serialize(new
        {
            profile.Name, task, runtimeContext, systemPromptOverlay
        });
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(digestInput)));
        return new PreparedChildStart(props, actorName, runId, definition.Name, digest, childStorage, definition.Tools.Count,
            new RunSubAgent
            {
                Scope = childScope,
                Task = task,
                RuntimeContext = runtimeContext,
                Timeout = subAgentTimeout,
                PrefillTimeout = prefillTimeout,
                NoProgressTimeout = noProgressTimeout
            });
    }

    internal async Task<SubAgentResult> EnrichWorkingContextResultAsync(
        SubAgentResult result,
        WorkingContextSnapshot initialSnapshot,
        TrustAudience audience,
        CancellationToken cancellationToken)
    {
        if (!result.Success || result.WorkingContext is not { } childContext)
            return result;

        var finalContext = new WorkingContext
        {
            ProjectDirectory = childContext.ProjectDirectory,
            RecentFiles = [.. childContext.ReadFiles
                .Concat(childContext.ConfirmedChangedFiles)
                .Distinct(FilePathComparer)
                .Take(WorkingContext.MaxRecentFiles)]
        };
        var finalSnapshot = await _workingContextSnapshots.CreateAsync(
            finalContext,
            audience,
            cancellationToken).ConfigureAwait(false);
        var initialChanged = CanonicalChangedFiles(initialSnapshot.Git);
        var observed = CanonicalChangedFiles(finalSnapshot.Git)
            .Except(initialChanged, FilePathComparer)
            .Except(childContext.ConfirmedChangedFiles, FilePathComparer)
            .Order(FilePathComparer)
            .ToArray();

        var delta = childContext with
        {
            Worktree = AvailableSnapshot(finalSnapshot.Git)?.Worktree,
            Branch = AvailableSnapshot(finalSnapshot.Git)?.Branch,
            Head = AvailableSnapshot(finalSnapshot.Git)?.Head,
            ObservedChangedFiles = observed
        };
        return result with
        {
            Completion = result.Completion switch
            {
                ChildRunCompletion.Completed => new ChildRunCompletion.Completed(delta),
                ChildRunCompletion.Partial partial => new ChildRunCompletion.Partial(partial.PartialReason, delta),
                _ => throw new InvalidOperationException("Only successful child runs can carry a working-context delta.")
            }
        };
    }

    private static IEnumerable<string> CanonicalChangedFiles(GitWorkingContextInspection inspection)
    {
        var snapshot = AvailableSnapshot(inspection);
        if (snapshot is null)
            return [];

        return snapshot.ChangedFiles.Select(path =>
            Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, snapshot.Worktree));
    }

    private static GitWorkingContextSnapshot? AvailableSnapshot(GitWorkingContextInspection inspection)
        => inspection is GitWorkingContextInspection.Available available ? available.Snapshot : null;

    private ResolvedSubAgentTools ResolveTools(ToolInvocationContext context)
    {
        // Sub-agents inherit the parent session's runtime tool policy. Agent
        // definition tool metadata is advisory only. The static child filter
        // denies recursive delegation and tools that need parent-only output transport.
        var tools = _toolRegistry.GetAllRegistrations()
            .Select(static registration => registration.Tool)
            .Where(static tool => SubAgentToolPolicy.IsAllowedForSubAgent(tool.Name));

        var visible = _toolAccessPolicy.FilterDiscoverableTools(tools, context);
        var coreToolNames = visible
            .Where(tool => _toolRegistry.IsCoreTool(tool.Name))
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        return new ResolvedSubAgentTools(visible, coreToolNames);
    }

    private sealed record ResolvedSubAgentTools(
        IReadOnlyList<INetclawTool> Tools,
        IReadOnlySet<string> CoreToolNames);

    private static void TryStopSubAgent(IActorRef subAgent)
    {
        subAgent.Tell(PoisonPill.Instance);
    }

    private static void CreateChildLogTarget(string logPath)
    {
        var directory = Path.GetDirectoryName(logPath)
            ?? throw new InvalidOperationException("A child log path requires a parent directory.");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(
            logPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private static string AppendSystemPromptOverlay(string basePrompt, string? overlay)
    {
        if (string.IsNullOrWhiteSpace(overlay))
            return basePrompt;

        return string.Concat(
            basePrompt.TrimEnd(),
            "\n\n",
            "[Skill Overlay]\n",
            overlay.Trim());
    }

    private string? ResolveProjectInstructions(ToolInvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectDirectory))
            return null;

        return _promptProvider.GetProjectInstructions(context.Audience, context.ProjectDirectory);
    }

    private string? ResolveOperatingRules(ToolInvocationContext context)
    {
        // Sub-agents inherit the audience-appropriate embedded operating core and
        // the deployment mission playbook. This keeps safety, grounding, and the
        // operator's quality workflow aligned without exposing SOUL.md or TOOLING.md.
        return _promptProvider.GetOperatingRules(context.Audience);
    }
}

/// <summary>Separates child completion from the locations that the spawner adds.</summary>
internal abstract class EnrichedChildRunResult
{
    private EnrichedChildRunResult(SubAgentResult response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Response = response with { LogPath = null, ArtifactDirectory = null };
    }

    // Preserve the public response at the adapter boundary. Locations exist only on SuccessfulRun.
    internal SubAgentResult Response { get; }

    internal sealed record RunLocations
    {
        internal RunLocations(SessionLogPath logPath, ArtifactDirectory artifactDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logPath.Value);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory.Value);
            LogPath = logPath;
            ArtifactDirectory = artifactDirectory;
        }

        internal SessionLogPath LogPath { get; }
        internal ArtifactDirectory ArtifactDirectory { get; }
    }

    internal sealed class SuccessfulRun : EnrichedChildRunResult
    {
        internal SuccessfulRun(SubAgentResult response, RunLocations locations) : base(response)
        {
            if (response.Completion is not (ChildRunCompletion.Completed or ChildRunCompletion.Partial))
                throw new ArgumentException("A successful run requires completed or partial completion.", nameof(response));
            Locations = locations ?? throw new ArgumentNullException(nameof(locations));
        }

        internal RunLocations Locations { get; }
    }

    internal sealed class OtherRun : EnrichedChildRunResult
    {
        internal OtherRun(SubAgentResult response) : base(response)
        {
            if (response.Completion is not (ChildRunCompletion.Failed or ChildRunCompletion.Cancelled))
                throw new ArgumentException("An unsuccessful run requires failed or cancelled completion.", nameof(response));
        }
    }

    internal SubAgentResult ToProtocolResult() => this switch
    {
        SuccessfulRun success => Response with
        {
            LogPath = success.Locations.LogPath.Value,
            ArtifactDirectory = success.Locations.ArtifactDirectory.Value
        },
        OtherRun => Response,
        _ => throw new InvalidOperationException("Unexpected enriched child result.")
    };
}
