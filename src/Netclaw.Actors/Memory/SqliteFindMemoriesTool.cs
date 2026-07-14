// -----------------------------------------------------------------------
// <copyright file="SqliteFindMemoriesTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
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

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        var limit = args.Limit is > 0 ? args.Limit.Value : 5;
        var includeStale = args.IncludeStale ?? false;
        var sessionIdRaw = string.IsNullOrWhiteSpace(context.SessionId)
            ? "manual/tool"
            : context.SessionId!;
        var sessionId = (SessionId)sessionIdRaw;
        var audience = MemoryPolicyScopeResolver.ResolveAudience(context.Audience, sessionIdRaw);

        var request = _planner.BuildRequest(
            sessionId,
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
            plan.MemoryClasses,
            limit,
            MemoryPolicyScopeResolver.ResolveBoundary(context.Boundary?.Value),
            audience,
            allowExpiredEvidence: includeStale,
            ct);

        if (results.Count == 0)
            return "No memories found.";

        var sb = new StringBuilder();
        foreach (var result in results)
        {
            var isStaleEvidence = string.Equals(result.MemoryClass, MemoryClass.Evidence.ToWireValue(), StringComparison.OrdinalIgnoreCase)
                && result.ExpiresAtMs is long expiresAt
                && expiresAt <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var snippet = BuildSnippet(result.Content);

            sb.AppendLine($"[{result.Id}] {result.Title}");
            sb.AppendLine($"  class={result.MemoryClass} sensitivity={result.Sensitivity} recall={result.RecallMode}{(isStaleEvidence ? " stale=true" : string.Empty)}");
            sb.AppendLine($"  {snippet}");
            sb.AppendLine();
        }

        sb.AppendLine($"Use get_memories(\"{string.Join(", ", results.Select(r => r.Id))}\") to load full content.");
        _logger.LogInformation("SQLite memory find completed: query='{Query}', results={Count}, includeStale={IncludeStale}", args.Query, results.Count, includeStale);
        return sb.ToString().TrimEnd();
    }

    private static string BuildSnippet(string content)
        => content.Length <= 160 ? content : content[..160] + "...";
}
