namespace Netclaw.Configuration;

/// <summary>
/// Marker interface for authentication schemes that support remote (non-loopback) connections.
/// <para>
/// Implementations are registered in the DI container alongside their authentication handler.
/// <c>ExposureModeValidationService</c> queries all registrations at startup — if the daemon
/// is configured for non-local exposure and no paired devices exist, at least one
/// <see cref="IRemoteAuthSchemeRegistration"/> must be present or startup will be aborted.
/// </para>
/// </summary>
public interface IRemoteAuthSchemeRegistration
{
    /// <summary>
    /// Display name for the scheme, used in diagnostic messages.
    /// </summary>
    string SchemeName { get; }
}
