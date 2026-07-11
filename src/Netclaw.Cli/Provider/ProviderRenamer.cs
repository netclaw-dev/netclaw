// -----------------------------------------------------------------------
// <copyright file="ProviderRenamer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Provider;

/// <summary>
/// Renames a provider entry across <c>netclaw.json</c> and
/// <c>.secrets/netclaw-secrets.json</c>. Swaps the <c>Providers</c>
/// dictionary key and cascades the rename to any
/// <c>Models.{Main,Fallback,Compaction}.Provider</c> entries that pointed
/// at the old name, so the daemon never sees a dangling reference after
/// a rename.
/// </summary>
internal readonly record struct RenameResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<string> ReassignedModelRoles)
{
    public static RenameResult Ok(IReadOnlyList<string> reassignedRoles) =>
        new(true, null, reassignedRoles);

    public static RenameResult Fail(string message) =>
        new(false, message, Array.Empty<string>());
}

internal static class ProviderRenamer
{
    private static readonly string[] ModelRoleNames = ["Main", "Fallback", "Compaction"];

    /// <summary>
    /// Rename a provider, cascading the rename to any model roles that
    /// reference it.
    /// </summary>
    /// <remarks>
    /// Validation rules:
    /// <list type="bullet">
    /// <item><paramref name="oldName"/> must exist in <c>Providers</c> in <c>netclaw.json</c>.</item>
    /// <item><paramref name="newName"/> must be non-empty after trimming.</item>
    /// <item><paramref name="newName"/> must not collide (case-insensitive) with any other
    /// provider key already present in either file.</item>
    /// <item>A case-only change (e.g. <c>my-vllm</c> → <c>My-Vllm</c>) is permitted and rewrites
    /// the key in place.</item>
    /// </list>
    /// </remarks>
    public static RenameResult Rename(NetclawPaths paths, string oldName, string newName)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return RenameResult.Fail("Provider name cannot be empty.");

        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(paths);

        var providers = ConfigFileHelper.GetSectionOrNull(config, "Providers");
        if (providers is null || !providers.ContainsKey(oldName))
            return RenameResult.Fail($"Provider '{oldName}' not found.");

        // Collision check: walk both config and secrets dictionaries. A key
        // that case-insensitive-equals oldName is the entry we're renaming and
        // is not a collision. Any other key that case-insensitive-equals the
        // new name is a collision.
        if (HasCollision(providers, oldName, trimmed))
            return RenameResult.Fail($"A provider named '{trimmed}' already exists.");

        var secretProviders = ConfigFileHelper.GetSectionOrNull(secrets, "Providers");
        if (secretProviders is not null && HasCollision(secretProviders, oldName, trimmed))
            return RenameResult.Fail($"A provider named '{trimmed}' already exists in secrets.");

        var entry = providers[oldName];
        providers.Remove(oldName);
        providers[trimmed] = entry;

        var reassigned = CascadeRenameModelRoles(config, oldName, trimmed);

        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        if (secretProviders is not null && secretProviders.TryGetValue(oldName, out var secretEntry))
        {
            secretProviders.Remove(oldName);
            secretProviders[trimmed] = secretEntry;
            ConfigFileHelper.WriteSecretsFile(paths, secrets);
        }

        return RenameResult.Ok(reassigned);
    }

    private static List<string> CascadeRenameModelRoles(
        Dictionary<string, object> config, string oldName, string newName)
    {
        var reassigned = new List<string>();
        var models = ConfigFileHelper.GetSectionOrNull(config, "Models");
        if (models is null) return reassigned;

        if (models.ContainsKey("Definitions"))
        {
            var definitions = ConfigFileHelper.GetSectionOrNull(models, "Definitions")
                              ?? throw new InvalidOperationException("Models:Definitions must be an object.");
            foreach (var definitionName in definitions.Keys.ToList())
            {
                var definition = ConfigFileHelper.GetSectionOrNull(definitions, definitionName);
                if (definition is null || !definition.TryGetValue("Provider", out var providerValue))
                    continue;
                var current = providerValue is JsonElement element
                    ? element.GetString()
                    : providerValue as string;
                if (!string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase))
                    continue;
                definition["Provider"] = newName;
                reassigned.Add(definitionName);
            }

            return reassigned;
        }

        foreach (var roleName in ModelRoleNames)
        {
            var role = ConfigFileHelper.GetSectionOrNull(models, roleName);
            if (role is null || !role.TryGetValue("Provider", out var providerValue))
                continue;

            // The leaf value may still be a JsonElement (loaded straight from
            // disk) or already a string (if the section was re-materialized
            // earlier in this call). Normalize both.
            var current = providerValue switch
            {
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
                string s => s,
                _ => null
            };

            if (current is not null
                && string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase))
            {
                role["Provider"] = newName;
                reassigned.Add(roleName);
            }
        }

        return reassigned;
    }

    private static bool HasCollision(
        Dictionary<string, object> section, string oldName, string newName)
    {
        foreach (var key in section.Keys)
        {
            if (string.Equals(key, oldName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(key, newName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
