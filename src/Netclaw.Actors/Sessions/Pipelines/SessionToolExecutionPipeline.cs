// -----------------------------------------------------------------------
// <copyright file="SessionToolExecutionPipeline.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Collections.Frozen;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Result of a single tool call execution, including the serialized message,
/// file attachments, and any sub-agent activity.
/// <paramref name="StartedBackgroundJob"/> is set when the call was routed to
/// background execution, so the session actor can track the job in
/// <c>SessionState.ActiveBackgroundJobs</c>.
/// </summary>
internal sealed record ToolCallResult(
    SerializableChatMessage Message,
    IReadOnlyList<SerializableMediaReference> ModelInputMediaReferences,
    IReadOnlyList<FileAttachmentInfo> FileAttachments,
    IReadOnlyList<CompletedSubAgentRun> CompletedSubAgentRuns,
    IReadOnlyList<AcceptedSubAgentFinding> AcceptedSubAgentFindings,
    Jobs.ActiveJobInfo? StartedBackgroundJob = null);

internal sealed record ModelInputMaterializationResult(
    IReadOnlyList<SerializableMediaReference> MediaReferences,
    int RequestedCount);

internal sealed class ModelInputBatchBudget(long maxBytes)
{
    private readonly object _sync = new();
    private long _reservedBytes;

    public bool TryReserve(long sizeBytes)
    {
        lock (_sync)
        {
            if (_reservedBytes + sizeBytes > maxBytes)
                return false;

            _reservedBytes += sizeBytes;
            return true;
        }
    }

    public void Release(long sizeBytes)
    {
        lock (_sync)
            _reservedBytes = Math.Max(0, _reservedBytes - sizeBytes);
    }
}

internal sealed class ToolApprovalRequests
{
    public ToolApprovalRequests(
        IApprovalChannel channel,
        Action<ToolInteractionRequestDispatch> emitRequest,
        ToolExecutionTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(emitRequest);
        ArgumentNullException.ThrowIfNull(timeout);

        Channel = channel;
        EmitRequest = emitRequest;
        Timeout = timeout;
    }

    public IApprovalChannel Channel { get; }
    public Action<ToolInteractionRequestDispatch> EmitRequest { get; }
    public ToolExecutionTimeout Timeout { get; }
}

internal abstract record BackgroundJobDispatch
{
    private BackgroundJobDispatch()
    {
    }

    public sealed record Unavailable : BackgroundJobDispatch;

    public sealed record Available : BackgroundJobDispatch
    {
        public Available(IActorRef manager)
        {
            ArgumentNullException.ThrowIfNull(manager);
            Manager = manager;
        }

        public IActorRef Manager { get; }
    }
}

internal sealed class SessionToolRunEnvironment
{
    private IReadOnlyList<string> _recentFiles = [];

    public required string SessionDirectory { get; init; }
    public required InlineOutputBudget InlineOutputBudget { get; init; }
    public required Func<object, string, CancellationToken, Task<object>> SpawnChildActor { get; init; }
    public ModelModality ModelInputModalities { get; init; } = ModelModality.Text;
    public string? ProjectDirectory { get; init; }
    public IReadOnlyList<string> RecentFiles
    {
        get => _recentFiles;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _recentFiles = Array.AsReadOnly(value.ToArray());
        }
    }
}

internal sealed class SessionToolBatch
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoApprovalPreSeed
        = new Dictionary<string, IReadOnlyList<string>>().ToFrozenDictionary();
    private static readonly IReadOnlyDictionary<string, ApprovalDecision> NoDecisionOverrides
        = new Dictionary<string, ApprovalDecision>().ToFrozenDictionary();
    private IReadOnlyList<FunctionCallContent> _toolCalls = [];
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _oneTimeApprovalPreSeed = NoApprovalPreSeed;
    private IReadOnlyDictionary<string, ApprovalDecision> _decisionOverrides = NoDecisionOverrides;

    public SessionToolBatch(TurnContext turnContext, SessionToolRunEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(turnContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment.SessionDirectory);
        ArgumentNullException.ThrowIfNull(environment.InlineOutputBudget);
        ArgumentNullException.ThrowIfNull(environment.SpawnChildActor);

        TurnContext = turnContext;
        RunScope = new ToolRunScope
        {
            Session = new ToolSessionScope.Bound(turnContext.SessionId.Value, environment.SessionDirectory),
            Audience = turnContext.Audience,
            Boundary = turnContext.Boundary,
            ChannelType = turnContext.ChannelType?.ToWireValue(),
            DefaultDeliveryTarget = turnContext.DefaultDeliveryTarget,
            RequestedDeliveryTarget = turnContext.RequestedDeliveryTarget,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
            InlineOutputBudget = environment.InlineOutputBudget,
            ModelInputModalities = environment.ModelInputModalities,
            SpawnChildActor = environment.SpawnChildActor,
            ProjectDirectory = environment.ProjectDirectory,
            RecentFiles = environment.RecentFiles
        };
    }

    public required IReadOnlyList<FunctionCallContent> ToolCalls
    {
        get => _toolCalls;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _toolCalls = Array.AsReadOnly(value.ToArray());
        }
    }
    public TurnContext TurnContext { get; }
    public ToolRunScope RunScope { get; }
    public required ToolExecutionTimeout DefaultTimeout { get; init; }
    public required IActorRef ReplyTo { get; init; }
    public required Action<SubAgentOutput> EmitSubAgentOutput { get; init; }
    public required ToolApprovalRequests ApprovalRequests { get; init; }
    public required BackgroundJobDispatch BackgroundJobs { get; init; }
    public bool SetWorkingDirectoryAvailable { get; init; }
    public bool StreamResults { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> OneTimeApprovalPreSeed
    {
        get => _oneTimeApprovalPreSeed;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _oneTimeApprovalPreSeed = value.ToFrozenDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)Array.AsReadOnly(entry.Value.ToArray()),
                StringComparer.Ordinal);
        }
    }
    public IReadOnlyDictionary<string, ApprovalDecision> DecisionOverrides
    {
        get => _decisionOverrides;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _decisionOverrides = value.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }
    public CancellationToken CancellationToken { get; init; }

    public SessionId SessionId => TurnContext.SessionId;

    public string SessionDirectory => RunScope.Session is ToolSessionScope.Bound bound
        && !string.IsNullOrWhiteSpace(bound.SessionDirectory)
            ? bound.SessionDirectory
            : throw new InvalidOperationException("Session tool batches require a bound session directory.");

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ToolCalls);
        ArgumentNullException.ThrowIfNull(DefaultTimeout);
        ArgumentNullException.ThrowIfNull(ReplyTo);
        ArgumentNullException.ThrowIfNull(EmitSubAgentOutput);
        ArgumentNullException.ThrowIfNull(ApprovalRequests);
        ArgumentNullException.ThrowIfNull(BackgroundJobs);
        ArgumentNullException.ThrowIfNull(OneTimeApprovalPreSeed);
        ArgumentNullException.ThrowIfNull(DecisionOverrides);
    }
}

/// <summary>
/// Async pipeline for parallel tool execution. Runs on the thread pool and
/// sends results back to the session actor via <c>self.Tell()</c>.
/// </summary>
internal sealed class SessionToolExecutionPipeline
{
    private const long MaxModelInputFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes;
    private const long MaxModelInputBatchBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes;

    private readonly IToolExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _logger;

    public SessionToolExecutionPipeline(
        IToolExecutor executor,
        TimeProvider timeProvider,
        ILoggingAdapter logger)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _executor = executor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(SessionToolBatch batch)
    {
        try
        {
            batch.Validate();
            // Execute all tool calls in parallel. Calls are not always
            // independent -- e.g. two file_edit calls on the same file -- so
            // file-mutating tools serialize their read-modify-write per target
            // path via FileMutationGate to avoid lost-update races here.
            var modelInputBudget = new ModelInputBatchBudget(MaxModelInputBatchBytes);
            var tasks = batch.ToolCalls.Select(async tc =>
            {
                var result = await ExecuteSingleToolAsync(
                    tc,
                    batch,
                    batch.OneTimeApprovalPreSeed.TryGetValue(tc.CallId, out var preSeedPatterns)
                        ? preSeedPatterns
                        : null,
                    batch.DecisionOverrides.TryGetValue(tc.CallId, out var overrideDecision)
                        ? overrideDecision
                        : null,
                    modelInputBudget);
                if (batch.StreamResults)
                    batch.ReplyTo.Tell(new ToolExecutionSingleCompleted(result));
                return result;
            });
            var results = await Task.WhenAll(tasks);

            if (batch.StreamResults)
            {
                batch.ReplyTo.Tell(new ToolExecutionBatchCompleted());
                return;
            }

            var fileAttachments = results.SelectMany(r => r.FileAttachments).ToList();
            var modelInputMediaReferences = results.SelectMany(r => r.ModelInputMediaReferences).ToList();
            batch.ReplyTo.Tell(new ToolExecutionCompleted
            {
                ToolResults = [.. results.Select(r => r.Message)],
                ModelInputMediaReferences = modelInputMediaReferences,
                FileAttachments = fileAttachments,
                CompletedSubAgentRuns = [.. results.SelectMany(r => r.CompletedSubAgentRuns)],
                AcceptedSubAgentFindings = [.. results.SelectMany(r => r.AcceptedSubAgentFindings)],
                StartedBackgroundJobs = [.. results.Where(r => r.StartedBackgroundJob is not null).Select(r => r.StartedBackgroundJob!)]
            });
        }
        catch (TimeoutException ex)
        {
            batch.ReplyTo.Tell(new ToolExecutionFailed { Cause = ex });
        }
        catch (OperationCanceledException ex)
        {
            // The tool-execution token is cancelled both by caller (turn/user) supersede
            // and by the session's own timeout watchdog; surface either as a failed
            // batch (the watchdog message is the authoritative one).
            batch.ReplyTo.Tell(new ToolExecutionFailed
            {
                Cause = new TimeoutException(
                    $"Tool execution exceeded timeout of {batch.DefaultTimeout.Value.TotalSeconds:F0}s",
                    ex)
            });
        }
        catch (Exception ex)
        {
            batch.ReplyTo.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    private async Task<ToolCallResult> ExecuteSingleToolAsync(
        FunctionCallContent tc,
        SessionToolBatch batch,
        IReadOnlyList<string>? oneTimeApprovalPreSeed,
        ApprovalDecision? decisionOverride,
        ModelInputBatchBudget modelInputBudget)
    {
        // Single execution-preflight seam, shared with the sub-agent path via
        // IToolExecutor.InterpretToolCall: validate the ORIGINAL arguments (parse
        // sentinel, invalid/ambiguous meta values, unrecognized keys) and, on
        // success, extract meta + strip meta keys.
        var interpretation = _executor.InterpretToolCall(tc);
        if (interpretation.Rejection is { } rejection)
        {
            return new ToolCallResult(new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Tool,
                Content = rejection.Message,
                ToolCallId = new ToolCallId(tc.CallId),
                Name = tc.Name
            }, [], [], [], []);
        }

        var meta = interpretation.Meta;
        tc = interpretation.Cleaned;

        // The agent's per-call timeout hint is honored as requested; when absent
        // the inherited default (SessionConfig.ToolExecutionTimeout) applies.
        // ExtractFrom only yields a positive hint, so there is nothing to clamp.
        var timeout = batch.DefaultTimeout.Value;
        if (meta?.TimeoutHintSeconds is { } hintSeconds)
            timeout = TimeSpan.FromSeconds(hintSeconds);

        var sw = Stopwatch.StartNew();
        string resultText;
        var completedRuns = new List<CompletedSubAgentRun>();
        var acceptedFindings = new List<AcceptedSubAgentFinding>();
        var outputs = new ToolExecutionOutputs(info =>
        {
            if (info.IsStarted)
            {
                batch.EmitSubAgentOutput(new SubAgentOutput
                {
                    SessionId = batch.SessionId,
                    TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    AgentName = new SubAgents.AgentName(info.AgentName),
                    Phase = Netclaw.Actors.SubAgents.SubAgentPhase.Started,
                    ToolCount = info.ToolCount,
                    Success = info.Success,
                    Duration = info.Duration
                });
            }

            if (!info.IsStarted)
            {
                string? decision = null;
                string? reason = null;

                if (info.Success && info.Findings.Count == 1)
                {
                    var singleDecision = ReviewSubAgentFinding(info.Findings[0], batch.SessionId);
                    decision = singleDecision.Decision.ToWireValue();
                    reason = singleDecision.Reason;
                }

                completedRuns.Add(new CompletedSubAgentRun
                {
                    RunId = info.RunId,
                    AgentName = new SubAgents.AgentName(info.AgentName),
                    Completion = ChildRunCompletion.FromReportedOutcome(
                        info.Outcome ?? (info.Success ? SubAgentRunOutcome.Completed : SubAgentRunOutcome.Failed),
                        info.OutcomeReason,
                        info.WorkingContext),
                    Duration = info.Duration,
                    FindingsCount = info.Findings.Count,
                    MemoryDecision = decision,
                    MemoryDecisionReason = reason,
                });
            }

            if (!info.IsStarted && info.Success)
            {
                foreach (var finding in info.Findings)
                {
                    var findingDecision = ReviewSubAgentFinding(finding, batch.SessionId);
                    acceptedFindings.Add(new AcceptedSubAgentFinding
                    {
                        RunId = info.RunId,
                        AgentName = new SubAgents.AgentName(info.AgentName),
                        Duration = info.Duration,
                        Shape = finding.Shape,
                        Title = finding.Title,
                        Content = finding.Content,
                        Kind = finding.Kind,
                        Sensitivity = finding.Sensitivity,
                        RecallMode = finding.RecallMode,
                        UpdateSemantics = finding.UpdateSemantics,
                        Confidence = finding.Confidence,
                        Durability = finding.Durability,
                        Reusability = finding.Reusability,
                        Evidence = finding.Evidence,
                        FreshnessAtMs = finding.FreshnessAtMs,
                        Decision = findingDecision.Decision,
                        DecisionReason = findingDecision.Reason
                    });
                }
            }
        });
        var approvalBridge = CanRequestInteractiveApproval(batch.TurnContext)
            ? new ParentSessionApprovalBridge(
                batch.ApprovalRequests.Channel,
                batch.ApprovalRequests.EmitRequest,
                batch.SessionId,
                tc.CallId,
                batch.TurnContext.RequesterSenderId,
                batch.TurnContext.RequesterPrincipal,
                batch.TurnContext.HasAdoptedContext,
                batch.TurnContext.HasThirdPartyAdoptedContext,
                batch.TurnContext.AdoptedSpeakerIds)
            : null;
        var callScope = batch.RunScope with
        {
            InteractiveApproval = approvalBridge is null
                ? new InteractiveApprovalCapability.Unavailable()
                : new InteractiveApprovalCapability.Available(approvalBridge)
        };
        var context = new ToolExecutionContext(
            callScope,
            new ToolExecutionTimeout(timeout),
            outputs);

        // Re-drive of an ApprovedOnce approval: the user already clicked
        // "approve once" before the session passivated, but there is no
        // persisted grant to satisfy the gate on the cold-recovered re-drive.
        // Pre-seed the one-time approval bypass for exactly this call id so the
        // gate passes once without emitting a duplicate approval prompt. The
        // bypass is still tool-name- and pattern-matched inside the gate
        // (DispatchingToolExecutor.IsOneTimeApprovalSatisfied) and the pipeline
        // clears it after the attempt — it cannot leak to any other call.
        if (oneTimeApprovalPreSeed is not null)
            context.Approval.SeedOneTimeApproval(tc.Name, oneTimeApprovalPreSeed);
        try
        {
            if (decisionOverride is ApprovalDecision.Denied or ApprovalDecision.TimedOut)
            {
                sw.Stop();
                resultText = decisionOverride == ApprovalDecision.TimedOut
                    ? "Tool access denied: approval_timed_out"
                    : $"Tool access denied: approval_denied_by_user ({tc.Name} requires interactive approval and the user declined it)";

                var deniedMessage = new SerializableChatMessage
                {
                    Role = Protocol.ChatRole.Tool,
                    Content = resultText,
                    ToolCallId = new ToolCallId(tc.CallId),
                    Name = tc.Name
                };

                return new ToolCallResult(deniedMessage, [], [], [], []);
            }

            if (meta is { Background: true })
            {
                if (!string.Equals(tc.Name, Tools.ShellTool.ToolName, StringComparison.Ordinal))
                {
                    _logger.Warning(
                        "Tool {ToolName} (call {CallId}) requested background execution — " +
                        "only shell_execute supports background mode; executing synchronously",
                        tc.Name, tc.CallId);
                }
                else if (batch.BackgroundJobs is BackgroundJobDispatch.Unavailable)
                {
                    _logger.Warning(
                        "Tool {ToolName} (call {CallId}) requested background execution — " +
                        "no background job manager available; executing synchronously",
                        tc.Name, tc.CallId);
                }
                else if (batch.BackgroundJobs is BackgroundJobDispatch.Available backgroundJobs)
                {
                    await _executor.AuthorizeAsync(tc, context, batch.CancellationToken);
                    sw.Stop();
                    return await RouteToBackgroundJobAsync(
                        tc, batch,
                        meta, backgroundJobs.Manager,
                        // Honor the agent's requested timeout; when absent, no
                        // kill timer is armed — a background job is a detached
                        // process with no completion expectation, reaped by its
                        // own exit, cancellation, or session passivation.
                        meta.TimeoutHintSeconds ?? 0);
                }
            }

            resultText = await ExecuteToolAttemptAsync(
                _executor, tc, context, timeout, _timeProvider, batch.CancellationToken);
            sw.Stop();

        }
        catch (ToolApprovalRequiredException approvalEx)
        {
            if (!CanRequestInteractiveApproval(batch.TurnContext))
            {
                sw.Stop();
                resultText = $"Tool requires approval but no interactive approval requester is available: {approvalEx.ApprovalContext.ToolName}";

                return new ToolCallResult(new SerializableChatMessage
                {
                    Role = Protocol.ChatRole.Tool,
                    Content = resultText,
                    ToolCallId = new ToolCallId(tc.CallId),
                    Name = tc.Name
                }, [], context.Outputs.FileAttachments, completedRuns, acceptedFindings);
            }

            // Mid-turn approval pause: emit request to channel, block on TCS
            var ctx = approvalEx.ApprovalContext;
            var waitTask = batch.ApprovalRequests.Channel.WaitForApprovalAsync(
                new ToolCallId(tc.CallId),
                batch.ApprovalRequests.Timeout.Value,
                batch.CancellationToken);

            batch.ApprovalRequests.EmitRequest(new ToolInteractionRequestDispatch(new ToolInteractionRequest
            {
                SessionId = batch.SessionId,
                Kind = "approval",
                CallId = new ToolCallId(tc.CallId),
                ToolName = new ToolName(ctx.ToolName),
                DisplayText = ctx.DisplayText,
                RequesterSenderId = batch.TurnContext.RequesterSenderId,
                RequesterPrincipal = batch.TurnContext.RequesterPrincipal,
                HasAdoptedContext = batch.TurnContext.HasAdoptedContext,
                HasThirdPartyAdoptedContext = batch.TurnContext.HasThirdPartyAdoptedContext,
                AdoptedSpeakerIds = batch.TurnContext.AdoptedSpeakerIds,
                PersistedAdoptedContext = batch.TurnContext.HasAdoptedContext,
                Patterns = ctx.Patterns,
                CandidateVerbs = ctx.CandidateVerbs,
                Candidates = ctx.Candidates ?? [],
                Cwd = ctx.Cwd,
                IsMessy = ctx.IsMessy,
                Options = ctx.Options
                    .Select(o => new ToolInteractionOption(o.Key, o.Label))
                    .ToList()
            }, PersistApprovalState: true));

            var decision = await waitTask;

            sw.Stop();

            if (decision.IsApprovalGrant())
            {
                // Retry execution now that approval is granted. Seed the one-time
                // bypass for the just-approved call regardless of scope
                // (https://github.com/netclaw-dev/netclaw/issues/1802). Broader
                // scopes (session/always) DO get a durable grant recorded by the
                // session actor, but that grant can legitimately not cover every
                // candidate: a piped command's standalone verbs (base64, head) have
                // no path argument and so are never persisted directory-scoped
                // (by design). Without the transient bypass, the immediate retry
                // re-hits the gate and fails a call the user just approved. This
                // matches the sub-agent loop (SubAgentActor), which seeds for every
                // approved scope. The bypass is per-call, pattern-scoped, and
                // cleared after the attempt, so it cannot leak to any other call.
                context.Approval.SeedOneTimeApproval(tc.Name, ctx.Patterns);

                sw = Stopwatch.StartNew();
                if (meta is { Background: true }
                    && string.Equals(tc.Name, Tools.ShellTool.ToolName, StringComparison.Ordinal)
                    && batch.BackgroundJobs is BackgroundJobDispatch.Available backgroundJobs)
                {
                    await _executor.AuthorizeAsync(tc, context, batch.CancellationToken);
                    sw.Stop();
                    return await RouteToBackgroundJobAsync(
                        tc, batch,
                        meta, backgroundJobs.Manager,
                        // Honor the agent's requested timeout; when absent, no
                        // kill timer is armed — a background job is a detached
                        // process with no completion expectation, reaped by its
                        // own exit, cancellation, or session passivation.
                        meta.TimeoutHintSeconds ?? 0);
                }

                resultText = await ExecuteToolAttemptAsync(
                    _executor, tc, context, timeout, _timeProvider, batch.CancellationToken);
                sw.Stop();

            }
            else
            {
                var reason = decision == ApprovalDecision.TimedOut
                    ? "Tool access denied: approval_timed_out"
                    : $"Tool access denied: approval_denied_by_user ({tc.Name} requires interactive approval and the user declined it)";

                // When a shell call is denied because its cwd is outside both
                // session_dir and project_dir, surface a one-line hint pointing
                // the agent at set_working_directory so it can self-correct on
                // the next turn rather than re-prompting the user. Suppressed
                // for non-shell tools, timeouts, hard-deny paths, and
                // audiences that can't call set_working_directory.
                var hint = BuildSetWorkingDirectoryHint(
                    toolName: tc.Name,
                    decision: decision,
                    cwd: context.Approval.Cwd,
                    sessionDirectory: context.SessionDirectory,
                    projectDirectory: context.ProjectDirectory,
                    setWorkingDirectoryAvailable: batch.SetWorkingDirectoryAvailable);
                resultText = string.IsNullOrEmpty(hint) ? reason : $"{reason}\n{hint}";

            }
        }
        catch (ToolAccessDeniedException ex)
        {
            sw.Stop();
            resultText = $"Tool access denied: {ex.DenyReason}";

        }
        catch (OperationCanceledException) when (batch.CancellationToken.IsCancellationRequested)
        {
            // Caller (turn/user) cancellation is not a tool failure. Self-monitoring
            // tools are bounded only by ct, so this is the normal cancel path; let it
            // propagate so the turn aborts cleanly instead of feeding the model an
            // "Error executing tool: The operation was canceled." result.
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            resultText = $"Error executing tool: {ex.Message}";

        }

        var modelInputMaterialization = MaterializeModelInputFiles(
            context, batch.SessionDirectory, _logger, modelInputBudget);
        // No inline clamp here: DispatchingToolExecutor already bounds every tool
        // result to the inline budget N (and spills the overflow). Clamping again
        // would re-window the already-windowed+steered result.
        if (modelInputMaterialization.RequestedCount > modelInputMaterialization.MediaReferences.Count)
            resultText = AppendModelInputHandoffWarning(
                resultText,
                modelInputMaterialization.RequestedCount - modelInputMaterialization.MediaReferences.Count);

        var message = new SerializableChatMessage
        {
            Role = Protocol.ChatRole.Tool,
            Content = resultText,
            ToolCallId = new ToolCallId(tc.CallId),
            Name = tc.Name,
            MediaReferences = modelInputMaterialization.MediaReferences
        };

        return new ToolCallResult(
            message,
            modelInputMaterialization.MediaReferences,
            context.Outputs.FileAttachments,
            completedRuns,
            acceptedFindings);
    }

    private static async Task<string> ExecuteToolAttemptAsync(
        IToolExecutor executor,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var grantedOneTimeToolName = context.Approval.OneTimeApprovedToolName;
        var grantedOneTimePatterns = context.Approval.OneTimeApprovedPatterns;

        try
        {
            var stream = executor.ExecuteStreamAsync(toolCall, context, cancellationToken);

            // Self-monitoring tools (spawn_agent) own their liveness end to end and
            // always drive their stream to a terminal item, so the parent does not
            // supervise them at all — it drains to that terminal item under caller
            // (turn/user) cancellation only. For spawn_agent the terminal item is
            // produced by SpawnAgentTool's stream, which completes when SpawnAsync
            // returns; SpawnAsync's finally unconditionally completes the activity
            // channel, and SubAgentActor.PostStop guarantees the reply that lets
            // SpawnAsync return even on a crash. (Note: PostStop alone only unblocks
            // the spawner Ask — the terminal stream item depends on that finally
            // running.)
            if (executor.GetLivenessMode(toolCall) == ToolLivenessMode.SelfMonitoring)
                return await DrainToCompletionAsync(stream, toolCall.Name, cancellationToken);

            // Opaque tools are bounded by one wall-clock budget. A TimeProvider-driven
            // timeout token (no hand-rolled timer, no volatile) cancels the drain when the
            // budget elapses; it is per call, so a slow tool times out without affecting
            // its siblings.
            using var budgetCts = new CancellationTokenSource(timeout, timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetCts.Token);
            try
            {
                return await DrainToCompletionAsync(stream, toolCall.Name, linkedCts.Token);
            }
            catch (OperationCanceledException)
                when (budgetCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Tool '{toolCall.Name}' exceeded execution budget of {timeout.TotalSeconds:F0}s and was stopped.");
            }
        }
        finally
        {
            // One-time approvals are valid for exactly one retry attempt.
            // Clear any grant we consumed (or attempted to consume), while
            // preserving whatever baseline state existed before this call.
            if (!string.IsNullOrWhiteSpace(grantedOneTimeToolName))
            {
                if (!string.Equals(context.Approval.OneTimeApprovedToolName, grantedOneTimeToolName, StringComparison.Ordinal)
                    || !SetsEqual(context.Approval.OneTimeApprovedPatterns, grantedOneTimePatterns))
                    context.Approval.ClearOneTimeApproval();
            }
        }
    }

    /// <summary>
    /// Drains a tool's stream to its terminal completion item under the supplied
    /// cancellation token. Self-monitoring tools pass the caller (turn/user) token
    /// directly — they own their liveness; opaque tools pass a token also linked to a
    /// wall-clock budget. A stream that ends without a completion item violates the
    /// tool-call contract and fails loudly.
    /// </summary>
    private static async Task<string> DrainToCompletionAsync(
        IAsyncEnumerable<ToolCallUpdate> stream, string toolName, CancellationToken cancellationToken)
    {
        await foreach (var update in stream.WithCancellation(cancellationToken))
        {
            if (update is ToolCompletedUpdate completed)
                return completed.Result;
        }

        throw new InvalidOperationException(
            $"Tool '{toolName}' stream ended without a completion item.");
    }

    private static bool SetsEqual(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        foreach (var item in left)
        {
            if (!right.Contains(item))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reviews a sub-agent finding to decide whether it should be accepted,
    /// deferred, or rejected for memory persistence.
    /// </summary>
    internal static SubAgentFindingReviewResult ReviewSubAgentFinding(
        SubAgentFinding finding,
        SessionId sessionId)
    {
        if (string.IsNullOrWhiteSpace(finding.Title))
            return new(SubAgentFindingReviewDecision.Deferred, "missing title");

        if (string.IsNullOrWhiteSpace(finding.Content))
            return new(SubAgentFindingReviewDecision.Rejected, "empty content");

        if (finding.Shape != SubAgentFindingShape.Conclusion)
            return new(SubAgentFindingReviewDecision.Rejected, "unsupported shape");

        if (!Enum.IsDefined(finding.Durability))
            return new(SubAgentFindingReviewDecision.Deferred, "missing durability");

        if (!Enum.IsDefined(finding.Reusability))
            return new(SubAgentFindingReviewDecision.Deferred, "missing reusability");

        if (finding.RecallMode == SubAgentFindingRecallMode.Never)
            return new(SubAgentFindingReviewDecision.Rejected, "recallMode=never");

        if (!string.Equals(finding.Kind, "record", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(finding.Kind, "document", StringComparison.OrdinalIgnoreCase))
            return new(SubAgentFindingReviewDecision.Deferred, "unsupported kind");

        if (finding.Sensitivity == SubAgentFindingSensitivity.Secret
            && finding.RecallMode == SubAgentFindingRecallMode.Auto)
            return new(SubAgentFindingReviewDecision.Rejected, "secret cannot auto-recall");

        if (finding.Durability != SubAgentFindingDurability.Durable)
            return new(SubAgentFindingReviewDecision.Deferred, "insufficient durability");

        if (finding.Reusability != SubAgentFindingReusability.Reusable)
            return new(SubAgentFindingReviewDecision.Deferred, "insufficient reusability");

        if (finding.Confidence < 0.55)
            return new(SubAgentFindingReviewDecision.Deferred, "low confidence");

        return new(SubAgentFindingReviewDecision.Accepted, null);
    }

    private async Task<ToolCallResult> RouteToBackgroundJobAsync(
        FunctionCallContent tc,
        SessionToolBatch batch,
        ToolCallMeta meta,
        IActorRef backgroundJobManager,
        int timeoutSeconds)
    {
        var command = ToolArgumentHelper.GetString(tc.Arguments, "Command");
        var workingDirectory = ToolArgumentHelper.GetString(tc.Arguments, "WorkingDirectory");

        if (string.IsNullOrWhiteSpace(command))
        {
            var message = new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Tool,
                Content = "Error: background shell execution requires a 'command' parameter",
                ToolCallId = new ToolCallId(tc.CallId),
                Name = tc.Name
            };
            return new ToolCallResult(message, [], [], [], []);
        }

        // A background job inherits the submitting turn's authority context.
        // There is no safe default — defaulting a missing context to Personal
        // would silently escalate the job's audience.
        var channelType = batch.TurnContext.ChannelType;
        if (channelType is null)
            throw new InvalidOperationException(
                "Background-job submission requires turn authority context; trust context cannot be defaulted.");

        var startCmd = new StartBackgroundJob
        {
            Command = command,
            WorkingDirectory = workingDirectory,
            SessionId = batch.SessionId,
            Rationale = meta.Rationale ?? "background shell execution",
            Audience = batch.TurnContext.Audience,
            Boundary = batch.TurnContext.Boundary,
            OriginChannelType = channelType.Value,
            TimeoutSeconds = timeoutSeconds,
            SenderId = batch.TurnContext.RequesterSenderId
        };

        try
        {
            var started = await backgroundJobManager.Ask<BackgroundJobStarted>(
                startCmd, TimeSpan.FromSeconds(30));

            _logger.Info(
                "Background job {JobId} submitted for shell command (session {SessionId})",
                started.JobId.Value, batch.SessionId.Value);

            var logPathHint = started.OutputLogPath is not null
                ? $" Output streams to {started.OutputLogPath} while the job runs — file_read/grep it to monitor."
                : string.Empty;
            var resultText = $"Background job {started.JobId.Value} submitted.{logPathHint} " +
                             "Use check_background_job to check status or cancel.";
            var resultMessage = new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Tool,
                Content = resultText,
                ToolCallId = new ToolCallId(tc.CallId),
                Name = tc.Name
            };
            var jobInfo = new Jobs.ActiveJobInfo
            {
                JobId = started.JobId,
                Command = command,
                Rationale = startCmd.Rationale,
                StartedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Audience = batch.TurnContext.Audience,
                Boundary = batch.TurnContext.Boundary,
                OutputLogPath = started.OutputLogPath
            };
            return new ToolCallResult(resultMessage, [], [], [], [], jobInfo);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to submit background job for {ToolName}", tc.Name);

            var errorMessage = new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Tool,
                Content = $"Error submitting background job: {ex.Message}",
                ToolCallId = new ToolCallId(tc.CallId),
                Name = tc.Name
            };
            return new ToolCallResult(errorMessage, [], [], [], []);
        }
    }

    internal static ModelInputMaterializationResult MaterializeModelInputFiles(
        ToolExecutionContext context,
        string sessionDir,
        ILoggingAdapter logger,
        ModelInputBatchBudget? batchBudget = null)
    {
        if (context.Outputs.ModelInputFiles.Count == 0)
            return new ModelInputMaterializationResult([], 0);

        try
        {
            SessionMediaStore.GetOrCreateMediaDirectory(sessionDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var mediaDir = Path.Combine(sessionDir, SessionDirectoryHelper.MediaSubdirectory);
            logger.Warning(ex, "Failed to create model input media directory: {Path}", mediaDir);
            return new ModelInputMaterializationResult([], context.Outputs.ModelInputFiles.Count);
        }

        var refs = new List<SerializableMediaReference>(context.Outputs.ModelInputFiles.Count);
        batchBudget ??= new ModelInputBatchBudget(MaxModelInputBatchBytes);
        foreach (var file in context.Outputs.ModelInputFiles)
        {
            var reservedBytes = 0L;
            try
            {
                // Treat tool-registered model input as a request, not proof it
                // is safe. This is the provider-boundary guardrail that keeps
                // future tools from smuggling arbitrary local bytes into the
                // next LLM call by setting a convincing MIME string.
                var mimeType = file.MimeType;
                if (!SessionMediaStore.TryGetSupportedModelInput(mimeType, out var mediaModality, out var requiredModelModality))
                {
                    logger.Warning("Model input file MIME type is not supported, skipping: {MimeType}", mimeType);
                    continue;
                }

                if (!context.ModelInputModalities.HasFlag(requiredModelModality))
                {
                    logger.Warning(
                        "Model input file requires unavailable modality {Modality}, skipping: {Path}",
                        requiredModelModality,
                        file.FilePath);
                    continue;
                }

                if (!File.Exists(file.FilePath))
                {
                    logger.Warning("Model input file not found, skipping: {Path}", file.FilePath);
                    continue;
                }

                var info = new FileInfo(file.FilePath);
                if (info.Length <= 0)
                {
                    logger.Warning("Model input file is empty, skipping: {Path}", file.FilePath);
                    continue;
                }

                if (info.Length > MaxModelInputFileBytes)
                {
                    logger.Warning("Model input file exceeds size limit, skipping: {Path}", file.FilePath);
                    continue;
                }

                if (!batchBudget.TryReserve(info.Length))
                {
                    logger.Warning("Model input file would exceed batch size limit, skipping: {Path}", file.FilePath);
                    continue;
                }

                reservedBytes = info.Length;

                if (!IsFileMagicCompatible(file.FilePath, mimeType))
                {
                    logger.Warning(
                        "Model input file MIME type does not match detected bytes, skipping: {Path}",
                        file.FilePath);
                    batchBudget.Release(reservedBytes);
                    reservedBytes = 0;
                    continue;
                }

                var mediaRef = SessionMediaStore.CopyFile(
                    file.FilePath,
                    sessionDir,
                    mimeType,
                    mediaModality,
                    info.Length);
                if (mediaRef is null)
                {
                    // Image could not be bounded under the egress caps. Release its
                    // reservation and skip; the RequestedCount > MediaReferences.Count
                    // gap drives the model-input handoff warning, so this is not silent.
                    batchBudget.Release(reservedBytes);
                    reservedBytes = 0;
                    logger.Warning("Model input image could not be bounded, skipping: {Path}", file.FilePath);
                    continue;
                }

                // The budget reserved the SOURCE size; the persisted image was resized
                // smaller, so release the headroom back to the batch — otherwise a batch
                // of large-but-downscalable images would be rejected on source size even
                // though the bounded artifacts easily fit.
                var overReserved = reservedBytes - mediaRef.FileSizeBytes;
                if (overReserved > 0)
                    batchBudget.Release(overReserved);
                reservedBytes = 0;

                refs.Add(mediaRef);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (reservedBytes > 0)
                    batchBudget.Release(reservedBytes);
                logger.Warning(ex, "Failed to materialize model input file: {Path}", file.FilePath);
            }
        }

        return new ModelInputMaterializationResult(refs, context.Outputs.ModelInputFiles.Count);
    }

    internal static string AppendModelInputHandoffWarning(string resultText, int failedCount)
    {
        var itemText = failedCount == 1 ? "file" : "files";
        return resultText +
               $"\n[model input media handoff warning: {failedCount} registered media {itemText} could not be attached to the next LLM call]";
    }

    private static bool IsFileMagicCompatible(string path, MimeType mimeType)
    {
        Span<byte> header = stackalloc byte[64];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var totalRead = 0;
        while (totalRead < header.Length)
        {
            var read = stream.Read(header[totalRead..]);
            if (read == 0)
                break;
            totalRead += read;
        }

        var detected = MagicByteValidator.DetectMimeType(header[..totalRead]);
        return string.Equals(MimeTypeCatalog.Normalize(detected), mimeType.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanRequestInteractiveApproval(TurnContext turnContext)
        => turnContext.SupportsInteractiveApproval && turnContext.HasApprovalRequester;

    /// <summary>
    /// Returns a one-line agent-facing hint pointing at <c>set_working_directory</c>
    /// when a shell call was denied specifically because its cwd is outside
    /// both <see cref="ToolExecutionContext.SessionDirectory"/> and
    /// <see cref="ToolExecutionContext.ProjectDirectory"/>. Empty for any
    /// other denial path so hard-deny refusals, timeouts, and unrelated
    /// approval declines do not get misleading "use set_working_directory"
    /// guidance.
    /// </summary>
    internal static string BuildSetWorkingDirectoryHint(
        string toolName,
        ApprovalDecision decision,
        string? cwd,
        string? sessionDirectory,
        string? projectDirectory,
        bool setWorkingDirectoryAvailable)
    {
        if (!setWorkingDirectoryAvailable)
            return string.Empty;

        if (decision != ApprovalDecision.Denied)
            return string.Empty;

        if (!string.Equals(toolName, Tools.ShellTool.ToolName, StringComparison.Ordinal))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(cwd))
            return string.Empty;

        // Already inside a safe space — denial was for a different reason.
        if (IsCwdInsideSafeSpace(cwd, sessionDirectory)
            || IsCwdInsideSafeSpace(cwd, projectDirectory))
        {
            return string.Empty;
        }

        return $"Hint: '{cwd}' is outside the session's trusted scope. Call set_working_directory \"{cwd}\" first, then retry — that brings the directory into your trusted scope so the approval policy can reason about it.";
    }

    private static bool IsCwdInsideSafeSpace(string cwd, string? safeSpace)
    {
        if (string.IsNullOrWhiteSpace(safeSpace))
            return false;

        try
        {
            return Netclaw.Security.PathUtility.IsWithinRoot(cwd, safeSpace);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }
}
