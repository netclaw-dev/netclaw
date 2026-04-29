// -----------------------------------------------------------------------
// <copyright file="SessionAffinityContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Ambient context that carries a session identifier through async call chains
/// so that an <see cref="SessionAffinityHandler"/> on the <see cref="HttpClient"/>
/// pipeline can promote it to an HTTP header. This enables load-balancer session
/// affinity for self-hosted inference servers — keeping the KV cache warm across
/// turns within the same session.
///
/// Only main-model calls should set this. Sidecar calls (title generation, memory
/// extraction, compaction observer) run from separate Akka message handlers with
/// fresh execution contexts, so they naturally see <c>null</c> and round-robin
/// across backends without competing for the main session's KV cache slot.
/// </summary>
public static class SessionAffinityContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? SessionId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
