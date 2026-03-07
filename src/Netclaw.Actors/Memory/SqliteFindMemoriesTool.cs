using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

[NetclawTool("find_memories",
    "Search cross-session memory for prior knowledge. Returns lightweight results (ID, title, score, snippet). "
    + "Use get_memories(ids) to load full content for selected results.",
    Grant = "builtin")]
public sealed partial class SqliteFindMemoriesTool : NetclawTool<SqliteFindMemoriesTool.Params>
{
    private readonly SQLiteMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Search query to find relevant memories")]
        string Query,
        [property: Description("Maximum number of results to return (default 5)")]
        int? Limit = null);

    public SqliteFindMemoriesTool(SQLiteMemoryStore store, ILogger<SqliteFindMemoriesTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var limit = args.Limit is > 0 ? args.Limit.Value : 5;
        var results = await _store.SearchMemoriesAsync(args.Query, limit, ct);
        if (results.Count == 0)
            return "No memories found.";

        var sb = new StringBuilder();
        foreach (var result in results)
        {
            var typedId = result.Kind == "record" ? $"rec:{result.Id}" : $"doc:{result.Id}";
            sb.AppendLine($"[{typedId}] {result.Title} (score: {result.Score:F2})");
            sb.AppendLine($"  domain={result.Domain} sensitivity={result.Sensitivity} recall={result.RecallMode}");
            sb.AppendLine($"  {result.Snippet}");
            sb.AppendLine();
        }

        sb.AppendLine($"Use get_memories(\"{string.Join(", ", results.Select(r => r.Kind == "record" ? $"rec:{r.Id}" : $"doc:{r.Id}"))}\") to load full content.");
        _logger.LogInformation("SQLite memory find completed: query='{Query}', results={Count}", args.Query, results.Count);
        return sb.ToString().TrimEnd();
    }
}
