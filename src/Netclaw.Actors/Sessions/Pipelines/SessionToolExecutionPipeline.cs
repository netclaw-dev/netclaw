// -----------------------------------------------------------------------
// <copyright file="SessionToolExecutionPipeline.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// Async pipeline for parallel tool execution. Runs on the thread pool and
/// sends results back to the session actor via <c>self.Tell()</c>.
/// </summary>
internal static class SessionToolExecutionPipeline
{
    private const long MaxModelInputFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes;
    private const long MaxModelInputBatchBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes;

    public static async Task ExecuteToolsAsync(
        IToolExecutor executor,
        List<FunctionCallContent> toolCalls,
        SessionId sessionId,
        MessageSource? source,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider,
        string sessionDir,
        int maxInlineToolResultChars,
        TimeSpan timeout,
        IActorRef self,
        Action<SubAgentOutput> emitSubAgentOutput,
        Func<object, string, CancellationToken, Task<object>> spawnChildActor,
        IApprovalChannel? approvalChannel = null,
        Action<ToolInteractionRequestDispatch>? emitApprovalRequest = null,
        TimeSpan? approvalTimeout = null,
        ILogger? logger = null,
        IActorRef? backgroundJobManager = null,
        string? projectDirectory = null,
        bool setWorkingDirectoryAvailable = false,
        bool streamToolResults = false,
        ModelModality modelInputModalities = ModelModality.Text,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? oneTimeApprovalPreSeed = null,
        IReadOnlyDictionary<string, ApprovalDecision>? decisionOverride = null,
        TurnContext? turnContext = null,
        CancellationToken ct = default)
    {
        try
        {
            // Execute all tool calls in parallel. Calls are not always
            // independent -- e.g. two file_edit calls on the same file -- so
            // file-mutating tools serialize their read-modify-write per target
            // path via FileMutationGate to avoid lost-update races here.
            var modelInputBudget = new ModelInputBatchBudget(MaxModelInputBatchBytes);
            var tasks = toolCalls.Select(async tc =>
            {
                var result = await ExecuteSingleToolAsync(
                    executor,
                    tc,
                    sessionId,
                    source,
                    auditLogger,
                    timeProvider,
                    sessionDir,
                    maxInlineToolResultChars,
                    emitSubAgentOutput,
                    spawnChildActor,
                    timeout,
                    ct,
                    approvalChannel,
                    emitApprovalRequest,
                    approvalTimeout ?? Timeout.InfiniteTimeSpan,
                    logger,
                    backgroundJobManager,
                    projectDirectory,
                    setWorkingDirectoryAvailable,
                    modelInputModalities,
                    oneTimeApprovalPreSeed is not null
                    && oneTimeApprovalPreSeed.TryGetValue(tc.CallId, out var preSeedPatterns)
                        ? preSeedPatterns
                        : null,
                    decisionOverride is not null && decisionOverride.TryGetValue(tc.CallId, out var overrideDecision)
                        ? overrideDecision
                        : null,
                    turnContext,
                    modelInputBudget);
                if (streamToolResults)
                    self.Tell(new ToolExecutionSingleCompleted(result));
                return result;
            });
            var results = await Task.WhenAll(tasks);

            if (streamToolResults)
            {
                self.Tell(new ToolExecutionBatchCompleted());
                return;
            }

            var fileAttachments = results.SelectMany(r => r.FileAttachments).ToList();
            var modelInputMediaReferences = results.SelectMany(r => r.ModelInputMediaReferences).ToList();
            self.Tell(new ToolExecutionCompleted
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
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
        catch (OperationCanceledException ex)
        {
            // The tool-execution token is cancelled both by caller (turn/user) supersede
            // and by the session's own timeout watchdog; surface either as a failed
            // batch (the watchdog message is the authoritative one).
            self.Tell(new ToolExecutionFailed
            {
                Cause = new TimeoutException(
                    $"Tool execution exceeded timeout of {timeout.TotalSeconds:F0}s",
                    ex)
            });
        }
        catch (Exception ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    public static async Task<ToolCallResult> ExecuteSingleToolAsync(
        IToolExecutor executor,
        FunctionCallContent tc,
        SessionId sessionId,
        MessageSource? source,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider,
        string sessionDir,
        int maxInlineToolResultChars,
        Action<SubAgentOutput> emitSubAgentOutput,
        Func<object, string, CancellationToken, Task<object>> spawnChildActor,
        TimeSpan timeout,
        CancellationToken ct,
        IApprovalChannel? approvalChannel = null,
        Action<ToolInteractionRequestDispatch>? emitApprovalRequest = null,
        TimeSpan? approvalTimeout = null,
        ILogger? logger = null,
        IActorRef? backgroundJobManager = null,
        string? projectDirectory = null,
        bool setWorkingDirectoryAvailable = false,
        ModelModality modelInputModalities = ModelModality.Text,
        IReadOnlyList<string>? oneTimeApprovalPreSeed = null,
        ApprovalDecision? decisionOverride = null,
        TurnContext? turnContext = null,
        ModelInputBatchBudget? modelInputBudget = null)
    {
        // Single execution-preflight seam, shared with the sub-agent path via
        // IToolExecutor.InterpretToolCall: validate the ORIGINAL arguments (parse
        // sentinel, invalid/ambiguous meta values, unrecognized keys) and, on
        // success, extract meta + strip meta keys. Rejecting here — rather than
        // letting ExecuteAsync return the rejection string — is what lets the denial
        // be audited as Allowed=false instead of being misreported as executed.
        var interpretation = executor.InterpretToolCall(tc);
        if (interpretation.Rejection is { } rejection)
        {
            auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, TimeSpan.Zero, meta: null) with
            {
                Allowed = false,
                DenyReason = rejection.DenyReason
            });

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
        if (meta?.TimeoutHintSeconds is { } hintSeconds)
            timeout = TimeSpan.FromSeconds(hintSeconds);

        var sw = Stopwatch.StartNew();
        string resultText;
        var context = BuildToolExecutionContext(
            sessionId,
            source,
            sessionDir,
            spawnChildActor,
            projectDirectory,
            turnContext,
            modelInputModalities,
            maxInlineToolResultChars);
        context.RequestedTimeoutSeconds = (int)timeout.TotalSeconds;

        // Re-drive of an ApprovedOnce approval: the user already clicked
        // "approve once" before the session passivated, but there is no
        // persisted grant to satisfy the gate on the cold-recovered re-drive.
        // Pre-seed the one-time approval bypass for exactly this call id so the
        // gate passes once without emitting a duplicate approval prompt. The
        // bypass is still tool-name- and pattern-matched inside the gate
        // (DispatchingToolExecutor.IsOneTimeApprovalSatisfied) and the pipeline
        // clears it after the attempt — it cannot leak to any other call.
        if (oneTimeApprovalPreSeed is not null)
        {
            context.OneTimeApprovedToolName = tc.Name;
            context.SetOneTimeApprovedPatterns(oneTimeApprovalPreSeed);
        }
        if (approvalChannel is not null
            && emitApprovalRequest is not null
            && CanRequestInteractiveApproval(source, turnContext))
        {
            context.ApprovalBridge = new ParentSessionApprovalBridge(
                approvalChannel,
                emitApprovalRequest,
                sessionId,
                tc.CallId,
                turnContext?.RequesterSenderId ?? source?.SenderId,
                turnContext?.RequesterPrincipal ?? source?.Principal,
                turnContext?.HasAdoptedContext ?? source?.HasAdoptedContext ?? false,
                turnContext?.HasThirdPartyAdoptedContext ?? source?.HasThirdPartyAdoptedContext ?? false,
                turnContext?.AdoptedSpeakerIds ?? source?.AdoptedSpeakerIds ?? []);
        }
        var completedRuns = new List<CompletedSubAgentRun>();
        var acceptedFindings = new List<AcceptedSubAgentFinding>();
        context.OnSubAgentActivity = info =>
        {
            if (info.IsStarted)
            {
                emitSubAgentOutput(new SubAgentOutput
                {
                    SessionId = sessionId,
                    TimestampMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
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
                    var singleDecision = ReviewSubAgentFinding(info.Findings[0], sessionId);
                    decision = singleDecision.Decision.ToWireValue();
                    reason = singleDecision.Reason;
                }

                completedRuns.Add(new CompletedSubAgentRun
                {
                    RunId = info.RunId,
                    AgentName = new SubAgents.AgentName(info.AgentName),
                    Success = info.Success,
                    Outcome = info.Outcome ?? (info.Success ? SubAgentRunOutcome.Completed : SubAgentRunOutcome.Failed),
                    OutcomeReason = info.OutcomeReason,
                    Duration = info.Duration,
                    FindingsCount = info.Findings.Count,
                    MemoryDecision = decision,
                    MemoryDecisionReason = reason
                });
            }

            if (!info.IsStarted && info.Success)
            {
                foreach (var finding in info.Findings)
                {
                    var findingDecision = ReviewSubAgentFinding(finding, sessionId);
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
        };
        try
        {
            if (decisionOverride is ApprovalDecision.Denied or ApprovalDecision.TimedOut)
            {
                sw.Stop();
                resultText = decisionOverride == ApprovalDecision.TimedOut
                    ? "Tool access denied: approval_timed_out"
                    : $"Tool access denied: approval_denied_by_user ({tc.Name} requires interactive approval and the user declined it)";

                auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
                {
                    Allowed = false,
                    DenyReason = resultText,
                    ApprovalDecision = decisionOverride.ToString()
                });

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
                    logger?.LogWarning(
                        "Tool {ToolName} (call {CallId}) requested background execution — " +
                        "only shell_execute supports background mode; executing synchronously",
                        tc.Name, tc.CallId);
                }
                else if (backgroundJobManager is null)
                {
                    logger?.LogWarning(
                        "Tool {ToolName} (call {CallId}) requested background execution — " +
                        "no background job manager available; executing synchronously",
                        tc.Name, tc.CallId);
                }
                else
                {
                    await executor.AuthorizeAsync(tc, context, ct);
                    sw.Stop();
                    return await RouteToBackgroundJobAsync(
                        tc, sessionId, source, auditLogger, timeProvider,
                        turnContext,
                        meta, backgroundJobManager,
                        // Honor the agent's requested timeout; when absent, no
                        // kill timer is armed — a background job is a detached
                        // process with no completion expectation, reaped by its
                        // own exit, cancellation, or session passivation.
                        meta.TimeoutHintSeconds ?? 0,
                        sw.Elapsed, logger,
                        context.AppliedApprovalDecision,
                        context.AppliedApprovalPattern);
                }
            }

            resultText = await ExecuteToolAttemptAsync(executor, tc, context, timeout, timeProvider, ct);
            sw.Stop();

            auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
            {
                Allowed = true,
                ApprovalDecision = context.AppliedApprovalDecision,
                ApprovalPattern = context.AppliedApprovalPattern
            });
        }
        catch (ToolApprovalRequiredException approvalEx)
            when (approvalChannel is not null && emitApprovalRequest is not null)
        {
            if (!CanRequestInteractiveApproval(source, turnContext))
            {
                sw.Stop();
                resultText = $"Tool requires approval but no interactive approval requester is available: {approvalEx.ApprovalContext.ToolName}";

                auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
                {
                    Allowed = false,
                    DenyReason = "interactive_approval_unavailable"
                });

                return new ToolCallResult(new SerializableChatMessage
                {
                    Role = Protocol.ChatRole.Tool,
                    Content = resultText,
                    ToolCallId = new ToolCallId(tc.CallId),
                    Name = tc.Name
                }, [], context.FileAttachments, completedRuns, acceptedFindings);
            }

            // Mid-turn approval pause: emit request to channel, block on TCS
            var ctx = approvalEx.ApprovalContext;
            var approvalWaitTimeout = approvalTimeout ?? Timeout.InfiniteTimeSpan;
            var waitTask = approvalChannel.WaitForApprovalAsync(
                new ToolCallId(tc.CallId),
                approvalWaitTimeout,
                ct);

            emitApprovalRequest(new ToolInteractionRequestDispatch(new ToolInteractionRequest
            {
                SessionId = sessionId,
                Kind = "approval",
                CallId = new ToolCallId(tc.CallId),
                ToolName = new ToolName(ctx.ToolName),
                DisplayText = ctx.DisplayText,
                RequesterSenderId = turnContext?.RequesterSenderId ?? source?.SenderId,
                RequesterPrincipal = turnContext?.RequesterPrincipal ?? source?.Principal,
                HasAdoptedContext = turnContext?.HasAdoptedContext ?? source?.HasAdoptedContext ?? false,
                HasThirdPartyAdoptedContext = turnContext?.HasThirdPartyAdoptedContext ?? source?.HasThirdPartyAdoptedContext ?? false,
                AdoptedSpeakerIds = turnContext?.AdoptedSpeakerIds ?? source?.AdoptedSpeakerIds ?? [],
                PersistedAdoptedContext = turnContext?.HasAdoptedContext ?? source?.HasAdoptedContext ?? false,
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

            if (decision is ApprovalDecision.ApprovedOnce
                or ApprovalDecision.ApprovedSession
                or ApprovalDecision.ApprovedAlways
                or ApprovalDecision.ApprovedEverywhere)
            {
                // Retry execution now that approval is granted
                // (Approve-once is retried through transient context state; broader scopes
                // are also recorded by the session actor into the shared approval service.)
                if (decision == ApprovalDecision.ApprovedOnce)
                {
                    context.OneTimeApprovedToolName = tc.Name;
                    context.SetOneTimeApprovedPatterns(ctx.Patterns);
                }

                sw = Stopwatch.StartNew();
                if (meta is { Background: true }
                    && string.Equals(tc.Name, Tools.ShellTool.ToolName, StringComparison.Ordinal)
                    && backgroundJobManager is not null)
                {
                    await executor.AuthorizeAsync(tc, context, ct);
                    sw.Stop();
                    return await RouteToBackgroundJobAsync(
                        tc, sessionId, source, auditLogger, timeProvider,
                        turnContext,
                        meta, backgroundJobManager,
                        // Honor the agent's requested timeout; when absent, no
                        // kill timer is armed — a background job is a detached
                        // process with no completion expectation, reaped by its
                        // own exit, cancellation, or session passivation.
                        meta.TimeoutHintSeconds ?? 0,
                        sw.Elapsed, logger,
                        decision.ToString(),
                        string.Join(", ", ctx.Patterns));
                }

                resultText = await ExecuteToolAttemptAsync(executor, tc, context, timeout, timeProvider, ct);
                sw.Stop();

                var patternStr = string.Join(", ", ctx.Patterns);
                auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
                {
                    Allowed = true,
                    ApprovalDecision = decision.ToString(),
                    ApprovalPattern = patternStr
                });
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
                    cwd: context.Cwd,
                    sessionDirectory: context.SessionDirectory,
                    projectDirectory: context.ProjectDirectory,
                    setWorkingDirectoryAvailable: setWorkingDirectoryAvailable);
                resultText = string.IsNullOrEmpty(hint) ? reason : $"{reason}\n{hint}";

                // Denied audit entries should describe the exact blocked units
                // the user saw in the prompt. Broader reusable approval entries
                // are only relevant when B/C is granted.
                var deniedPatternStr = string.Join(", ", ctx.Patterns);
                auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
                {
                    Allowed = false,
                    DenyReason = reason,
                    ApprovalDecision = decision.ToString(),
                    ApprovalPattern = deniedPatternStr
                });
            }
        }
        catch (ToolApprovalRequiredException approvalEx)
        {
            // No approval channel available — treat as denied
            sw.Stop();
            resultText = $"Tool requires approval but no approval channel is available: {approvalEx.ApprovalContext.ToolName}";

            auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
            {
                Allowed = false,
                DenyReason = "no_approval_channel"
            });
        }
        catch (ToolAccessDeniedException ex)
        {
            sw.Stop();
            resultText = $"Tool access denied: {ex.DenyReason}";

            auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
            {
                Allowed = false,
                DenyReason = ex.DenyReason
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

            auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, sw.Elapsed, meta) with
            {
                Allowed = false,
                DenyReason = $"tool_execution_error:{ex.GetType().Name}"
            });
        }

        modelInputBudget ??= new ModelInputBatchBudget(MaxModelInputBatchBytes);
        var modelInputMaterialization = MaterializeModelInputFiles(context, sessionDir, logger, modelInputBudget);
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
            context.FileAttachments,
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
        var grantedOneTimeToolName = context.OneTimeApprovedToolName;
        var grantedOneTimePatterns = context.OneTimeApprovedPatterns;

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
                if (!string.Equals(context.OneTimeApprovedToolName, grantedOneTimeToolName, StringComparison.Ordinal)
                    || !SetsEqual(context.OneTimeApprovedPatterns, grantedOneTimePatterns))
                {
                    context.OneTimeApprovedToolName = null;
                    context.SetOneTimeApprovedPatterns([]);
                }
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

    private static async Task<ToolCallResult> RouteToBackgroundJobAsync(
        FunctionCallContent tc,
        SessionId sessionId,
        MessageSource? source,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider,
        TurnContext? turnContext,
        ToolCallMeta meta,
        IActorRef backgroundJobManager,
        int timeoutSeconds,
        TimeSpan duration,
        ILogger? logger,
        string? approvalDecision = null,
        string? approvalPattern = null)
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
        var audience = turnContext?.Audience ?? source?.Audience;
        var boundary = turnContext?.Boundary ?? source?.Boundary;
        var channelType = turnContext?.ChannelType ?? source?.ChannelType;
        var senderId = turnContext?.RequesterSenderId ?? source?.SenderId;
        if (audience is null || boundary is null || channelType is null)
            throw new InvalidOperationException(
                "Background-job submission requires turn authority context; trust context cannot be defaulted.");

        var startCmd = new StartBackgroundJob
        {
            Command = command,
            WorkingDirectory = workingDirectory,
            SessionId = sessionId,
            Rationale = meta.Rationale ?? "background shell execution",
            Audience = audience.Value,
            Boundary = boundary.Value,
            OriginChannelType = channelType.Value,
            TimeoutSeconds = timeoutSeconds,
            SenderId = senderId
        };

        try
        {
            var started = await backgroundJobManager.Ask<BackgroundJobStarted>(
                startCmd, TimeSpan.FromSeconds(30));

            logger?.LogInformation(
                "Background job {JobId} submitted for shell command (session {SessionId})",
                started.JobId.Value, sessionId.Value);

            auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, duration, meta) with
            {
                Allowed = true,
                ApprovalDecision = approvalDecision,
                ApprovalPattern = approvalPattern
            });

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
                StartedAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Audience = audience.Value,
                Boundary = boundary.Value,
                OutputLogPath = started.OutputLogPath
            };
            return new ToolCallResult(resultMessage, [], [], [], [], jobInfo);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to submit background job for {ToolName}", tc.Name);

            auditLogger?.Log(BuildAuditEntry(sessionId, tc, timeProvider, duration, meta) with
            {
                Allowed = false,
                DenyReason = $"background_job_submission_failed:{ex.GetType().Name}",
                ApprovalDecision = approvalDecision,
                ApprovalPattern = approvalPattern
            });

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
        ILogger? logger,
        ModelInputBatchBudget? batchBudget = null)
    {
        if (context.ModelInputFiles.Count == 0)
            return new ModelInputMaterializationResult([], 0);

        try
        {
            SessionMediaStore.GetOrCreateMediaDirectory(sessionDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var mediaDir = Path.Combine(sessionDir, SessionDirectoryHelper.MediaSubdirectory);
            logger?.LogWarning(ex, "Failed to create model input media directory: {Path}", mediaDir);
            return new ModelInputMaterializationResult([], context.ModelInputFiles.Count);
        }

        var refs = new List<SerializableMediaReference>(context.ModelInputFiles.Count);
        batchBudget ??= new ModelInputBatchBudget(MaxModelInputBatchBytes);
        foreach (var file in context.ModelInputFiles)
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
                    logger?.LogWarning("Model input file MIME type is not supported, skipping: {MimeType}", mimeType);
                    continue;
                }

                if (!context.ModelInputModalities.HasFlag(requiredModelModality))
                {
                    logger?.LogWarning(
                        "Model input file requires unavailable modality {Modality}, skipping: {Path}",
                        requiredModelModality,
                        file.FilePath);
                    continue;
                }

                if (!File.Exists(file.FilePath))
                {
                    logger?.LogWarning("Model input file not found, skipping: {Path}", file.FilePath);
                    continue;
                }

                var info = new FileInfo(file.FilePath);
                if (info.Length <= 0)
                {
                    logger?.LogWarning("Model input file is empty, skipping: {Path}", file.FilePath);
                    continue;
                }

                if (info.Length > MaxModelInputFileBytes)
                {
                    logger?.LogWarning("Model input file exceeds size limit, skipping: {Path}", file.FilePath);
                    continue;
                }

                if (!batchBudget.TryReserve(info.Length))
                {
                    logger?.LogWarning("Model input file would exceed batch size limit, skipping: {Path}", file.FilePath);
                    continue;
                }

                reservedBytes = info.Length;

                if (!IsFileMagicCompatible(file.FilePath, mimeType))
                {
                    logger?.LogWarning(
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
                    logger?.LogWarning("Model input image could not be bounded, skipping: {Path}", file.FilePath);
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
                logger?.LogWarning(ex, "Failed to materialize model input file: {Path}", file.FilePath);
            }
        }

        return new ModelInputMaterializationResult(refs, context.ModelInputFiles.Count);
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

    private static ToolExecutionContext BuildToolExecutionContext(
        SessionId sessionId,
        MessageSource? source,
        string sessionDir,
        Func<object, string, CancellationToken, Task<object>> spawnChildActor,
        string? projectDirectory,
        TurnContext? turnContext,
        ModelModality modelInputModalities,
        int maxInlineToolResultChars)
    {
        // A turn with no authority context carries no trust context — fall closed
        // to the most-restrictive audience. The default is resolved once, here,
        // so every downstream tool gate reads a guaranteed audience.
        var context = new ToolExecutionContext(sessionId.Value, sessionDir)
        {
            Audience = turnContext?.Audience ?? source?.Audience ?? TrustAudience.Public,
            // The session content budget; DispatchingToolExecutor uses it (or a
            // tool's own override) to bound results and spill the overflow.
            MaxInlineToolResultChars = maxInlineToolResultChars,
        };
        context.Boundary = turnContext?.Boundary ?? source?.Boundary;
        context.ChannelType = turnContext?.ChannelType?.ToWireValue()
                              ?? (source is null ? null : source.ChannelType.ToWireValue());
        context.DefaultDeliveryTarget = turnContext?.DefaultDeliveryTarget ?? source?.DefaultDeliveryTarget;
        context.RequestedDeliveryTarget = turnContext?.RequestedDeliveryTarget ?? source?.RequestedDeliveryTarget;
        context.SupportsInteractiveApproval = turnContext?.SupportsInteractiveApproval
                                               ?? source?.ChannelType.SupportsInteractiveApproval();
        context.ModelInputModalities = modelInputModalities;
        context.SpawnChildActor = spawnChildActor;
        context.ProjectDirectory = projectDirectory;
        return context;
    }

    private static bool CanRequestInteractiveApproval(MessageSource? source, TurnContext? turnContext)
    {
        if (turnContext is not null)
            return turnContext.SupportsInteractiveApproval && turnContext.HasApprovalRequester;

        return source is not null && source.ChannelType.SupportsInteractiveApproval();
    }

    private static ToolAuditEntry BuildAuditEntry(
        SessionId sessionId,
        FunctionCallContent tc,
        TimeProvider timeProvider,
        TimeSpan duration,
        ToolCallMeta? meta) => new()
    {
        SessionId = sessionId,
        ToolName = new ToolName(tc.Name),
        CallId = new ToolCallId(tc.CallId),
        Timestamp = timeProvider.GetUtcNow(),
        Allowed = false,
        Duration = duration,
        Rationale = meta?.Rationale,
        TimeoutHintSeconds = meta?.TimeoutHintSeconds
    };

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
