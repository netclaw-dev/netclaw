using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
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
        Func<object, string, CancellationToken, Task<object>> spawnChildActor)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);

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
                cts.Token));
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
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string resultText;
        var context = new ToolExecutionContext(sessionId.Value, sessionDir);
        context.Audience = source is null ? null : source.Audience.ToWireValue();
        context.Boundary = source?.Boundary;
        context.ChannelType = source is null ? null : source.ChannelType.ToWireValue();
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
                        Domain = finding.Domain,
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
            resultText = await executor.ExecuteAsync(tc, context, ct);
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
                Allowed = true,
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

        var expectedDomain = new SessionId(sessionId).ToMemoryDomain();
        if (!string.Equals(finding.Domain, expectedDomain, StringComparison.OrdinalIgnoreCase))
            return new(SubAgentFindingReviewDecision.Deferred, $"domain mismatch: expected {expectedDomain}");

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
