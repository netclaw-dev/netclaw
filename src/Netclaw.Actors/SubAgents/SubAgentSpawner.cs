// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
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
/// Shared utility that encapsulates the subagent spawn-ask-report lifecycle.
/// Resolves audience-scoped runtime tools, spawns a <see cref="SubAgentActor"/> as a child of the
/// owning session actor, awaits results, and emits observability notifications.
/// Singleton — registered in DI.
/// </summary>
public sealed class SubAgentSpawner
{
    private static readonly StringComparer FilePathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private const int SubAgentMaxToolIterations = 30;

    private readonly IChatClientProvider _chatClientProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly SubAgentConfig _subAgentConfig;
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
        // Parent-side spawn breadcrumbs — each event is fanned out to daemon.log/Seq and
        // the parent's session.log from one place (see SubAgentSpawnBreadcrumbs), covering
        // request → outcome plus early rejections that happen before the child even exists.
        SubAgentSpawnBreadcrumbs.SpawnRequested(_logger, context, profile.Name, task.Length);

        if (context.SpawnChildActor is null)
        {
            SubAgentSpawnBreadcrumbs.NoSessionContext(_logger, context, profile.Name);
            activitySink?.TryComplete();
            return new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnUnavailable),
                Output = $"Cannot spawn subagent '{profile.Name}': no session context available.",
                AgentName = new AgentName(profile.Name)
            };
        }

        var exposure = ResolveTools(context);
        if (exposure.Tools.Count == 0)
        {
            SubAgentSpawnBreadcrumbs.NoToolsAvailable(_logger, context, profile.Name);
            activitySink?.TryComplete();
            return new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.NoToolsAvailable),
                Output = $"Cannot spawn subagent '{profile.Name}': no tools are available under the parent audience policy.",
                AgentName = new AgentName(profile.Name)
            };
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
        var subAgentTimeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
        var prefillTimeout = TimeSpan.FromSeconds(
            profile.PrefillTimeoutSeconds ?? _subAgentConfig.PrefillTimeoutSeconds);
        var noProgressTimeout = TimeSpan.FromSeconds(_subAgentConfig.NoProgressTimeoutSeconds);
        var subAgentScopeId = !string.IsNullOrWhiteSpace(context.SessionId)
            ? $"{context.SessionId}/subagent/{definition.Name}/{runId}"
            : $"subagent/{definition.Name}/{runId}";
        var scopeId = new SubAgentScopeId(subAgentScopeId);
        var parentWorkingContext = new WorkingContext
        {
            ProjectDirectory = context.ProjectDirectory,
            RecentFiles = [.. context.RecentFiles.Take(WorkingContext.MaxRecentFiles)]
        };
        WorkingContextSnapshot initialWorkingSnapshot;
        try
        {
            initialWorkingSnapshot = await _workingContextSnapshots.CreateAsync(
                parentWorkingContext,
                context.Audience,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            activitySink?.TryComplete();
            return CancelledResult(definition.Name, runId, scopeId);
        }
        catch (Exception ex) when (!FatalExceptionPolicy.IsFatal(ex))
        {
            SubAgentSpawnBreadcrumbs.RunFailed(_logger, context, profile.Name, runId, ex);
            activitySink?.TryComplete();
            return new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnError),
                Output = $"Subagent error: {ex.Message}",
                AgentName = definition.Name,
                RunId = runId,
                ScopeId = scopeId
            };
        }
        catch (Exception ex) when (FatalExceptionPolicy.IsFatal(ex))
        {
            activitySink?.TryComplete();
            throw;
        }
        // A transport can own an approval channel without being able to service
        // interactive prompts. Fork only the admitted capability, never bridge
        // presence by itself.
        var interactiveApproval = context.RunScope.InteractiveApproval;
        var childScope = new ChildRunScope
        {
            ScopeId = scopeId,
            Authority = new ToolRunScope
            {
                Session = new ToolSessionScope.Bound(scopeId.Value, context.SessionDirectory),
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

        context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
        {
            RunId = runId,
            AgentName = definition.Name.Value,
            IsStarted = true,
            ToolCount = exposure.Tools.Count
        });

        // Spawn as child of the session actor via the context factory
        var props = SubAgentActor.CreatePropsWithProjectInstructionProvider(
            definition,
            chatClient,
            _toolAccessPolicy,
            _promptProvider,
            _approvalService,
            SubAgentMaxToolIterations,
            _sessionMetrics,
            exposure.CoreToolNames,
            _logger);
        var actorName = $"subagent-{definition.Name}-{runId}";
        IActorRef subAgent;
        try
        {
            subAgent = (IActorRef)await context.SpawnChildActor(props, actorName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = false,
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.CancelledByParent
            });
            activitySink?.TryComplete();
            return CancelledResult(definition.Name, runId, scopeId);
        }
        catch (Exception ex) when (!FatalExceptionPolicy.IsFatal(ex))
        {
            // The child actor was never created (session actor ActorOf failed or the
            // spawn ask timed out). Record it to the session transcript before the
            // exception propagates to the tool pipeline.
            SubAgentSpawnBreadcrumbs.ChildSpawnFailed(_logger, context, profile.Name, runId, ex);
            // Balance the IsStarted=true notification above: the non-streaming path
            // (activitySink is null) relies solely on the per-call output sink, so without
            // a terminal event the session UI shows a sub-agent stuck in "Started".
            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = false,
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.SpawnError
            });
            activitySink?.TryComplete();
            throw;
        }
        catch (Exception ex) when (FatalExceptionPolicy.IsFatal(ex))
        {
            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = false,
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.SpawnError
            });
            activitySink?.TryComplete();
            throw;
        }

        SubAgentSpawnBreadcrumbs.ChildSpawned(_logger, context, profile.Name, runId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await subAgent.Ask<SubAgentResult>(
                new RunSubAgent
                {
                    Scope = childScope,
                    Task = task,
                    RuntimeContext = runtimeContext,
                    Timeout = subAgentTimeout,
                    PrefillTimeout = prefillTimeout,
                    NoProgressTimeout = noProgressTimeout,
                    Cancellation = ct,
                    // Null for non-streaming callers such as routed skills and the
                    // legacy ExecuteAsync path; the sub-agent surfaces its progress
                    // through its own session-correlated logs regardless. Streaming
                    // spawn_agent calls pass a real sink so the parent tool's
                    // liveness watchdog sees progress.
                    ActivitySink = activitySink
                },
                // No Ask timeout: a healthy run is bounded by the sub-agent's own
                // watchdogs, not by wall-clock, so any finite ceiling here could
                // pre-empt a legitimately long run. The sub-agent self-completes on
                // two internal budgets: the liveness watchdog (no bytes at all,
                // including keepalives) and the no-progress deadline (keepalives but
                // no real tokens for NoProgressTimeoutSeconds). ct — the spawning
                // tool call's token — only adds parent-turn / user cancellation on
                // top; once streaming spawn_agent has emitted its first activity,
                // the parent no longer applies an inter-item watchdog. Keepalive
                // wedges are bounded by the sub-agent's own no-progress watchdog.
                timeout: Timeout.InfiniteTimeSpan,
                cancellationToken: ct);

            sw.Stop();

            result = await EnrichWorkingContextResultAsync(
                result,
                initialWorkingSnapshot,
                context.Audience,
                ct).ConfigureAwait(false);

            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = result.Success,
                Outcome = result.Outcome,
                OutcomeReason = result.OutcomeReason,
                Duration = sw.Elapsed,
                Findings = result.Findings,
                WorkingContext = result.WorkingContext
            });

            SubAgentSpawnBreadcrumbs.Completed(_logger, context, profile.Name, runId, result.Success, sw.ElapsedMilliseconds);

            return result with
            {
                RunId = runId,
                ScopeId = scopeId
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            TryStopSubAgent(subAgent);

            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = false,
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.CancelledByParent,
                Duration = sw.Elapsed
            });

            return CancelledResult(definition.Name, runId, scopeId);
        }
        catch (Exception ex) when (!FatalExceptionPolicy.IsFatal(ex))
        {
            sw.Stop();

            TryStopSubAgent(subAgent);

            context.Outputs.ReportSubAgentActivity(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = false,
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.SpawnError,
                Duration = sw.Elapsed
            });

            SubAgentSpawnBreadcrumbs.RunFailed(_logger, context, profile.Name, runId, ex);
            return new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnError),
                Output = $"Subagent error: {ex.Message}",
                AgentName = new AgentName(profile.Name),
                RunId = runId,
                ScopeId = scopeId
            };
        }
        finally
        {
            // Terminate the streaming caller's activity reader even on failure.
            activitySink?.TryComplete();
        }
    }

    private static SubAgentResult CancelledResult(
        AgentName agentName,
        SubAgentRunId runId,
        SubAgentScopeId scopeId) => new()
        {
            Completion = new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent),
            Output = "Subagent cancelled by parent",
            AgentName = agentName,
            RunId = runId,
            ScopeId = scopeId
        };

    private async Task<SubAgentResult> EnrichWorkingContextResultAsync(
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
