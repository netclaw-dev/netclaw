// -----------------------------------------------------------------------
// <copyright file="DevicePairingSchemeRegistration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Registers the device bearer token scheme as a remote-capable authentication scheme.
/// Registered in DI so <c>ExposureModeValidationService</c> can detect that at least one
/// remote auth scheme is active when the daemon is configured for non-local exposure.
/// </summary>
internal sealed class DevicePairingSchemeRegistration : IRemoteAuthSchemeRegistration
{
    public string SchemeName => DeviceTokenAuthenticationHandler.SchemeName;
}
