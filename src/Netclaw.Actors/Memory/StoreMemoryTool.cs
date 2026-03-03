using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// File-backed memory storage tool. Saves memories as markdown files
/// in the local memories directory. Only registered when Memory.Provider = "files".
/// </summary>
[NetclawTool("store_memory",
    "Save knowledge to cross-session memory for future retrieval. "
    + "Use for solutions, decisions, research findings, and project context. "
    + "Write descriptive titles (not 'DB fix' — say 'PostgreSQL connection pooling fix for Npgsql 8.x'). "
    + "Include WHY not just WHAT. Use markdown formatting, code blocks, and links to PRs/docs.",
    Grant = "builtin")]
public sealed partial class StoreMemoryTool : NetclawTool<StoreMemoryTool.Params>
{
    private readonly FileMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Title for the memory entry")]
        string Title,
        [property: Description("Content to store — use markdown, include code blocks, provide full context")]
        string Content,
        [property: Description("Optional comma-separated tags for categorization (e.g. \"reference, how-to, decision\")")]
        string? Tags = null);

    public StoreMemoryTool(FileMemoryStore store, ILogger<StoreMemoryTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var tags = ParseTags(args.Tags);

        try
        {
            await _store.StoreAsync(args.Title, args.Content, tags, ct);
            _logger.LogInformation("Memory stored: title='{Title}'", args.Title);
            return $"Memory saved: \"{args.Title}\"";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store memory: title='{Title}'", args.Title);
            return $"Error saving memory: {ex.Message}";
        }
    }

    private static string[]? ParseTags(string? tagsCsv)
    {
        if (string.IsNullOrWhiteSpace(tagsCsv))
            return null;

        var tags = tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tags.Length > 0 ? tags : null;
    }
}
