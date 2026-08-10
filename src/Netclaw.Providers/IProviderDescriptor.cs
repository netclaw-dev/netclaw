// -----------------------------------------------------------------------
// <copyright file="IProviderDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>
/// Metadata and capabilities for an LLM provider type.
/// Carries everything the CLI/TUI needs to render provider pickers,
/// auth flows, credential inputs, and guidance text.
/// </summary>
public interface IProviderDescriptor
{
    /// <summary>Provider type identifier (e.g. "ollama", "openai").</summary>
    string TypeKey { get; }

    /// <summary>Human-readable display name (e.g. "Ollama", "OpenAI").</summary>
    string DisplayName { get; }

    /// <summary>Default base URL when no explicit endpoint is configured.</summary>
    string DefaultEndpoint { get; }

    /// <summary>Relative path to the model listing API.</summary>
    string ModelListingPath { get; }

    /// <summary>Authentication configuration for this provider.</summary>
    IProviderAuth Auth { get; }

    /// <summary>
    /// Validate credentials and discover available models.
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default);
}
