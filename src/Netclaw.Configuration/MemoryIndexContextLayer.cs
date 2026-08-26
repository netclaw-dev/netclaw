// -----------------------------------------------------------------------
// <copyright file="MemoryIndexContextLayer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The possible states for the memory context layer.
/// </summary>
public enum MemoryContextState
{
    /// <summary>
    /// SQLite-backed memory with automatic pre-turn recall.
    /// </summary>
    SqlitePrimary,

    /// <summary>
    /// SQLite-backed memory is degraded or unavailable.
    /// </summary>
    SqliteDegraded
}

/// <summary>
/// Dynamic context layer that provides memory subsystem guidance to the LLM.
/// Updated after MCP startup completes.
/// Returns empty for Public audience or when memory is disabled.
/// </summary>
public sealed class MemoryIndexContextLayer : IContextLayerProvider
{
    private readonly MemoryConfig _config;
    private volatile string _status = string.Empty;

    public MemoryIndexContextLayer() : this(new MemoryConfig()) { }

    public MemoryIndexContextLayer(MemoryConfig config)
    {
        _config = config;
    }

    public ContextLayerTiming Timing => ContextLayerTiming.OnceAtStart;

    /// <summary>
    /// Update the memory context layer based on the resolved state.
    /// </summary>
    public void Update(MemoryContextState state)
    {
        _status = state switch
        {
            MemoryContextState.SqlitePrimary => """
                [memories — sqlite-backed with automatic recall]
                Tools: find_memories, get_memories, store_memory, update_memory
                Durable memory recall is automatic before each user-facing turn.
                Use explicit memory tools only for deliberate manual control.
                Automatic recall injects durable_fact and evidence memories, and only
                injects items that clear a relevance floor — many turns inject nothing.
                Deliberate find_memories searches may return durable_fact plus evidence.
                Trace data is excluded from normal search results.
                Expired evidence is hidden from normal find_memories results unless explicitly requested for audit/debug review.

                Use find_memories/get_memories when automatic recall is insufficient or the
                user explicitly asks what you remember.

                Use store_memory only for explicit remember/save requests.
                Use update_memory only for corrections, supersede, tombstone, or metadata changes.

                Do not call explicit memory write tools as a reflex on every turn.

                For full guidance: file_read netclaw-memory.
                On errors or degraded memory: file_read netclaw-operations.
                """,

            MemoryContextState.SqliteDegraded => """
                [memories — sqlite degraded]
                Automatic durable recall is currently degraded.
                Continue the turn without assuming recall data is complete.
                Use explicit memory tools only for deliberate/manual control paths.
                Check netclaw-operations for memory health and recovery guidance.
                """,

            _ => string.Empty
        };
    }

    public string GetContextLayer(TrustAudience audience)
    {
        if (audience == TrustAudience.Public)
            return string.Empty;
        if (!_config.Enabled)
            return string.Empty;
        return _status;
    }
}
