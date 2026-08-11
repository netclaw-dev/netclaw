// -----------------------------------------------------------------------
// <copyright file="ListWebhooksTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        var filter = args.Filter?.ToLowerInvariant() ?? "active";
        if (filter is not ("active" or "all"))
        {
            return Task.FromResult(
                $"Error: Parameter 'Filter' value '{args.Filter}' is not supported. "
                + "Valid values: active, all. The tool was NOT executed.");
        }

        var configured = _store.ListRouteFiles();

        if (configured.Count == 0)
            return Task.FromResult("No webhook routes configured.");

        // 'active' keeps unreadable routes visible: they cannot be classified
        // as disabled, and hiding a broken route under the default filter
        // would hide exactly the problem this tool exists to surface.
        var routes = filter == "active"
            ? configured.Where(r => r.Definition is null || r.Definition.Enabled).ToList()
            : [.. configured];

        var sb = new StringBuilder();
        sb.AppendLine($"Webhook routes ({routes.Count} of {configured.Count}, filter: {filter}):");
        sb.AppendLine();

        foreach (var (routeName, _, definition) in routes)
        {
            sb.AppendLine($"  Route: {routeName}");
            sb.AppendLine($"  Status: {(definition is null ? "invalid_or_unreadable" : "configured")}");
            if (definition is not null)
            {
                sb.AppendLine($"  Enabled: {definition.Enabled}");
                sb.AppendLine($"  Audience: {definition.Audience}");
                sb.AppendLine($"  Verification: {definition.Verification.Kind}");
                sb.AppendLine($"  DeliveryRequired: {definition.DeliveryRequired}");
            }

            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
