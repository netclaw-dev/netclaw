// -----------------------------------------------------------------------
// <copyright file="FakeCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Fake resolver for tests — always returns text-only capabilities.
/// </summary>
internal sealed class FakeCapabilityResolver : IModelCapabilityResolver
{
    public Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
        => Task.FromResult<ResolvedModelCapabilities?>(
            new ResolvedModelCapabilities(modelId, ModelModality.Text, ModelModality.Text));
}
