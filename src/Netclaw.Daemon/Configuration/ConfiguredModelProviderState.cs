// -----------------------------------------------------------------------
// <copyright file="ConfiguredModelProviderState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Startup snapshot of the effective model provider configuration used by
/// daemon services that need to reason about provider availability outside the
/// chat-client factory.
/// </summary>
public sealed class ConfiguredModelProviderState
{
    public ConfiguredModelProviderState(
        IReadOnlyDictionary<string, ProviderEntry> providers,
        ModelSelection models)
    {
        Providers = new Dictionary<string, ProviderEntry>(providers, StringComparer.OrdinalIgnoreCase);
        Models = models;
    }

    public IReadOnlyDictionary<string, ProviderEntry> Providers { get; }

    public ModelSelection Models { get; }
}
