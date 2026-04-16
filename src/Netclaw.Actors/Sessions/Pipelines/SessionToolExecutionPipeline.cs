using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Result of a single tool call execution, including the serialized message,
/// file attachments, and any sub-agent activity.
/// </summary>
internal sealed record ToolCallResult(
    SerializableChatMessage Message,
    IReadOnlyList<FileAttachmentInfo> FileAttachments,
    IReadOnlyList<CompletedSubAgentRun> CompletedSubAgentRuns,
    IReadOnlyList<AcceptedSubAgentFinding> AcceptedSubAgentFindings);

/// <summary>
/// Async pipeline for parallel tool execution. Runs on the thread pool and
/// sends results back to the session actor via <c>self.Tell()</c>.
/// </summary>
internal static class SessionToolExecutionPipeline
{
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
        Action<ToolInteractionRequest>? emitApprovalRequest = null,
        TimeSpan? approvalTimeout = null)
    {
        try
        {
            // Execute all tool calls in parallel -- each is independent
            var tasks = toolCalls.Select(tc => ExecuteSingleToolAsync(
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
                CancellationToken.None,
                approvalChannel,
                emitApprovalRequest,
                approvalTimeout ?? TimeSpan.FromMinutes(5)));
            var results = await Task.WhenAll(tasks);

            var fileAttachments = results.SelectMany(r => r.FileAttachments).ToList();
            self.Tell(new ToolExecutionCompleted
            {
                ToolResults = results.Select(r => r.Message).ToList(),
                FileAttachments = fileAttachments,
                CompletedSubAgentRuns = results
                    .SelectMany(r => r.CompletedSubAgentRuns)
                    .ToList(),
                AcceptedSubAgentFindings = results
                    .SelectMany(r => r.AcceptedSubAgentFindings)
                    .ToList()
            });
        }
        catch (TimeoutException ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
        catch (OperationCanceledException ex)
        {
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
        Action<ToolInteractionRequest>? emitApprovalRequest = null,
        TimeSpan? approvalTimeout = null)
    {
        var sw = Stopwatch.StartNew();
        string resultText;
        var context = new ToolExecutionContext(sessionId.Value, sessionDir);
        context.Audience = source is null ? null : source.Audience.ToWireValue();
        context.Boundary = source?.Boundary;
        context.ChannelType = source is null ? null : source.ChannelType.ToWireValue();
        context.SupportsInteractiveApproval = source?.ChannelType.SupportsInteractiveApproval();
        context.SpawnChildActor = spawnChildActor;
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
                    AgentName = info.AgentName,
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
                    var singleDecision = ReviewSubAgentFinding(info.Findings[0], sessionId.Value);
                    decision = singleDecision.Decision.ToWireValue();
                    reason = singleDecision.Reason;
                }

                completedRuns.Add(new CompletedSubAgentRun
                {
                    RunId = info.RunId,
                    AgentName = info.AgentName,
                    Success = info.Success,
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
                    var findingDecision = ReviewSubAgentFinding(finding, sessionId.Value);
                    acceptedFindings.Add(new AcceptedSubAgentFinding
                    {
                        RunId = info.RunId,
                        AgentName = info.AgentName,
                        Duration = info.Duration,
                        Shape = finding.Shape.ToWireValue(),
                        Title = finding.Title,
                        Content = finding.Content,
                        Kind = finding.Kind,
                        Sensitivity = finding.Sensitivity.ToWireValue(),
                        RecallMode = finding.RecallMode.ToWireValue(),
                        UpdateSemantics = finding.UpdateSemantics,
                        Confidence = finding.Confidence,
                        Durability = finding.Durability.ToWireValue(),
                        Reusability = finding.Reusability.ToWireValue(),
                        Evidence = finding.Evidence,
                        FreshnessAtMs = finding.FreshnessAtMs,
                        Decision = findingDecision.Decision.ToWireValue(),
                        DecisionReason = findingDecision.Reason
                    });
                }
            }
        };
        try
        {
            resultText = await ExecuteToolAttemptAsync(executor, tc, context, timeout, ct);
            sw.Stop();

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = true,
                Duration = sw.Elapsed
            });
        }
        catch (ToolApprovalRequiredException approvalEx)
            when (approvalChannel is not null && emitApprovalRequest is not null)
        {
            // Mid-turn approval pause: emit request to channel, block on TCS
            var ctx = approvalEx.ApprovalContext;
            var approvalWaitTimeout = approvalTimeout ?? Timeout.InfiniteTimeSpan;
            var waitTask = approvalChannel.WaitForApprovalAsync(
                new ToolCallId(tc.CallId),
                approvalWaitTimeout,
                CancellationToken.None);

            emitApprovalRequest(new ToolInteractionRequest
            {
                SessionId = sessionId,
                Kind = "approval",
                CallId = tc.CallId,
                ToolName = ctx.ToolName,
                DisplayText = ctx.DisplayText,
                RequesterSenderId = source?.SenderId,
                Patterns = ctx.UnapprovedPatterns,
                Options = ctx.Options
                    .Select(o => new ToolInteractionOption(o.Key, o.Label))
                    .ToList()
            });

            var decision = await waitTask;

            sw.Stop();

            if (decision is ApprovalDecision.ApprovedOnce or ApprovalDecision.ApprovedSession or ApprovalDecision.ApprovedAlways)
            {
                // Retry execution now that approval is granted
                // (Approve-once is retried through transient context state; broader scopes
                // are also recorded by the session actor into the shared approval service.)
                if (decision == ApprovalDecision.ApprovedOnce)
                {
                    context.OneTimeApprovedToolName = tc.Name;
                    context.SetOneTimeApprovedPatterns(ctx.UnapprovedPatterns);
                }

                sw = Stopwatch.StartNew();
                resultText = await ExecuteToolAttemptAsync(executor, tc, context, timeout, ct);
                sw.Stop();

                var patternStr = string.Join(", ", ctx.UnapprovedPatterns);
                auditLogger?.Log(new ToolAuditEntry
                {
                    SessionId = sessionId.Value,
                    ToolName = tc.Name,
                    CallId = tc.CallId,
                    Timestamp = timeProvider.GetUtcNow(),
                    Allowed = true,
                    Duration = sw.Elapsed,
                    ApprovalDecision = decision.ToString(),
                    ApprovalPattern = patternStr
                });
            }
            else
            {
                var reason = decision == ApprovalDecision.TimedOut
                    ? "Tool access denied: approval_timed_out"
                    : $"Tool access denied: approval_denied_by_user ({tc.Name} requires interactive approval and the user declined it)";
                resultText = reason;

                var deniedPatternStr = string.Join(", ", ctx.UnapprovedPatterns);
                auditLogger?.Log(new ToolAuditEntry
                {
                    SessionId = sessionId.Value,
                    ToolName = tc.Name,
                    CallId = tc.CallId,
                    Timestamp = timeProvider.GetUtcNow(),
                    Allowed = false,
                    DenyReason = reason,
                    Duration = sw.Elapsed,
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

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = false,
                DenyReason = "no_approval_channel",
                Duration = sw.Elapsed
            });
        }
        catch (ToolAccessDeniedException ex)
        {
            sw.Stop();
            resultText = $"Tool access denied: {ex.DenyReason}";

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = false,
                DenyReason = ex.DenyReason,
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            resultText = $"Error executing tool: {ex.Message}";

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = false,
                DenyReason = $"tool_execution_error:{ex.GetType().Name}",
                Duration = sw.Elapsed
            });
        }

        resultText = ClampToolResult(resultText, maxInlineToolResultChars);

        var message = new SerializableChatMessage
        {
            Role = Protocol.ChatRole.Tool,
            Content = resultText,
            ToolCallId = tc.CallId,
            Name = tc.Name
        };

        return new ToolCallResult(
            message,
            context.FileAttachments,
            completedRuns,
            acceptedFindings);
    }

    private static async Task<string> ExecuteToolAttemptAsync(
        IToolExecutor executor,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var grantedOneTimeToolName = context.OneTimeApprovedToolName;
        var grantedOneTimePatterns = context.OneTimeApprovedPatterns;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(timeout);

        try
        {
            return await executor.ExecuteAsync(toolCall, context, timeoutCts.Token);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested
                  && timeout != Timeout.InfiniteTimeSpan
                  && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Tool execution exceeded timeout of {timeout.TotalSeconds:F0}s",
                ex);
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
        string sessionId)
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

    /// <summary>
    /// Truncates a tool result to fit within the configured inline character limit.
    /// </summary>
    public static string ClampToolResult(string resultText, int maxInlineToolResultChars)
    {
        if (maxInlineToolResultChars <= 0 || resultText.Length <= maxInlineToolResultChars)
            return resultText;

        var omittedChars = resultText.Length - maxInlineToolResultChars;
        return resultText[..maxInlineToolResultChars]
               + $"\n[tool result truncated: omitted {omittedChars} chars to protect context window]";
    }
}
