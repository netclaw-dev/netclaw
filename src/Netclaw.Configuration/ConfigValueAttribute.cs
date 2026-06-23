// -----------------------------------------------------------------------
// <copyright file="ConfigValueAttribute.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;

namespace Netclaw.Configuration;

/// <summary>
/// Declares the logical configuration key and persisted home for a runtime config property.
/// These annotations are passive metadata for editoring and persistence helpers only; they do
/// not replace Netclaw's existing runtime IConfiguration overlay behavior.
/// </summary>
public enum ConfigPersistStore
{
    NetclawJson,
    SecretsJson,
    McpOAuthTokens,
}

/// <summary>
/// Passive metadata describing where a runtime config value is persisted.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ConfigValueAttribute : Attribute
{
    public required string Key { get; init; }

    public ConfigPersistStore PersistTo { get; init; } = ConfigPersistStore.NetclawJson;
}

/// <summary>
/// Reflected metadata for a runtime config property annotated with <see cref="ConfigValueAttribute"/>.
/// </summary>
public sealed record ConfigValueMetadata(
    string PropertyName,
    string Key,
    ConfigPersistStore PersistTo,
    Type ValueType,
    bool IsSecret);

/// <summary>
/// Reflection helper for passive config metadata.
/// </summary>
public static class ConfigValueMetadataProvider
{
    public static ConfigValueMetadata Get<TConfig>(string propertyName)
        => Get(typeof(TConfig), propertyName);

    public static ConfigValueMetadata Get(Type configType, string propertyName)
    {
        var property = configType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"Property '{propertyName}' was not found on config type '{configType.FullName}'.");

        var attribute = property.GetCustomAttribute<ConfigValueAttribute>()
            ?? throw new InvalidOperationException(
                $"Property '{configType.FullName}.{propertyName}' is missing [{nameof(ConfigValueAttribute)}].");

        var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return new ConfigValueMetadata(
            PropertyName: property.Name,
            Key: attribute.Key,
            PersistTo: attribute.PersistTo,
            ValueType: valueType,
            IsSecret: valueType == typeof(SensitiveString));
    }
}
