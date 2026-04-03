using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public sealed class WebhookRouteCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NetclawPaths _paths;
    private readonly WebhooksConfig _config;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookRouteCatalog> _logger;
    private readonly ConcurrentDictionary<string, RegisteredWebhookRoute> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedRouteVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public WebhookRouteCatalog(
        NetclawPaths paths,
        WebhooksConfig config,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        ILogger<WebhookRouteCatalog> logger)
    {
        _paths = paths;
        _config = config;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool TryGetRoute(string routeName, out RegisteredWebhookRoute route)
    {
        route = default!;
        if (!_config.Enabled)
            return false;

        RefreshRoutes(routeName);
        return _routes.TryGetValue(routeName, out route!);
    }

    public IReadOnlyCollection<RegisteredWebhookRoute> Routes
    {
        get
        {
            if (!_config.Enabled)
                return [];

            RefreshAllRoutes();
            return _routes.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void RefreshRoutes(string? routeNameHint)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_paths.WebhooksDirectory);

            if (string.IsNullOrWhiteSpace(routeNameHint))
            {
                RefreshAllRoutes();
                return;
            }

            RefreshSingleRoute(routeNameHint);
        }
    }

    private void RefreshAllRoutes()
    {
        var discoveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_paths.WebhooksDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var routeName = GetRouteName(file);
            if (string.IsNullOrWhiteSpace(routeName))
                continue;

            discoveredNames.Add(routeName);
            RefreshRouteFromFile(routeName, file);
        }

        foreach (var existing in _routes.Keys.ToList())
        {
            if (!discoveredNames.Contains(existing))
                RemoveRoute(existing, reason: "route_file_missing", filePath: GetRouteFilePath(existing), emitAlert: false);
        }
    }

    private void RefreshSingleRoute(string routeName)
    {
        var filePath = GetRouteFilePath(routeName);
        if (!File.Exists(filePath))
        {
            RemoveRoute(routeName, reason: "route_file_missing", filePath: filePath, emitAlert: false);
            return;
        }

        RefreshRouteFromFile(routeName, filePath);
    }

    private void RefreshRouteFromFile(string routeName, string filePath)
    {
        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
            var lastModifiedUtc = new DateTimeOffset(lastWriteUtc, TimeSpan.Zero);

            if (_routes.TryGetValue(routeName, out var existing) && existing.LastModifiedUtc >= lastModifiedUtc)
                return;

            if (_failedRouteVersions.TryGetValue(routeName, out var failedVersion) && failedVersion >= lastModifiedUtc)
                return;

            var route = LoadRoute(filePath, lastWriteUtc);
            if (!route.Config.Enabled)
            {
                _failedRouteVersions.TryRemove(route.Name, out _);
                RemoveRoute(route.Name, reason: "route_disabled", filePath: filePath, emitAlert: false);
                return;
            }

            _routes[route.Name] = route;
            _failedRouteVersions.TryRemove(route.Name, out _);
            _logger.LogInformation("Loaded webhook route {Route} from {File}", route.Name, filePath);
        }
        catch (Exception ex)
        {
            _failedRouteVersions[routeName] = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero);
            RemoveRoute(routeName, reason: ex.Message, filePath: filePath, emitAlert: true);
            _logger.LogWarning(ex, "Webhook route '{Route}' is invalid; route removed until the file is fixed.", routeName);
        }
    }

    private RegisteredWebhookRoute LoadRoute(string filePath, DateTime lastWriteUtc)
    {
        var routeName = GetRouteName(filePath);
        if (string.IsNullOrWhiteSpace(routeName))
            throw new InvalidOperationException("Webhook route filenames must not be empty.");

        var text = File.ReadAllText(filePath);
        var route = JsonSerializer.Deserialize<WebhookRouteConfig>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Webhook route '{routeName}' could not be parsed.");

        ValidateRoute(routeName, route);
        return new RegisteredWebhookRoute(routeName, filePath, new DateTimeOffset(lastWriteUtc, TimeSpan.Zero), route);
    }

    private static void ValidateRoute(string routeName, WebhookRouteConfig route)
    {
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

        if (route.Verification.Kind == WebhookVerifierKind.Hmac
            && string.IsNullOrWhiteSpace(route.Verification.SignatureHeaderName)
            && string.IsNullOrWhiteSpace(route.Verification.SignaturePrefix))
        {
            return;
        }
    }

    private void RemoveRoute(string routeName, string reason, string filePath, bool emitAlert)
    {
        _routes.TryRemove(routeName, out _);
        if (string.Equals(reason, "route_file_missing", StringComparison.Ordinal))
            _failedRouteVersions.TryRemove(routeName, out _);

        if (!emitAlert)
            return;

        _notificationSink.Emit(new OperationalAlert
        {
            AlertId = Guid.NewGuid().ToString("N")[..12],
            Type = "webhook.route.invalid",
            Category = AlertType.WebhookRouteInvalid,
            Summary = $"Webhook route '{routeName}' is unavailable: {reason}",
            Timestamp = _timeProvider.GetUtcNow(),
            Severity = "warning",
            Source = routeName,
            Context = new Dictionary<string, string>
            {
                ["route"] = routeName,
                ["file"] = filePath,
                ["reason"] = reason,
            }
        });
    }

    private string GetRouteFilePath(string routeName)
        => Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json");

    private static string GetRouteName(string filePath)
        => Path.GetFileNameWithoutExtension(filePath);
}
