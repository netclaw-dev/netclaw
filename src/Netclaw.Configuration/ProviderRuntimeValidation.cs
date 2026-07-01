// -----------------------------------------------------------------------
// <copyright file="ProviderRuntimeValidation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

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
        ModelSelection models,
        ProviderRuntimeConfiguration configuration)
    {
        var available = providers.Keys.ToList();
        var main = models.Main;
        var providersEmpty = providers.Count == 0;

        if (!configuration.Main.RoleConfigured)
        {
            return new(
                ProviderRuntimeStatus.NoProviderConfigured,
                "no main model configured (Models:Main missing)",
                available);
        }

        var modelMissing = ModelReferenceMissing(main) || !configuration.Main.ProviderConfigured ||
                           !configuration.Main.ModelIdConfigured;

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

        var invalidMain = ValidateReferencedProvider(
            nameof(models.Main),
            main,
            providers,
            configuration,
            missingProviderStatus: ProviderRuntimeStatus.NoProviderConfigured);
        if (invalidMain is not null)
            return invalidMain;

        var invalidFallback = ValidateOptionalRole(
            nameof(models.Fallback),
            models.Fallback,
            configuration.Fallback,
            providers,
            configuration);
        if (invalidFallback is not null)
            return invalidFallback;

        var invalidCompaction = ValidateOptionalRole(
            nameof(models.Compaction),
            models.Compaction,
            configuration.Compaction,
            providers,
            configuration);
        if (invalidCompaction is not null)
            return invalidCompaction;

        return new(ProviderRuntimeStatus.Valid, null, available);
    }

    private static ProviderRuntimeValidation? ValidateOptionalRole(
        string role,
        ModelReference? model,
        ModelReferenceRuntimeConfiguration roleConfiguration,
        IReadOnlyDictionary<string, ProviderEntry> providers,
        ProviderRuntimeConfiguration configuration)
    {
        if (!roleConfiguration.RoleConfigured)
            return null;

        if (model is null || ModelReferenceMissing(model) || !roleConfiguration.ProviderConfigured ||
            !roleConfiguration.ModelIdConfigured)
        {
            return new(
                ProviderRuntimeStatus.Invalid,
                $"model '{role}' is configured but incomplete (Provider and ModelId are required)",
                providers.Keys.ToList());
        }

        return ValidateReferencedProvider(
            role,
            model,
            providers,
            configuration,
            missingProviderStatus: ProviderRuntimeStatus.Invalid);
    }

    private static ProviderRuntimeValidation? ValidateReferencedProvider(
        string role,
        ModelReference model,
        IReadOnlyDictionary<string, ProviderEntry> providers,
        ProviderRuntimeConfiguration configuration,
        ProviderRuntimeStatus missingProviderStatus)
    {
        var available = providers.Keys.ToList();

        if (providers.Count == 0)
        {
            return new(
                ProviderRuntimeStatus.NoProviderConfigured,
                $"model '{role}' references provider '{model.Provider}' but no providers are configured",
                available);
        }

        if (!providers.TryGetValue(model.Provider, out var provider))
        {
            return new(
                missingProviderStatus,
                $"model '{role}' references provider '{model.Provider}' which is not configured (available: {string.Join(", ", available)})",
                available);
        }

        if (!configuration.HasExplicitProviderType(model.Provider) || string.IsNullOrWhiteSpace(provider.Type))
        {
            return new(
                ProviderRuntimeStatus.Invalid,
                $"provider '{model.Provider}' referenced by model '{role}' is missing required Type",
                available);
        }

        return null;
    }

    private static bool ModelReferenceMissing(ModelReference model)
        => string.IsNullOrWhiteSpace(model.Provider) ||
           string.IsNullOrWhiteSpace(model.ModelId);
}

/// <summary>
/// Raw configuration presence data needed before bound defaults are applied.
/// </summary>
public sealed record ProviderRuntimeConfiguration(
    ModelReferenceRuntimeConfiguration Main,
    ModelReferenceRuntimeConfiguration Fallback,
    ModelReferenceRuntimeConfiguration Compaction,
    IReadOnlyCollection<string> ProvidersWithExplicitType)
{
    public bool HasExplicitProviderType(string providerName) =>
        ProvidersWithExplicitType.Contains(providerName, StringComparer.OrdinalIgnoreCase);

    public static ProviderRuntimeConfiguration FromConfiguration(IConfiguration configuration)
    {
        var models = configuration.GetSection("Models");
        var providers = configuration.GetSection("Providers");

        return new ProviderRuntimeConfiguration(
            Main: ModelReferenceRuntimeConfiguration.FromConfiguration(models.GetSection("Main")),
            Fallback: ModelReferenceRuntimeConfiguration.FromConfiguration(models.GetSection("Fallback")),
            Compaction: ModelReferenceRuntimeConfiguration.FromConfiguration(models.GetSection("Compaction")),
            ProvidersWithExplicitType: providers.GetChildren()
                .Where(provider => provider.GetSection(nameof(ProviderEntry.Type)).Exists())
                .Select(provider => provider.Key)
                .ToList());
    }

    public static ProviderRuntimeConfiguration FromJson(JsonObject? root)
    {
        var models = root?["Models"] as JsonObject;
        var providers = root?["Providers"] as JsonObject;

        return new ProviderRuntimeConfiguration(
            Main: ModelReferenceRuntimeConfiguration.FromJson(models?["Main"] as JsonObject),
            Fallback: ModelReferenceRuntimeConfiguration.FromJson(models?["Fallback"] as JsonObject),
            Compaction: ModelReferenceRuntimeConfiguration.FromJson(models?["Compaction"] as JsonObject),
            ProvidersWithExplicitType: providers is null
                ? []
                : providers
                    .Where(provider => provider.Value is JsonObject obj && HasProperty(obj, nameof(ProviderEntry.Type)))
                    .Select(provider => provider.Key)
                    .ToList());
    }

    public static ProviderRuntimeConfiguration FromExplicitRoles(
        IReadOnlyDictionary<string, ProviderEntry> providers,
        bool main,
        bool fallback,
        bool compaction)
    {
        return new ProviderRuntimeConfiguration(
            ModelReferenceRuntimeConfiguration.FromCompleteRole(main),
            ModelReferenceRuntimeConfiguration.FromCompleteRole(fallback),
            ModelReferenceRuntimeConfiguration.FromCompleteRole(compaction),
            providers
                .Where(provider => !string.IsNullOrWhiteSpace(provider.Value.Type))
                .Select(provider => provider.Key)
                .ToList());
    }

    internal static bool HasProperty(JsonObject obj, string propertyName) =>
        obj.Any(property => string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase));
}

public sealed record ModelReferenceRuntimeConfiguration(
    bool RoleConfigured,
    bool ProviderConfigured,
    bool ModelIdConfigured)
{
    public static ModelReferenceRuntimeConfiguration FromConfiguration(IConfigurationSection section)
    {
        return new ModelReferenceRuntimeConfiguration(
            RoleConfigured: section.Exists(),
            ProviderConfigured: section.GetSection(nameof(ModelReference.Provider)).Exists(),
            ModelIdConfigured: section.GetSection(nameof(ModelReference.ModelId)).Exists());
    }

    public static ModelReferenceRuntimeConfiguration FromJson(JsonObject? obj)
    {
        return new ModelReferenceRuntimeConfiguration(
            RoleConfigured: obj is not null,
            ProviderConfigured: obj is not null && ProviderRuntimeConfiguration.HasProperty(obj, nameof(ModelReference.Provider)),
            ModelIdConfigured: obj is not null && ProviderRuntimeConfiguration.HasProperty(obj, nameof(ModelReference.ModelId)));
    }

    public static ModelReferenceRuntimeConfiguration FromCompleteRole(bool configured) =>
        configured
            ? new ModelReferenceRuntimeConfiguration(true, true, true)
            : new ModelReferenceRuntimeConfiguration(false, false, false);
}
