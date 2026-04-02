namespace Netclaw.Configuration;

/// <summary>
/// Controls whether an automated execution must produce a human-facing
/// notification.
/// </summary>
public enum NotificationPolicy
{
    /// <summary>
    /// Execution fails if notification instructions are present but no
    /// notification was produced.
    /// </summary>
    Required = 0,

    /// <summary>
    /// Notification is optional; the agent may skip it if nothing warrants a
    /// human-facing update.
    /// </summary>
    Conditional = 1
}
