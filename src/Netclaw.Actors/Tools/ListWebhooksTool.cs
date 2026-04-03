using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool("list_webhooks",
    "List configured inbound webhook routes and whether each route currently loads successfully.",
    Grant = "webhook_admin")]
public sealed partial class ListWebhooksTool : NetclawTool<ListWebhooksTool.Params>
{
    private readonly WebhookRouteStore _store;

    public record Params(
        [property: Description("Optional filter: 'active' (default) or 'all'.")]
        string? Filter = null);

    public ListWebhooksTool(WebhookRouteStore store)
    {
        _store = store;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var configured = _store.ListRouteFiles();

        if (configured.Count == 0)
            return Task.FromResult("No webhook routes configured.");

        var sb = new StringBuilder();
        sb.AppendLine($"Webhook routes ({configured.Count}):");
        sb.AppendLine();

        foreach (var (routeName, _, definition) in configured)
        {
            sb.AppendLine($"  Route: {routeName}");
            sb.AppendLine($"  Status: {(definition is null ? "invalid_or_unreadable" : "configured")}");
            if (definition is not null)
            {
                sb.AppendLine($"  Audience: {definition.Audience}");
                sb.AppendLine($"  Verification: {definition.Verification.Kind}");
                sb.AppendLine($"  NotifyPolicy: {definition.NotifyPolicy}");
            }

            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
