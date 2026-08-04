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

    public ModelProxyAssignments Proxies { get; set; } = new();
}

public sealed class ModelRoleAssignments
{
    public string Main { get; set; } = string.Empty;
    public string? Fallback { get; set; }
    public string? Compaction { get; set; }
}

public sealed class ModelProxyAssignments
{
    public string? Image { get; set; }
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
        var hasProxies = modelsSection.GetSection(nameof(NamedModelConfiguration.Proxies)).Exists();
        var hasNamed = hasDefinitions || hasRoles || hasProxies;

        if (hasLegacy && hasNamed)
            throw new ModelConfigurationException(
                "Models configuration mixes legacy inline roles with named Definitions/Roles. " +
                "Run `netclaw doctor --fix` after removing one representation.");

        if (!hasNamed)
        {
            var legacySelection = modelsSection.Get<ModelSelection>() ?? new ModelSelection();
            return new ModelConfigurationResolution(
                legacySelection,
                IsLegacy: hasLegacy,
                Runtime: BuildLegacyRuntime(legacySelection));
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
            Main = ResolveRequired(named, "Roles:Main", named.Roles.Main),
            Fallback = ResolveOptional(named, "Roles:Fallback", named.Roles.Fallback),
            Compaction = ResolveOptional(named, "Roles:Compaction", named.Roles.Compaction),
        };

        _ = ResolveOptional(named, "Proxies:Image", named.Proxies.Image);

        return new ModelConfigurationResolution(
            selection,
            IsLegacy: false,
            Runtime: BuildNamedRuntime(named));
    }

    public static ModelConfigurationResolution Resolve(IConfiguration configuration)
        => Resolve(configuration.GetSection("Models"));

    private static ModelReference ResolveRequired(
        NamedModelConfiguration named, string assignmentPath, string definitionName)
    {
        if (string.IsNullOrWhiteSpace(definitionName))
            throw new ModelConfigurationException(
                $"Models:{assignmentPath} must reference a model definition.");

        return ResolveDefinition(named, assignmentPath, definitionName);
    }

    private static ModelReference? ResolveOptional(
        NamedModelConfiguration named, string assignmentPath, string? definitionName)
        => string.IsNullOrWhiteSpace(definitionName)
            ? null
            : ResolveDefinition(named, assignmentPath, definitionName);

    private static ModelReference ResolveDefinition(
        NamedModelConfiguration named, string assignmentPath, string definitionName)
    {
        var match = named.Definitions.FirstOrDefault(pair =>
            string.Equals(pair.Key, definitionName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(match.Key))
        {
            throw new ModelConfigurationException(
                $"Models:{assignmentPath} references unknown definition '{definitionName}'.");
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

    private static ModelRuntimeConfiguration BuildLegacyRuntime(ModelSelection selection)
    {
        var definitions = new Dictionary<string, ModelReference>(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = Clone(selection.Main)
        };
        var roles = new ModelRoleAssignments { Main = "main" };

        if (selection.Fallback is not null)
        {
            definitions["fallback"] = Clone(selection.Fallback);
            roles.Fallback = "fallback";
        }

        if (selection.Compaction is not null)
        {
            definitions["compaction"] = Clone(selection.Compaction);
            roles.Compaction = "compaction";
        }

        return new ModelRuntimeConfiguration(definitions, roles, new ModelProxyAssignments());
    }

    private static ModelRuntimeConfiguration BuildNamedRuntime(NamedModelConfiguration named)
    {
        var definitions = named.Definitions.ToDictionary(
            pair => pair.Key,
            pair => Clone(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        var roles = new ModelRoleAssignments
        {
            Main = named.Roles.Main,
            Fallback = named.Roles.Fallback,
            Compaction = named.Roles.Compaction
        };
        var proxies = new ModelProxyAssignments { Image = named.Proxies.Image };
        return new ModelRuntimeConfiguration(definitions, roles, proxies);
    }
}

public sealed record ModelRuntimeConfiguration(
    IReadOnlyDictionary<string, ModelReference> Definitions,
    ModelRoleAssignments Roles,
    ModelProxyAssignments Proxies);

public sealed record ModelConfigurationResolution(
    ModelSelection Selection,
    bool IsLegacy,
    ModelRuntimeConfiguration Runtime);

/// <summary>
/// Represents an invalid operator-authored model configuration that cannot be resolved safely.
/// </summary>
public sealed class ModelConfigurationException(string message) : InvalidOperationException(message);
