using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// File-backed memory retrieval tool. Loads full content for selected memory IDs.
/// Second phase of two-phase retrieval: <c>find_memories</c> → <c>get_memories</c>.
/// Only registered when Memory.Provider = "files".
/// </summary>
[NetclawTool("get_memories",
    "Load full content for one or more memories by ID. "
    + "Use find_memories first to discover IDs, then get_memories to load the ones you need.",
    Grant = "builtin")]
public sealed partial class FileGetMemoriesTool : NetclawTool<FileGetMemoriesTool.Params>
{
    private readonly FileMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Comma-separated memory IDs to load (e.g. \"2026-03-02-my-memory, 2026-03-01-other-note\")")]
        string Ids);

    public FileGetMemoriesTool(FileMemoryStore store, ILogger<FileGetMemoriesTool>? logger = null)
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

        try
        {
            var entries = await _store.GetByIdsAsync(ids, ct);
            var foundIds = new HashSet<string>(entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var notFound = ids.Where(id => !foundIds.Contains(id)).ToArray();

            if (entries.Count == 0)
                return $"No memories found for IDs: {string.Join(", ", ids)}";

            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                sb.AppendLine($"━━━ {entry.Title} [{entry.Id}] ━━━");
                if (entry.Tags.Length > 0)
                    sb.AppendLine($"Tags: {string.Join(", ", entry.Tags)}");
                sb.AppendLine(entry.Content);
                sb.AppendLine();
            }

            if (notFound.Length > 0)
                sb.AppendLine($"Not found: {string.Join(", ", notFound)}");

            var formatted = sb.ToString().TrimEnd();
            _logger.LogInformation(
                "Memory get: requested={Requested}, found={Found}",
                ids.Length, entries.Count);

            return formatted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory get failed: ids='{Ids}'", args.Ids);
            return $"Error loading memories: {ex.Message}";
        }
    }
}
