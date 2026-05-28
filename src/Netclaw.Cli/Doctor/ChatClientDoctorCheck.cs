// -----------------------------------------------------------------------
// <copyright file="ChatClientDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;

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

    public ChatClientDoctorCheck(NetclawPaths paths)
    {
        _paths = paths;
    }

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(_paths);
        if (error is not null)
            return Task.FromResult(error);

        // No config file at all → treated the same as "no provider configured".
        var providers = ReadProviders(root);
        var models = ReadModels(root);
        var validation = ProviderRuntimeValidation.Evaluate(providers, models);

        return Task.FromResult(validation.Status switch
        {
            ProviderRuntimeStatus.Valid => DoctorCheckResult.Pass(
                CheckName,
                $"Real chat client configured for provider '{models.Main.Provider}' / model '{models.Main.ModelId}'."),

            ProviderRuntimeStatus.NoProviderConfigured => DoctorCheckResult.Warning(
                CheckName,
                $"No-Op chat client will be active ({validation.Reason}). " +
                "The daemon will start, but chat turns return a configuration banner instead of model output.",
                "Run `netclaw model` to pick a provider/model interactively, or edit `netclaw.json` and restart the daemon."),

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

    private static Dictionary<string, ProviderEntry> ReadProviders(JsonObject? root)
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase);
        if (root?["Providers"] is not JsonObject providersObj)
            return providers;

        foreach (var (name, value) in providersObj)
        {
            // We only need to know which provider keys exist for the validation
            // outcome — credentials and types are checked elsewhere.
            providers[name] = new ProviderEntry
            {
                Type = (value as JsonObject)?["Type"]?.GetValue<string>() ?? "",
            };
        }

        return providers;
    }

    private static ModelSelection ReadModels(JsonObject? root)
    {
        var models = new ModelSelection();
        if (root?["Models"]?["Main"] is not JsonObject main)
            return models;

        models.Main = new ModelReference
        {
            Provider = main["Provider"]?.GetValue<string>() ?? "",
            ModelId = main["ModelId"]?.GetValue<string>() ?? "",
        };
        return models;
    }
}
