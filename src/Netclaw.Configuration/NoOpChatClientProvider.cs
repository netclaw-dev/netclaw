// -----------------------------------------------------------------------
// <copyright file="NoOpChatClientProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Configuration;

/// <summary>
/// <see cref="IChatClientProvider"/> registered when configuration validation
/// reports <see cref="ProviderRuntimeStatus.NoProviderConfigured"/>. Returns
/// the same <see cref="NoOpChatClient"/> instance for every
/// <see cref="ModelRole"/> and reports <see cref="IsDegraded"/> = true so
/// doctor and diagnostics can surface the degraded state.
/// </summary>
public sealed class NoOpChatClientProvider : IChatClientProvider
{
    private readonly NoOpChatClient _client;

    public NoOpChatClientProvider(IReadOnlyList<string>? availableProviders = null)
    {
        _client = new NoOpChatClient(availableProviders);
    }

    public IChatClient GetClient(ModelRole role) => _client;

    public bool IsDegraded => true;
}
