// -----------------------------------------------------------------------
// <copyright file="SkillSyncConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for skill index access.
/// </summary>
public sealed class SkillSyncConfig
{
    /// <summary>
    /// When false, skill index tools are unavailable to the session.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
