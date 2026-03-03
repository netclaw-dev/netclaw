using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Memory update/delete tool that delegates to Memorizer MCP.
/// Delete → <c>memorizer/archive_memory</c> (soft delete).
/// Edit → <c>memorizer/edit</c> (find-and-replace).
///
/// Registered when <c>Memory.Provider = "memorizer"</c>.
/// </summary>
[NetclawTool("update_memory",
    "Edit or delete a memory by ID. For edits, provide old_text and new_text for find-and-replace. "
    + "For deletion, set delete to \"true\" (archives the memory). Use find_memories to discover IDs first.",
    Grant = "builtin")]
public sealed partial class MemorizerUpdateMemoryTool : NetclawTool<MemorizerUpdateMemoryTool.Params>
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger _logger;

    private const string MemorizerEditToolName = "memorizer/edit";
    private const string MemorizerArchiveToolName = "memorizer/archive_memory";

    public record Params(
        [property: Description("Memory ID to update or delete")]
        string Id,
        [property: Description("Text to find in the memory content (required for edits)")]
        string? OldText = null,
        [property: Description("Replacement text (required for edits)")]
        string? NewText = null,
        [property: Description("Set to \"true\" to delete (archive) the memory")]
        string? Delete = null);

    public MemorizerUpdateMemoryTool(
        ToolRegistry toolRegistry,
        ILogger<MemorizerUpdateMemoryTool>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Id))
            return "Error: memory ID is required.";

        try
        {
            // Delete mode → archive
            if (string.Equals(args.Delete, "true", StringComparison.OrdinalIgnoreCase))
            {
                var archiveTool = _toolRegistry.GetByName(MemorizerArchiveToolName);
                if (archiveTool is null)
                {
                    _logger.LogWarning("Memorizer archive tool not available");
                    return "Memory update unavailable: Memorizer MCP server not connected.";
                }

                var arguments = new Dictionary<string, object?> { ["id"] = args.Id };
                var result = await archiveTool.ExecuteAsync(arguments, ct);
                _logger.LogInformation("Memory archived: id='{Id}'", args.Id);
                return $"Memory \"{args.Id}\" archived (soft-deleted). {result}";
            }

            // Edit mode
            if (!string.IsNullOrEmpty(args.OldText) && args.NewText is not null)
            {
                var editTool = _toolRegistry.GetByName(MemorizerEditToolName);
                if (editTool is null)
                {
                    _logger.LogWarning("Memorizer edit tool not available");
                    return "Memory update unavailable: Memorizer MCP server not connected.";
                }

                var arguments = new Dictionary<string, object?>
                {
                    ["id"] = args.Id,
                    ["old_text"] = args.OldText,
                    ["new_text"] = args.NewText
                };
                var result = await editTool.ExecuteAsync(arguments, ct);
                _logger.LogInformation("Memory edited: id='{Id}'", args.Id);
                return $"Memory \"{args.Id}\" updated. {result}";
            }

            return "Error: provide either (old_text + new_text) for editing or (delete=\"true\") for deletion.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory update error: id='{Id}'", args.Id);
            return $"Error updating memory: {ex.Message}";
        }
    }
}
