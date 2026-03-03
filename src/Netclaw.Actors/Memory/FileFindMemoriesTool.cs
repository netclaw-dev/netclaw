using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// File-backed lightweight memory search. Returns IDs, titles, scores, tags, and snippets —
/// NOT full content. Use <c>get_memories</c> to load full content for selected IDs.
/// Only registered when Memory.Provider = "files".
/// </summary>
[NetclawTool("find_memories",
    "Search cross-session memory for prior knowledge. Returns lightweight results (ID, title, score, snippet). "
    + "Use get_memories(ids) to load full content for selected results.",
    Grant = "builtin")]
public sealed partial class FileFindMemoriesTool : NetclawTool<FileFindMemoriesTool.Params>
{
    private readonly FileMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Search query to find relevant memories")]
        string Query,
        [property: Description("Maximum number of results to return (default 5)")]
        int? Limit = null,
        [property: Description("Optional comma-separated tags to filter results (e.g. \"reference, how-to\")")]
        string? Tags = null);

    public FileFindMemoriesTool(FileMemoryStore store, ILogger<FileFindMemoriesTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var limit = args.Limit is > 0 ? args.Limit.Value : 5;
        var filterTags = ParseTags(args.Tags);

        try
        {
            var results = await _store.SearchAsync(args.Query, limit, filterTags, ct);

            if (results.Count == 0)
            {
                _logger.LogInformation("Memory find: query='{Query}', no results", args.Query);
                return "No memories found.";
            }

            var sb = new StringBuilder();
            foreach (var result in results)
            {
                var normalizedScore = NormalizeScore(result.Score, args.Query);
                sb.AppendLine($"[{result.Id}] {result.Title} (score: {normalizedScore:F2})");
                if (result.Tags.Length > 0)
                    sb.AppendLine($"  Tags: {string.Join(", ", result.Tags)}");
                sb.AppendLine($"  {Snippet(result.Content)}");
                sb.AppendLine();
            }

            sb.AppendLine($"Use get_memories(\"{string.Join(", ", results.Select(r => r.Id))}\") to load full content.");

            var formatted = sb.ToString().TrimEnd();
            _logger.LogInformation(
                "Memory find: query='{Query}', results={Count}",
                args.Query, results.Count);

            return formatted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory find failed: query='{Query}'", args.Query);
            return $"Error searching memories: {ex.Message}";
        }
    }

    internal static double NormalizeScore(double rawScore, string query)
    {
        var queryTerms = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maxPossible = queryTerms.Length * 6.0; // title(3) + tag(2) + content(1)
        return maxPossible > 0 ? Math.Min(1.0, rawScore / maxPossible) : 0;
    }

    private static string Snippet(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";
        var trimmed = content.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= 150 ? trimmed : trimmed[..150] + "…";
    }

    private static string[]? ParseTags(string? tagsCsv)
    {
        if (string.IsNullOrWhiteSpace(tagsCsv))
            return null;
        var tags = tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tags.Length > 0 ? tags : null;
    }
}
