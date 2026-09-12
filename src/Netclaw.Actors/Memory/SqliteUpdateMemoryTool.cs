// -----------------------------------------------------------------------
// <copyright file="SqliteUpdateMemoryTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

[NetclawTool("update_memory",
    "Edit or delete a memory by ID. For document edits, provide either old_text and new_text for find-and-replace, "
    + "or new_content to replace the document content. For deletion, set delete to true. Use IDs from memory recall, find_memories, or get_memories directly.",
    Grant = "builtin")]
public sealed partial class SqliteUpdateMemoryTool : NetclawTool<SqliteUpdateMemoryTool.Params>
{
    private readonly SQLiteMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Memory ID to update or delete. Copy the id shown by memory recall, find_memories, or get_memories verbatim (e.g. doc-… or rec-…).")]
        string Id,
        [property: Description("Text to find in the memory content (required for document edits)")]
        string? OldText = null,
        [property: Description("Replacement text for document edits or new payload for record supersede")]
        string? NewText = null,
        [property: Description("Full replacement content for document edits. Do not combine with old_text/new_text.")]
        string? NewContent = null,
        [property: Description("Set to true to tombstone the memory")]
        bool? Delete = null);

    public SqliteUpdateMemoryTool(
        SQLiteMemoryStore store,
        ILogger<SqliteUpdateMemoryTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Id))
            return "Error: memory ID is required.";

        var sessionId = string.IsNullOrWhiteSpace(context.SessionId) ? "manual/tool" : context.SessionId!;
        var audience = MemoryPolicyScopeResolver.ResolveAudience(context.Audience, sessionId);
        var boundary = MemoryPolicyScopeResolver.ResolveBoundary(context.Boundary?.Value);
        var resolved = await _store.ResolveMemoryHandleAsync(args.Id, boundary, audience, ct);
        if (!resolved.Resolved)
            return $"Error: {resolved.Error}";

        var storageId = resolved.StorageId!.Value;
        var delete = args.Delete ?? false;
        if (delete)
        {
            var tombstoned = resolved.Kind == MemoryKind.Document
                ? await _store.TombstoneDocumentAsync(storageId.Value, ct)
                : await _store.TombstoneRecordAsync(storageId.Value, ct);
            if (!tombstoned)
                return $"Memory \"{resolved.Handle}\" not found.";

            _logger.LogInformation("SQLite update_memory tombstoned memory={MemoryId}", resolved.Handle);
            return $"Memory \"{resolved.Handle}\" tombstoned.";
        }

        if (resolved.Kind == MemoryKind.Document)
        {
            if (args.NewContent is not null)
            {
                if (!string.IsNullOrEmpty(args.OldText) || args.NewText is not null)
                    return "Error: provide either new_content OR old_text/new_text, not both.";
                if (string.IsNullOrWhiteSpace(args.NewContent))
                    return "Error: new_content cannot be empty. To remove a memory, set delete to true.";

                var replaced = await _store.ReplaceDocumentTextAsync(storageId.Value, args.NewContent, ct);
                if (!replaced)
                    return $"Edit failed for \"{resolved.Handle}\". Document missing.";

                _logger.LogInformation("SQLite update_memory replaced document memory={MemoryId}", resolved.Handle);
                return $"Memory \"{resolved.Handle}\" updated.";
            }

            if (string.IsNullOrEmpty(args.OldText) || args.NewText is null)
                return "Error: document update requires old_text and new_text, or new_content.";

            var updated = await _store.UpdateDocumentTextAsync(storageId.Value, args.OldText, args.NewText, ct);
            if (!updated)
                return $"Edit failed for \"{resolved.Handle}\". Document missing or old_text not found.";

            _logger.LogInformation("SQLite update_memory edited document memory={MemoryId}", resolved.Handle);
            return $"Memory \"{resolved.Handle}\" updated.";
        }

        if (args.OldText is not null)
            return "Error: records do not support old_text/new_text find-and-replace; provide new_text or new_content as the full replacement payload.";

        var recordPayload = args.NewContent ?? args.NewText;
        if (string.IsNullOrWhiteSpace(recordPayload))
            return "Error: record update requires new_text or new_content as replacement payload.";

        var superseded = await _store.SupersedeRecordAsync(storageId.Value, recordPayload, ct);
        if (!superseded)
            return $"Record \"{resolved.Handle}\" not found.";

        _logger.LogInformation("SQLite update_memory superseded record memory={MemoryId}", resolved.Handle);
        return $"Record \"{resolved.Handle}\" superseded.";
    }

}
