using System.Text;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Manages per-turn memory recall state. Owns the turn recall cache and the
/// progressive recall exclusion set. Only accessed from the actor's mailbox thread.
/// </summary>
internal sealed class SessionRecallManager
{
    private AutomaticRecallResult? _turnRecallCache;
    private readonly HashSet<string> _injectedMemoryIds = new(StringComparer.Ordinal);

    /// <summary>
    /// The cached recall result for the current turn, or null if not yet resolved.
    /// </summary>
    public AutomaticRecallResult? TurnRecallCache => _turnRecallCache;

    /// <summary>
    /// Resolves the recall bundle for the current turn. Caches the result so
    /// subsequent calls within the same turn reuse it.
    /// </summary>
    public AutomaticRecallResult ResolveForTurn(
        string? recallQuery,
        SessionState state,
        SessionId sessionId,
        MessageSource? turnSource,
        IMemoryRecallCoordinator coordinator)
    {
        var query = string.IsNullOrWhiteSpace(recallQuery)
            ? state.FindLastUserMessage()?.Content ?? string.Empty
            : recallQuery;

        if (string.IsNullOrWhiteSpace(query))
            return new AutomaticRecallResult([]);

        var audience = turnSource?.Audience
            ?? SecurityPolicyDefaults.ResolveAudienceFromSessionId(sessionId.Value);
        var recentUser = state.History
            .Where(x => x.Role == Protocol.ChatRole.User && !SessionState.IsSystemNudge(x))
            .Select(x => x.Content)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(3)
            .ToArray();

        var request = new AutomaticRecallRequest(
            sessionId.Value,
            query,
            recentUser,
            3,
            Audience: audience,
            Boundary: turnSource?.Boundary
                      ?? SecurityPolicyDefaults.ResolveBoundaryFromSessionId(sessionId.Value, audience),
            RecentAssistantMessages: state.History
                .Where(x => x.Role == Protocol.ChatRole.Assistant)
                .Select(x => x.Content)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .TakeLast(3)
                .ToArray(),
            RecentEntities: [],
            HardScopeOverride: sessionId.ToMemoryDomain(),
            ThreadTitle: state.Title);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            return coordinator.RecallAsync(request, cts.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return new AutomaticRecallResult([], true, ex.Message, "resolution");
        }
    }

    /// <summary>
    /// Applies exclusion-based progressive recall filtering and caches the result.
    /// Returns the filtered recall result and tracks injected memory IDs.
    /// </summary>
    public AutomaticRecallResult ApplyProgressiveRecall(AutomaticRecallResult resolved, ILoggingAdapter log)
    {
        // Exclusion-based progressive recall: filter out already-injected memories
        if (_injectedMemoryIds.Count > 0 && resolved.Items.Count > 0)
        {
            var filtered = resolved.Items
                .Where(i => !_injectedMemoryIds.Contains(i.Id))
                .ToArray();

            if (filtered.Length == 0 && resolved.Items.Count > 0)
            {
                log.Info(
                    "progressive_recall_exhausted allCandidatesAlreadyInjected={0} totalInjected={1}",
                    resolved.Items.Count,
                    _injectedMemoryIds.Count);
            }

            resolved = new AutomaticRecallResult(filtered, resolved.Degraded, resolved.DegradeReason, resolved.DegradeStage);
        }

        _turnRecallCache = resolved;

        // Track injected IDs for progressive recall across turns
        foreach (var item in resolved.Items)
            _injectedMemoryIds.Add(item.Id);

        return resolved;
    }

    /// <summary>
    /// Injects automatic recall results into the transient message list as a system message.
    /// </summary>
    public static void InjectIntoMessages(List<AiChatMessage> messages, AutomaticRecallResult recall)
    {
        if (recall.Degraded)
        {
            var degraded = new AiChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                "[memory-recall]\nstatus: degraded\nreason: automatic recall unavailable for this turn");
            var insertAt = messages.FindLastIndex(m => m.Role == Microsoft.Extensions.AI.ChatRole.System);
            messages.Insert(insertAt >= 0 ? insertAt + 1 : 0, degraded);
            return;
        }

        if (recall.Items.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("[memory-recall]");
        sb.AppendLine("status: healthy");
        sb.AppendLine("mode: automatic");
        foreach (var item in recall.Items)
        {
            sb.AppendLine($"- {item.Title} [{item.Id}] domain={item.Domain} sensitivity={item.Sensitivity} score={item.Score:F2}");
            sb.AppendLine($"  {item.Content}");
        }

        var recallMessage = new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, sb.ToString().TrimEnd());
        var index = messages.FindLastIndex(m => m.Role == Microsoft.Extensions.AI.ChatRole.System);
        messages.Insert(index >= 0 ? index + 1 : 0, recallMessage);
    }

    /// <summary>
    /// Formats recall results for persistence in session history.
    /// </summary>
    public static string FormatForHistory(AutomaticRecallResult recall)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[recalled-memories]");
        foreach (var item in recall.Items)
        {
            sb.AppendLine($"- {item.Title}: {item.Content}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Resets the turn recall cache for a new turn boundary.
    /// </summary>
    public void ResetForNewTurn() => _turnRecallCache = null;

    /// <summary>
    /// Resets all recall state after compaction (cache + progressive exclusion set).
    /// </summary>
    public void ResetForCompaction()
    {
        _turnRecallCache = null;
        _injectedMemoryIds.Clear();
    }
}
