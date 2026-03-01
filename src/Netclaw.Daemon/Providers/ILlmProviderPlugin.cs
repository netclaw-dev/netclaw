using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Extends <see cref="IProviderDescriptor"/> with SDK-specific methods
/// that only the daemon uses (client construction, vendor options).
/// </summary>
public interface ILlmProviderPlugin : IProviderDescriptor
{
    /// <summary>
    /// Create an <see cref="IChatClient"/> for the given provider entry and model.
    /// </summary>
    IChatClient CreateChatClient(ProviderEntry entry, ModelReference model);

    /// <summary>
    /// Create a vendor-specific options source, if any.
    /// Returns null for providers that don't need special options handling.
    /// </summary>
    IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry) => null;
}
