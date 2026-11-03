// -----------------------------------------------------------------------
// <copyright file="NotificationWebhookSecretsDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Warns when netclaw.json holds a notification webhook URL or header value. A webhook URL can be a
/// credential (a Slack incoming-webhook URL is one), so Netclaw keeps both in secrets.json.
/// <c>netclaw doctor --fix</c> moves them, after a backup, and converts the old list shape to the keyed shape.
/// The message names the webhook and never repeats a value.
/// </summary>
public sealed class NotificationWebhookSecretsDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Notification Webhook Secrets";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Skipped (config missing or invalid)."));

        var exposed = NotificationWebhookSecretsMigration.Find(root!);
        if (exposed.Count == 0)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "netclaw.json holds no notification webhook URL or header value."));

        var withValues = exposed.Where(static webhook => webhook.Url is not null || webhook.Headers.Count > 0)
            .Select(static webhook => webhook.Label).ToList();
        var listOnly = exposed.Where(static webhook => webhook.Url is null && webhook.Headers.Count == 0)
            .Select(static webhook => webhook.Label).ToList();

        var parts = new List<string>();
        if (withValues.Count > 0)
            parts.Add($"netclaw.json holds a URL or header value for notification webhook(s): {string.Join(", ", withValues)}. A webhook URL can be a credential.");
        if (listOnly.Count > 0)
            parts.Add($"netclaw.json uses the old list shape for notification webhook(s): {string.Join(", ", listOnly)}.");

        return Task.FromResult(DoctorCheckResult.Warning(
            CheckName,
            string.Join(' ', parts),
            "Run `netclaw doctor --fix` to convert the list to named entries and move the values to secrets.json after a backup."));
    }
}

/// <summary>
/// Reads and rewrites <c>Notifications.Webhooks</c> in a parsed netclaw.json. It handles the old list
/// shape and the keyed shape, because the CLI reads raw JSON and an old file can hold either.
/// </summary>
internal static class NotificationWebhookSecretsMigration
{
    private const string NotificationsKey = "Notifications";
    private const string WebhooksKey = "Webhooks";
    private const string UrlKey = "Url";
    private const string HeadersKey = "Headers";
    private const string NameKey = "Name";

    /// <summary>
    /// The secret values of one webhook. <paramref name="Key"/> is the key in the keyed shape.
    /// <paramref name="IndexKey"/> is the list position ("0", "1", ...) that the binder used as the key
    /// of a list item, or null for a keyed entry. A list item can have no value in netclaw.json.
    /// </summary>
    internal sealed record WebhookSecrets(
        string Key, string Label, string? Url, IReadOnlyDictionary<string, string> Headers, string? IndexKey = null);

    /// <summary>The webhook entries of either shape, in file order.</summary>
    internal static IEnumerable<JsonObject> Entries(JsonObject config)
    {
        if (FindKey(config, NotificationsKey) is not { } notificationsKey
            || config[notificationsKey] is not JsonObject notifications
            || FindKey(notifications, WebhooksKey) is not { } webhooksKey)
        {
            return [];
        }

        return notifications[webhooksKey] switch
        {
            JsonArray list => list.OfType<JsonObject>(),
            JsonObject map => map.Select(static property => property.Value).OfType<JsonObject>(),
            _ => []
        };
    }

    /// <summary>
    /// Finds every list item, and every keyed entry whose URL or header values sit in netclaw.json.
    /// Does not change the config.
    /// </summary>
    internal static IReadOnlyList<WebhookSecrets> Find(JsonObject config)
        => Extract(config, mutate: false);

    /// <summary>
    /// Rewrites <c>Notifications.Webhooks</c> in <paramref name="config"/> to the keyed shape without URLs or
    /// header values, and returns the values that left the file. Returns an empty list and changes nothing
    /// when the file has neither a list nor a value.
    /// </summary>
    internal static IReadOnlyList<WebhookSecrets> ConvertToKeyed(JsonObject config)
    {
        if (Find(config).Count == 0)
            return [];

        return Extract(config, mutate: true);
    }

    /// <summary>
    /// Copies the values into secrets.json in one locked update and encrypts them. A secrets.json entry
    /// under a list position ("0") moves to the name key, so no URL stays under a key that no webhook has.
    /// The entry under the position wins over an entry under the name key, because the daemon used the position
    /// entry for that item. The replaced value stays in the secrets.json backup. A value that secrets.json
    /// already holds wins over netclaw.json, because secrets.json overrides netclaw.json at bind time.
    /// </summary>
    internal static void MoveToSecrets(
        NetclawPaths paths,
        IReadOnlyList<WebhookSecrets> secrets,
        Configuration.Secrets.ISecretsProtector protector)
    {
        if (secrets.Count == 0)
            return;

        SecretsFileWriter.Update<bool>(paths.SecretsPath, (root, _) =>
        {
            var before = root.ToJsonString();
            var webhooks = FindKey(root, NotificationsKey) is { } notificationsKey
                           && root[notificationsKey] is JsonObject notifications
                           && FindKey(notifications, WebhooksKey) is { } webhooksKey
                           && notifications[webhooksKey] is JsonObject existingWebhooks
                ? existingWebhooks
                : null;

            // Detach every position entry first, so a name that equals another item's position cannot take it.
            var detached = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            if (webhooks is not null)
            {
                foreach (var webhook in secrets)
                {
                    if (webhook.IndexKey is { } position
                        && !position.Equals(webhook.Key, StringComparison.OrdinalIgnoreCase)
                        && FindKey(webhooks, position) is { } positionKey
                        && webhooks[positionKey] is JsonObject entry)
                    {
                        webhooks.Remove(positionKey);
                        detached[webhook.Key] = entry;
                    }
                }
            }

            foreach (var webhook in secrets)
            {
                detached.TryGetValue(webhook.Key, out var moved);
                if (moved is null && webhook.Url is null && webhook.Headers.Count == 0)
                    continue;

                webhooks ??= GetOrAddObject(GetOrAddObject(root, NotificationsKey), WebhooksKey);
                var target = GetOrAddObject(webhooks, webhook.Key);
                if (moved is not null)
                    Merge(target, moved);

                if (webhook.Url is not null && FindKey(target, UrlKey) is null)
                    target[UrlKey] = webhook.Url;

                if (webhook.Headers.Count == 0)
                    continue;

                var headers = GetOrAddObject(target, HeadersKey);
                foreach (var (name, value) in webhook.Headers)
                {
                    if (FindKey(headers, name) is null)
                        headers[name] = value;
                }
            }

            var changed = !string.Equals(before, root.ToJsonString(), StringComparison.Ordinal);
            return (changed ? root : null, changed);
        }, protector);
    }

    // Source values replace target values of the same name.
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source.ToArray())
        {
            if (name.Equals(HeadersKey, StringComparison.OrdinalIgnoreCase) && value is JsonObject sourceHeaders)
            {
                var headers = GetOrAddObject(target, HeadersKey);
                foreach (var (header, headerValue) in sourceHeaders.ToArray())
                    Replace(headers, header, headerValue);
            }
            else
            {
                Replace(target, name, value);
            }
        }
    }

    private static void Replace(JsonObject target, string name, JsonNode? value)
    {
        if (FindKey(target, name) is { } existing)
            target.Remove(existing);

        target[name] = value?.DeepClone();
    }

    /// <summary>
    /// Throws when secrets.json holds <c>Notifications.Webhooks</c> as a JSON array. The fix writes the keyed shape
    /// there and would have to drop the array. An operator must decide what that array means.
    /// </summary>
    internal static void RequireKeyedSecrets(NetclawPaths paths)
    {
        if (!File.Exists(paths.SecretsPath))
            return;

        // Plain parse: the shape does not depend on decryption, and an unreadable file fails later with its own error.
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(paths.SecretsPath), documentOptions: JsonDefaults.ConfigDocument) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (root is not null
            && FindKey(root, NotificationsKey) is { } notificationsKey
            && root[notificationsKey] is JsonObject notifications
            && FindKey(notifications, WebhooksKey) is { } webhooksKey
            && notifications[webhooksKey] is JsonArray)
        {
            throw new InvalidOperationException(
                $"{paths.SecretsPath} holds Notifications:Webhooks as a list. Netclaw keeps the webhooks there as an object keyed by name. "
                + "Convert or remove that list in secrets.json, then run `netclaw doctor --fix` again. No file changed.");
        }
    }

    private static List<WebhookSecrets> Extract(JsonObject config, bool mutate)
    {
        var found = new List<WebhookSecrets>();
        if (FindKey(config, NotificationsKey) is not { } notificationsKey
            || config[notificationsKey] is not JsonObject notifications
            || FindKey(notifications, WebhooksKey) is not { } webhooksKey)
        {
            return found;
        }

        var keyed = new JsonObject();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (notifications[webhooksKey])
        {
            case JsonArray list:
                for (var index = 0; index < list.Count; index++)
                {
                    if (list[index] is not JsonObject item)
                        continue;

                    var key = UniqueKey(StringProperty(item, NameKey), $"webhook-{index}", usedKeys);
                    Take(item, key, $"{key} (list item {index})", index.ToString(CultureInfo.InvariantCulture), found, keyed);
                }

                break;
            case JsonObject map:
                foreach (var (key, node) in map.ToArray())
                {
                    usedKeys.Add(key);
                    if (node is JsonObject item)
                        Take(item, key, key, null, found, keyed);
                    else
                        keyed[key] = node?.DeepClone();
                }

                break;
        }

        if (mutate)
            notifications[webhooksKey] = keyed;

        return found;
    }

    // Copies the entry into keyed without Name, Url and Headers and records the values that it leaves out.
    // The check discards keyed, so one code path serves the check and the fix.
    private static void Take(
        JsonObject item, string key, string label, string? indexKey, List<WebhookSecrets> found, JsonObject keyed)
    {
        var url = StringProperty(item, UrlKey);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (FindKey(item, HeadersKey) is { } headersKey && item[headersKey] is JsonObject headerNodes)
        {
            foreach (var (name, value) in headerNodes.ToArray())
            {
                if (value is JsonValue headerValue && headerValue.TryGetValue<string>(out var text) && text.Length > 0)
                    headers[name] = text;
            }
        }

        var entry = new JsonObject();
        foreach (var (name, value) in item.ToArray())
        {
            if (name.Equals(NameKey, StringComparison.OrdinalIgnoreCase)
                || name.Equals(UrlKey, StringComparison.OrdinalIgnoreCase)
                || name.Equals(HeadersKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entry[name] = value?.DeepClone();
        }

        keyed[key] = entry;

        // A list item is a finding by its shape alone: its URL can sit in secrets.json under its position.
        if (indexKey is not null || url is not null || headers.Count > 0)
            found.Add(new WebhookSecrets(key, label, url, headers, indexKey));
    }

    private static string? StringProperty(JsonObject obj, string name)
        => FindKey(obj, name) is { } key
           && obj[key] is JsonValue value
           && value.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    // The key joins a netclaw.json entry to a secrets.json entry. The configuration path and the secrets
    // path both split on '.' and ':', so a name keeps neither. A repeated name gets a number.
    private static string UniqueKey(string? name, string fallback, HashSet<string> used)
    {
        var baseKey = string.IsNullOrWhiteSpace(name)
            ? fallback
            : name.Trim().Replace('.', '-').Replace(':', '-');

        var key = baseKey;
        for (var number = 2; !used.Add(key); number++)
            key = $"{baseKey}-{number}";

        return key;
    }

    private static JsonObject GetOrAddObject(JsonObject parent, string name)
    {
        if (FindKey(parent, name) is { } key && parent[key] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    // The configuration binder reads keys without case, so a lookup does the same.
    private static string? FindKey(JsonObject parent, string name)
        => parent.Select(static property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}
