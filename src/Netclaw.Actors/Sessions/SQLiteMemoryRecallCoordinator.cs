using Netclaw.Actors.Memory;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Automatic recall coordinator over SQLite-backed durable memory.
/// </summary>
public sealed class SQLiteMemoryRecallCoordinator(SQLiteMemoryStore store) : IMemoryRecallCoordinator
{
    public async Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
    {
        try
        {
            var domain = ResolveDomain(request.SessionId);
            var maxItems = request.MaxItems <= 0 ? 3 : request.MaxItems;

            var primary = await store.SearchAutoRecallDocumentsAsync(
                request.Query,
                domain,
                maxItems,
                ct);

            var documents = primary;
            if (documents.Count == 0 && request.RecentUserMessages.Count > 0)
            {
                var fallbackQuery = request.RecentUserMessages[^1];
                documents = await store.SearchAutoRecallDocumentsAsync(
                    fallbackQuery,
                    domain,
                    maxItems,
                    ct);
            }

            var items = documents
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
}
