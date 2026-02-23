using Microsoft.Extensions.AI;

namespace Netclaw.Configuration;

/// <summary>
/// Wraps a single <see cref="IChatClient"/> and returns it for all roles.
/// Used in tests and as a fallback when only one model is configured.
/// </summary>
public sealed class SingleClientProvider(IChatClient client) : IChatClientProvider
{
    public IChatClient GetClient(ModelRole role) => client;
}
