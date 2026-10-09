// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Prepares a child run under the parent authority and requests durable owner acceptance.
/// The session owner controls the child lifetime and terminal delivery.
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

    internal abstract record ChildPreparation
    {
        internal sealed record Ready(PreparedChildRun Run) : ChildPreparation;
        internal sealed record Rejected(EnrichedChildRunResult Result) : ChildPreparation;
    }

    internal async Task<ChildPreparation> PrepareRunAsync(
        SubAgentProfile profile, string task, string? runtimeContext, ToolInvocationContext context,
        CancellationToken ct, string? systemPromptOverlay)
    {
        // Parent-side spawn breadcrumbs — each event is fanned out to daemon.log/Seq and
        // the parent's session.log from one place (see SubAgentSpawnBreadcrumbs), covering
        // request → outcome plus early rejections that happen before the child even exists.
        SubAgentSpawnBreadcrumbs.SpawnRequested(_logger, context, profile.Name, task.Length);

        if (context.SpawnChildActor is null)
        {
            SubAgentSpawnBreadcrumbs.NoSessionContext(_logger, context, profile.Name);
            return new ChildPreparation.Rejected(new EnrichedChildRunResult.OtherRun(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnUnavailable),
                Output = $"Cannot spawn subagent '{profile.Name}': no session context available.",
                AgentName = new AgentName(profile.Name)
            }));
        }

        var exposure = ResolveTools(context);
        if (exposure.Tools.Count == 0)
        {
            SubAgentSpawnBreadcrumbs.NoToolsAvailable(_logger, context, profile.Name);
            return new ChildPreparation.Rejected(new EnrichedChildRunResult.OtherRun(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.NoToolsAvailable),
                Output = $"Cannot spawn subagent '{profile.Name}': no tools are available under the parent audience policy.",
                AgentName = new AgentName(profile.Name)
            }));
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
        var parentStorage = context.SessionStorage
            ?? throw new InvalidOperationException("A subagent run requires resolved session storage.");
        var childStorage = parentStorage.ForChild(runId, scopeId);
        CreateChildLogTarget(childStorage.LogPath.Value);
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
            return new ChildPreparation.Rejected(new EnrichedChildRunResult.OtherRun(CancelledResult(definition.Name, runId, scopeId)));
        }
        catch (Exception ex) when (!FatalExceptionPolicy.IsFatal(ex))
        {
            SubAgentSpawnBreadcrumbs.RunFailed(_logger, context, profile.Name, runId, ex);
            return new ChildPreparation.Rejected(new EnrichedChildRunResult.OtherRun(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnError),
                Output = $"Subagent error: {ex.Message}",
                AgentName = definition.Name,
                RunId = runId,
                ScopeId = scopeId
            }));
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
        var execution = new RunSubAgent
                {
                    Scope = childScope,
                    Task = task,
                    RuntimeContext = runtimeContext,
                    Timeout = subAgentTimeout,
                    PrefillTimeout = prefillTimeout,
                    NoProgressTimeout = noProgressTimeout,
                    Cancellation = ct,
                    ActivitySink = null
                };
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            profile.Name, definition.SystemPrompt, definition.ProjectInstructions, definition.OperatingRules,
            definition.ModelRole, definition.EmitStructuredFindings, task, runtimeContext,
            timeout = subAgentTimeout.Ticks, prefill = prefillTimeout.Ticks, noProgress = noProgressTimeout.Ticks,
            tools = exposure.Tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                .Select(static tool =>
                {
                    var function = tool.ToAITool() as Microsoft.Extensions.AI.AIFunctionDeclaration
                        ?? throw new InvalidDataException("A child tool requires its canonical function definition.");
                    return new { tool.Name, function.Description, schema = function.JsonSchema.GetRawText() };
                }).ToArray()
        }))));
        return new ChildPreparation.Ready(new PreparedChildRun
        {
            Props = props, RunId = runId, AgentName = definition.Name, ToolCount = exposure.Tools.Count,
            ArgumentsDigest = digest, Execution = execution
        });
    }

    internal async Task<string> StartRunAsync(
        SubAgentProfile profile, string task, string? runtimeContext, ToolInvocationContext context,
        CancellationToken ct, string? systemPromptOverlay)
    {
        var preparation = await PrepareRunAsync(profile, task, runtimeContext, context, ct, systemPromptOverlay).ConfigureAwait(false);
        if (preparation is ChildPreparation.Rejected rejected)
        {
            context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure));
            return rejected.Result.Response.Output;
        }
        var prepared = ((ChildPreparation.Ready)preparation).Run;
        var factory = context.SpawnChildActor ?? throw new InvalidOperationException("A child start requires its owning session adapter.");
        var response = await factory(prepared, $"child-run-{prepared.RunId.Value}", ct).ConfigureAwait(false);
        switch (response)
        {
            case ChildStartReply.Accepted accepted:
                accepted.Validate(prepared);
                if (accepted.Run.StartKey.SessionId.Value != context.SessionId
                    || accepted.Run.OriginalContext.Audience != context.Audience
                    || accepted.Run.OriginalContext.Boundary != context.Boundary
                    || accepted.Run.OriginalContext.ChannelType != context.ChannelType)
                    throw new InvalidDataException("The child acceptance differs from its invoking session authority.");
                context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
                return JsonSerializer.Serialize(new
                {
                    run_id = accepted.Run.RunId.Value, scope_id = accepted.Run.ScopeId.Value,
                    state = accepted.State.ToString(), control_tool = "check_agent_run"
                });
            case ChildStartReply.Conflict:
                context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
                return "Error: start_conflict. The start key already identifies different canonical arguments.";
            default:
                throw new InvalidDataException("The session owner returned a noncanonical child acceptance.");
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

    internal static async Task<SubAgentResult> EnrichWorkingContextResultAsync(
        SubAgentResult result,
        WorkingContextSnapshot initialSnapshot,
        TrustAudience audience,
        IWorkingContextSnapshotProvider snapshots,
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
        var finalSnapshot = await snapshots.CreateAsync(
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
