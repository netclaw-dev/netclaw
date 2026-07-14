// -----------------------------------------------------------------------
// <copyright file="SqliteGetMemoriesTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

[NetclawTool("get_memories",
    "Load full content for one or more memories by ID. "
    + "Use find_memories first to discover IDs, then get_memories to load the ones you need.",
    Grant = "builtin")]
public sealed partial class SqliteGetMemoriesTool : NetclawTool<SqliteGetMemoriesTool.Params>
{
    private readonly SQLiteMemoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Comma-separated memory IDs to load. Copy the ids shown by find_memories, get_memories, or recall verbatim (e.g. \"doc-1a2b3c, rec-9d8e7f\").")]
        string Ids);

    public SqliteGetMemoriesTool(SQLiteMemoryStore store, TimeProvider? timeProvider = null, ILogger<SqliteGetMemoriesTool>? logger = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Ids))
            return "No memory IDs provided.";

        var ids = args.Ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
            return "No memory IDs provided.";

        var sessionId = string.IsNullOrWhiteSpace(context.SessionId) ? "manual/tool" : context.SessionId!;
        var audience = MemoryPolicyScopeResolver.ResolveAudience(context.Audience, sessionId);
        var boundary = MemoryPolicyScopeResolver.ResolveBoundary(context.Boundary?.Value);
        var resolved = await _store.ResolveMemoryHandlesAsync(ids, boundary, audience, ct);
        var unresolved = resolved.Where(x => !x.Resolved).ToArray();
        if (unresolved.Length == resolved.Count)
            return string.Join(Environment.NewLine, unresolved.Select(x => $"Error: {x.Error}"));

        var entries = await _store.GetMemoriesByResolvedHandlesAsync(resolved, boundary, audience, ct);
        if (entries.Count == 0)
            return $"No memories found for IDs: {string.Join(", ", ids)}";

        var sb = new StringBuilder();
        foreach (var entry in entries.OrderByDescending(e => e.UpdatedAtMs))
        {
            var isStaleEvidence = string.Equals(entry.MemoryClass, MemoryClass.Evidence.ToWireValue(), StringComparison.OrdinalIgnoreCase)
                && entry.ExpiresAtMs is long expiresAt
                && expiresAt <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            sb.AppendLine($"━━━ {entry.Title} [{entry.Id}] ━━━");
            sb.AppendLine($"kind={entry.Kind} class={entry.MemoryClass} sensitivity={entry.Sensitivity} recall={entry.RecallMode} semantics={entry.UpdateSemantics}{(isStaleEvidence ? " stale=true" : string.Empty)}");
            sb.AppendLine(entry.Content);
            sb.AppendLine();
        }

        foreach (var unresolvedId in unresolved)
            sb.AppendLine($"Error: {unresolvedId.Error}");

        _logger.LogInformation("SQLite memory get completed: requested={Requested}, found={Found}", ids.Length, entries.Count);
        return sb.ToString().TrimEnd();
    }

}
