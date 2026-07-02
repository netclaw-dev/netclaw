// -----------------------------------------------------------------------
// <copyright file="ChatClientDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Reports whether the daemon has a real inference provider configured or is
/// running with the No-Op chat client fallback. Mirrors
/// <see cref="ProviderRuntimeValidation"/> so the result lines up with what
/// the host will actually do at startup.
/// </summary>
public sealed class ChatClientDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Chat Client";
    private readonly NetclawPaths _paths;
    private readonly IConfiguration _configuration;
    private readonly ProviderDescriptorRegistry _registry;

    public ChatClientDoctorCheck(
        NetclawPaths paths,
        IConfiguration configuration,
        ProviderDescriptorRegistry registry)
    {
        _paths = paths;
        _configuration = configuration;
        _registry = registry;
    }

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(_paths);
        if (error is not null)
            return Task.FromResult(error);

        if (TryFindNonStringInferenceValue(root, out var invalidPath))
        {
            return Task.FromResult(DoctorCheckResult.Error(
                CheckName,
                $"Invalid inference configuration: {invalidPath} must be a string.",
                "Fix the provider/model values in `netclaw.json`, then rerun `netclaw doctor`."));
        }

        ProviderRuntimeValidation validation;
        Dictionary<string, ProviderEntry> providers;
        ModelSelection models;
        ProviderRuntimeConfiguration runtimeConfiguration;
        try
        {
            providers = ProviderConfigurationLoader.Load(_configuration.GetSection("Providers"));
            models = _configuration.GetSection("Models").Get<ModelSelection>() ?? new ModelSelection();
            runtimeConfiguration = ProviderRuntimeConfiguration.FromJson(root);
            validation = ProviderRuntimeValidation.Evaluate(
                providers,
                models,
                runtimeConfiguration);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                CheckName,
                $"Invalid inference configuration: {ex.Message}",
                "Fix the provider/model values in `netclaw.json` and `secrets.json`, then rerun `netclaw doctor`."));
        }

        return Task.FromResult(validation.Status switch
        {
            ProviderRuntimeStatus.Valid => ValidateProviderReadiness(
                providers,
                models,
                runtimeConfiguration) ?? DoctorCheckResult.Pass(
                CheckName,
                $"Real chat client configured for provider '{models.Main.Provider}' / model '{models.Main.ModelId}'."),

            ProviderRuntimeStatus.NoProviderConfigured => DoctorCheckResult.Warning(
                CheckName,
                $"No-Op chat client will be active ({validation.Reason}). " +
                "The daemon will start, but chat turns return a configuration banner instead of model output.",
                BuildNoProviderRemediation(validation.AvailableProviders)),

            ProviderRuntimeStatus.Invalid => DoctorCheckResult.Error(
                CheckName,
                $"Invalid inference configuration: {validation.Reason}. " +
                "Daemon startup will fail until this is resolved.",
                "Fix the model/provider mismatch in `netclaw.json` and restart the daemon."),

            _ => DoctorCheckResult.Error(
                CheckName,
                $"Unexpected validation status: {validation.Status}",
                "File a bug — this status is not handled by the doctor check."),
        });
    }

    private static string BuildNoProviderRemediation(IReadOnlyList<string> availableProviders)
    {
        return availableProviders.Count == 0
            ? "Run `netclaw init` to configure a provider and main model. "
              + "For manual repair, run `netclaw provider add` first, then `netclaw model set main ...`."
            : "Run `netclaw model` to pick one of the configured providers and a main model, "
              + "or run `netclaw init` and choose start over to re-run setup.";
    }

    private DoctorCheckResult? ValidateProviderReadiness(
        IReadOnlyDictionary<string, ProviderEntry> providers,
        ModelSelection models,
        ProviderRuntimeConfiguration runtimeConfiguration)
    {
        foreach (var (role, model, configured) in EnumerateConfiguredRoles(models, runtimeConfiguration))
        {
            if (!configured)
                continue;

            if (!providers.TryGetValue(model.Provider, out var provider))
                continue;

            if (!_registry.TryGet(provider.Type, out var descriptor))
            {
                return DoctorCheckResult.Error(
                    CheckName,
                    $"Invalid inference configuration: provider '{model.Provider}' referenced by model '{role}' has unknown Type '{provider.Type}'.",
                    $"Set Providers.{model.Provider}.Type to one of: {string.Join(", ", _registry.KnownTypeKeys)}.");
            }

            if (MissingCredentialMessage(model.Provider, provider, descriptor) is { } missingCredential)
            {
                return DoctorCheckResult.Error(
                    CheckName,
                    $"Invalid inference configuration: {missingCredential} Daemon startup will fail until this is resolved.",
                    $"Run `netclaw provider fix {model.Provider}` or update `secrets.json`, then rerun `netclaw doctor`.");
            }
        }

        return null;
    }

    private static IEnumerable<(string Role, ModelReference Model, bool Configured)> EnumerateConfiguredRoles(
        ModelSelection models,
        ProviderRuntimeConfiguration runtimeConfiguration)
    {
        yield return (nameof(models.Main), models.Main, runtimeConfiguration.Main.RoleConfigured);

        if (models.Fallback is not null)
            yield return (nameof(models.Fallback), models.Fallback, runtimeConfiguration.Fallback.RoleConfigured);

        if (models.Compaction is not null)
            yield return (nameof(models.Compaction), models.Compaction, runtimeConfiguration.Compaction.RoleConfigured);
    }

    private static string? MissingCredentialMessage(
        string providerName,
        ProviderEntry provider,
        IProviderDescriptor descriptor)
    {
        var supported = descriptor.Auth.SupportedAuthMethods;
        if (supported.Contains(AuthMethod.None))
            return null;

        var hasApiKey = !provider.ApiKey.IsNullOrEmpty();
        var hasOAuthToken = !provider.OAuthAccessToken.IsNullOrEmpty();
        var supportsApiKey = supported.Contains(AuthMethod.ApiKey);
        var supportsOAuth = supported.Any(IsOAuth);

        if (supportsApiKey && supportsOAuth)
        {
            return provider.AuthMethod switch
            {
                AuthMethod.ApiKey when !hasApiKey =>
                    $"provider '{providerName}' ({descriptor.TypeKey}) requires ApiKey in secrets.json.",
                AuthMethod.OAuthDevice or AuthMethod.OAuthPkce when !hasOAuthToken =>
                    $"provider '{providerName}' ({descriptor.TypeKey}) requires OAuthAccessToken in secrets.json.",
                AuthMethod.None when !hasApiKey && !hasOAuthToken =>
                    $"provider '{providerName}' ({descriptor.TypeKey}) requires ApiKey or OAuthAccessToken in secrets.json.",
                _ => null,
            };
        }

        if (supportsApiKey && !hasApiKey)
            return $"provider '{providerName}' ({descriptor.TypeKey}) requires ApiKey in secrets.json.";

        if (supportsOAuth && !hasOAuthToken)
            return $"provider '{providerName}' ({descriptor.TypeKey}) requires OAuthAccessToken in secrets.json.";

        return null;
    }

    private static bool IsOAuth(AuthMethod method) => method is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce;

    private static bool TryFindNonStringInferenceValue(JsonObject? root, out string path)
    {
        if (root?["Providers"] is JsonObject providers)
        {
            foreach (var (name, value) in providers)
            {
                if (value is JsonObject provider
                    && TryGetProperty(provider, nameof(ProviderEntry.Type), out var type)
                    && IsPresentNonString(type))
                {
                    path = $"Providers.{name}.Type";
                    return true;
                }
            }
        }

        if (root?["Models"] is JsonObject models)
        {
            foreach (var role in new[] { "Main", "Fallback", "Compaction" })
            {
                if (models[role] is not JsonObject model)
                    continue;

                if (TryGetProperty(model, nameof(ModelReference.Provider), out var provider)
                    && IsPresentNonString(provider))
                {
                    path = $"Models.{role}.Provider";
                    return true;
                }

                if (TryGetProperty(model, nameof(ModelReference.ModelId), out var modelId)
                    && IsPresentNonString(modelId))
                {
                    path = $"Models.{role}.ModelId";
                    return true;
                }
            }
        }

        path = string.Empty;
        return false;
    }

    private static bool TryGetProperty(JsonObject obj, string propertyName, out JsonNode? value)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsPresentNonString(JsonNode? node)
    {
        if (node is null)
            return false;

        try
        {
            _ = node.GetValue<string>();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
