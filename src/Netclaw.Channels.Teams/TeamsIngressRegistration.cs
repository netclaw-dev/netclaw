// -----------------------------------------------------------------------
// <copyright file="TeamsIngressRegistration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

public enum TeamsIngressRegistrationState
{
    Disabled,
    IncompleteConfiguration,
    Ready
}

/// <summary>
/// Non-secret result of evaluating whether the SDK can be activated. The
/// registration decision is shared by daemon DI and status diagnostics so an
/// incomplete credential set cannot accidentally initialize the SDK.
/// </summary>
public sealed record TeamsIngressRegistration(
    TeamsIngressRegistrationState State,
    string ReasonCode)
{
    public bool CanActivateSdk => State == TeamsIngressRegistrationState.Ready;

    public static TeamsIngressRegistration Evaluate(TeamsChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return new TeamsIngressRegistration(TeamsIngressRegistrationState.Disabled, "disabled");

        if (!Enum.IsDefined(options.AuthenticationMode))
            return new TeamsIngressRegistration(TeamsIngressRegistrationState.IncompleteConfiguration, "unsupported_authentication_mode");

        if (options.AuthenticationMode != TeamsAuthenticationMode.ClientSecret)
            return new TeamsIngressRegistration(TeamsIngressRegistrationState.IncompleteConfiguration, "unsupported_authentication_mode");

        if (string.IsNullOrWhiteSpace(options.TenantId))
            return new TeamsIngressRegistration(TeamsIngressRegistrationState.IncompleteConfiguration, "missing_tenant_id");

        if (string.IsNullOrWhiteSpace(options.ClientId))
            return new TeamsIngressRegistration(TeamsIngressRegistrationState.IncompleteConfiguration, "missing_client_id");

        if (options.ClientSecret.IsNullOrEmpty())
            return new TeamsIngressRegistration(TeamsIngressRegistrationState.IncompleteConfiguration, "missing_client_secret");

        return new TeamsIngressRegistration(TeamsIngressRegistrationState.Ready, "ready");
    }
}

public sealed class TeamsChannelRuntimeSnapshotProvider(
    ChannelDescriptor descriptor,
    TeamsIngressRegistration registration) : IChannelRuntimeSnapshotProvider
{
    public ChannelDescriptorKey Key => descriptor.Key;

    public ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var (health, detail, connected, ready) = registration.State switch
        {
            TeamsIngressRegistrationState.Disabled => (
                ChannelHealthStatus.Degraded,
                "Teams connector is disabled in configuration.",
                false,
                false),
            TeamsIngressRegistrationState.IncompleteConfiguration => (
                ChannelHealthStatus.Degraded,
                $"Teams ingress configuration is incomplete ({registration.ReasonCode}).",
                false,
                false),
            TeamsIngressRegistrationState.Ready => (
                ChannelHealthStatus.Degraded,
                "Teams ingress is configured; live tenant authentication has not been validated.",
                false,
                false),
            _ => throw new ArgumentOutOfRangeException()
        };

        return ValueTask.FromResult(new ChannelRuntimeSnapshot(
            descriptor.Key,
            descriptor.IsEnabled,
            health,
            detail,
            connected,
            ready));
    }
}
