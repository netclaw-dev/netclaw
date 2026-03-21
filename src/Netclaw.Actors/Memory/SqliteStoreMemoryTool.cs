using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

[NetclawTool("store_memory",
    "Save knowledge to cross-session memory for future retrieval. "
    + "Use for solutions, decisions, research findings, and project context. "
    + "Write descriptive titles and include WHY not just WHAT.",
    Grant = "builtin")]
public sealed partial class SqliteStoreMemoryTool : NetclawTool<SqliteStoreMemoryTool.Params>
{
    private readonly IMemoryCheckpointSink _checkpointSink;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Title for the memory entry")]
        string Title,
        [property: Description("Content to store — use markdown, include code blocks, provide full context")]
        string Content,
        [property: Description("Optional comma-separated tags")]
        string? Tags = null);

    public SqliteStoreMemoryTool(IMemoryCheckpointSink checkpointSink, ILogger<SqliteStoreMemoryTool>? logger = null)
    {
        _checkpointSink = checkpointSink;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var sessionId = string.IsNullOrWhiteSpace(context.SessionId)
            ? "manual/tool"
            : context.SessionId!;
        var domain = new Protocol.SessionId(sessionId).ToMemoryDomain();
        var audience = MemoryPolicyScopeResolver.ResolveAudience(context.Audience, sessionId);
        var boundary = MemoryPolicyScopeResolver.ResolveBoundary(context.Boundary, audience, sessionId, domain);

        var payload = new MemoryCheckpointPayload(
            SessionId: sessionId,
            TriggerType: CheckpointTriggerType.ExplicitMemoryRequest.ToWireValue(),
            Source: "store_memory",
            Content: args.Content,
            UserContent: args.Content,
            AssistantContent: null,
            IsExplicitRequest: true,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: false,
            Domain: domain,
            Boundary: boundary,
            Audience: audience.ToWireValue(),
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: MemoryRecallMode.Auto.ToWireValue(),
            Confidence: 0.95,
            Title: args.Title,
            UpdateSemantics: MemoryUpdateSemantics.MergeDocument.ToWireValue(),
            Kind: MemoryKind.Document.ToWireValue());

        var result = await _checkpointSink.EnqueueAsync(new MemoryCheckpointRequest(
            SessionId: new Protocol.SessionId(sessionId),
            TurnId: null,
            TriggerType: CheckpointTriggerType.ExplicitMemoryRequest,
            Priority: 100,
            Payload: payload), ct);

        _logger.LogInformation("SQLite store_memory committed checkpoint={CheckpointId} session={SessionId}",
            result.CheckpointId, sessionId);
        return $"Memory save confirmed: \"{args.Title}\" (checkpoint: {result.CheckpointId}).";
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);
}
