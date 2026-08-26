// -----------------------------------------------------------------------
// <copyright file="SkillSyncConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for startup synchronization of built-in system skills.
/// </summary>
public sealed class SkillSyncConfig
{
    /// <summary>
    /// When false, the skill sync subsystem is disabled entirely.
    /// No system skill synchronization is performed regardless of other settings.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, skip feed-based system skill sync at daemon startup and use
    /// local built-in/on-disk skills only.
    /// </summary>
    public bool DisableSystemSkillSync { get; set; } = false;
}
