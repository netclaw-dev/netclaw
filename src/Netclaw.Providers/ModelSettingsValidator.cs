// -----------------------------------------------------------------------
// <copyright file="ModelSettingsValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers;

/// <summary>
/// The one validator of the output limit and the thinking level of a model definition. The CLI and
/// the TUI run <see cref="Check"/> before they write. Startup and doctor run <see cref="CheckDialect"/>,
/// because the model capability is known only after a probe.
/// </summary>
public static class ModelSettingsValidator
{
    private static readonly ThinkingSupport AnyCapability = ThinkingSupport.Controllable(
        ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max);

    /// <summary>
    /// Checks that the provider dialect resolves and can send the configured level. Returns the error
    /// text, or null.
    /// </summary>
    public static string? CheckDialect(
        string providerName, ProviderEntry entry, IProviderDescriptor descriptor, ModelReference model)
        => ResolveDialect(providerName, entry, descriptor, out var dialect)
           ?? CheckLevel(providerName, model.Thinking, ThinkingDialectMap.OfferedLevels(dialect, AnyCapability), dialect);

    /// <summary>
    /// Checks every rule: the configuration rules, the model maximum output, the dialect, the offered
    /// levels, and the Ollama <c>DisableThinking</c> option. <paramref name="keyPath"/> is the key of
    /// the definition, for example <c>Models:Definitions:fast</c>. Returns the error text, or null.
    /// </summary>
    public static string? Check(
        string keyPath,
        string providerName,
        ProviderEntry entry,
        IProviderDescriptor descriptor,
        ModelReference model,
        ThinkingSupport capability,
        int? modelMaxOutputTokens)
    {
        if (ModelSelectionValidator.ValidateModel(keyPath, model) is [var configError, ..])
            return configError;

        if (model.MaxOutputTokens is { } limit && modelMaxOutputTokens is { } max && limit > max)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{keyPath}:MaxOutputTokens ({limit:N0}) is above the maximum output of this model ({max:N0} tokens).");
        }

        if (ResolveDialect(providerName, entry, descriptor, out var dialect) is { } dialectError)
            return dialectError;

        if (CheckLevel(providerName, model.Thinking, ThinkingDialectMap.OfferedLevels(dialect, capability), dialect) is { } levelError)
            return levelError;

        if (model.Thinking is { } level && level != ThinkingLevel.Off
            && string.Equals(entry.Type, "ollama", StringComparison.OrdinalIgnoreCase)
            && entry.GetVendorOptions<OllamaVendorOptions>() is { DisableThinking: true })
        {
            return $"Providers:{providerName}:VendorOptions:DisableThinking is true, so thinking level "
                   + $"'{ThinkingDialectMap.Format(level)}' conflicts with it. Use thinking level off, or set DisableThinking to false.";
        }

        return null;
    }

    private static string? ResolveDialect(
        string providerName, ProviderEntry entry, IProviderDescriptor descriptor, out ThinkingDialect dialect)
    {
        try
        {
            dialect = descriptor.ResolveThinkingDialect(entry);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            dialect = ThinkingDialect.Unresolved;
            return $"Providers:{providerName}:{ex.Message}";
        }
    }

    private static string? CheckLevel(
        string providerName, ThinkingLevel? level, OfferedThinkingLevels offered, ThinkingDialect dialect)
    {
        if (level is not { } value || !offered.Unavailable.TryGetValue(value, out var reason))
            return null;

        var list = offered.Offered.Count == 0 ? "no thinking level" : ThinkingDialectMap.Format(offered.Offered);
        var message = $"Thinking level '{ThinkingDialectMap.Format(value)}' is not offered by provider '{providerName}': {reason}. "
                      + $"Provider '{providerName}' offers {list}.";
        return dialect == ThinkingDialect.Unresolved
            ? message + $" Set Providers:{providerName}:VendorOptions:{ThinkingDialectMap.VendorOptionKey} to {ThinkingDialectMap.ConfigurableDialectList}."
            : message;
    }
}
