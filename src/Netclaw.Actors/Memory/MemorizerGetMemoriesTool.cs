using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Memory retrieval tool that delegates to <c>memorizer/get_many</c> MCP.
/// Loads full content for selected memory IDs.
///
/// Registered when <c>Memory.Provider = "memorizer"</c>.
/// </summary>
[NetclawTool("get_memories",
    "Load full content for one or more memories by ID. "
    + "Use find_memories first to discover IDs, then get_memories to load the ones you need.",
    Grant = "builtin")]
public sealed partial class MemorizerGetMemoriesTool : NetclawTool<MemorizerGetMemoriesTool.Params>
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger _logger;

    private const string MemorizerGetManyToolName = "memorizer/get_many";

    public record Params(
        [property: Description("Comma-separated memory IDs to load")]
        string Ids);

    public MemorizerGetMemoriesTool(
        ToolRegistry toolRegistry,
        ILogger<MemorizerGetMemoriesTool>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Ids))
            return "No memory IDs provided.";

        var getTool = _toolRegistry.GetByName(MemorizerGetManyToolName);
        if (getTool is null)
        {
            _logger.LogWarning("Memorizer get_many tool not available");
            return "Memory retrieval unavailable: Memorizer MCP server not connected.";
        }

        var ids = args.Ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
            return "No memory IDs provided.";

        var arguments = new Dictionary<string, object?> { ["ids"] = ids };

        try
        {
            var result = await getTool.ExecuteAsync(arguments, ct);
            _logger.LogInformation("Memory get completed: requested={Count}, result={ResultLength} chars",
                ids.Length, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory get error: ids='{Ids}'", args.Ids);
            return $"Error loading memories: {ex.Message}";
        }
    }
}
