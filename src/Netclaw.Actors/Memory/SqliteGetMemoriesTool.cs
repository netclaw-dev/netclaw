using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

[NetclawTool("get_memories",
    "Load full content for one or more memories by ID. "
    + "Use find_memories first to discover IDs, then get_memories to load the ones you need.",
    Grant = "builtin")]
public sealed partial class SqliteGetMemoriesTool : NetclawTool<SqliteGetMemoriesTool.Params>
{
    private readonly SQLiteMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Comma-separated memory IDs to load (e.g. \"doc:abc, rec:def\")")]
        string Ids);

    public SqliteGetMemoriesTool(SQLiteMemoryStore store, ILogger<SqliteGetMemoriesTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Ids))
            return "No memory IDs provided.";

        var ids = args.Ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
            return "No memory IDs provided.";

        var entries = await _store.GetMemoriesByIdsAsync(ids, ct);
        if (entries.Count == 0)
            return $"No memories found for IDs: {string.Join(", ", ids)}";

        var sb = new StringBuilder();
        foreach (var entry in entries.OrderByDescending(e => e.UpdatedAtMs))
        {
            var typedId = new MemoryTypedId(
                MemoryDomainEnumExtensions.TryFromWireValue(entry.Kind, out MemoryKind kind) ? kind : MemoryKind.Document,
                entry.Id);
            var isStaleEvidence = string.Equals(entry.MemoryClass, MemoryClass.Evidence.ToWireValue(), StringComparison.OrdinalIgnoreCase)
                && entry.ExpiresAtMs is long expiresAt
                && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            sb.AppendLine($"━━━ {entry.Title} [{typedId.ToWireValue()}] ━━━");
            sb.AppendLine($"kind={entry.Kind} class={entry.MemoryClass} domain={entry.Domain} sensitivity={entry.Sensitivity} recall={entry.RecallMode} semantics={entry.UpdateSemantics}{(isStaleEvidence ? " stale=true" : string.Empty)}");
            sb.AppendLine(entry.Content);
            sb.AppendLine();
        }

        _logger.LogInformation("SQLite memory get completed: requested={Requested}, found={Found}", ids.Length, entries.Count);
        return sb.ToString().TrimEnd();
    }
}
