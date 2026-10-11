// -----------------------------------------------------------------------
// <copyright file="ModelConfigurationValidation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>The providers and models that passed <see cref="ModelConfigurationValidation.Check"/>.</summary>
public sealed record ValidModelConfiguration(
    Dictionary<string, ProviderEntry> Providers,
    ModelSelection Models,
    ProviderRuntimeValidation Validation);

/// <summary>
/// Outcome of <see cref="ModelConfigurationValidation.Check"/>: the error text, or the valid configuration.
/// </summary>
public sealed record ModelConfigurationCheck(string? Error, ValidModelConfiguration? Valid);

/// <summary>
/// The one validation of the Models section that the daemon runs at startup and that the config
/// watcher runs before it restarts the daemon. An invalid section is an operator error: startup
/// fails with <see cref="ModelConfigurationCheck.Error"/> and the watcher keeps the running config.
/// "No main model configured" is not an error. It stays the No-Op chat client outcome.
/// </summary>
public static class ModelConfigurationValidation
{
    /// <summary>Startup entry point: returns the valid configuration, or throws the error as a <see cref="ModelConfigurationException"/>.</summary>
    public static ValidModelConfiguration Require(IConfiguration configuration)
    {
        var check = Check(configuration);
        return check.Valid ?? throw new ModelConfigurationException(check.Error!);
    }

    public static ModelConfigurationCheck Check(IConfiguration configuration)
    {
        Dictionary<string, ProviderEntry> providers;
        try
        {
            providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));
        }
        catch (ModelConfigurationException ex)
        {
            return Failed($"Invalid model configuration: {ex.Message} Fix the Providers section of netclaw.json.");
        }

        if (!ModelConfigurationResolver.TryResolve(configuration, out var resolution, out var error))
            return Failed(error);

        var models = resolution.Selection;
        if (ValidateSelection(configuration, models) is { } selectionError)
            return Failed(selectionError);

        var validation = ProviderRuntimeValidation.Evaluate(
            providers, models, ProviderRuntimeConfiguration.FromConfiguration(configuration));
        if (validation.Status == ProviderRuntimeStatus.Invalid)
        {
            return Failed(
                $"Invalid model configuration: {validation.Reason}. Fix the Providers or Models section of netclaw.json.");
        }

        return new ModelConfigurationCheck(null, new ValidModelConfiguration(providers, models, validation));
    }

    /// <summary>
    /// Checks the values of the role-bound models. Under the current shape the message names the
    /// definition that the role selects, because that is the key the operator edits.
    /// </summary>
    public static string? ValidateSelection(IConfiguration configuration, ModelSelection models)
    {
        var result = new ModelSelectionValidator().Validate(null, models);
        if (!result.Failed)
            return null;

        var failures = result.Failures ?? [];
        var roles = configuration.GetSection("Models").GetSection(nameof(NamedModelConfiguration.Roles));
        return "Invalid model configuration: " + string.Join(" ", failures.Select(failure =>
        {
            foreach (var role in new[] { nameof(ModelSelection.Main), nameof(ModelSelection.Fallback), nameof(ModelSelection.Compaction) })
            {
                var definition = roles[role];
                if (!string.IsNullOrWhiteSpace(definition) && failure.StartsWith($"Models:{role}:", StringComparison.Ordinal))
                    return $"Models:Definitions:{definition}:" + failure[$"Models:{role}:".Length..];
            }

            return failure;
        }));
    }

    private static ModelConfigurationCheck Failed(string error) => new(error, null);
}
