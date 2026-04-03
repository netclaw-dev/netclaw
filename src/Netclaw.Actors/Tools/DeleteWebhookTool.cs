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
        if (string.IsNullOrWhiteSpace(args.RouteName))
            return Task.FromResult("Error: 'routeName' is required.");

        return Task.FromResult(_store.Delete(NormalizeRouteName(args.RouteName))
            ? $"Webhook route '{NormalizeRouteName(args.RouteName)}' deleted."
            : $"Webhook route '{NormalizeRouteName(args.RouteName)}' not found.");
    }

    private static string NormalizeRouteName(string value)
        => value.Trim().ToLowerInvariant();
}
