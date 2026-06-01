// -----------------------------------------------------------------------
// <copyright file="InboundWebhookRoutesDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class InboundWebhookRoutesDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Inbound Webhook Routes";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.WebhooksDirectory);
        var inboundWebhooksEnabled = IsInboundWebhooksEnabled();

        var routeFiles = Directory.EnumerateFiles(paths.WebhooksDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (routeFiles.Count == 0)
        {
            if (inboundWebhooksEnabled)
            {
                return Task.FromResult(DoctorCheckResult.Error(
                    CheckName,
                    "Inbound webhooks are enabled but no route files are configured.",
                    $"Create at least one valid route with `netclaw webhooks set` or disable Webhooks.Enabled. Routes live under {paths.WebhooksDirectory}."));
            }

            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "No inbound webhook route files configured."));
        }

        var invalidRoutes = new List<string>();
        var enabledRouteCount = 0;
        foreach (var filePath in routeFiles)
        {
            var routeName = Path.GetFileNameWithoutExtension(filePath);
            try
            {
                var route = JsonSerializer.Deserialize<WebhookRouteConfig>(File.ReadAllText(filePath), JsonDefaults.ConfigRead)
                    ?? throw new InvalidOperationException($"Webhook route '{routeName}' could not be parsed.");

                WebhookRouteValidator.ValidateOrThrow(routeName, route);
                if (route.Enabled)
                    enabledRouteCount++;
            }
            catch (Exception ex)
            {
                invalidRoutes.Add($"{Path.GetFileName(filePath)} ({ex.Message})");
            }
        }

        if (invalidRoutes.Count == 0)
        {
            if (inboundWebhooksEnabled && enabledRouteCount == 0)
            {
                return Task.FromResult(DoctorCheckResult.Error(
                    CheckName,
                    "Inbound webhooks are enabled but no valid enabled route files are configured.",
                    "Enable or create at least one valid route with `netclaw webhooks set`, or disable Webhooks.Enabled."));
            }

            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                $"Validated {routeFiles.Count} inbound webhook route file(s)."));
        }

        return Task.FromResult(DoctorCheckResult.Error(
            CheckName,
            $"Invalid inbound webhook route files: {string.Join("; ", invalidRoutes)}",
            $"Fix or remove invalid files under {paths.WebhooksDirectory}. Netclaw fails these routes closed at runtime."));
    }

    private bool IsInboundWebhooksEnabled()
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        return ConfigFileHelper.TryGetPathValue(config, "Webhooks.Enabled", out var enabled)
            && enabled is bool enabledFlag
            && enabledFlag;
    }
}
