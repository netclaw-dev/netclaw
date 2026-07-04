// -----------------------------------------------------------------------
// <copyright file="MemoryEmbedderHolder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Mutable holder for the process's <see cref="IMemoryEmbedder"/> singleton
/// (memory-core-redesign Slice 2, task 2.7).
///
/// <para>
/// <b>Why a holder, not a plain DI singleton:</b> the real embedder is only known once
/// <c>EmbeddingWarmupHostedService</c> (Netclaw.Daemon) finishes provisioning and loading the
/// model — an <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync"/> step that
/// necessarily runs after the DI container has already been built and every other singleton
/// (the curation actor's session, the checkpoint worker) has already resolved its constructor
/// dependencies. A container builds its singleton graph once; there is no way to inject "the
/// embedder after warmup completes" into a constructor, only a slot that gets filled in later.
/// Consumers MUST read <see cref="Current"/> at the time they actually need to embed (never
/// cache the value they read), so the transition from unavailable to available — or the
/// reverse, if a future re-provision fails — surfaces without a process restart.
/// </para>
///
/// <para>
/// Every reader always sees a valid <see cref="IMemoryEmbedder"/> (construction requires an
/// initial value, typically an <see cref="UnavailableMemoryEmbedder"/> stub while warmup is
/// still running) — the holder itself is never null-valued, only whatever it currently holds
/// may report <see cref="IMemoryEmbedder.IsAvailable"/> as false.
/// </para>
/// </summary>
public sealed class MemoryEmbedderHolder
{
    private volatile IMemoryEmbedder _current;

    public MemoryEmbedderHolder(IMemoryEmbedder initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The embedder to use right now. Always non-null.</summary>
    public IMemoryEmbedder Current => _current;

    /// <summary>
    /// Replaces the current embedder. Called only by <c>EmbeddingWarmupHostedService</c> once
    /// provisioning completes — successfully (an <c>OnnxMemoryEmbedder</c>) or not (a fresh
    /// <see cref="UnavailableMemoryEmbedder"/> carrying the failure reason).
    /// </summary>
    public void Set(IMemoryEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        _current = embedder;
    }
}
