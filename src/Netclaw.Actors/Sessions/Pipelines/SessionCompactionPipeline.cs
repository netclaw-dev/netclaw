using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Bundles the many parameters needed by the compaction pipeline into a single record.
/// </summary>
internal sealed record CompactionParameters(
    SessionId SessionId,
    long PreCompactionInputTokens,
    int KeepRecentToolResults,
    int KeepRecentMessages,
    int ContextWindowTokens,
    TimeSpan SidecarTimeout,
    TimeSpan CompactionTimeout);

/// <summary>
/// Async pipeline for tiered context compaction. Runs on the thread pool and
/// sends results back to the session actor via <c>self.Tell()</c>.
/// </summary>
internal static class SessionCompactionPipeline
{
    public static async Task ExecuteAsync(
        SessionState stateSnapshot,
        CompactionParameters parameters,
        IChatClient client,
        IActorRef self,
        ILoggingAdapter log,
        long operationId)
    {
        try
        {
            using var cts = new CancellationTokenSource(parameters.CompactionTimeout);

            var messagesBefore = stateSnapshot.History.Count;
            var (compactionState, clearedCount) = stateSnapshot.ClearOldToolResults(parameters.KeepRecentToolResults);
            var history = compactionState.History;

            if (clearedCount > 0)
            {
                log.Info("Phase 1: Cleared {ClearedCount} old tool result(s)", clearedCount);
            }

            var systemOffset = history.Count > 0 && history[0].Role == Protocol.ChatRole.System ? 1 : 0;
            var effectiveKeepCount = parameters.KeepRecentMessages;
            List<SerializableChatMessage> compactedMessages;
            int discardStartIndex;

            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();

                var reducer = new ExtractiveSessionReducer(effectiveKeepCount);
                var meaiMessages = ChatMessageConverter.ToAiMessages(history);
                var reduced = await reducer.ReduceAsync(meaiMessages, cts.Token);

                var reducedList = reduced as IList<Microsoft.Extensions.AI.ChatMessage> ?? reduced.ToList();
                var keptNonSystemCount = reducedList.Count(m => m.Role != Microsoft.Extensions.AI.ChatRole.System);

                var startIndex = history.Count - keptNonSystemCount;
                discardStartIndex = startIndex;
                compactedMessages = [];
                for (var i = Math.Max(systemOffset, startIndex); i < history.Count; i++)
                {
                    compactedMessages.Add(history[i]);
                }

                var estimatedTokens = EstimateTokens(compactedMessages, systemOffset > 0 ? history[0] : null);
                var budgetHalf = parameters.ContextWindowTokens / 2;

                if (estimatedTokens <= budgetHalf || effectiveKeepCount <= 2)
                    break;

                var newKeepCount = Math.Max(2, effectiveKeepCount / 2);
                log.Info("Adaptive compaction: estimated {EstimatedTokens} tokens > {Budget} budget half, reducing keep count {OldKeep} -> {NewKeep}",
                    estimatedTokens, budgetHalf, effectiveKeepCount, newKeepCount);
                effectiveKeepCount = newKeepCount;
            }

            log.Info("Phase 2: Extractive reduction (history={HistoryCount} -> {KeptCount} messages, keepCount={KeepCount})",
                history.Count, compactedMessages.Count + systemOffset, effectiveKeepCount);

            var observationText = await GenerateObservationsAsync(
                client,
                parameters.SessionId,
                history,
                systemOffset,
                discardStartIndex,
                parameters.SidecarTimeout,
                log,
                cts.Token);

            if (!string.IsNullOrWhiteSpace(observationText))
            {
                var observationMsg = new SerializableChatMessage
                {
                    Role = Protocol.ChatRole.User,
                    Content = ObservationPromptBuilder.WrapObservations(observationText, parameters.SessionId)
                };
                compactedMessages.Insert(0, observationMsg);

                log.Info("Observer: generated {ObsLength} chars of observations from {DiscardedCount} discarded messages",
                    observationText.Length, Math.Max(0, discardStartIndex - systemOffset));
            }

            self.Tell(new CompactionWorkCompleted
            {
                OperationId = operationId,
                Summary = observationText ?? string.Empty,
                CompactedMessages = compactedMessages,
                MessagesBefore = messagesBefore,
                ClearedCount = clearedCount,
                PreCompactionInputTokens = parameters.PreCompactionInputTokens,
                KeepCountUsed = effectiveKeepCount
            });
        }
        catch (Exception ex)
        {
            self.Tell(new CompactionWorkFailed
            {
                OperationId = operationId,
                Cause = ex
            });
        }
    }

    /// <summary>
    /// Observer phase: compress discarded messages into observation notes via LLM call.
    /// Returns null if no messages to compress or if the LLM call fails (graceful degradation).
    /// </summary>
    public static async Task<string?> GenerateObservationsAsync(
        IChatClient client,
        SessionId sessionId,
        IReadOnlyList<SerializableChatMessage> history,
        int systemOffset,
        int keepStartIndex,
        TimeSpan sidecarTimeout,
        ILoggingAdapter log,
        CancellationToken cancellationToken)
    {
        var discardedMessages = new List<SerializableChatMessage>();
        for (var i = systemOffset; i < keepStartIndex && i < history.Count; i++)
        {
            discardedMessages.Add(history[i]);
        }

        if (discardedMessages.Count == 0)
            return null;

        // Lift any prior [session-summary ...] block out of the discarded
        // window and include it in the observer's system prompt with an
        // explicit "preserve verbatim" instruction. Structural defense
        // against summary-over-summary decay: the prior summary becomes
        // an anchor the model sees before the new material, reducing the
        // chance of rewrite or decay.
        var (priorSummary, remainingDiscarded) =
            ObservationPromptBuilder.ExtractPriorSummary(discardedMessages);

        if (priorSummary is not null)
            log.Info("Observer: lifting prior session summary ({Length} chars) into system prompt for verbatim preservation", priorSummary.Length);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(sidecarTimeout);
            var observerMessages = new List<AiChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System,
                    ObservationPromptBuilder.BuildObservationSystemPrompt(sessionId, priorSummary)),
                new(Microsoft.Extensions.AI.ChatRole.User,
                    ObservationPromptBuilder.BuildObservationUserPrompt(remainingDiscarded))
            };

            var response = await client.GetResponseAsync(
                observerMessages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                log.Warning("Observer returned empty observation text -- falling back to extractive-only");
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Observer LLM call failed -- falling back to extractive-only compaction");
            return null;
        }
    }

    /// <summary>
    /// Naive token estimation: total character count / 4.
    /// Includes the system prompt (if present) plus all compacted messages.
    /// </summary>
    public static int EstimateTokens(
        List<SerializableChatMessage> messages,
        SerializableChatMessage? systemPrompt)
    {
        var totalChars = 0;
        if (systemPrompt is not null)
            totalChars += systemPrompt.Content?.Length ?? 0;
        foreach (var msg in messages)
        {
            totalChars += msg.Content?.Length ?? 0;
            foreach (var tc in msg.ToolCalls)
                totalChars += tc.ArgumentsJson?.Length ?? 0;
        }
        return totalChars / 4;
    }
}
