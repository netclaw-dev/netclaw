using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// File-backed memory update/delete tool. Supports find-and-replace editing
/// and deletion of memory entries. Only registered when Memory.Provider = "files".
/// </summary>
[NetclawTool("update_memory",
    "Edit or delete a memory by ID. For edits, provide old_text and new_text for find-and-replace. "
    + "For deletion, set delete to \"true\". Use find_memories to discover IDs first.",
    Grant = "builtin")]
public sealed partial class FileUpdateMemoryTool : NetclawTool<FileUpdateMemoryTool.Params>
{
    private readonly FileMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Memory ID to update or delete")]
        string Id,
        [property: Description("Text to find in the memory content (required for edits)")]
        string? OldText = null,
        [property: Description("Replacement text (required for edits)")]
        string? NewText = null,
        [property: Description("Set to \"true\" to delete the memory")]
        string? Delete = null);

    public FileUpdateMemoryTool(FileMemoryStore store, ILogger<FileUpdateMemoryTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Id))
            return "Error: memory ID is required.";

        try
        {
            // Delete mode
            if (string.Equals(args.Delete, "true", StringComparison.OrdinalIgnoreCase))
            {
                var deleted = await _store.DeleteAsync(args.Id, ct);
                if (deleted)
                {
                    _logger.LogInformation("Memory deleted: id='{Id}'", args.Id);
                    return $"Memory \"{args.Id}\" deleted.";
                }

                return $"Memory \"{args.Id}\" not found.";
            }

            // Edit mode
            if (!string.IsNullOrEmpty(args.OldText) && args.NewText is not null)
            {
                var edited = await _store.EditAsync(args.Id, args.OldText, args.NewText, ct);
                if (edited)
                {
                    _logger.LogInformation("Memory edited: id='{Id}'", args.Id);
                    return $"Memory \"{args.Id}\" updated.";
                }

                return $"Edit failed: old text not found in memory \"{args.Id}\".";
            }

            return "Error: provide either (old_text + new_text) for editing or (delete=\"true\") for deletion.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory update failed: id='{Id}'", args.Id);
            return $"Error updating memory: {ex.Message}";
        }
    }
}
