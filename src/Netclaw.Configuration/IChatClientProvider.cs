// -----------------------------------------------------------------------
// <copyright file="IChatClientProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Configuration;

/// <summary>
/// Resolves an <see cref="IChatClient"/> by model role.
/// Implementations handle provider lookup and client creation.
/// The actor layer consumes this interface without knowing about
/// provider credentials, endpoints, or specific provider SDKs.
/// </summary>
public interface IChatClientProvider
{
    /// <summary>
    /// Returns the <see cref="IChatClient"/> for the specified model role.
    /// If the requested role has no configured model, falls back to
    /// <see cref="ModelRole.Main"/>.
    /// </summary>
    IChatClient GetClient(ModelRole role);

    /// <summary>
    /// True when the provider is serving a No-Op fallback because no valid
    /// inference provider configuration was detected. Diagnostic surfaces
    /// (notably <c>netclaw doctor</c>) check this so they can report the
    /// degraded state without inspecting concrete implementation types.
    /// Real provider implementations leave this as <c>false</c>.
    /// </summary>
    bool IsDegraded => false;
}
