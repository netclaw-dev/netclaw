// -----------------------------------------------------------------------
// <copyright file="ModelRole.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Named model roles for multi-model configuration.
/// Each role can point to a different provider and model.
/// </summary>
public enum ModelRole
{
    /// <summary>Primary model for all interactions.</summary>
    Main,

    /// <summary>Automatic failover model (same task class as Main).</summary>
    Fallback,

    /// <summary>Cheaper/faster model for compaction and summarization.</summary>
    Compaction
}
