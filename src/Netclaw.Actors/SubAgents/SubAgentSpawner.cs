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
    private const int SubAgentMaxToolIterations = 30;

    private readonly IChatClientProvider _chatClientProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly ILogger<SubAgentSpawner> _logger;

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
        ILogger<SubAgentSpawner> logger,
        SubAgentConfig? subAgentConfig = null,
        Telemetry.ISessionMetrics? sessionMetrics = null)
    {
        _chatClientProvider = chatClientProvider;
        _toolRegistry = toolRegistry;
        _toolAccessPolicy = toolAccessPolicy;
        _approvalService = approvalService;
        _promptProvider = promptProvider;
        _subAgentConfig = subAgentConfig ?? new SubAgentConfig();
        _logger = logger;
        _sessionMetrics = sessionMetrics;
    }

    /// <summary>
    /// Spawn a subagent as a child of the owning session, execute the task, and return the result.
    /// The subagent is created via <see cref="ToolExecutionContext.SpawnChildActor"/> which is
    /// wired by <c>LlmSessionActor</c> to <c>Context.ActorOf</c>. If no spawn factory is
    /// available (e.g., in tests or standalone mode), returns a failure result.
    /// Reports start/complete notifications via <see cref="ToolExecutionContext.OnSubAgentActivity"/>.
    /// </summary>
    public async Task<SubAgentResult> SpawnAsync(
        SubAgentProfile profile,
        string task,
        string? runtimeContext,
        ToolExecutionContext context,
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
                Success = false,
                Output = $"Cannot spawn subagent '{profile.Name}': no session context available.",
                AgentName = new AgentName(profile.Name),
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.SpawnUnavailable
            };
        }

        var tools = ResolveTools(profile, context);
        if (tools.Count == 0)
        {
            SubAgentSpawnBreadcrumbs.NoToolsAvailable(_logger, context, profile.Name);
            activitySink?.TryComplete();
            return new SubAgentResult
            {
                Success = false,
                Output = $"Cannot spawn subagent '{profile.Name}': no tools are available under the parent audience policy.",
                AgentName = new AgentName(profile.Name),
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.NoToolsAvailable
            };
        }

        var definition = new SubAgentDefinition
        {
            Name = new AgentName(profile.Name),
            SystemPrompt = AppendSystemPromptOverlay(profile.SystemPrompt, systemPromptOverlay),
            Tools = tools,
            ModelRole = profile.ModelRole,
            EmitStructuredFindings = profile.EmitStructuredFindings,
            ProjectInstructions = ResolveProjectInstructions(context),
            OperatingRules = ResolveOperatingRules(context)
        };

        var runId = SubAgentRunId.New();

        // Notify session that subagent is starting
        context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
        {
            RunId = runId,
            AgentName = definition.Name.Value,
            IsStarted = true,
            ToolCount = tools.Count
        });

        var chatClient = _chatClientProvider.GetClient(definition.ModelRole);
        var subAgentTimeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
        var prefillTimeout = TimeSpan.FromSeconds(
            profile.PrefillTimeoutSeconds ?? _subAgentConfig.PrefillTimeoutSeconds);
        var noProgressTimeout = TimeSpan.FromSeconds(_subAgentConfig.NoProgressTimeoutSeconds);
        var subAgentScopeId = !string.IsNullOrWhiteSpace(context.SessionId)
            ? $"{context.SessionId}/subagent/{definition.Name}/{runId}"
            : $"subagent/{definition.Name}/{runId}";
        var scopeId = new SubAgentScopeId(subAgentScopeId);

        // Spawn as child of the session actor via the context factory
        var props = SubAgentActor.CreateProps(
            definition,
            chatClient,
            _toolAccessPolicy,
            _approvalService,
            SubAgentMaxToolIterations,
            _sessionMetrics);
        var actorName = $"subagent-{definition.Name}-{runId}";
        IActorRef subAgent;
        try
        {
            subAgent = (IActorRef)await context.SpawnChildActor(props, actorName, ct);
        }
        catch (Exception ex)
        {
            // The child actor was never created (session actor ActorOf failed or the
            // spawn ask timed out). Record it to the session transcript before the
            // exception propagates to the tool pipeline.
            SubAgentSpawnBreadcrumbs.ChildSpawnFailed(_logger, context, profile.Name, runId, ex);
            // Balance the IsStarted=true notification above: the non-streaming path
            // (activitySink is null) relies solely on OnSubAgentActivity, so without
            // a terminal event the session UI shows a sub-agent stuck in "Started".
            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
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
                    Task = task,
                    RuntimeContext = runtimeContext,
                    Timeout = subAgentTimeout,
                    PrefillTimeout = prefillTimeout,
                    NoProgressTimeout = noProgressTimeout,
                    SessionScopeId = subAgentScopeId,
                    Audience = context.Audience,
                    Boundary = context.Boundary,
                    ChannelType = context.ChannelType,
                    DefaultDeliveryTarget = context.DefaultDeliveryTarget,
                    RequestedDeliveryTarget = context.RequestedDeliveryTarget,
                    ModelInputModalities = context.ModelInputModalities,
                    ParentSessionDirectory = context.SessionDirectory,
                    ParentProjectDirectory = context.ProjectDirectory,
                    ParentCwd = context.ResolveShellCwd(null),
                    Cancellation = ct,
                    ApprovalBridge = context.ApprovalBridge,
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

            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = result.Success,
                Outcome = result.Outcome,
                OutcomeReason = result.OutcomeReason,
                Duration = sw.Elapsed,
                Findings = result.Findings
            });

            SubAgentSpawnBreadcrumbs.Completed(_logger, context, profile.Name, runId, result.Success, sw.ElapsedMilliseconds);

            return result with
            {
                RunId = runId,
                ScopeId = scopeId
            };
        }
        catch (Exception ex)
        {
            sw.Stop();

            TryStopSubAgent(subAgent);

            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
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
                Success = false,
                Output = $"Subagent error: {ex.Message}",
                AgentName = new AgentName(profile.Name),
                Outcome = SubAgentRunOutcome.Failed,
                OutcomeReason = SubAgentOutcomeReason.SpawnError,
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

    private IReadOnlyList<INetclawTool> ResolveTools(SubAgentProfile profile, ToolExecutionContext context)
    {
        // Sub-agents inherit the parent session's runtime tool policy. Agent
        // definition tool metadata is advisory only; the only static
        // sub-agent-specific filter denies recursive spawn_agent delegation.
        var candidates = _toolRegistry.GetAllRegistrations().Select(r => r.Tool);
        var tools = new List<INetclawTool>();
        foreach (var tool in candidates)
        {
            if (SubAgentToolPolicy.IsAllowedForSubAgent(tool.Name))
            {
                tools.Add(tool);
            }
            else
            {
                SubAgentSpawnBreadcrumbs.ToolDenied(_logger, context, profile.Name, tool.Name);
            }
        }

        return _toolAccessPolicy.FilterDiscoverableTools(tools, context);
    }

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

    private string? ResolveProjectInstructions(ToolExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectDirectory))
            return null;

        return _promptProvider.GetProjectInstructions(context.Audience, context.ProjectDirectory);
    }

    private string? ResolveOperatingRules(ToolExecutionContext context)
    {
        // Sub-agents inherit the embedded AGENTS.md operating rules from the
        // parent session's trust audience. This gives them the same safety,
        // grounding, and policy constraints as main agents (Resource Hard Deny,
        // search citation policy, grounding rules, "I don't know" policy, etc.)
        // without exposing personal identity layers (SOUL.md, TOOLING.md).
        return _promptProvider.GetOperatingRules(context.Audience);
    }
}
