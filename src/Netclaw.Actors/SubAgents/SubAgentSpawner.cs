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

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Shared utility that encapsulates the subagent spawn-ask-report lifecycle.
/// Resolves tools by name, spawns a <see cref="SubAgentActor"/> as a child of the
/// owning session actor, awaits results, and emits observability notifications.
/// Singleton — registered in DI.
/// </summary>
public sealed class SubAgentSpawner
{
    private readonly IChatClientProvider _chatClientProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly ILogger<SubAgentSpawner> _logger;

    public SubAgentSpawner(
        IChatClientProvider chatClientProvider,
        ToolRegistry toolRegistry,
        ToolAccessPolicy toolAccessPolicy,
        IToolApprovalService? approvalService,
        ISystemPromptProvider promptProvider,
        ILogger<SubAgentSpawner> logger)
    {
        _chatClientProvider = chatClientProvider;
        _toolRegistry = toolRegistry;
        _toolAccessPolicy = toolAccessPolicy;
        _approvalService = approvalService;
        _promptProvider = promptProvider;
        _logger = logger;
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
        if (context.SpawnChildActor is null)
        {
            _logger.LogWarning("SubAgent [{AgentName}] cannot spawn — no session context available", profile.Name);
            activitySink?.TryComplete();
            return new SubAgentResult
            {
                Success = false,
                Output = $"Cannot spawn subagent '{profile.Name}': no session context available.",
                AgentName = new AgentName(profile.Name)
            };
        }

        var tools = ResolveTools(profile);
        if (tools.Count == 0)
        {
            _logger.LogWarning("SubAgent [{AgentName}] has no resolvable tools — cannot spawn", profile.Name);
            activitySink?.TryComplete();
            return new SubAgentResult
            {
                Success = false,
                Output = $"Cannot spawn subagent '{profile.Name}': none of its tools are currently available.",
                AgentName = new AgentName(profile.Name)
            };
        }

        var definition = new SubAgentDefinition
        {
            Name = new AgentName(profile.Name),
            SystemPrompt = AppendSystemPromptOverlay(profile.SystemPrompt, systemPromptOverlay),
            Tools = tools,
            ModelRole = profile.ModelRole,
            EmitStructuredFindings = profile.EmitStructuredFindings,
            ProjectInstructions = ResolveProjectInstructions(context)
        };

        var runId = Guid.NewGuid().ToString("N");

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
        var subAgentScopeId = !string.IsNullOrWhiteSpace(context.SessionId)
            ? $"{context.SessionId}/subagent/{definition.Name}/{runId}"
            : $"subagent/{definition.Name}/{runId}";

        // Spawn as child of the session actor via the context factory
        var props = SubAgentActor.CreateProps(definition, chatClient, _toolAccessPolicy, _approvalService);
        var actorName = $"subagent-{definition.Name}-{runId}";
        var subAgent = (IActorRef)await context.SpawnChildActor(props, actorName, ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await subAgent.Ask<SubAgentResult>(
                new RunSubAgent
                {
                    Task = task,
                    RuntimeContext = runtimeContext,
                    Timeout = subAgentTimeout,
                    SessionScopeId = subAgentScopeId,
                    Audience = context.Audience,
                    Boundary = context.Boundary,
                    ChannelType = context.ChannelType,
                    ParentSessionDirectory = context.SessionDirectory,
                    ParentProjectDirectory = context.ProjectDirectory,
                    ParentCwd = context.ResolveShellCwd(null),
                    Cancellation = ct,
                    ApprovalBridge = context.ApprovalBridge,
                    // Null for non-streaming callers such as routed skills and
                    // the legacy ExecuteAsync path. Streaming spawn_agent calls
                    // pass a real sink so parent tool liveness sees progress.
                    ActivitySink = activitySink
                },
                // No Ask timeout: a healthy run is inactivity-bounded, not
                // wall-clock-bounded (like the parent LLM session), so any finite
                // ceiling could pre-empt a legitimately long run. A stalled run
                // self-completes via the sub-agent's inactivity watchdog; a wedged
                // run is cancelled through ct — the spawning tool call's token,
                // governed by the parent's per-call watchdog.
                timeout: Timeout.InfiniteTimeSpan,
                cancellationToken: ct);

            sw.Stop();

            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name.Value,
                IsStarted = false,
                Success = result.Success,
                Duration = sw.Elapsed,
                Findings = result.Findings
            });

            _logger.LogInformation(
                "SubAgent [{AgentName}] completed (success={Success}, duration={Duration}ms)",
                profile.Name, result.Success, sw.ElapsedMilliseconds);

            return result;
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
                Duration = sw.Elapsed
            });

            _logger.LogError(ex, "SubAgent [{AgentName}] spawn failed", profile.Name);
            return new SubAgentResult
            {
                Success = false,
                Output = $"Subagent error: {ex.Message}",
                AgentName = new AgentName(profile.Name)
            };
        }
        finally
        {
            // Terminate the streaming caller's activity reader even on failure.
            activitySink?.TryComplete();
        }
    }

    private IReadOnlyList<INetclawTool> ResolveTools(SubAgentProfile profile)
    {
        // When no tools specified, inherit all registered tools (matches Claude Code behavior).
        // When tools are specified, use them as a whitelist to limit access.
        IEnumerable<INetclawTool> candidates;
        if (profile.ToolNames.Count == 0)
        {
            candidates = _toolRegistry.GetAllRegistrations().Select(r => r.Tool);
        }
        else
        {
            var resolved = new List<INetclawTool>();
            foreach (var name in profile.ToolNames)
            {
                var tool = _toolRegistry.GetByName(name);
                if (tool is not null)
                {
                    resolved.Add(tool);
                }
                else
                {
                    _logger.LogWarning(
                        "SubAgent [{AgentName}] references tool '{ToolName}' which is not registered — skipping",
                        profile.Name, name);
                }
            }

            candidates = resolved;
        }

        // Sub-agents inherit the parent session's runtime tool policy; the only
        // static sub-agent-specific filter denies recursive spawn_agent delegation.
        var tools = new List<INetclawTool>();
        foreach (var tool in candidates)
        {
            if (SubAgentToolPolicy.IsAllowedForSubAgent(tool.Name))
            {
                tools.Add(tool);
            }
            else
            {
                _logger.LogDebug(
                    "SubAgent [{AgentName}] tool '{ToolName}' denied by SubAgentToolPolicy",
                    profile.Name, tool.Name);
            }
        }

        return tools;
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
}
