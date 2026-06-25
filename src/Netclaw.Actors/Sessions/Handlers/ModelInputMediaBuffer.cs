// -----------------------------------------------------------------------
// <copyright file="ModelInputMediaBuffer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Buffers media references that tools load for model-visible inspection during
/// a streamed tool batch. References accumulate per tool result and are drained
/// into a single system nudge once the batch completes, or cleared when the turn
/// fails or its tool-batch tracking resets. Transient and actor-owned: never
/// persisted, rebuilt implicitly per batch.
/// </summary>
internal sealed class ModelInputMediaBuffer
{
    private readonly List<SerializableMediaReference> _pending = [];

    public void Add(IEnumerable<SerializableMediaReference> references) => _pending.AddRange(references);

    /// <summary>Returns the buffered references and clears the buffer.</summary>
    public IReadOnlyList<SerializableMediaReference> DrainSnapshot()
    {
        if (_pending.Count == 0)
            return [];

        var snapshot = _pending.ToArray();
        _pending.Clear();
        return snapshot;
    }

    public void Clear() => _pending.Clear();
}
