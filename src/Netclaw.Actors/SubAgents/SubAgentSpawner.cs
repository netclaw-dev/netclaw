using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
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
    private readonly ILogger<SubAgentSpawner> _logger;

    public SubAgentSpawner(
        IChatClientProvider chatClientProvider,
        ToolRegistry toolRegistry,
        ILogger<SubAgentSpawner> logger)
    {
        _chatClientProvider = chatClientProvider;
        _toolRegistry = toolRegistry;
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
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        if (context.SpawnChildActor is null)
        {
            _logger.LogWarning("SubAgent [{AgentName}] cannot spawn — no session context available", profile.Name);
            return new SubAgentResult
            {
                Success = false,
                Output = $"Cannot spawn subagent '{profile.Name}': no session context available.",
                AgentName = profile.Name
            };
        }

        var tools = ResolveTools(profile);
        if (tools.Count == 0)
        {
            _logger.LogWarning("SubAgent [{AgentName}] has no resolvable tools — cannot spawn", profile.Name);
            return new SubAgentResult
            {
                Success = false,
                Output = $"Cannot spawn subagent '{profile.Name}': none of its tools are currently available.",
                AgentName = profile.Name
            };
        }

        var definition = new SubAgentDefinition
        {
            Name = profile.Name,
            SystemPrompt = profile.SystemPrompt,
            Tools = tools,
            ModelRole = profile.ModelRole,
            EmitStructuredFindings = profile.EmitStructuredFindings
        };

        var runId = Guid.NewGuid().ToString("N");

        // Notify session that subagent is starting
        context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
        {
            RunId = runId,
            AgentName = definition.Name,
            IsStarted = true,
            ToolCount = tools.Count
        });

        var chatClient = _chatClientProvider.GetClient(definition.ModelRole);
        var subAgentTimeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
        var subAgentScopeId = !string.IsNullOrWhiteSpace(context.SessionId)
            ? $"{context.SessionId}/subagent/{definition.Name}/{runId}"
            : $"subagent/{definition.Name}/{runId}";

        // Spawn as child of the session actor via the context factory
        var props = SubAgentActor.CreateProps(definition, chatClient);
        var actorName = $"subagent-{definition.Name}-{runId}";
        var subAgent = (IActorRef)await context.SpawnChildActor(props, actorName, ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await subAgent.Ask<SubAgentResult>(
                new RunSubAgent
                {
                    Task = task,
                    Timeout = subAgentTimeout,
                    SessionScopeId = subAgentScopeId,
                    Cancellation = ct
                },
                timeout: subAgentTimeout.Add(TimeSpan.FromSeconds(5)),
                cancellationToken: ct);

            sw.Stop();

            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name,
                IsStarted = false,
                Success = result.Success,
                Duration = sw.Elapsed,
                Findings = result.Findings
                    .Select(f => new SubAgentFindingCandidate
                    {
                        Shape = f.Shape,
                        Title = f.Title,
                        Content = f.Content,
                        Kind = f.Kind,
                        Domain = f.Domain,
                        Sensitivity = f.Sensitivity,
                        RecallMode = f.RecallMode,
                        UpdateSemantics = f.UpdateSemantics,
                        Confidence = f.Confidence,
                        Durability = f.Durability,
                        Reusability = f.Reusability,
                        Evidence = f.Evidence,
                        FreshnessAtMs = f.FreshnessAtMs
                    })
                    .ToArray()
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
                AgentName = definition.Name,
                IsStarted = false,
                Success = false,
                Duration = sw.Elapsed
            });

            _logger.LogError(ex, "SubAgent [{AgentName}] spawn failed", profile.Name);
            return new SubAgentResult
            {
                Success = false,
                Output = $"Subagent error: {ex.Message}",
                AgentName = profile.Name
            };
        }
    }

    private IReadOnlyList<INetclawTool> ResolveTools(SubAgentProfile profile)
    {
        var tools = new List<INetclawTool>();
        foreach (var name in profile.ToolNames)
        {
            if (profile.Visibility == SubAgentVisibility.UserFacing
                && !SubAgentToolPolicy.IsAllowedForUserFacing(name))
            {
                _logger.LogWarning(
                    "SubAgent [{AgentName}] references tool '{ToolName}' which is not allowed for user-facing agents — skipping",
                    profile.Name,
                    name);
                continue;
            }

            var tool = _toolRegistry.GetByName(name);
            if (tool is not null)
            {
                tools.Add(tool);
            }
            else
            {
                _logger.LogWarning(
                    "SubAgent [{AgentName}] references tool '{ToolName}' which is not registered — skipping",
                    profile.Name, name);
            }
        }

        return tools;
    }

    private static void TryStopSubAgent(IActorRef subAgent)
    {
        subAgent.Tell(PoisonPill.Instance);
    }
}
