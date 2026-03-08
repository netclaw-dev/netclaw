using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        var (kind, id) = ParseTypedId(args.Id);
        if (kind == "unknown")
            return "Error: ID must be prefixed with doc: or rec:.";

        if (delete)
        {
            var tombstoned = kind == "document"
                ? await _store.TombstoneDocumentAsync(id, ct)
                : await _store.TombstoneRecordAsync(id, ct);
            if (!tombstoned)
                return $"Memory \"{args.Id}\" not found.";

            await EnqueueAuditCheckpoint(args, context, kind, "tombstone", ct);
            return $"Memory \"{args.Id}\" tombstoned.";
        }

        if (kind == "document")
        {
            if (string.IsNullOrEmpty(args.OldText) || args.NewText is null)
                return "Error: document update requires old_text and new_text.";

            var updated = await _store.UpdateDocumentTextAsync(id, args.OldText, args.NewText, ct);
            if (!updated)
                return $"Edit failed for \"{args.Id}\". Document missing or old_text not found.";

            await EnqueueAuditCheckpoint(args, context, kind, "merge-document", ct);
            return $"Memory \"{args.Id}\" updated.";
        }

        if (args.NewText is null)
            return "Error: record update requires new_text as replacement payload.";

        var superseded = await _store.SupersedeRecordAsync(id, args.NewText, ct);
        if (!superseded)
            return $"Record \"{args.Id}\" not found.";

        await EnqueueAuditCheckpoint(args, context, kind, "supersede-record", ct);
        return $"Record \"{args.Id}\" superseded.";
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    private async Task EnqueueAuditCheckpoint(Params args, ToolExecutionContext context, string kind, string semantics, CancellationToken ct)
    {
        var sessionId = string.IsNullOrWhiteSpace(context.SessionId) ? "manual/tool" : context.SessionId!;
        var payload = new MemoryCheckpointPayload(
            SessionId: sessionId,
            TriggerType: "explicit-memory-request",
            Source: "update_memory",
            Content: args.NewText ?? args.OldText ?? args.Id,
            UserContent: args.NewText ?? args.OldText ?? args.Id,
            AssistantContent: null,
            IsExplicitRequest: true,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: false,
            Domain: ResolveDomain(sessionId),
            Sensitivity: "normal",
            RecallMode: "manual",
            Confidence: 0.95,
            MemoryId: ParseTypedId(args.Id).Id,
            UpdateOldText: args.OldText,
            UpdateNewText: args.NewText,
            Delete: string.Equals(args.Delete, "true", StringComparison.OrdinalIgnoreCase),
            Kind: kind,
            UpdateSemantics: semantics,
            Title: args.Id);

        var result = await _checkpointSink.EnqueueAsync(new MemoryCheckpointRequest(
            SessionId: sessionId,
            TurnId: null,
            TriggerType: payload.TriggerType,
            Priority: 95,
            Payload: payload), ct);

        _logger.LogInformation("SQLite update_memory audit checkpoint={CheckpointId} memory={MemoryId}", result.CheckpointId, args.Id);
    }

    private static (string Kind, string Id) ParseTypedId(string raw)
    {
        if (raw.StartsWith("doc:", StringComparison.OrdinalIgnoreCase))
            return ("document", raw[4..]);
        if (raw.StartsWith("rec:", StringComparison.OrdinalIgnoreCase))
            return ("record", raw[4..]);
        return ("unknown", raw);
    }

    private static string ResolveDomain(string sessionId)
    {
        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
            return $"project:{sessionId[..slash].ToLowerInvariant()}";
        return "project:default";
    }
}
