using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public sealed class WebhookRouteCatalog
{
    private readonly Dictionary<string, RegisteredWebhookRoute> _routes;

    public WebhookRouteCatalog(WebhooksConfig config)
    {
        _routes = config.Routes
            .Where(kvp => kvp.Value.Enabled)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => new RegisteredWebhookRoute(kvp.Key, kvp.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetRoute(string routeName, out RegisteredWebhookRoute route)
        => _routes.TryGetValue(routeName, out route!);

    public IReadOnlyCollection<RegisteredWebhookRoute> Routes => _routes.Values.ToList();
}
