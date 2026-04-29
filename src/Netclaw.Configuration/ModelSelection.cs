// -----------------------------------------------------------------------
// <copyright file="ModelSelection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Named model roles bound from the "Models" configuration section.
/// Each role points to a <see cref="ModelReference"/> identifying
/// which provider and model to use for that purpose.
/// </summary>
public sealed class ModelSelection
{
    /// <summary>Primary model for all interactions.</summary>
    public ModelReference Main { get; set; } = new();

    /// <summary>Automatic failover model. Falls back to Main if not set.</summary>
    public ModelReference? Fallback { get; set; }

    /// <summary>Cheaper/faster model for compaction. Falls back to Main if not set.</summary>
    public ModelReference? Compaction { get; set; }
}
