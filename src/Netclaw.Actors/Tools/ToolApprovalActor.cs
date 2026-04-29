// -----------------------------------------------------------------------
// <copyright file="ToolApprovalActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ToolApprovalActor : ReceiveActor
{
    private readonly ToolApprovalStore? _persistentStore;
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _sessionApprovals = new(StringComparer.Ordinal);

    public ToolApprovalActor(ToolApprovalStore? persistentStore = null)
    {
        _persistentStore = persistentStore;

        Receive<GetUnapprovedPatterns>(msg =>
        {
            var unapproved = new List<string>(msg.Patterns.Count);

            foreach (var pattern in msg.Patterns)
            {
                if (!IsApproved(msg.SessionId, msg.Audience, msg.ToolName, pattern))
                    unapproved.Add(pattern);
            }

            Sender.Tell(new UnapprovedPatternsResponse(unapproved));
        });

        Receive<RecordToolApproval>(msg =>
        {
            foreach (var pattern in msg.Patterns)
            {
                AddSessionApproval(msg.SessionId, msg.Audience, msg.ToolName, pattern);

                if (msg.Persistent)
                    _persistentStore?.AddApproval(msg.Audience, msg.ToolName.Value, pattern);
            }

            Sender.Tell(ToolApprovalRecorded.Instance);
        });
    }

    public static Props CreateProps(ToolApprovalStore? persistentStore = null)
        => Props.Create(() => new ToolApprovalActor(persistentStore));

    private bool IsApproved(SessionId? sessionId, TrustAudience audience, ToolName toolName, string pattern)
    {
        if (sessionId.HasValue
            && _sessionApprovals.TryGetValue(BuildSessionKey(sessionId.Value, audience), out var toolMap)
            && toolMap.TryGetValue(toolName.Value, out var patterns)
            && ApprovalPatternMatching.MatchesAny(pattern, patterns))
        {
            return true;
        }

        if (_persistentStore is null)
            return false;

        return ApprovalPatternMatching.MatchesAny(pattern, _persistentStore.GetApprovedPatterns(audience, toolName.Value));
    }

    private void AddSessionApproval(SessionId sessionId, TrustAudience audience, ToolName toolName, string pattern)
    {
        var sessionKey = BuildSessionKey(sessionId, audience);
        if (!_sessionApprovals.TryGetValue(sessionKey, out var toolMap))
        {
            toolMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            _sessionApprovals[sessionKey] = toolMap;
        }

        if (!toolMap.TryGetValue(toolName.Value, out var patterns))
        {
            patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            toolMap[toolName.Value] = patterns;
        }

        patterns.Add(pattern);
    }

    private static string BuildSessionKey(SessionId sessionId, TrustAudience audience)
        => $"{sessionId.Value}|{audience.ToWireValue()}";
}

internal sealed record ToolApprovalRecorded
{
    public static ToolApprovalRecorded Instance { get; } = new();
}
