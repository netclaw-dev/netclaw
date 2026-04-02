using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public sealed class WebhookRouteValidationService : IHostedService
{
    private readonly WebhooksConfig _config;
    private readonly ILogger<WebhookRouteValidationService> _logger;

    public WebhookRouteValidationService(WebhooksConfig config, ILogger<WebhookRouteValidationService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
            return Task.CompletedTask;

        foreach (var (routeName, route) in _config.Routes)
        {
            if (!route.Enabled)
                continue;

            if (string.IsNullOrWhiteSpace(routeName))
                throw new InvalidOperationException("Webhook route names must not be empty.");

            if (string.IsNullOrWhiteSpace(route.Prompt))
                throw new InvalidOperationException($"Webhook route '{routeName}' is missing a Prompt.");

            if (route.Verification.Secret is null || string.IsNullOrWhiteSpace(route.Verification.Secret.Value))
                throw new InvalidOperationException($"Webhook route '{routeName}' is missing a verification secret.");

            if (route.MaxBodyBytes < 1)
                throw new InvalidOperationException($"Webhook route '{routeName}' must set MaxBodyBytes >= 1.");

            if (route.RateLimitPerMinute < 1)
                throw new InvalidOperationException($"Webhook route '{routeName}' must set RateLimitPerMinute >= 1.");

            if (route.Events.Any(x => string.IsNullOrWhiteSpace(x)))
                throw new InvalidOperationException($"Webhook route '{routeName}' contains a blank event filter.");

            if (route.NotifyPolicy == NotificationPolicy.Required && route.NotificationTarget is null)
            {
                throw new InvalidOperationException(
                    $"Webhook route '{routeName}' requires a NotificationTarget when NotifyPolicy is Required.");
            }

            if (route.NotificationTarget is { Kind: NotificationTargetKind.Slack } target
                && string.IsNullOrWhiteSpace(target.ChannelId))
            {
                throw new InvalidOperationException(
                    $"Webhook route '{routeName}' must set NotificationTarget.ChannelId for Slack targets.");
            }
        }

        _logger.LogInformation("Validated {RouteCount} enabled webhook route(s)", _config.Routes.Count(x => x.Value.Enabled));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
