// -----------------------------------------------------------------------
// <copyright file="ModelOverride.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Persisted operator overrides for a single (provider, modelId) pair.
/// Lives in <see cref="ModelSelection.Catalog"/>; merged into matching
/// role records at read time. Only fields the operator has explicitly
/// customized are stored — auto-detected values stay ephemeral and are
/// re-resolved at every daemon startup, so a default install has no
/// catalog entries at all.
/// </summary>
public sealed class ModelOverride
{
    /// <summary>Clamps the runtime session budget; same semantics as <see cref="ModelReference.ContextWindow"/>.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>Manual input-modality override; same semantics as <see cref="ModelReference.InputModalities"/>.</summary>
    public ModelModality? InputModalities { get; set; }

    /// <summary>Manual output-modality override; same semantics as <see cref="ModelReference.OutputModalities"/>.</summary>
    public ModelModality? OutputModalities { get; set; }
}
