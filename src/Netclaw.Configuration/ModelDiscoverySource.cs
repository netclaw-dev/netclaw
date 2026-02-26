namespace Netclaw.Configuration;

/// <summary>
/// Tracks how a model ID was resolved during onboarding or model selection.
/// Used for provenance tagging so doctor and diagnostics can report
/// confidence level of the selected model.
/// </summary>
public enum ModelDiscoverySource
{
    /// <summary>Discovered from the provider's live model listing API.</summary>
    Live,

    /// <summary>Resolved from curated provider defaults shipped with the application.</summary>
    Defaults,

    /// <summary>Manually entered by the operator.</summary>
    Manual
}
