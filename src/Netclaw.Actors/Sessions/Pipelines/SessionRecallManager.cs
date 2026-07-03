// -----------------------------------------------------------------------
// <copyright file="SessionRecallManager.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    /// Returns empty when the memory subsystem is disabled or the audience is Public.
    /// </summary>
    public AutomaticRecallResult ResolveForTurn(
        string? recallQuery,
        SessionState state,
        SessionId sessionId,
        MessageSource? turnSource,
        IMemoryRecallCoordinator coordinator,
        MemoryConfig memoryConfig,
        TurnContext? turnContext = null)
    {
        var audience = turnContext?.Audience
            ?? turnSource?.Audience
            ?? SecurityPolicyDefaults.ResolveAudienceFromSessionId(sessionId.Value);

        // Memory recall is disabled for Public audience or when the subsystem is off
        if (audience == TrustAudience.Public || !memoryConfig.Enabled)
            return new AutomaticRecallResult([]);

        var query = string.IsNullOrWhiteSpace(recallQuery)
            ? state.FindLastUserMessage()?.Content ?? string.Empty
            : recallQuery;

        if (string.IsNullOrWhiteSpace(query))
            return new AutomaticRecallResult([]);
        var recentUser = state.History
            .Where(x => x.Role == Protocol.ChatRole.User && !SessionState.IsSystemNudge(x))
            .Select(x => x.Content)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(3)
            .ToArray();

        var request = new AutomaticRecallRequest(
            sessionId,
            query,
            recentUser,
            memoryConfig.AutoRecallMaxItems,
            Audience: audience,
            Boundary: turnContext?.Boundary.Value
                      ?? turnSource?.Boundary.Value
                      ?? SecurityPolicyDefaults.ResolveBoundaryFromSessionId(sessionId.Value, audience).Value,
            RecentAssistantMessages: state.History
                .Where(x => x.Role == Protocol.ChatRole.Assistant)
                .Select(x => x.Content)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .TakeLast(3)
                .ToArray(),
            RecentEntities: [],
            ThreadTitle: state.Title);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(memoryConfig.RecallTimeoutMs));
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
                .Where(i => !_injectedMemoryIds.Contains(i.Id.Value))
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
            _injectedMemoryIds.Add(item.Id.Value);

        return resolved;
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
