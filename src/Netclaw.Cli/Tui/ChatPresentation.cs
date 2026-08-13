// -----------------------------------------------------------------------
// <copyright file="ChatPresentation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tui;

internal enum ChatBlockKind
{
    System,
    User,
    Assistant,
    Thought,
    Tool,
    Parallel,
    SubAgent,
    Approval,
    File,
    Error,
    Usage,
    Compaction,
    Diagnostic
}

internal sealed record ChatPresentationBlock(
    string Key,
    ChatBlockKind Kind,
    string Label,
    string Summary,
    string SemanticText,
    long TimestampMs,
    string? TurnId = null,
    string? Detail = null,
    bool IsFailure = false);

internal sealed record ToolActivityPresentation(
    string CallId,
    string ToolName,
    string? Rationale,
    string? ArgumentsJson,
    string Phase,
    string? Summary,
    long StartedAtMs,
    string? TurnId,
    string BatchId,
    int BatchSize,
    int PassageIndex,
    string? Result,
    long? CompletedAtMs,
    string? FailureCode);

internal sealed record ReplyPassagePresentation(
    int Index,
    long StartedAtMs,
    string Text,
    bool IsFinal,
    ImmutableList<string> ToolCallIds);

internal sealed record AgentPullPresentation(
    string BatchId,
    string TurnId,
    long TimestampMs,
    int AfterPassageIndex,
    ImmutableList<PulledUserMessage> Messages);

internal sealed record SubAgentActivityPresentation(
    string RunId,
    string? ParentCallId,
    string AgentName,
    string Phase,
    string? Summary,
    long StartedAtMs,
    string? ActiveToolName,
    long? CompletedAtMs,
    string? Outcome,
    string? Detail,
    bool IsFailure);

internal sealed record ChatPresentationState
{
    public static readonly ChatPresentationState Empty = new();

    public ImmutableList<ChatPresentationBlock> Transcript { get; init; } = [];

    public ImmutableDictionary<string, ToolActivityPresentation> Tools { get; init; } =
        ImmutableDictionary<string, ToolActivityPresentation>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableDictionary<string, SubAgentActivityPresentation> SubAgents { get; init; } =
        ImmutableDictionary<string, SubAgentActivityPresentation>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableHashSet<string> CommittedToolBatches { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    public ImmutableQueue<ToolInteractionRequest> PendingApprovals { get; init; } =
        ImmutableQueue<ToolInteractionRequest>.Empty;

    public ImmutableList<ChatPresentationBlock> CompletedApprovals { get; init; } = [];

    public ImmutableList<ReplyPassagePresentation> ReplyPassages { get; init; } = [];

    public ImmutableList<AgentPullPresentation> AgentPulls { get; init; } = [];

    public string ThoughtText { get; init; } = string.Empty;

    public int TurnNumber { get; init; } = 1;

    public string? CurrentTurnId { get; init; }

    public string? SessionTitle { get; init; }

    public double? ContextUsagePercent { get; init; }

    public bool HasJoined { get; init; }

    public bool IsProcessing { get; init; }

    public ToolInteractionRequest? PendingApproval =>
        PendingApprovals.IsEmpty ? null : PendingApprovals.Peek();

    public int PendingApprovalCount => PendingApprovals.Count();

    public int ApprovalQueuePosition(string callId)
    {
        var position = 1;
        foreach (var approval in PendingApprovals)
        {
            if (string.Equals(approval.CallId.Value, callId, StringComparison.Ordinal))
                return position;

            position++;
        }

        return 0;
    }
}

internal abstract record ChatPresentationEffect
{
    public sealed record Commit(ChatPresentationBlock Block) : ChatPresentationEffect;

    public sealed record RefreshLiveRegion : ChatPresentationEffect;

    public sealed record SetStatus(string Text) : ChatPresentationEffect;

    public sealed record ShowApproval(ToolInteractionRequest Request) : ChatPresentationEffect;

    public sealed record ClearApproval : ChatPresentationEffect;
}

internal sealed record ChatReduction(
    ChatPresentationState State,
    IReadOnlyList<ChatPresentationEffect> Effects);

internal static class ChatPresentationReducer
{
    public static ChatReduction Reduce(ChatPresentationState state, SessionOutput output)
    {
        var effects = new List<ChatPresentationEffect>();
        var next = output switch
        {
            SessionJoined joined => ReduceJoined(state, joined, effects),
            TextDeltaOutput textDelta => AppendAssistantDelta(state, textDelta),
            TextOutput text => FinalizeAssistantPassage(state, text),
            ThinkingDeltaOutput thoughtDelta => state with
            {
                ThoughtText = state.ThoughtText + thoughtDelta.Delta
            },
            ThinkingOutput thought => FinalizeThought(state, thought),
            ToolCallOutput toolCall => StartTool(state, toolCall),
            ToolActivityOutput activity => UpdateTool(state, activity),
            ToolResultOutput toolResult => CompleteTool(state, toolResult),
            SubAgentOutput subAgent => ReduceSubAgent(state, subAgent),
            UsageOutput usage => CommitUsage(state, usage, effects),
            ErrorOutput error => Commit(state, ErrorBlock(error, state.CurrentTurnId), effects),
            FileOutput file => Commit(state, FileBlock(file, state.CurrentTurnId), effects),
            CompactionOutput compaction => Commit(state, CompactionBlock(compaction, state.CurrentTurnId), effects),
            ToolInteractionRequest approval => ShowApproval(state, approval, effects),
            ApprovalOutcomeOutput approval => ResolveApproval(state, approval, effects),
            UserMessageQueuedOutput => state,
            UserMessagesPulledOutput pulled => RecordAgentPull(state, pulled),
            TurnCompleted completed => CompleteTurn(state, completed, effects),
            ProcessingStateOutput processing => state with { IsProcessing = processing.IsProcessing },
            SessionTitleOutput title => CommitTitle(state, title, effects),
            BufferFlush => FinalizeAssistantPassage(state, null),
            _ => Commit(state, DiagnosticBlock(
                $"unsupported:{output.GetType().Name}:{output.TimestampMs}",
                $"Unsupported session output: {output.GetType().Name}",
                output.TimestampMs,
                state.CurrentTurnId), effects)
        };

        if (output is TextDeltaOutput or TextOutput or ThinkingDeltaOutput or ThinkingOutput
            or ToolCallOutput or ToolActivityOutput or ToolResultOutput
            or ProcessingStateOutput or UserMessageQueuedOutput or UserMessagesPulledOutput)
        {
            effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        }

        return new ChatReduction(next, effects);
    }

    public static ChatReduction RecordUserPrompt(
        ChatPresentationState state,
        string prompt,
        long timestampMs)
    {
        var block = new ChatPresentationBlock(
            $"turn:{state.TurnNumber}:user",
            ChatBlockKind.User,
            "YOU",
            prompt,
            $"YOU\n{prompt}",
            timestampMs,
            state.CurrentTurnId,
            prompt);
        return new ChatReduction(
            state with { Transcript = state.Transcript.Add(block) },
            [new ChatPresentationEffect.Commit(block)]);
    }

    private static ChatPresentationState ReduceJoined(
        ChatPresentationState state,
        SessionJoined joined,
        List<ChatPresentationEffect> effects)
    {
        if (state.HasJoined)
        {
            effects.Add(new ChatPresentationEffect.SetStatus("Reconnected"));
            return state;
        }

        state = state with { SessionTitle = joined.Title };

        if (joined.RecentTranscript is { Count: > 0 })
        {
            var restoredTurns = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < joined.RecentTranscript.Count; index++)
            {
                var entry = joined.RecentTranscript[index];
                if (entry.TurnId is { Length: > 0 } turnId && IsReplyEntry(entry))
                {
                    if (restoredTurns.Add(turnId))
                    {
                        var turnEntries = joined.RecentTranscript
                            .Where(candidate => string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal)
                                                && IsReplyEntry(candidate))
                            .ToList();
                        state = Commit(state, ResumeReplyBlock(turnId, turnEntries), effects);
                    }

                    continue;
                }

                if (entry is
                    {
                        Type: SessionTranscriptEntryTypes.Tool,
                        BatchSize: > 1,
                        BatchId.Length: > 0
                    }
                    && !state.CommittedToolBatches.Contains(entry.BatchId))
                {
                    state = CommitParallelGroup(
                        state,
                        entry.BatchId,
                        entry.BatchSize.Value,
                        entry.TimestampMs,
                        entry.TurnId,
                        effects);
                }

                state = Commit(state, ResumeBlock(entry, index), effects);
            }
        }
        else if (joined.RecentMessages is { Count: > 0 })
        {
            for (var index = 0; index < joined.RecentMessages.Count; index++)
            {
                var message = joined.RecentMessages[index];
                var kind = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    ? ChatBlockKind.User
                    : ChatBlockKind.Assistant;
                var label = kind == ChatBlockKind.User ? "YOU" : "NETCLAW";
                state = Commit(state, new ChatPresentationBlock(
                    $"legacy:{index}:{message.Role}",
                    kind,
                    label,
                    message.Content,
                    $"{label}\n{message.Content}",
                    joined.TimestampMs,
                    Detail: message.Content), effects);
            }
        }

        effects.Add(new ChatPresentationEffect.SetStatus("Ready"));
        return state with
        {
            HasJoined = true,
            TurnNumber = joined.TurnCount + 1
        };
    }

    private static bool IsReplyEntry(SessionTranscriptEntry entry) => entry.Type is
        SessionTranscriptEntryTypes.Assistant
        or SessionTranscriptEntryTypes.Tool
        or SessionTranscriptEntryTypes.Approval
        or SessionTranscriptEntryTypes.SubAgent;

    private static ChatPresentationBlock ResumeReplyBlock(
        string turnId,
        IReadOnlyList<SessionTranscriptEntry> entries)
    {
        var prose = string.Join("\n\n", entries
            .Where(entry => entry.Type == SessionTranscriptEntryTypes.Assistant)
            .Select(entry => entry.Text?.Trim())
            .Where(text => !string.IsNullOrEmpty(text)));
        var toolCount = entries.Count(entry => entry.Type == SessionTranscriptEntryTypes.Tool);
        var agentCount = entries.Count(entry => entry.Type == SessionTranscriptEntryTypes.SubAgent);
        var decisionCount = entries.Count(entry => entry.Type == SessionTranscriptEntryTypes.Approval);
        var receiptParts = new[]
        {
            CountLabel(toolCount, "tool", "tools"),
            CountLabel(agentCount, "agent", "agents"),
            CountLabel(decisionCount, "decision", "decisions")
        }.Where(value => value.Length > 0).ToArray();
        var receipt = receiptParts.Length == 0
            ? string.Empty
            : $"Completed work  · {string.Join("  · ", receiptParts)}";
        var summary = string.Join("\n\n", new[] { prose, receipt }
            .Where(value => value.Length > 0));
        var detail = string.Join("\n\n", entries
            .Select(entry => entry.Type == SessionTranscriptEntryTypes.Assistant
                ? entry.Text ?? string.Empty
                : ResumeDetail(entry))
            .Where(value => value.Length > 0));
        return new ChatPresentationBlock(
            $"resume:reply:{turnId}",
            ChatBlockKind.Assistant,
            "NETCLAW",
            summary,
            detail.Length == 0 ? "NETCLAW" : $"NETCLAW\n{detail}",
            entries.Min(entry => entry.TimestampMs),
            turnId,
            detail,
            entries.Any(entry => string.Equals(entry.Outcome, "failed", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(entry.ApprovalSelectedKey, ApprovalOptionKeys.Deny, StringComparison.Ordinal)));
    }

    private static string CountLabel(int count, string singular, string plural) => count switch
    {
        0 => string.Empty,
        1 => $"1 {singular}",
        _ => $"{count} {plural}"
    };

    private static ChatPresentationState CommitTitle(
        ChatPresentationState state,
        SessionTitleOutput output,
        List<ChatPresentationEffect> effects)
    {
        var block = new ChatPresentationBlock(
            $"title:{output.TimestampMs}",
            ChatBlockKind.System,
            "TITLE",
            output.Title,
            $"Session title: {output.Title}",
            output.TimestampMs);
        return Commit(state with { SessionTitle = output.Title }, block, effects);
    }

    private static ChatPresentationState CommitUsage(
        ChatPresentationState state,
        UsageOutput output,
        List<ChatPresentationEffect> effects)
    {
        var usagePercent = output.UsagePercent;
        if (usagePercent is null && output.InputTokens is { } inputTokens && output.ContextWindowTokens > 0)
            usagePercent = (double)inputTokens / output.ContextWindowTokens;

        return Commit(
            state with { ContextUsagePercent = usagePercent },
            UsageBlock(output, state.CurrentTurnId),
            effects);
    }

    private static ChatPresentationState AppendAssistantDelta(
        ChatPresentationState state,
        TextDeltaOutput output)
    {
        var passages = EnsureOpenPassage(state.ReplyPassages, output.TimestampMs);
        var passage = passages[^1];
        return state with
        {
            ReplyPassages = passages.SetItem(passages.Count - 1, passage with
            {
                Text = passage.Text + output.Delta
            })
        };
    }

    private static ChatPresentationState FinalizeAssistantPassage(
        ChatPresentationState state,
        TextOutput? output)
    {
        var text = output?.Text;
        if (state.ReplyPassages.Count == 0)
        {
            if (string.IsNullOrEmpty(text))
                return state;

            return state with
            {
                ReplyPassages =
                [new ReplyPassagePresentation(0, output!.TimestampMs, text, true, [])]
            };
        }

        var passages = state.ReplyPassages;
        var passage = passages[^1];
        if (passage.IsFinal)
        {
            if (string.IsNullOrEmpty(text) || string.Equals(passage.Text, text, StringComparison.Ordinal))
                return state;

            var next = new ReplyPassagePresentation(
                passages.Count,
                output!.TimestampMs,
                text,
                true,
                []);
            return state with { ReplyPassages = passages.Add(next) };
        }

        var finalText = string.IsNullOrEmpty(text) ? passage.Text : text;
        if (string.IsNullOrEmpty(finalText) && passage.ToolCallIds.Count == 0)
            return state;

        return state with
        {
            ReplyPassages = passages.SetItem(passages.Count - 1, passage with
            {
                Text = finalText,
                IsFinal = true
            })
        };
    }

    private static ChatPresentationState FinalizeThought(
        ChatPresentationState state,
        ThinkingOutput output)
    {
        var text = string.IsNullOrEmpty(output.Text) ? state.ThoughtText : output.Text;
        return state with { ThoughtText = text };
    }

    private static ChatPresentationState RecordAgentPull(
        ChatPresentationState state,
        UserMessagesPulledOutput output)
    {
        if (state.AgentPulls.Any(pull => string.Equals(
                pull.BatchId,
                output.BatchId,
                StringComparison.Ordinal)))
        {
            return state;
        }

        var pull = new AgentPullPresentation(
            output.BatchId,
            output.TurnId.Value,
            output.TimestampMs,
            state.ReplyPassages.Count - 1,
            output.Messages.ToImmutableList());
        return state with
        {
            CurrentTurnId = output.TurnId.Value,
            AgentPulls = state.AgentPulls.Add(pull)
        };
    }

    private static ChatPresentationState StartTool(
        ChatPresentationState state,
        ToolCallOutput output)
    {
        var passages = EnsureToolPassage(state, output.TimestampMs);
        var passage = passages[^1];

        var tool = new ToolActivityPresentation(
            output.CallId.Value,
            output.ToolName.Value,
            output.Rationale,
            output.ArgumentsJson,
            output.FailureCode is null ? "queued" : "rejected",
            null,
            output.TimestampMs,
            state.CurrentTurnId,
            output.BatchId,
            output.BatchSize,
            passage.Index,
            null,
            null,
            output.FailureCode);
        passage = passage with { ToolCallIds = passage.ToolCallIds.Add(tool.CallId) };
        return state with
        {
            ReplyPassages = passages.SetItem(passages.Count - 1, passage),
            Tools = state.Tools.SetItem(tool.CallId, tool)
        };
    }

    private static ChatPresentationState UpdateTool(ChatPresentationState state, ToolActivityOutput output)
    {
        var key = output.CallId.Value;
        var existing = state.Tools.TryGetValue(key, out var tool)
            ? tool
            : new ToolActivityPresentation(
                key,
                output.ToolName.Value,
                null,
                null,
                output.Phase,
                output.Summary,
                output.TimestampMs,
                output.TurnId.Value,
                string.Empty,
                1,
                state.ReplyPassages.Count == 0 ? 0 : state.ReplyPassages[^1].Index,
                null,
                null,
                null);
        return state with
        {
            CurrentTurnId = output.TurnId.Value,
            Tools = state.Tools.SetItem(key, existing with
            {
                Phase = output.Phase,
                Summary = output.Summary,
                TurnId = output.TurnId.Value
            })
        };
    }

    private static ChatPresentationState CompleteTool(
        ChatPresentationState state,
        ToolResultOutput output)
    {
        var key = output.CallId.Value;
        var passages = state.ReplyPassages;
        if (!state.Tools.TryGetValue(key, out var active))
        {
            passages = EnsureToolPassage(state, output.TimestampMs);
            var passage = passages[^1];
            active = new ToolActivityPresentation(
                key,
                output.ToolName.Value,
                null,
                null,
                output.FailureCode is null ? "completed" : "rejected",
                null,
                output.TimestampMs,
                state.CurrentTurnId,
                string.Empty,
                1,
                passage.Index,
                output.Result,
                output.TimestampMs,
                output.FailureCode);
            passage = passage with { ToolCallIds = passage.ToolCallIds.Add(key) };
            passages = passages.SetItem(passages.Count - 1, passage);
        }
        else
        {
            active = active with
            {
                Phase = output.FailureCode is null ? "completed" : "rejected",
                Result = output.Result,
                CompletedAtMs = output.TimestampMs,
                FailureCode = output.FailureCode
            };
        }

        return state with
        {
            ReplyPassages = passages,
            Tools = state.Tools.SetItem(key, active)
        };
    }

    private static ChatPresentationState ReduceSubAgent(
        ChatPresentationState state,
        SubAgentOutput output)
    {
        var key = output.RunId?.Value ?? $"legacy:{output.AgentName.Value}";
        if (output.Phase != Actors.SubAgents.SubAgentPhase.Completed)
        {
            var current = state.SubAgents.TryGetValue(key, out var active)
                ? active
                : new SubAgentActivityPresentation(
                    key,
                    output.ParentCallId?.Value,
                    output.AgentName.Value,
                    output.Phase.ToString().ToLowerInvariant(),
                    output.ActivitySummary,
                    output.TimestampMs,
                    null,
                    null,
                    null,
                    null,
                    false);
            var activeToolName = ActiveSubAgentTool(output.ActivityPhase) ?? current.ActiveToolName;
            if (output.ActivityPhase is "processing tool results" or "calling the model")
                activeToolName = null;
            return state with
            {
                SubAgents = state.SubAgents.SetItem(key, current with
                {
                    Phase = output.ActivityPhase ?? output.Phase.ToString().ToLowerInvariant(),
                    Summary = output.ActivitySummary,
                    ActiveToolName = activeToolName
                })
            };
        }

        var outcome = output.Outcome.ToString().ToLowerInvariant();
        var detail = $"Run: {key}\nOutcome: {outcome}\nDuration: {output.Duration.TotalSeconds:F1}s"
                     + (output.OutcomeReason is null ? string.Empty : $"\nReason: {output.OutcomeReason.Value.Value}")
                     + (output.MemoryDecision is null ? string.Empty : $"\nMemory: {output.MemoryDecision}");
        var completed = state.SubAgents.TryGetValue(key, out var completedActive)
            ? completedActive
            : new SubAgentActivityPresentation(
                key,
                output.ParentCallId?.Value,
                output.AgentName.Value,
                "completed",
                output.ActivitySummary,
                output.TimestampMs,
                null,
                null,
                null,
                null,
                false);
        return state with
        {
            SubAgents = state.SubAgents.SetItem(key, completed with
            {
                Phase = "completed",
                Summary = output.ActivitySummary,
                ActiveToolName = null,
                CompletedAtMs = output.TimestampMs,
                Outcome = outcome,
                Detail = detail,
                IsFailure = output.Outcome == SubAgentRunOutcome.Failed
            })
        };
    }

    private static string? ActiveSubAgentTool(string? phase)
    {
        const string prefix = "running tools: ";
        if (phase is null || !phase.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        return phase[prefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static ChatPresentationState ShowApproval(
        ChatPresentationState state,
        ToolInteractionRequest approval,
        List<ChatPresentationEffect> effects)
    {
        if (state.ApprovalQueuePosition(approval.CallId.Value) > 0)
            return state;

        effects.Add(new ChatPresentationEffect.ShowApproval(approval));
        effects.Add(new ChatPresentationEffect.SetStatus("Approval required"));
        effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        return state with { PendingApprovals = state.PendingApprovals.Enqueue(approval) };
    }

    private static ChatPresentationState ResolveApproval(
        ChatPresentationState state,
        ApprovalOutcomeOutput output,
        List<ChatPresentationEffect> effects)
    {
        var requester = state.SubAgents.Values.FirstOrDefault(run =>
            output.ParentCallId.Length > 0
            && string.Equals(run.ParentCallId, output.ParentCallId, StringComparison.Ordinal));
        var path = requester is null
            ? output.ParentCallId.Length > 0
                ? $"sub-agent › {output.ToolName.Value}"
                : output.ToolName.Value
            : $"{requester.AgentName} › {output.ToolName.Value}";
        var decision = ApprovalDecisionText(output.SelectedKey.Value);
        var detail = $"Tool: {output.ToolName.Value}\nCall: {output.CallId.Value}"
                     + (output.ParentCallId.Length == 0 ? string.Empty : $"\nParent call: {output.ParentCallId}")
                     + $"\nDecision: {decision}";
        var block = new ChatPresentationBlock(
            $"approval:{output.CallId.Value}:{output.TimestampMs}",
            ChatBlockKind.Approval,
            "APPROVAL",
            $"{path}  {decision}",
            $"Approval: {path}\n{detail}",
            output.TimestampMs,
            state.CurrentTurnId,
            detail,
            string.Equals(output.SelectedKey.Value, ApprovalOptionKeys.Deny, StringComparison.Ordinal));
        var remaining = ImmutableQueue.CreateRange(state.PendingApprovals.Where(request =>
            !string.Equals(request.CallId.Value, output.CallId.Value, StringComparison.Ordinal)));
        state = state with
        {
            PendingApprovals = remaining,
            CompletedApprovals = state.CompletedApprovals.Add(block)
        };
        effects.Add(new ChatPresentationEffect.SetStatus(
            remaining.IsEmpty ? "Generating..." : "Approval required"));
        effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        return state;
    }

    private static ChatPresentationState CompleteTurn(
        ChatPresentationState state,
        TurnCompleted completed,
        List<ChatPresentationEffect> effects)
    {
        state = FinalizeAssistantPassage(state, null);
        var incompleteTools = state.Tools.Values
            .Where(tool => tool.CompletedAtMs is null)
            .OrderBy(tool => tool.StartedAtMs)
            .ToList();

        if (state.ReplyPassages.Count > 0
            || state.Tools.Count > 0
            || state.SubAgents.Count > 0
            || state.CompletedApprovals.Count > 0)
        {
            state = Commit(state, BuildSettledReply(state, completed, incompleteTools), effects);
        }

        foreach (var subAgent in state.SubAgents.Values
                     .Where(run => run.CompletedAtMs is null)
                     .OrderBy(run => run.StartedAtMs))
        {
            state = Commit(state, DiagnosticBlock(
                $"subagent:{subAgent.RunId}:incomplete",
                $"Sub-agent '{subAgent.AgentName}' ended without a terminal event.",
                completed.TimestampMs,
                state.CurrentTurnId), effects);
        }

        effects.Add(new ChatPresentationEffect.ClearApproval());
        effects.Add(new ChatPresentationEffect.SetStatus("Ready"));
        effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        return state with
        {
            Tools = state.Tools.Clear(),
            SubAgents = state.SubAgents.Clear(),
            ReplyPassages = [],
            ThoughtText = string.Empty,
            PendingApprovals = ImmutableQueue<ToolInteractionRequest>.Empty,
            CompletedApprovals = [],
            AgentPulls = [],
            IsProcessing = false,
            TurnNumber = Math.Max(state.TurnNumber + 1, completed.TurnNumber.Value + 1),
            CurrentTurnId = null
        };
    }

    private static ChatPresentationBlock BuildSettledReply(
        ChatPresentationState state,
        TurnCompleted completed,
        IReadOnlyCollection<ToolActivityPresentation> incompleteTools)
    {
        var prose = string.Join("\n\n", state.ReplyPassages
            .Select(passage => passage.Text.Trim())
            .Where(text => text.Length > 0));
        var rejectedToolCount = state.Tools.Count(tool => tool.Value.FailureCode is not null);
        var requestedToolCount = state.Tools.Count - rejectedToolCount;
        var completedToolCount = requestedToolCount - incompleteTools.Count;
        var toolReceipt = requestedToolCount switch
        {
            0 => string.Empty,
            1 when incompleteTools.Count == 0 => "1 tool",
            _ when incompleteTools.Count == 0 => $"{requestedToolCount} tools",
            _ => $"{completedToolCount}/{requestedToolCount} tools completed"
        };
        var rejectedToolReceipt = CountLabel(
            rejectedToolCount,
            "rejected request",
            "rejected requests");
        var completedAgentCount = state.SubAgents.Count(run => run.Value.CompletedAtMs is not null);
        var agentReceipt = completedAgentCount switch
        {
            0 => string.Empty,
            1 => "1 agent",
            _ => $"{completedAgentCount} agents"
        };
        var approvalReceipt = state.CompletedApprovals.Count switch
        {
            0 => string.Empty,
            1 => "1 decision",
            _ => $"{state.CompletedApprovals.Count} decisions"
        };
        var pulledMessageCount = state.AgentPulls.Sum(pull => pull.Messages.Count);
        var pullReceipt = CountLabel(pulledMessageCount, "follow-up", "follow-ups");
        var receiptParts = new[] { toolReceipt, rejectedToolReceipt, agentReceipt, approvalReceipt, pullReceipt }
            .Where(value => value.Length > 0)
            .ToArray();
        var receipt = receiptParts.Length == 0
            ? string.Empty
            : $"{(incompleteTools.Count == 0 ? "Completed work" : "Work stopped")}  · {string.Join("  · ", receiptParts)}";
        var summary = string.Join("\n\n", new[] { prose, receipt }
            .Where(value => value.Length > 0));

        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.ThoughtText))
            detailParts.Add($"Reasoning:\n{state.ThoughtText.Trim()}");
        if (state.Tools.Count > 0)
        {
            var toolDetail = state.Tools.Values
                .OrderBy(tool => tool.PassageIndex)
                .ThenBy(tool => tool.StartedAtMs)
                .Select(ToolDetail);
            detailParts.Add($"Work trace:\n{string.Join("\n\n", toolDetail)}");
        }
        if (state.SubAgents.Count > 0)
        {
            var agentDetail = state.SubAgents.Values
                .OrderBy(run => run.StartedAtMs)
                .Select(SubAgentDetail);
            detailParts.Add($"Agent trace:\n{string.Join("\n\n", agentDetail)}");
        }
        if (state.CompletedApprovals.Count > 0)
        {
            detailParts.Add($"Decisions:\n{string.Join("\n\n", state.CompletedApprovals.Select(
                approval => approval.Detail ?? approval.Summary))}");
        }
        if (state.AgentPulls.Count > 0)
        {
            var pullDetail = state.AgentPulls.Select(pull =>
                $"Pulled by agent:\n{string.Join("\n", pull.Messages.Select(message => message.Content))}");
            detailParts.Add($"User steering:\n{string.Join("\n\n", pullDetail)}");
        }

        var detail = string.Join("\n\n", new[] { prose }
            .Concat(detailParts)
            .Where(value => value.Length > 0));
        var semanticText = detail.Length == 0 ? "NETCLAW" : $"NETCLAW\n{detail}";
        var firstTimestamp = state.ReplyPassages.Count > 0
            ? state.ReplyPassages[0].StartedAtMs
            : state.Tools.Count > 0
                ? state.Tools.Values.Min(tool => tool.StartedAtMs)
                : state.SubAgents.Count > 0
                    ? state.SubAgents.Values.Min(run => run.StartedAtMs)
                    : state.CompletedApprovals.Count > 0
                        ? state.CompletedApprovals.Min(approval => approval.TimestampMs)
                        : state.AgentPulls.Min(pull => pull.TimestampMs);
        return new ChatPresentationBlock(
            $"turn:{state.TurnNumber}:reply",
            ChatBlockKind.Assistant,
            "NETCLAW",
            summary,
            semanticText,
            firstTimestamp,
            state.CurrentTurnId,
            detail,
            incompleteTools.Count > 0
            || state.CompletedApprovals.Any(approval => approval.IsFailure)
            || completed.Outcome == TurnOutcome.Failed);
    }

    private static string ToolDetail(ToolActivityPresentation tool)
    {
        var duration = tool.CompletedAtMs is { } completedAt
            ? $"\nDuration: {Math.Max(0, completedAt - tool.StartedAtMs)} ms"
            : string.Empty;
        return $"{ToolWorkTitle(tool)}\nTool: {tool.ToolName}\nCall: {tool.CallId}\nState: {tool.Phase}"
               + (tool.ArgumentsJson is null ? string.Empty : $"\nArguments: {tool.ArgumentsJson}")
               + (tool.Result is null ? string.Empty : $"\nResult: {tool.Result}")
               + duration;
    }

    private static string SubAgentDetail(SubAgentActivityPresentation run) =>
        $"{run.AgentName}\nRun: {run.RunId}\nState: {run.Outcome ?? run.Phase}"
        + (run.Summary is null ? string.Empty : $"\nActivity: {run.Summary}")
        + (run.Detail is null ? string.Empty : $"\n{run.Detail}");

    private static ImmutableList<ReplyPassagePresentation> EnsureOpenPassage(
        ImmutableList<ReplyPassagePresentation> passages,
        long timestampMs)
    {
        if (passages.Count > 0 && !passages[^1].IsFinal)
            return passages;

        return passages.Add(new ReplyPassagePresentation(
            passages.Count,
            timestampMs,
            string.Empty,
            false,
            []));
    }

    private static ImmutableList<ReplyPassagePresentation> EnsureToolPassage(
        ChatPresentationState state,
        long timestampMs)
    {
        if (state.ReplyPassages.Count == 0)
        {
            return
            [
                new ReplyPassagePresentation(0, timestampMs, string.Empty, true, [])
            ];
        }

        var last = state.ReplyPassages[^1];
        var hasActiveTool = last.ToolCallIds.Any(callId =>
            state.Tools.TryGetValue(callId, out var tool) && tool.CompletedAtMs is null);
        if (last.ToolCallIds.Count == 0 || hasActiveTool)
        {
            return state.ReplyPassages.SetItem(state.ReplyPassages.Count - 1, last with
            {
                IsFinal = true
            });
        }

        return state.ReplyPassages.Add(new ReplyPassagePresentation(
            state.ReplyPassages.Count,
            timestampMs,
            string.Empty,
            true,
            []));
    }

    private static ChatPresentationState Commit(
        ChatPresentationState state,
        ChatPresentationBlock block,
        List<ChatPresentationEffect> effects)
    {
        effects.Add(new ChatPresentationEffect.Commit(block));
        return state with { Transcript = state.Transcript.Add(block) };
    }

    private static ChatPresentationBlock ResumeBlock(SessionTranscriptEntry entry, int index)
    {
        var kind = entry.Type switch
        {
            SessionTranscriptEntryTypes.User => ChatBlockKind.User,
            SessionTranscriptEntryTypes.Assistant => ChatBlockKind.Assistant,
            SessionTranscriptEntryTypes.Tool => ChatBlockKind.Tool,
            SessionTranscriptEntryTypes.Approval => ChatBlockKind.Approval,
            SessionTranscriptEntryTypes.SubAgent => ChatBlockKind.SubAgent,
            SessionTranscriptEntryTypes.File => ChatBlockKind.File,
            SessionTranscriptEntryTypes.Error => ChatBlockKind.Error,
            SessionTranscriptEntryTypes.Usage => ChatBlockKind.Usage,
            SessionTranscriptEntryTypes.Compaction => ChatBlockKind.Compaction,
            _ => ChatBlockKind.Diagnostic
        };
        var label = Label(kind);
        var summary = kind switch
        {
            ChatBlockKind.User or ChatBlockKind.Assistant => entry.Text ?? string.Empty,
            ChatBlockKind.Tool => $"{ToolWorkTitle(entry.Rationale)}  · {entry.ToolName ?? "unknown"}",
            ChatBlockKind.Approval => $"{entry.ToolName ?? "unknown"}  {ApprovalDecisionText(entry.ApprovalSelectedKey)}",
            ChatBlockKind.SubAgent => $"{entry.AgentName ?? "sub-agent"}  {entry.Outcome ?? "complete"}",
            ChatBlockKind.File => $"{entry.FileName ?? "file"}  {entry.FilePath}",
            ChatBlockKind.Error => entry.ErrorMessage ?? "Unknown error",
            ChatBlockKind.Usage => UsageSummary(entry),
            ChatBlockKind.Compaction => $"{entry.MessagesBefore ?? 0} → {entry.MessagesAfter ?? 0} messages",
            _ => entry.Text ?? $"Unsupported transcript entry: {entry.Type}"
        };
        var detail = ResumeDetail(entry);
        var identity = entry.CallId ?? entry.RunId ?? entry.TurnId ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ChatPresentationBlock(
            $"resume:{entry.Type}:{identity}:{index}",
            kind,
            label,
            summary,
            $"{label}\n{detail}",
            entry.TimestampMs,
            entry.TurnId,
            detail,
            kind == ChatBlockKind.Error || string.Equals(entry.Outcome, "failed", StringComparison.Ordinal));
    }

    private static ChatPresentationBlock UsageBlock(UsageOutput usage, string? turnId)
    {
        var summary = $"{usage.InputTokens ?? 0} in  {usage.OutputTokens ?? 0} out"
                      + (usage.ReasoningTokens is > 0 ? $"  {usage.ReasoningTokens} thought" : string.Empty)
                      + (usage.UsagePercent is not null ? $"  {usage.UsagePercent:P0} context" : string.Empty);
        var detail = $"Input tokens: {usage.InputTokens ?? 0}\nOutput tokens: {usage.OutputTokens ?? 0}"
                     + $"\nCached input tokens: {usage.CachedInputTokens ?? 0}"
                     + $"\nReasoning tokens: {usage.ReasoningTokens ?? 0}"
                     + (usage.PromptMs is null ? string.Empty : $"\nPrompt time: {usage.PromptMs:F1} ms")
                     + (usage.PredictedPerSecond is null ? string.Empty : $"\nSpeed: {usage.PredictedPerSecond:F1} tokens/s");
        return new ChatPresentationBlock(
            $"usage:{usage.TimestampMs}",
            ChatBlockKind.Usage,
            "USAGE",
            summary,
            $"Usage\n{detail}",
            usage.TimestampMs,
            turnId,
            detail);
    }

    private static ChatPresentationBlock ErrorBlock(ErrorOutput error, string? turnId)
    {
        var detail = $"Category: {error.Category}\nCorrelation: {error.CorrelationId:D}"
                     + (error.Cause is null ? string.Empty : $"\n{error.Cause}");
        return new ChatPresentationBlock(
            $"error:{error.CorrelationId:D}",
            ChatBlockKind.Error,
            "ERROR",
            error.Message,
            $"Error: {error.Message}\n{detail}",
            error.TimestampMs,
            turnId,
            detail,
            true);
    }

    private static ChatPresentationBlock FileBlock(FileOutput file, string? turnId)
    {
        var detail = $"Name: {file.FileName}\nType: {file.MimeType.Value}\nPath: {file.FilePath}";
        return new ChatPresentationBlock(
            $"file:{file.TimestampMs}:{file.FilePath}",
            ChatBlockKind.File,
            "FILE",
            $"{file.FileName}  {file.MimeType.Value}",
            detail,
            file.TimestampMs,
            turnId,
            detail);
    }

    private static ChatPresentationBlock CompactionBlock(CompactionOutput output, string? turnId)
    {
        var detail = $"Messages: {output.MessagesBefore} → {output.MessagesAfter}"
                     + $"\nTool results cleared: {output.ToolResultsCleared}"
                     + $"\nSummary created: {output.Summarized}"
                     + $"\nInput tokens: {output.PreCompactionInputTokens}"
                     + $"\nKeep count: {output.KeepCountUsed}";
        return new ChatPresentationBlock(
            $"compaction:{output.TimestampMs}",
            ChatBlockKind.Compaction,
            "CONTEXT",
            $"{output.MessagesBefore} → {output.MessagesAfter} messages",
            $"Context compaction\n{detail}",
            output.TimestampMs,
            turnId,
            detail);
    }

    private static ChatPresentationBlock DiagnosticBlock(
        string key,
        string text,
        long timestampMs,
        string? turnId) => new(
        key,
        ChatBlockKind.Diagnostic,
        "DIAGNOSTIC",
        text,
        text,
        timestampMs,
        turnId,
        text,
        true);

    private static string ResumeDetail(SessionTranscriptEntry entry) => entry.Type switch
    {
        SessionTranscriptEntryTypes.User or SessionTranscriptEntryTypes.Assistant => entry.Text ?? string.Empty,
        SessionTranscriptEntryTypes.Tool => $"Tool: {entry.ToolName ?? "unknown"}\nCall: {entry.CallId ?? "unknown"}"
                                            + (entry.Rationale is null ? string.Empty : $"\nRationale: {entry.Rationale}")
                                            + (entry.ArgumentsJson is null ? string.Empty : $"\nArguments: {entry.ArgumentsJson}")
                                            + $"\nResult: {entry.Result ?? string.Empty}",
        SessionTranscriptEntryTypes.Approval => $"Tool: {entry.ToolName ?? "unknown"}\nCall: {entry.CallId ?? "unknown"}"
                                                + (string.IsNullOrEmpty(entry.ParentCallId)
                                                    ? string.Empty
                                                    : $"\nParent call: {entry.ParentCallId}")
                                                + $"\nDecision: {ApprovalDecisionText(entry.ApprovalSelectedKey)}",
        SessionTranscriptEntryTypes.SubAgent => $"Agent: {entry.AgentName ?? "unknown"}\nRun: {entry.RunId ?? "unknown"}"
                                                + $"\nOutcome: {entry.Outcome ?? "unknown"}"
                                                + (entry.OutcomeReason is null ? string.Empty : $"\nReason: {entry.OutcomeReason}"),
        SessionTranscriptEntryTypes.File => $"Name: {entry.FileName}\nType: {entry.MimeType}\nPath: {entry.FilePath}",
        SessionTranscriptEntryTypes.Error => $"Error: {entry.ErrorMessage}\nCategory: {entry.ErrorCategory}"
                                             + $"\nCorrelation: {entry.ErrorCorrelationId}"
                                             + (entry.ErrorDetail is null ? string.Empty : $"\n{entry.ErrorDetail}"),
        SessionTranscriptEntryTypes.Usage => UsageSummary(entry),
        SessionTranscriptEntryTypes.Compaction => $"Messages: {entry.MessagesBefore ?? 0} → {entry.MessagesAfter ?? 0}",
        _ => entry.Text ?? $"Unsupported transcript entry: {entry.Type}"
    };

    private static string UsageSummary(SessionTranscriptEntry entry) =>
        $"{entry.InputTokens ?? 0} in  {entry.OutputTokens ?? 0} out"
        + (entry.ReasoningTokens is > 0 ? $"  {entry.ReasoningTokens} thought" : string.Empty);

    private static string Label(ChatBlockKind kind) => kind switch
    {
        ChatBlockKind.User => "YOU",
        ChatBlockKind.Assistant => "NETCLAW",
        ChatBlockKind.Thought => "THOUGHT",
        ChatBlockKind.Tool => "TOOL",
        ChatBlockKind.Parallel => "PARALLEL",
        ChatBlockKind.SubAgent => "AGENT",
        ChatBlockKind.Approval => "APPROVAL",
        ChatBlockKind.File => "FILE",
        ChatBlockKind.Error => "ERROR",
        ChatBlockKind.Usage => "USAGE",
        ChatBlockKind.Compaction => "CONTEXT",
        _ => "DIAGNOSTIC"
    };

    private static string ToolWorkTitle(string? rationale) => string.IsNullOrWhiteSpace(rationale)
        ? "No rationale supplied"
        : rationale.Trim();

    internal static string ToolWorkTitle(ToolActivityPresentation tool) => tool.FailureCode switch
    {
        "invalid_rationale" => "Rejected tool request · rationale missing",
        _ => ToolWorkTitle(tool.Rationale)
    };

    private static ChatPresentationState CommitParallelGroup(
        ChatPresentationState state,
        string batchId,
        int batchSize,
        long timestampMs,
        string? turnId,
        List<ChatPresentationEffect> effects)
    {
        var block = new ChatPresentationBlock(
            $"parallel:{batchId}",
            ChatBlockKind.Parallel,
            "PARALLEL",
            $"{batchSize} tool calls",
            $"Parallel tool batch: {batchId}\nCalls: {batchSize}",
            timestampMs,
            turnId,
            $"Batch: {batchId}\nCalls: {batchSize}");
        return Commit(
            state with { CommittedToolBatches = state.CommittedToolBatches.Add(batchId) },
            block,
            effects);
    }

    private static string ApprovalDecisionText(string? selectedKey) => selectedKey switch
    {
        ApprovalOptionKeys.ApproveOnce => "approved once",
        ApprovalOptionKeys.ApproveSession => "approved for this chat",
        ApprovalOptionKeys.ApproveAlways => "approved for this directory",
        ApprovalOptionKeys.ApproveEverywhere => "approved everywhere",
        ApprovalOptionKeys.Deny => "denied",
        _ => "resolved"
    };
}
