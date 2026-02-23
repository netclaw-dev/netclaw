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
}
