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
/// The one validator of the output limit and the thinking level of a model definition. Startup, hot
/// reload, and doctor run <see cref="CheckDialect"/>. The CLI and the TUI run <see cref="Check"/> on
/// each definition that they write, and add the capability check of the field that they change,
/// because the model capability is known only after a probe.
/// </summary>
public static class ModelSettingsValidator
{
    private static readonly ThinkingSupport AnyCapability = ThinkingSupport.Controllable(
        ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max);

    /// <summary>
    /// Checks that the provider dialect resolves and can send the configured level, and that the level
    /// does not conflict with the Ollama <c>DisableThinking</c> option. Returns the error text, or null.
    /// </summary>
    public static string? CheckDialect(
        string providerName, ProviderEntry entry, IProviderDescriptor descriptor, ModelReference model)
    {
        if (ResolveDialect(providerName, entry, descriptor, out var dialect) is { } dialectError)
            return dialectError;
        if (CheckLevel(providerName, model.Thinking, ThinkingDialectMap.OfferedLevels(dialect, AnyCapability), dialect) is { } levelError)
            return levelError;

        // DisableThinking already forces think=false, so another level would send two switches.
        if (model.Thinking is { } level && level != ThinkingLevel.Off
            && string.Equals(entry.Type, "ollama", StringComparison.OrdinalIgnoreCase)
            && entry.GetVendorOptions<OllamaVendorOptions>() is { DisableThinking: true })
        {
            return $"Providers:{providerName}:VendorOptions:DisableThinking is true, so thinking level "
                   + $"'{ThinkingDialectMap.Format(level)}' conflicts with it. Use thinking level off, or set DisableThinking to false.";
        }

        return null;
    }

    /// <summary>
    /// Checks the configuration rules of the definition at <paramref name="keyPath"/>, for example
    /// <c>Models:Definitions:fast</c>, and <see cref="CheckDialect"/>. Returns the error text, or null.
    /// </summary>
    public static string? Check(
        string keyPath, string providerName, ProviderEntry entry, IProviderDescriptor descriptor, ModelReference model)
        => ModelSelectionValidator.ValidateModel(keyPath, model) is [var configError, ..]
            ? configError
            : CheckDialect(providerName, entry, descriptor, model);

    /// <summary>Checks a changed output limit against the model maximum that a probe reported.</summary>
    public static string? CheckModelMax(string keyPath, ModelReference model, int? modelMaxOutputTokens)
        => model.MaxOutputTokens is { } limit && modelMaxOutputTokens is { } max && limit > max
            ? string.Create(CultureInfo.InvariantCulture,
                $"{keyPath}:MaxOutputTokens ({limit:N0}) is above the maximum output of this model ({max:N0} tokens).")
            : null;

    /// <summary>Checks a changed thinking level against the levels that the dialect and the model offer.</summary>
    public static string? CheckOfferedLevel(
        string providerName, ProviderEntry entry, IProviderDescriptor descriptor, ModelReference model, ThinkingSupport capability)
        => ResolveDialect(providerName, entry, descriptor, out var dialect)
           ?? CheckLevel(providerName, model.Thinking, ThinkingDialectMap.OfferedLevels(dialect, capability), dialect);

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
