// -----------------------------------------------------------------------
// <copyright file="SessionMemoryCheckpointFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal static class SessionMemoryCheckpointFactory
{
    public static MemoryCheckpointPayload ForSubAgentFinding(
        SessionId sessionId,
        string boundary,
        string audience,
        AcceptedSubAgentFinding finding)
        => new(
            SessionId: sessionId.Value,
            TriggerType: "subagent-findings",
            Source: finding.AgentName.Value,
            Content: finding.Content,
            UserContent: null,
            AssistantContent: finding.Content,
            IsExplicitRequest: false,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: true,
            Boundary: boundary,
            Audience: audience,
            Sensitivity: finding.Sensitivity.ToWireValue(),
            RecallMode: finding.RecallMode.ToWireValue(),
            Confidence: finding.Confidence,
            Title: finding.Title,
            Kind: finding.Kind,
            UpdateSemantics: finding.UpdateSemantics,
            Evidence: finding.Evidence,
            FreshnessAtMs: finding.FreshnessAtMs);

    public static MemoryCheckpointPayload ForCompactionBoundary(
        SessionId sessionId,
        string boundary,
        string audience,
        string? summary)
    {
        var content = string.IsNullOrWhiteSpace(summary)
            ? "Compaction completed"
            : summary;

        return new MemoryCheckpointPayload(
            SessionId: sessionId.Value,
            TriggerType: "compaction-boundary",
            Source: "compaction",
            Content: content,
            UserContent: null,
            AssistantContent: string.IsNullOrWhiteSpace(summary) ? null : summary,
            IsExplicitRequest: false,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: true,
            HasAcceptedSubAgentFinding: false,
            Boundary: boundary,
            Audience: audience,
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: MemoryRecallMode.Auto.ToWireValue(),
            Confidence: 0.8,
            Kind: MemoryKind.Document.ToWireValue(),
            Title: "compaction-boundary",
            UpdateSemantics: "append-document");
    }

    public static MemoryCheckpointPayload ForTurnComplete(
        SessionId sessionId,
        TurnRecorded turn,
        string boundary,
        string audience)
    {
        var userContent = string.Join(
            "\n\n",
            (turn.UserMessages.Count > 0 ? turn.UserMessages : [turn.UserMessage])
            .Select(message => message.Content));

        return new(
            SessionId: sessionId.Value,
            TriggerType: CheckpointTriggerType.TurnComplete.ToWireValue(),
            Source: "session",
            Content: $"User: {userContent}\nAssistant: {turn.AssistantReply.Content}",
            UserContent: userContent,
            AssistantContent: turn.AssistantReply.Content,
            IsExplicitRequest: false,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: false,
            Boundary: boundary,
            Audience: audience,
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: MemoryRecallMode.Auto.ToWireValue(),
            Confidence: 0.7,
            Kind: MemoryKind.Document.ToWireValue(),
            Title: "turn-completion",
            UpdateSemantics: "append-document");
    }
}
