// -----------------------------------------------------------------------
// <copyright file="NullNotificationSink.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// No-op notification sink used when no webhook targets are configured.
/// Alerts are silently discarded — operational events are still logged
/// by the emitting component via ILogger.
/// </summary>
public sealed class NullNotificationSink : IOperationalNotificationSink
{
    public static readonly NullNotificationSink Instance = new();

    public void Emit(OperationalAlert alert)
    {
        // Intentionally empty — logging happens at the emission site
    }
}
