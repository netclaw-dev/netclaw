// -----------------------------------------------------------------------
// <copyright file="NamedModelConfiguration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Canonical model configuration. Definitions own model metadata; roles only select definitions.
/// </summary>
public sealed class NamedModelConfiguration
{
    public Dictionary<string, ModelReference> Definitions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public ModelRoleAssignments Roles { get; set; } = new();
}

public sealed class ModelRoleAssignments
{
    public string Main { get; set; } = string.Empty;
    public string? Fallback { get; set; }
    public string? Compaction { get; set; }
}

/// <summary>
/// Resolves either the legacy inline role shape or the canonical named-definition shape into the
/// runtime representation consumed by provider and actor composition. Mixed shapes fail loudly.
/// </summary>
public static class ModelConfigurationResolver
{
    private static readonly string[] LegacyRoles = ["Main", "Fallback", "Compaction"];

    public static ModelConfigurationResolution Resolve(IConfigurationSection modelsSection)
    {
        var hasLegacy = LegacyRoles.Any(role => modelsSection.GetSection(role).Exists());
        var hasDefinitions = modelsSection.GetSection(nameof(NamedModelConfiguration.Definitions)).Exists();
        var hasRoles = modelsSection.GetSection(nameof(NamedModelConfiguration.Roles)).Exists();
        var hasNamed = hasDefinitions || hasRoles;

        if (hasLegacy && hasNamed)
            throw new ModelConfigurationException(
                "Models configuration mixes legacy inline roles with named Definitions/Roles. " +
                "Run `netclaw doctor --fix` after removing one representation.");

        if (!hasNamed)
        {
            return new ModelConfigurationResolution(
                modelsSection.Get<ModelSelection>() ?? new ModelSelection(),
                IsLegacy: hasLegacy);
        }

        if (!hasDefinitions || !hasRoles)
            throw new ModelConfigurationException(
                "Named Models configuration requires both Definitions and Roles sections.");

        var duplicateDefinition = modelsSection.GetSection(nameof(NamedModelConfiguration.Definitions))
            .GetChildren()
            .GroupBy(child => child.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDefinition is not null)
        {
            throw new ModelConfigurationException(
                $"Models:Definitions contains duplicate case-insensitive name '{duplicateDefinition.Key}'.");
        }

        var named = modelsSection.Get<NamedModelConfiguration>() ?? new NamedModelConfiguration();
        if (named.Definitions.Count == 0)
            throw new ModelConfigurationException("Models:Definitions must contain at least one model definition.");

        var selection = new ModelSelection
        {
            Main = ResolveRequired(named, nameof(named.Roles.Main), named.Roles.Main),
            Fallback = ResolveOptional(named, nameof(named.Roles.Fallback), named.Roles.Fallback),
            Compaction = ResolveOptional(named, nameof(named.Roles.Compaction), named.Roles.Compaction),
        };

        return new ModelConfigurationResolution(selection, IsLegacy: false);
    }

    public static ModelConfigurationResolution Resolve(IConfiguration configuration)
        => Resolve(configuration.GetSection("Models"));

    private static ModelReference ResolveRequired(
        NamedModelConfiguration named, string role, string definitionName)
    {
        if (string.IsNullOrWhiteSpace(definitionName))
            throw new ModelConfigurationException($"Models:Roles:{role} must reference a model definition.");

        return ResolveDefinition(named, role, definitionName);
    }

    private static ModelReference? ResolveOptional(
        NamedModelConfiguration named, string role, string? definitionName)
        => string.IsNullOrWhiteSpace(definitionName)
            ? null
            : ResolveDefinition(named, role, definitionName);

    private static ModelReference ResolveDefinition(
        NamedModelConfiguration named, string role, string definitionName)
    {
        var match = named.Definitions.FirstOrDefault(pair =>
            string.Equals(pair.Key, definitionName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(match.Key))
        {
            throw new ModelConfigurationException(
                $"Models:Roles:{role} references unknown definition '{definitionName}'.");
        }

        return Clone(match.Value);
    }

    private static ModelReference Clone(ModelReference source) => new()
    {
        Provider = source.Provider,
        ModelId = source.ModelId,
        ContextWindow = source.ContextWindow,
        Provenance = source.Provenance,
        InputModalities = source.InputModalities,
        OutputModalities = source.OutputModalities,
    };
}

public sealed record ModelConfigurationResolution(ModelSelection Selection, bool IsLegacy);

/// <summary>
/// Represents an invalid operator-authored model configuration that cannot be resolved safely.
/// </summary>
public sealed class ModelConfigurationException(string message) : InvalidOperationException(message);
