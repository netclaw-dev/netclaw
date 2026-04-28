// -----------------------------------------------------------------------
// <copyright file="InboundWebhookRoutesDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class InboundWebhookRoutesDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Inbound Webhook Routes";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.WebhooksDirectory);

        var routeFiles = Directory.EnumerateFiles(paths.WebhooksDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (routeFiles.Count == 0)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "No inbound webhook route files configured."));

        var invalidRoutes = new List<string>();
        foreach (var filePath in routeFiles)
        {
            var routeName = Path.GetFileNameWithoutExtension(filePath);
            try
            {
                var route = JsonSerializer.Deserialize<WebhookRouteConfig>(File.ReadAllText(filePath), JsonDefaults.ConfigRead)
                    ?? throw new InvalidOperationException($"Webhook route '{routeName}' could not be parsed.");

                WebhookRouteValidator.ValidateOrThrow(routeName, route);
            }
            catch (Exception ex)
            {
                invalidRoutes.Add($"{Path.GetFileName(filePath)} ({ex.Message})");
            }
        }

        if (invalidRoutes.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                $"Validated {routeFiles.Count} inbound webhook route file(s)."));
        }

        return Task.FromResult(DoctorCheckResult.Error(
            CheckName,
            $"Invalid inbound webhook route files: {string.Join("; ", invalidRoutes)}",
            $"Fix or remove invalid files under {paths.WebhooksDirectory}. Netclaw fails these routes closed at runtime."));
    }
}
