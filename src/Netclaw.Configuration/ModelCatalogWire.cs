// -----------------------------------------------------------------------
// <copyright file="ModelCatalogWire.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Wire contracts for <c>/api/models</c> (catalog) and <c>/api/model/selection</c>.
/// </summary>
public static class ModelCatalogWire
{
    /// <summary>
    /// A single model entry returned from the catalog.
    /// <para>
    /// <c>InputModalities</c> and <c>OutputModalities</c> are arrays of modality
    /// strings (e.g. <c>["Text", "Image"]</c>).
    /// </para>
    /// </summary>
    public sealed class ModelEntry : IWireType
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

    public sealed class GetCatalogResponse : IWireType
    {
        public required IReadOnlyList<ModelEntry> Models { get; init; }
        public string? Warning { get; init; }
    }

    /// <summary>ModelReference projected onto the wire — omits internal types.</summary>
    public sealed class ModelReferenceWire : IWireType
    {
        public required string Provider { get; init; }
        public required string ModelId { get; init; }
        public int? ContextWindow { get; init; }
        public string? Provenance { get; init; }
        public string? InputModalities { get; init; }
        public string? OutputModalities { get; init; }
    }

    public sealed class GetSelectionResponse : IWireType
    {
        public required ModelReferenceWire Main { get; init; }
        public ModelReferenceWire? Fallback { get; init; }
        public ModelReferenceWire? Compaction { get; init; }
    }

    /// <summary>
    /// Replaces one model role. <see cref="Role"/> must be "Main", "Fallback", or "Compaction".
    /// </summary>
    public sealed class PutSelectionRequest : IWireType
    {
        public required string Role { get; set; }
        public required ModelReferenceWire Reference { get; set; }
    }

    public sealed class PutSelectionResponse : IWireType
    {
        public required string ConfigPath { get; init; }

        /// <summary>
        /// Always true: model selection is bound at daemon startup. The daemon
        /// must be restarted before the new selection takes effect.
        /// </summary>
        public bool RestartRequired { get; init; } = true;
    }

    public sealed class PutSelectionErrorResponse : IWireType
    {
        public required string Message { get; init; }
        public string[] ValidationErrors { get; init; } = [];
    }
}
