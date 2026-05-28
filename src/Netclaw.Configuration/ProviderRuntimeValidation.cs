// -----------------------------------------------------------------------
// <copyright file="ProviderRuntimeValidation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Result of evaluating provider+model configuration at host startup.
/// Tri-state: a valid configuration produces a real chat client; "no provider
/// configured" produces a No-Op client (degraded but operational); "invalid"
/// fails startup loudly.
/// </summary>
public enum ProviderRuntimeStatus
{
    Valid,
    NoProviderConfigured,
    Invalid,
}

/// <summary>
/// Evaluation outcome for provider+model configuration. The host composition
/// root branches on <see cref="Status"/> to decide whether to register the
/// real <see cref="IChatClientProvider"/> or the No-Op fallback.
/// </summary>
public sealed record ProviderRuntimeValidation(
    ProviderRuntimeStatus Status,
    string? Reason,
    IReadOnlyList<string> AvailableProviders)
{
    public static ProviderRuntimeValidation Evaluate(
        IReadOnlyDictionary<string, ProviderEntry> providers,
        ModelSelection models)
    {
        var available = providers.Keys.ToList();
        var main = models.Main;
        var providersEmpty = providers.Count == 0;
        var modelMissing = string.IsNullOrWhiteSpace(main.Provider) ||
                           string.IsNullOrWhiteSpace(main.ModelId);

        if (providersEmpty && modelMissing)
        {
            return new(
                ProviderRuntimeStatus.NoProviderConfigured,
                "no providers or models configured",
                available);
        }

        if (modelMissing)
        {
            return new(
                ProviderRuntimeStatus.NoProviderConfigured,
                "no model selected (Models:Main missing or incomplete)",
                available);
        }

        if (providersEmpty)
        {
            return new(
                ProviderRuntimeStatus.NoProviderConfigured,
                $"model 'Main' references provider '{main.Provider}' but no providers are configured",
                available);
        }

        if (!providers.ContainsKey(main.Provider))
        {
            return new(
                ProviderRuntimeStatus.Invalid,
                $"model 'Main' references unknown provider '{main.Provider}' (configured: {string.Join(", ", available)})",
                available);
        }

        return new(ProviderRuntimeStatus.Valid, null, available);
    }
}
