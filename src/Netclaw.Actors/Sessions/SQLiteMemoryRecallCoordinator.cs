using Netclaw.Actors.Memory;
using Microsoft.Extensions.Logging;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Automatic recall coordinator over SQLite-backed durable memory.
/// </summary>
public sealed class SQLiteMemoryRecallCoordinator(
    SQLiteMemoryStore store,
    ILogger<SQLiteMemoryRecallCoordinator> logger) : IMemoryRecallCoordinator
{
    public async Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
    {
        try
        {
            var domain = ResolveDomain(request.SessionId);
            var maxItems = request.MaxItems <= 0 ? 3 : request.MaxItems;
            var effectiveQuery = string.IsNullOrWhiteSpace(request.Query)
                ? request.RecentUserMessages.LastOrDefault() ?? string.Empty
                : request.Query;

            var primary = await store.SearchAutoRecallDocumentsAsync(
                effectiveQuery,
                domain,
                Math.Max(maxItems * 3, 12),
                ct);

            var documents = primary;
            string? fallbackQuery = null;
            if (documents.Count == 0 && request.RecentUserMessages.Count > 0)
            {
                fallbackQuery = request.RecentUserMessages[^1];
                documents = await store.SearchAutoRecallDocumentsAsync(
                    fallbackQuery,
                    domain,
                    Math.Max(maxItems * 3, 12),
                    ct);
            }

            LogRecallTrace(
                effectiveQuery,
                fallbackQuery,
                domain,
                maxItems,
                primary.Count,
                documents.Count,
                documents.Select(d => d.DocumentId));

            var items = documents
                .OrderByDescending(RecallRank)
                .Take(maxItems)
                .Select(d => new AutomaticRecallItem(
                    d.DocumentId,
                    d.Title,
                    d.MarkdownBody,
                    d.Domain,
                    d.Sensitivity,
                    d.Confidence))
                .ToArray();

            return new AutomaticRecallResult(items);
        }
        catch (Exception ex)
        {
            return new AutomaticRecallResult([], true, ex.Message);
        }
    }

    private static string ResolveDomain(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "project:default";

        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return "project:default";

        var prefix = sessionId[..slash].Trim();
        return string.IsNullOrWhiteSpace(prefix)
            ? "project:default"
            : $"project:{prefix.ToLowerInvariant()}";
    }

    private void LogRecallTrace(
        string query,
        string? fallbackQuery,
        string domain,
        int maxItems,
        int primaryCount,
        int selectedCount,
        IEnumerable<string> selectedDocumentIds)
    {
        var queryTerms = TokenizeTerms(query);
        var fallbackTerms = string.IsNullOrWhiteSpace(fallbackQuery)
            ? Array.Empty<string>()
            : TokenizeTerms(fallbackQuery);
        var selectedIds = string.Join(",", selectedDocumentIds.Take(maxItems));

        logger.LogInformation(
            "memory_recall_query_trace domain={Domain} maxItems={MaxItems} primaryCount={PrimaryCount} selectedCount={SelectedCount} queryTerms={QueryTerms} fallbackTerms={FallbackTerms} selectedIds={SelectedIds}",
            domain,
            maxItems,
            primaryCount,
            selectedCount,
            string.Join("|", queryTerms),
            string.Join("|", fallbackTerms),
            string.IsNullOrWhiteSpace(selectedIds) ? "-" : selectedIds);
    }

    private static string[] TokenizeTerms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static int RecallRank(SQLiteMemoryDocument document)
    {
        var score = 0;

        // Prefer deterministic durable classes and explicit/inferred semantics.
        if (string.Equals(document.UpdateSemantics, "merge-document", StringComparison.OrdinalIgnoreCase))
            score += 80;
        else if (string.Equals(document.UpdateSemantics, "append-document", StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (string.Equals(document.UpdateSemantics, "conversation_trace", StringComparison.OrdinalIgnoreCase))
            score -= 300;

        if (string.Equals(document.UpdateSemantics, "immutable-record", StringComparison.OrdinalIgnoreCase))
            score += 30;

        if (string.Equals(document.Title, "turn-completion", StringComparison.OrdinalIgnoreCase))
            score -= 200;

        if (string.Equals(document.Title, "verified-tool-finding", StringComparison.OrdinalIgnoreCase))
            score += 25;

        score += (int)Math.Round(document.Confidence * 20.0);

        // Prefer fresher entries, bounded contribution.
        if (document.FreshnessAtMs.HasValue)
            score += 10;

        return score;
    }
}
