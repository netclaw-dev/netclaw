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
    private List<SerializableMediaReference> _pending = [];

    public void Add(IEnumerable<SerializableMediaReference> references) => _pending.AddRange(references);

    /// <summary>
    /// Hands off the buffered references and resets the buffer to empty. The
    /// existing list is returned by reference and the buffer adopts a fresh one,
    /// so no copy is made here — the consumer (AddSystemNudge / BuildNudgeMessage)
    /// makes the single defensive copy the immutable persistence type needs.
    /// </summary>
    public IReadOnlyList<SerializableMediaReference> DrainSnapshot()
    {
        if (_pending.Count == 0)
            return [];

        var drained = _pending;
        _pending = [];
        return drained;
    }

    public void Clear() => _pending.Clear();
}
