using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Memory extractor that persists session extraction results to local markdown files
/// via <see cref="FileMemoryStore"/>. Used when Memory.Provider = "files".
/// </summary>
public sealed class FileMemoryExtractor : IMemoryExtractor
{
    private readonly FileMemoryStore _store;

    public FileMemoryExtractor(FileMemoryStore store)
    {
        _store = store;
    }

    public async Task PersistAsync(string sessionId, string extractedMemories, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(extractedMemories))
            return;

        await _store.StoreAsync(
            $"Session extraction — {sessionId}",
            extractedMemories,
            ["extraction", "compaction"],
            ct);
    }
}
