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

                ValidateRoute(routeName, route);
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

    private static void ValidateRoute(string routeName, WebhookRouteConfig route)
    {
        if (string.IsNullOrWhiteSpace(routeName))
            throw new InvalidOperationException("Webhook route filename must not be empty.");

        if (string.IsNullOrWhiteSpace(route.Prompt))
            throw new InvalidOperationException($"Webhook route '{routeName}' is missing a Prompt.");

        if (route.Verification.Secret is null || string.IsNullOrWhiteSpace(route.Verification.Secret.Value))
            throw new InvalidOperationException($"Webhook route '{routeName}' is missing a verification secret.");

        if (route.MaxBodyBytes < 1)
            throw new InvalidOperationException($"Webhook route '{routeName}' must set MaxBodyBytes >= 1.");

        if (route.RateLimitPerMinute < 1)
            throw new InvalidOperationException($"Webhook route '{routeName}' must set RateLimitPerMinute >= 1.");

        if (route.Events.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Webhook route '{routeName}' contains a blank event filter.");

        if (route.DeliveryRequired
            && route.NotificationTarget is null
            && !string.IsNullOrWhiteSpace(route.NotifyInstructions))
        {
            throw new InvalidOperationException(
                $"Webhook route '{routeName}' must set NotificationTarget when DeliveryRequired is true and NotifyInstructions are provided.");
        }

        if (route.NotificationTarget is { Kind: NotificationTargetKind.Slack } target
            && string.IsNullOrWhiteSpace(target.ChannelId))
        {
            throw new InvalidOperationException(
                $"Webhook route '{routeName}' must set NotificationTarget.ChannelId for Slack targets.");
        }
    }
}
