// -----------------------------------------------------------------------
// <copyright file="IOperationalNotificationSink.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Receives operational alerts from daemon components. Producers call
/// <see cref="Emit"/> to report events; the implementation decides how
/// to deliver them (webhook, log-only, etc.).
///
/// Designed to be injected as a singleton into any service that detects
/// operational issues. Must be thread-safe. The method is intentionally
/// fire-and-forget (void, not Task) — producers should never block on
/// notification delivery.
/// </summary>
public interface IOperationalNotificationSink
{
    void Emit(OperationalAlert alert);
}
