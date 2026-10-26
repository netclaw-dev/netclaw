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

    /// <summary>
    /// The thinking dialect of a provider entry. The CLI, doctor, and the daemon use this one
    /// resolution, so each surface offers and sends the same levels.
    /// </summary>
    ThinkingDialect ResolveThinkingDialect(ProviderEntry entry);

    /// <summary>
    /// True for a provider whose endpoint the operator runs (Ollama, an OpenAI-compatible server).
    /// Its default output limit clamps only to a window that the endpoint or the operator states,
    /// and a catalog model maximum does not cap it. A hosted API keeps the default false.
    /// </summary>
    bool IsSelfHosted => false;
}
