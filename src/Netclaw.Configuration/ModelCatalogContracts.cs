// -----------------------------------------------------------------------
// <copyright file="ModelCatalogContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// A single model entry returned from the catalog.
/// <para>
/// <c>InputModalities</c> and <c>OutputModalities</c> are arrays of modality
/// strings (e.g. <c>["Text", "Image"]</c>).
/// </para>
/// </summary>
public sealed record ModelCatalogEntry
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required string DisplayName { get; init; }

    public int? ContextWindow { get; init; }

    public string[] InputModalities { get; init; } = [];

    public string[] OutputModalities { get; init; } = [];

    /// <summary>UI category badges such as "frontier", "fast", "local".</summary>
    public string[] Badges { get; init; } = [];

    public string? Notes { get; init; }
}

/// <summary>
/// Response for <c>GET /api/models</c>.
/// </summary>
public sealed record GetModelCatalogResponse
{
    public required IReadOnlyList<ModelCatalogEntry> Models { get; init; }

    public string? Warning { get; init; }
}

/// <summary>
/// A model reference projected onto the wire — omits internal types.
/// </summary>
public sealed record ModelSelectionReference
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public int? ContextWindow { get; init; }

    public string? Provenance { get; init; }

    public string? InputModalities { get; init; }

    public string? OutputModalities { get; init; }
}

/// <summary>
/// Response for <c>GET /api/model/selection</c>.
/// </summary>
public sealed record GetModelSelectionResponse
{
    public ModelSelectionReference? Main { get; init; }

    public ModelSelectionReference? Fallback { get; init; }

    public ModelSelectionReference? Compaction { get; init; }
}

/// <summary>
/// Request for <c>PUT /api/model/selection</c>. <see cref="Role"/> must be
/// "Main", "Fallback", or "Compaction".
/// </summary>
public sealed record PutModelSelectionRequest
{
    public required string Role { get; init; }

    public required ModelSelectionReference Reference { get; init; }
}

/// <summary>
/// Response for <c>PUT /api/model/selection</c>.
/// </summary>
public sealed record PutModelSelectionResponse
{
    public required string ConfigPath { get; init; }

    /// <summary>
    /// Always true: model selection is bound at daemon startup. The daemon
    /// must be restarted before the new selection takes effect.
    /// </summary>
    public bool RestartRequired { get; init; } = true;
}

/// <summary>
/// Error response for <c>PUT /api/model/selection</c> when the request fails
/// validation or schema checks.
/// </summary>
public sealed record PutModelSelectionErrorResponse
{
    public required string Message { get; init; }

    public string[] ValidationErrors { get; init; } = [];
}
