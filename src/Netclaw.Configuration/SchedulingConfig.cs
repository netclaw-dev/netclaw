// -----------------------------------------------------------------------
// <copyright file="SchedulingConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the scheduling / reminders subsystem.
/// Controls whether scheduled tasks and reminder tools are wired up.
/// </summary>
public sealed class SchedulingConfig
{
    /// <summary>
    /// When false, the scheduling subsystem is disabled.
    /// Reminder and scheduling tools are not registered regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
