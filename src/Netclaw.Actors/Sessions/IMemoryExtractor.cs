// -----------------------------------------------------------------------
// <copyright file="IMemoryExtractor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Interface for persisting extracted memories during pre-compaction memory flush.
/// The session actor fires a memory extraction LLM call and passes the result
/// to this interface for durable storage.
///
/// Implementations write to the SQLite memory store.
/// </summary>
public interface IMemoryExtractor
{
    /// <summary>
    /// Persist extracted memories from a pre-compaction memory flush.
    /// </summary>
    /// <param name="sessionId">The session that is being compacted.</param>
    /// <param name="extractedMemories">Structured memory text from the extraction LLM call.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PersistAsync(SessionId sessionId, string extractedMemories, CancellationToken ct = default);
}

/// <summary>
/// No-op memory extractor that discards extracted memories.
/// Used when no external memory backend is configured.
/// </summary>
public sealed class NullMemoryExtractor : IMemoryExtractor
{
    public static readonly NullMemoryExtractor Instance = new();

    public Task PersistAsync(SessionId sessionId, string extractedMemories, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
