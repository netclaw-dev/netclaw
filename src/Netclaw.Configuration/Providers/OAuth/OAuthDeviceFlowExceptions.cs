namespace Netclaw.Configuration.Providers.OAuth;

/// <summary>
/// Thrown when the user denies the device authorization request.
/// </summary>
public sealed class OAuthDeviceFlowDeniedException : Exception
{
    public OAuthDeviceFlowDeniedException()
        : base("The user denied the device authorization request.")
    {
    }
}

/// <summary>
/// Thrown when the device code expires before the user completes authorization.
/// </summary>
public sealed class OAuthDeviceFlowExpiredException : Exception
{
    public OAuthDeviceFlowExpiredException()
        : base("The device code has expired. Please start the authorization flow again.")
    {
    }
}
