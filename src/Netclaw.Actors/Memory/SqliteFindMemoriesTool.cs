using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions;
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
    private readonly SidecarRecallPlanner _planner = new();
    private readonly RecallPlanGate _gate = new();
    private readonly TimeProvider _timeProvider;

    public record Params(
        [property: Description("Search query to find relevant memories")]
        string Query,
        [property: Description("Maximum number of results to return (default 5)")]
        int? Limit = null,
        [property: Description("Set true to include expired evidence for audit/debug search")]
        bool? IncludeStale = null);

    public SqliteFindMemoriesTool(SQLiteMemoryStore store, TimeProvider? timeProvider = null, ILogger<SqliteFindMemoriesTool>? logger = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var limit = args.Limit is > 0 ? args.Limit.Value : 5;
        var includeStale = args.IncludeStale ?? false;
        var sessionId = string.IsNullOrWhiteSpace(context.SessionId)
            ? "manual/tool"
            : context.SessionId!;
        var domain = ResolveDomain(sessionId);

        var request = _planner.BuildRequest(
            sessionId,
            domain,
            args.Query,
            [args.Query],
            [],
            [],
            "intentional",
            8,
            limit);
        var plan = _gate.Clamp(null, request);

        var results = await _store.SearchByPlanAsync(
            plan.SearchTerms,
            domain,
            plan.MemoryClasses,
            limit,
            allowExpiredEvidence: includeStale,
            ct);

        if (results.Count == 0)
            return "No memories found.";

        var sb = new StringBuilder();
        foreach (var result in results)
        {
            var typedId = result.Kind == "record" ? $"rec:{result.Id}" : $"doc:{result.Id}";
            var isStaleEvidence = string.Equals(result.MemoryClass, "evidence", StringComparison.OrdinalIgnoreCase)
                && result.ExpiresAtMs is long expiresAt
                && expiresAt <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var snippet = BuildSnippet(result.Content);

            sb.AppendLine($"[{typedId}] {result.Title}");
            sb.AppendLine($"  class={result.MemoryClass} domain={result.Domain} sensitivity={result.Sensitivity} recall={result.RecallMode}{(isStaleEvidence ? " stale=true" : string.Empty)}");
            sb.AppendLine($"  {snippet}");
            sb.AppendLine();
        }

        sb.AppendLine($"Use get_memories(\"{string.Join(", ", results.Select(r => r.Kind == "record" ? $"rec:{r.Id}" : $"doc:{r.Id}"))}\") to load full content.");
        _logger.LogInformation("SQLite memory find completed: query='{Query}', results={Count}, includeStale={IncludeStale}", args.Query, results.Count, includeStale);
        return sb.ToString().TrimEnd();
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    private static string ResolveDomain(string sessionId)
    {
        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
            return $"project:{sessionId[..slash].ToLowerInvariant()}";
        return "project:default";
    }

    private static string BuildSnippet(string content)
        => content.Length <= 160 ? content : content[..160] + "...";
}
