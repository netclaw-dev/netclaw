// -----------------------------------------------------------------------
// <copyright file="NamedModelConfiguration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
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
/// runtime representation consumed by provider and actor composition. A section that cannot be
/// resolved throws <see cref="ModelConfigurationException"/>. Its message names the keys at fault and
/// says which keys to remove or add, because every surface shows that text as is.
/// </summary>
public static class ModelConfigurationResolver
{
    private static readonly string[] LegacyRoles = ["Main", "Fallback", "Compaction"];
    private static readonly string[] EnvironmentModelKeys = ["Main", "Fallback", "Compaction", "Definitions", "Roles"];

    /// <summary>
    /// Resolves the section. Returns false with the <see cref="ModelConfigurationException"/> text
    /// when the section cannot be resolved.
    /// </summary>
    public static bool TryResolve(
        IConfiguration configuration,
        [NotNullWhen(true)] out ModelConfigurationResolution? resolution,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            resolution = Resolve(configuration);
            error = null;
            return true;
        }
        catch (ModelConfigurationException ex)
        {
            resolution = null;
            error = ex.Message;
            return false;
        }
    }

    public static ModelConfigurationResolution Resolve(IConfigurationSection modelsSection)
    {
        var legacyKeys = LegacyRoles.Where(role => modelsSection.GetSection(role).Exists()).ToList();
        var namedKeys = new[] { nameof(NamedModelConfiguration.Definitions), nameof(NamedModelConfiguration.Roles) }
            .Where(key => modelsSection.GetSection(key).Exists())
            .ToList();
        var hasDefinitions = namedKeys.Contains(nameof(NamedModelConfiguration.Definitions));
        var hasRoles = namedKeys.Contains(nameof(NamedModelConfiguration.Roles));

        if (legacyKeys.Count > 0 && namedKeys.Count > 0)
        {
            throw new ModelConfigurationException(
                $"Models configuration mixes legacy inline roles ({FormatKeys(legacyKeys)}) with current keys ({FormatKeys(namedKeys)}). " +
                $"Keep one set. To keep the current keys, remove {FormatKeys(legacyKeys)}. " +
                $"To keep the legacy keys, remove {FormatKeys(namedKeys)}. " +
                "Remove the keys from netclaw.json and from any NETCLAW_Models__* environment variable" +
                $"{FormatEnvironmentVariables()}. " +
                "`netclaw model set` cannot repair this.");
        }

        if (namedKeys.Count == 0)
        {
            return new ModelConfigurationResolution(
                Bind<ModelSelection>(modelsSection) ?? new ModelSelection(),
                IsLegacy: legacyKeys.Count > 0);
        }

        if (!hasDefinitions)
        {
            throw new ModelConfigurationException(
                "Models:Roles is set but Models:Definitions is missing or empty. " +
                "Add Models:Definitions with the models that Models:Roles names. " +
                "If Models:Definitions is an empty {} in netclaw.json, `netclaw model set main <provider> <model>` repairs it. " +
                "If the key is absent, remove Models:Roles first, then run that command.");
        }

        if (!hasRoles)
        {
            var names = modelsSection.GetSection(nameof(NamedModelConfiguration.Definitions))
                .GetChildren().Select(child => child.Key);
            throw new ModelConfigurationException(
                "Models:Definitions is set but Models:Roles is missing or empty. " +
                $"Add \"Roles\": {{ \"Main\": \"<name>\" }} under Models. <name> is one of: {string.Join(", ", names)}. " +
                "If Models:Roles is an empty {} in netclaw.json, `netclaw model set main <provider> <model>` repairs it.");
        }

        var duplicateDefinition = modelsSection.GetSection(nameof(NamedModelConfiguration.Definitions))
            .GetChildren()
            .GroupBy(child => child.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDefinition is not null)
        {
            throw new ModelConfigurationException(
                $"Models:Definitions contains duplicate case-insensitive name '{duplicateDefinition.Key}'.");
        }

        var named = Bind<NamedModelConfiguration>(modelsSection) ?? new NamedModelConfiguration();
        ThrowIfDefinitionWasDropped(modelsSection.GetSection(nameof(NamedModelConfiguration.Definitions)), named);
        if (named.Definitions.Count == 0)
            throw new ModelConfigurationException(
                "Models:Definitions has no usable model definition. " +
                "Each definition needs Provider and ModelId, and valid ContextWindow and modality values.");

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

    // The binder drops a dictionary entry it cannot convert without an error. Binding that entry
    // alone raises the error with the key path ("...at 'Models:Definitions:fast:InputModalities'").
    private static void ThrowIfDefinitionWasDropped(IConfigurationSection definitions, NamedModelConfiguration named)
    {
        foreach (var child in definitions.GetChildren().Where(child => !named.Definitions.ContainsKey(child.Key)))
        {
            Bind<ModelReference>(child);
            throw new ModelConfigurationException(
                $"Models:Definitions:{child.Key} cannot be read. " +
                "Each definition needs Provider and ModelId, and valid ContextWindow and modality values.");
        }
    }

    private static string FormatEnvironmentVariables()
    {
        var names = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(name => EnvironmentModelKeys.Any(key =>
                name.StartsWith($"NETCLAW_Models__{key}", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? string.Empty : $" (set now: {string.Join(", ", names)})";
    }

    private static string FormatKeys(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(key => $"Models:{key}"));

    // The binder throws a plain InvalidOperationException for a value it cannot convert
    // (for example an unknown modality). Its message already names the key path.
    private static T? Bind<T>(IConfigurationSection section)
    {
        try
        {
            return section.Get<T>();
        }
        catch (InvalidOperationException ex) when (ex is not ModelConfigurationException)
        {
            throw new ModelConfigurationException(
                $"Models configuration has a value that cannot be read. {ex.Message}");
        }
    }

    private static ModelReference ResolveRequired(
        NamedModelConfiguration named, string role, string definitionName)
    {
        if (string.IsNullOrWhiteSpace(definitionName))
            throw new ModelConfigurationException(
                $"Models:Roles:{role} must name a model definition. " +
                $"Run `netclaw model set {role.ToLowerInvariant()} <provider> <model>`.");

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
                $"Models:Roles:{role} references unknown definition '{definitionName}'. " +
                $"Defined: {string.Join(", ", named.Definitions.Keys)}. " +
                $"Fix the name, or run `netclaw model set {role.ToLowerInvariant()} <provider> <model>`.");
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
