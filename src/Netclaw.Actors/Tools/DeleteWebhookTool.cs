using System.ComponentModel;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool("delete_webhook",
    "Delete an inbound webhook route by name. Use list_webhooks to discover route names.",
    Grant = "webhook_admin")]
public sealed partial class DeleteWebhookTool : NetclawTool<DeleteWebhookTool.Params>
{
    private readonly WebhookRouteStore _store;

    public record Params(
        [property: Description("Webhook route name to delete (for example 'github-issues').")]
        string RouteName);

    public DeleteWebhookTool(WebhookRouteStore store)
    {
        _store = store;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (!WebhookRouteStore.TryNormalizeRouteName(args.RouteName, out var routeName, out var routeError))
            return Task.FromResult($"Error: {routeError}");

        return Task.FromResult(_store.Delete(routeName)
            ? $"Webhook route '{routeName}' deleted."
            : $"Webhook route '{routeName}' not found.");
    }
}
