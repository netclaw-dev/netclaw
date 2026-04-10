using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

[NetclawTool("update_memory",
    "Edit or delete a memory by ID. For edits, provide old_text and new_text for find-and-replace. "
    + "For deletion, set delete to \"true\". Use find_memories to discover IDs first.",
    Grant = "builtin")]
public sealed partial class SqliteUpdateMemoryTool : NetclawTool<SqliteUpdateMemoryTool.Params>
{
    private readonly SQLiteMemoryStore _store;
    private readonly IMemoryCheckpointSink _checkpointSink;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Memory ID to update or delete (prefix with doc: or rec:)")]
        string Id,
        [property: Description("Text to find in the memory content (required for document edits)")]
        string? OldText = null,
        [property: Description("Replacement text for document edits or new payload for record supersede")]
        string? NewText = null,
        [property: Description("Set to \"true\" to tombstone the memory")]
        string? Delete = null);

    public SqliteUpdateMemoryTool(
        SQLiteMemoryStore store,
        IMemoryCheckpointSink checkpointSink,
        ILogger<SqliteUpdateMemoryTool>? logger = null)
    {
        _store = store;
        _checkpointSink = checkpointSink;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Id))
            return "Error: memory ID is required.";

        var delete = string.Equals(args.Delete, "true", StringComparison.OrdinalIgnoreCase);
        var typedId = MemoryTypedId.Parse(args.Id);
        if (typedId.Kind == MemoryKind.Unknown)
            return "Error: ID must be prefixed with doc: or rec:.";

        if (delete)
        {
            var tombstoned = typedId.Kind == MemoryKind.Document
                ? await _store.TombstoneDocumentAsync(typedId.Id, ct)
                : await _store.TombstoneRecordAsync(typedId.Id, ct);
            if (!tombstoned)
                return $"Memory \"{args.Id}\" not found.";

            await EnqueueAuditCheckpoint(args, context, typedId.Kind, MemoryUpdateSemantics.Tombstone, ct);
            return $"Memory \"{args.Id}\" tombstoned.";
        }

        if (typedId.Kind == MemoryKind.Document)
        {
            if (string.IsNullOrEmpty(args.OldText) || args.NewText is null)
                return "Error: document update requires old_text and new_text.";

            var updated = await _store.UpdateDocumentTextAsync(typedId.Id, args.OldText, args.NewText, ct);
            if (!updated)
                return $"Edit failed for \"{args.Id}\". Document missing or old_text not found.";

            await EnqueueAuditCheckpoint(args, context, typedId.Kind, MemoryUpdateSemantics.MergeDocument, ct);
            return $"Memory \"{args.Id}\" updated.";
        }

        if (args.NewText is null)
            return "Error: record update requires new_text as replacement payload.";

        var superseded = await _store.SupersedeRecordAsync(typedId.Id, args.NewText, ct);
        if (!superseded)
            return $"Record \"{args.Id}\" not found.";

        await EnqueueAuditCheckpoint(args, context, typedId.Kind, MemoryUpdateSemantics.SupersedeRecord, ct);
        return $"Record \"{args.Id}\" superseded.";
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    private async Task EnqueueAuditCheckpoint(Params args, ToolExecutionContext context, MemoryKind kind, MemoryUpdateSemantics semantics, CancellationToken ct)
    {
        var sessionId = string.IsNullOrWhiteSpace(context.SessionId) ? "manual/tool" : context.SessionId!;
        var domain = new Protocol.SessionId(sessionId).ToMemoryDomain();
        var audience = MemoryPolicyScopeResolver.ResolveAudience(context.Audience, sessionId);
        var boundary = MemoryPolicyScopeResolver.ResolveBoundary(context.Boundary);
        var payload = new MemoryCheckpointPayload(
            SessionId: sessionId,
            TriggerType: CheckpointTriggerType.ExplicitMemoryRequest.ToWireValue(),
            Source: "update_memory",
            Content: args.NewText ?? args.OldText ?? args.Id,
            UserContent: args.NewText ?? args.OldText ?? args.Id,
            AssistantContent: null,
            IsExplicitRequest: true,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: false,
            Domain: domain,
            Boundary: boundary,
            Audience: audience.ToWireValue(),
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: MemoryRecallMode.Manual.ToWireValue(),
            Confidence: 0.95,
            MemoryId: MemoryTypedId.Parse(args.Id).Id,
            UpdateOldText: args.OldText,
            UpdateNewText: args.NewText,
            Delete: string.Equals(args.Delete, "true", StringComparison.OrdinalIgnoreCase),
            Kind: kind.ToWireValue(),
            UpdateSemantics: semantics.ToWireValue(),
            Title: args.Id);

        var result = await _checkpointSink.EnqueueAsync(new MemoryCheckpointRequest(
            SessionId: new Protocol.SessionId(sessionId),
            TurnId: null,
            TriggerType: CheckpointTriggerType.ExplicitMemoryRequest,
            Priority: 95,
            Payload: payload), ct);

        _logger.LogInformation("SQLite update_memory audit checkpoint={CheckpointId} memory={MemoryId}", result.CheckpointId, args.Id);
    }
}
