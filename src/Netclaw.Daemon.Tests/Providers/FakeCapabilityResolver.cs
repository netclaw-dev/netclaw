// -----------------------------------------------------------------------
// <copyright file="FakeCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>Returns one fixed capability result and records that the chain called it.</summary>
internal sealed class FakeCapabilityResolver : IModelCapabilityResolver
{
    private readonly ResolvedModelCapabilities? _result;
    public bool WasCalled { get; private set; }
    public string? ProviderType { get; }

    public FakeCapabilityResolver(ResolvedModelCapabilities? result, string? providerType = null)
    {
        _result = result;
        ProviderType = providerType;
    }

    public Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        WasCalled = true;
        return Task.FromResult(_result);
    }
}
