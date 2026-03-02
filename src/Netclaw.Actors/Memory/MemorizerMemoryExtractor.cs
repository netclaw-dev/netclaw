using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Memory extractor that persists session extraction results to Memorizer MCP server.
/// Resolves the <c>memorizer/store</c> MCP tool at call time via <see cref="ToolRegistry"/>.
/// Graceful no-op if Memorizer is disconnected.
/// </summary>
public sealed class MemorizerMemoryExtractor : IMemoryExtractor
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger _logger;

    /// <summary>
    /// Known MCP tool names for Memorizer's store operation. Checked in order.
    /// </summary>
    private static readonly string[] MemorizerStoreToolNames =
    [
        "memorizer/store",
        "memorizer/store_memory"
    ];

    public MemorizerMemoryExtractor(ToolRegistry toolRegistry, ILogger<MemorizerMemoryExtractor>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task PersistAsync(string sessionId, string extractedMemories, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(extractedMemories))
            return;

        var mcpTool = FindStoreTool();
        if (mcpTool is null)
        {
            _logger.LogWarning(
                "Memory extraction for session {SessionId} skipped — Memorizer MCP not connected",
                sessionId);
            return;
        }

        var arguments = new Dictionary<string, object?>
        {
            ["type"] = "reference",
            ["title"] = $"Session extraction — {sessionId}",
            ["text"] = extractedMemories,
            ["source"] = "compaction",
            ["tags"] = new[] { "extraction", "compaction" }
        };

        try
        {
            await mcpTool.ExecuteAsync(arguments, ct);
            _logger.LogInformation(
                "Memory extraction persisted to Memorizer for session {SessionId}",
                sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist memory extraction for session {SessionId}",
                sessionId);
        }
    }

    private INetclawTool? FindStoreTool()
    {
        foreach (var name in MemorizerStoreToolNames)
        {
            var tool = _toolRegistry.GetByName(name);
            if (tool is not null)
                return tool;
        }

        return null;
    }
}
