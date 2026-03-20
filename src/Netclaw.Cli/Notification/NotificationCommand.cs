using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Notification;

internal static class NotificationCommand
{
    public static Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        HttpMessageHandler? probeHandler = null,
        TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "webhook" => NotificationWebhookCommand.RunAsync(args, paths, probeHandler, writer),
            "help" or "-h" or "--help" => Task.FromResult(WriteHelp(writer)),
            _ => Task.FromResult(WriteHelp(writer, $"Unsupported notification subcommand '{subcommand}'."))
        };
    }

    private static int WriteHelp(TextWriter writer, string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
            writer.WriteLine($"Error: {error}");

        writer.WriteLine("Usage: netclaw notification <group> [options]");
        writer.WriteLine();
        writer.WriteLine("Offline plain-CLI notification management commands.");
        writer.WriteLine();
        writer.WriteLine("Groups:");
        writer.WriteLine("  webhook                  Manage outbound notification webhook targets");
        writer.WriteLine();
        writer.WriteLine("Run `netclaw notification webhook --help` for webhook subcommands.");
        return string.IsNullOrWhiteSpace(error) ? 0 : 2;
    }
}

internal static class NotificationWebhookCommand
{
    private const int SuccessExitCode = 0;
    private const int FailureExitCode = 1;
    private const int UsageExitCode = 2;
    private const int ResponseSnippetLimit = 200;

    public static Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        HttpMessageHandler? probeHandler = null,
        TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 2 ? args[2] : "help";

        return subcommand switch
        {
            "list" => Task.FromResult(RunList(paths, writer)),
            "add" => Task.FromResult(RunAdd(args, paths, writer)),
            "remove" => Task.FromResult(RunRemove(args, paths, writer)),
            "test" => RunTestAsync(args, paths, probeHandler, writer),
            "help" or "-h" or "--help" => Task.FromResult(WriteHelp(writer)),
            _ => Task.FromResult(WriteHelp(writer, $"Unsupported notification webhook subcommand '{subcommand}'."))
        };
    }

    private static int RunList(NetclawPaths paths, TextWriter writer)
    {
        var state = NotificationWebhookConfigStore.Load(paths);
        if (state.Targets.Count == 0)
        {
            writer.WriteLine("No notification webhook targets are configured.");
            writer.WriteLine("Run `netclaw notification webhook add --url <url>` to add one.");
            return SuccessExitCode;
        }

        foreach (var target in state.Targets)
        {
            writer.WriteLine($"[{target.Index}] {NotificationConfigValidator.FormatTargetIdentity(target.Target, target.Index)}");
            writer.WriteLine($"  URL: {NotificationConfigValidator.SanitizeUrlForDisplay(target.Target.Url?.Value)}");
            writer.WriteLine($"  Headers: {FormatHeaderSummary(target.Target.Headers)}");
            writer.WriteLine($"  Header source: {FormatHeaderLocation(target.HeaderLocation)}");
        }

        return SuccessExitCode;
    }

    private static int RunAdd(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (!TryParseAddOptions(args, out var url, out var name, out var headers, out var usageError))
        {
            return WriteHelp(writer, usageError);
        }

        var state = NotificationWebhookConfigStore.Load(paths);
        var updatedTargets = CloneTargets(state.Config.Webhooks);
        updatedTargets.Add(new WebhookTarget
        {
            Url = new SensitiveString(url!),
            Name = name,
            Headers = headers.Count == 0
                ? null
                : headers.ToDictionary(
                    static pair => pair.Key,
                    static pair => new SensitiveString(pair.Value),
                    StringComparer.OrdinalIgnoreCase)
        });

        var updatedConfig = CloneConfig(state.Config, updatedTargets);
        var validation = NotificationConfigValidator.Validate(updatedConfig);
        if (!validation.IsValid)
            return WriteValidationFailure(writer, "add", validation);

        NotificationWebhookConfigStore.WriteAdd(paths, state, updatedTargets);

        var index = updatedTargets.Count - 1;
        var addedTarget = updatedTargets[index];
        writer.WriteLine($"Added notification webhook [{index}] {NotificationConfigValidator.FormatTargetIdentity(addedTarget, index)}");
        writer.WriteLine($"Headers: {FormatHeaderSummary(addedTarget.Headers)}");
        return SuccessExitCode;
    }

    private static int RunRemove(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (!TryParseSelectorArgs(args, out var selector, out var usageError))
            return WriteHelp(writer, usageError);

        var state = NotificationWebhookConfigStore.Load(paths);
        if (!TryResolveTarget(state.Targets, selector, writer, out var resolved))
            return FailureExitCode;

        var updatedTargets = CloneTargets(state.Config.Webhooks);
        updatedTargets.RemoveAt(resolved.Index);

        var updatedConfig = CloneConfig(state.Config, updatedTargets);
        var validation = NotificationConfigValidator.Validate(updatedConfig);
        if (!validation.IsValid)
            return WriteValidationFailure(writer, "remove", validation);

        NotificationWebhookConfigStore.WriteRemove(paths, state, updatedTargets);

        writer.WriteLine($"Removed notification webhook [{resolved.Index}] {NotificationConfigValidator.FormatTargetIdentity(resolved.Target, resolved.Index)}");
        return SuccessExitCode;
    }

    private static async Task<int> RunTestAsync(
        string[] args,
        NetclawPaths paths,
        HttpMessageHandler? probeHandler,
        TextWriter writer)
    {
        if (!TryParseSelectorArgs(args, out var selector, out var usageError))
            return WriteHelp(writer, usageError);

        var state = NotificationWebhookConfigStore.Load(paths);
        if (!TryResolveTarget(state.Targets, selector, writer, out var resolved))
            return FailureExitCode;

        var validation = NotificationConfigValidator.Validate(CloneConfig(state.Config, [CloneTarget(resolved.Target)]));
        if (!validation.IsValid)
            return WriteValidationFailure(writer, "test", validation);

        using var httpClient = probeHandler is null
            ? new HttpClient()
            : new HttpClient(probeHandler, disposeHandler: false);

        httpClient.Timeout = TimeSpan.FromSeconds(state.Config.TimeoutSeconds);

        using var request = new HttpRequestMessage(HttpMethod.Post, resolved.Target.Url?.Value)
        {
            Content = new StringContent(BuildProbeBody(resolved), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        ApplyHeaders(request, resolved.Target.Headers);

        try
        {
            using var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                writer.WriteLine($"Probe succeeded for [{resolved.Index}] {NotificationConfigValidator.FormatTargetIdentity(resolved.Target, resolved.Index)} -> HTTP {(int)response.StatusCode} {response.StatusCode}");
                return SuccessExitCode;
            }

            var snippet = await TryReadResponseSnippetAsync(response, resolved.Target.Headers);
            writer.WriteLine($"Probe failed for [{resolved.Index}] {NotificationConfigValidator.FormatTargetIdentity(resolved.Target, resolved.Index)} -> HTTP {(int)response.StatusCode} {response.StatusCode}");
            if (!string.IsNullOrWhiteSpace(snippet))
                writer.WriteLine($"Response: {snippet}");

            return FailureExitCode;
        }
        catch (TaskCanceledException) when (httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            writer.WriteLine($"Probe timed out for [{resolved.Index}] {NotificationConfigValidator.FormatTargetIdentity(resolved.Target, resolved.Index)} after {state.Config.TimeoutSeconds}s.");
            return FailureExitCode;
        }
        catch (HttpRequestException ex)
        {
            writer.WriteLine($"Probe failed for [{resolved.Index}] {NotificationConfigValidator.FormatTargetIdentity(resolved.Target, resolved.Index)}: {SanitizeDiagnostic(ex.Message, resolved.Target.Headers)}");
            return FailureExitCode;
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, SensitiveString>? headers)
    {
        if (headers is null)
            return;

        foreach (var (key, value) in headers)
        {
            if (request.Headers.TryAddWithoutValidation(key, value.Value))
                continue;

            request.Content?.Headers.TryAddWithoutValidation(key, value.Value);
        }
    }

    private static string BuildProbeBody(LoadedWebhookTarget resolved)
    {
        var body = new JsonObject
        {
            ["type"] = "netclaw.notification_webhook_probe",
            ["target"] = new JsonObject
            {
                ["index"] = resolved.Index,
                ["name"] = resolved.Target.Name,
                ["url"] = NotificationConfigValidator.SanitizeUrlForDisplay(resolved.Target.Url?.Value)
            }
        };

        return body.ToJsonString(ConfigFileHelper.JsonOptions);
    }

    private static async Task<string?> TryReadResponseSnippetAsync(HttpResponseMessage response, IReadOnlyDictionary<string, SensitiveString>? headers)
    {
        if (response.Content is null)
            return null;

        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var normalized = string.Join(' ', body.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > ResponseSnippetLimit)
            normalized = normalized[..ResponseSnippetLimit] + "...";

        return SanitizeDiagnostic(normalized, headers);
    }

    private static string SanitizeDiagnostic(string text, IReadOnlyDictionary<string, SensitiveString>? headers)
    {
        if (headers is null || headers.Count == 0)
            return text;

        var sanitized = text;
        foreach (var value in headers.Values.Select(static value => value.Value).Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            sanitized = sanitized.Replace(value, "<redacted>", StringComparison.Ordinal);

        return sanitized;
    }

    private static bool TryParseAddOptions(
        string[] args,
        out string? url,
        out string? name,
        out Dictionary<string, string> headers,
        out string? usageError)
    {
        url = null;
        name = null;
        usageError = null;
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case "--header" when i + 1 < args.Length:
                    var headerValue = args[++i];
                    var colonIndex = headerValue.IndexOf(':', StringComparison.Ordinal);
                    if (colonIndex <= 0)
                    {
                        usageError = $"Invalid --header value '{headerValue}'. Expected 'Name: Value'.";
                        return false;
                    }

                    headers[headerValue[..colonIndex].Trim()] = headerValue[(colonIndex + 1)..].Trim();
                    break;
                case "--help" or "-h":
                    usageError = null;
                    return false;
                default:
                    usageError = $"Unknown option '{args[i]}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            usageError = "Missing required --url option.";
            return false;
        }

        return true;
    }

    private static bool TryParseSelectorArgs(string[] args, out WebhookSelector selector, out string? usageError)
    {
        selector = default;
        usageError = null;
        int? index = null;
        string? name = null;

        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedIndex) || parsedIndex < 0)
                    {
                        usageError = "--index requires a zero-based non-negative integer.";
                        return false;
                    }

                    index = parsedIndex;
                    break;
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                default:
                    usageError = $"Unknown option '{args[i]}'.";
                    return false;
            }
        }

        if ((index is null && string.IsNullOrWhiteSpace(name)) || (index is not null && !string.IsNullOrWhiteSpace(name)))
        {
            usageError = "Specify exactly one selector: --index <n> or --name <value>.";
            return false;
        }

        selector = new WebhookSelector(index, name);
        return true;
    }

    private static bool TryResolveTarget(
        IReadOnlyList<LoadedWebhookTarget> targets,
        WebhookSelector selector,
        TextWriter writer,
        out LoadedWebhookTarget resolved)
    {
        resolved = default;

        if (targets.Count == 0)
        {
            writer.WriteLine("No notification webhook targets are configured.");
            return false;
        }

        if (selector.Index is int index)
        {
            if (index < 0 || index >= targets.Count)
            {
                writer.WriteLine($"Notification webhook index {index} is out of range. Available indexes: 0 to {targets.Count - 1}.");
                return false;
            }

            resolved = targets[index];
            return true;
        }

        var matches = targets
            .Where(target => string.Equals(target.Target.Name, selector.Name, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
        {
            writer.WriteLine($"No notification webhook named '{selector.Name}' was found.");
            return false;
        }

        if (matches.Length > 1)
        {
            writer.WriteLine($"Notification webhook selector '{selector.Name}' is ambiguous. Rerun the command with --index.");
            foreach (var match in matches)
                writer.WriteLine($"  - [{match.Index}] {NotificationConfigValidator.FormatTargetIdentity(match.Target, match.Index)}");

            return false;
        }

        resolved = matches[0];
        return true;
    }

    private static int WriteValidationFailure(TextWriter writer, string operation, NotificationConfigValidationResult validation)
    {
        writer.WriteLine($"[FAIL] notification webhook {operation}: resulting notification config is invalid.");
        foreach (var issue in validation.Issues)
        {
            writer.WriteLine($"  - {issue.FieldPath}: {issue.Message}");
            writer.WriteLine($"    fix: {issue.Remediation}");
        }

        return FailureExitCode;
    }

    private static string FormatHeaderSummary(IReadOnlyDictionary<string, SensitiveString>? headers)
    {
        return headers is null || headers.Count == 0
            ? "none"
            : NotificationConfigValidator.FormatRedactedHeaders(headers);
    }

    private static string FormatHeaderLocation(WebhookHeaderLocation headerLocation)
    {
        return headerLocation switch
        {
            WebhookHeaderLocation.None => "none",
            WebhookHeaderLocation.BaseConfig => "netclaw.json (legacy)",
            WebhookHeaderLocation.Secrets => "secrets.json",
            WebhookHeaderLocation.Mixed => "netclaw.json + secrets.json",
            _ => "unknown"
        };
    }

    private static NotificationsConfig CloneConfig(NotificationsConfig source, List<WebhookTarget> updatedTargets)
    {
        return new NotificationsConfig
        {
            DeduplicationWindowSeconds = source.DeduplicationWindowSeconds,
            MaxRetries = source.MaxRetries,
            TimeoutSeconds = source.TimeoutSeconds,
            Webhooks = updatedTargets
        };
    }

    private static List<WebhookTarget> CloneTargets(IReadOnlyList<WebhookTarget> targets)
    {
        return targets.Select(CloneTarget).ToList();
    }

    internal static WebhookTarget CloneTarget(WebhookTarget target)
    {
        return new WebhookTarget
        {
            Url = target.Url is null ? null : new SensitiveString(target.Url.Value),
            Name = target.Name,
            Headers = target.Headers is null
                ? null
                : target.Headers.ToDictionary(
                    static pair => pair.Key,
                    static pair => new SensitiveString(pair.Value.Value),
                    StringComparer.OrdinalIgnoreCase)
        };
    }

    private static int WriteHelp(TextWriter writer, string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
            writer.WriteLine($"Error: {error}");

        writer.WriteLine("Usage: netclaw notification webhook <subcommand> [options]");
        writer.WriteLine();
        writer.WriteLine("Plain CLI, offline management for outbound notification webhooks.");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  list                           List configured webhook targets");
        writer.WriteLine("  add --url <url> [--name <n>] [--header \"Name: Value\"]...");
        writer.WriteLine("                                 Add a webhook target");
        writer.WriteLine("  remove (--index <n> | --name <n>)");
        writer.WriteLine("                                 Remove a webhook target");
        writer.WriteLine("  test (--index <n> | --name <n>)");
        writer.WriteLine("                                 Send a single webhook probe");
        return string.IsNullOrWhiteSpace(error) ? SuccessExitCode : UsageExitCode;
    }

    private readonly record struct WebhookSelector(int? Index, string? Name);
}

internal static class NotificationWebhookConfigStore
{
    public static NotificationWebhookConfigState Load(NetclawPaths paths)
    {
        var configRoot = ReadJsonObject(paths.NetclawConfigPath);
        var secretsRoot = ReadJsonObject(paths.SecretsPath);

        if (MoveLegacyWebhookSecrets(configRoot, secretsRoot))
        {
            WriteJsonObject(paths.NetclawConfigPath, configRoot, encryptSecrets: false, paths: paths);
            WriteJsonObject(paths.SecretsPath, secretsRoot, encryptSecrets: true, paths: paths);
        }

        var configNotifications = configRoot["Notifications"] as JsonObject;
        var secretsNotifications = secretsRoot["Notifications"] as JsonObject;

        var baseTargets = ReadTargets(configNotifications?["Webhooks"] as JsonArray, paths, decryptValues: false);
        var secretTargets = ReadTargets(secretsNotifications?["Webhooks"] as JsonArray, paths, decryptValues: true);

        var targetCount = Math.Max(baseTargets.Count, secretTargets.Count);
        var mergedTargets = new List<LoadedWebhookTarget>(targetCount);
        for (var i = 0; i < targetCount; i++)
        {
            var baseTarget = i < baseTargets.Count ? baseTargets[i] : new WebhookTarget();
            var secretTarget = i < secretTargets.Count ? secretTargets[i] : new WebhookTarget();
            var secretHeaders = i < secretTargets.Count ? secretTargets[i].Headers : null;
            var mergedHeaders = MergeHeaders(baseTarget.Headers, secretHeaders);
            mergedTargets.Add(new LoadedWebhookTarget(
                i,
                new WebhookTarget
                {
                    Url = secretTarget.Url is not null
                        ? new SensitiveString(secretTarget.Url.Value)
                        : (baseTarget.Url is not null ? new SensitiveString(baseTarget.Url.Value) : null),
                    Name = baseTarget.Name ?? secretTarget.Name,
                    Headers = mergedHeaders.Count == 0 ? null : mergedHeaders
                },
                GetHeaderLocation(baseTarget.Headers, secretHeaders)));
        }

        var config = new NotificationsConfig
        {
            DeduplicationWindowSeconds = ReadInt(configNotifications, "DeduplicationWindowSeconds", 300),
            MaxRetries = ReadInt(configNotifications, "MaxRetries", 2),
            TimeoutSeconds = ReadInt(configNotifications, "TimeoutSeconds", 10),
            Webhooks = mergedTargets.Select(static target => NotificationWebhookCommand.CloneTarget(target.Target)).ToList()
        };

        return new NotificationWebhookConfigState(configRoot, secretsRoot, config, mergedTargets);
    }

    public static void WriteAdd(NetclawPaths paths, NotificationWebhookConfigState state, IReadOnlyList<WebhookTarget> updatedTargets)
    {
        var rewrittenSecretsRoot = RewriteSecretsRoot(state.SecretsRoot, updatedTargets);
        var rewrittenConfigRoot = RewriteConfigRoot(state.ConfigRoot, updatedTargets);
        WriteJsonObject(paths.SecretsPath, rewrittenSecretsRoot, encryptSecrets: true, paths: paths);
        WriteJsonObject(paths.NetclawConfigPath, rewrittenConfigRoot, encryptSecrets: false, paths: paths);
    }

    public static void WriteRemove(NetclawPaths paths, NotificationWebhookConfigState state, IReadOnlyList<WebhookTarget> updatedTargets)
    {
        var rewrittenConfigRoot = RewriteConfigRoot(state.ConfigRoot, updatedTargets);
        var rewrittenSecretsRoot = RewriteSecretsRoot(state.SecretsRoot, updatedTargets);
        WriteJsonObject(paths.NetclawConfigPath, rewrittenConfigRoot, encryptSecrets: false, paths: paths);
        WriteJsonObject(paths.SecretsPath, rewrittenSecretsRoot, encryptSecrets: true, paths: paths);
    }

    private static JsonObject RewriteConfigRoot(JsonObject originalRoot, IReadOnlyList<WebhookTarget> targets)
    {
        var root = (JsonObject)originalRoot.DeepClone();
        var notifications = GetOrCreateObject(root, "Notifications");
        notifications["Webhooks"] = new JsonArray(targets.Select(ToBaseTargetNode).ToArray<JsonNode>());
        return root;
    }

    private static JsonObject RewriteSecretsRoot(JsonObject originalRoot, IReadOnlyList<WebhookTarget> targets)
    {
        var root = (JsonObject)originalRoot.DeepClone();
        var notifications = GetOrCreateObject(root, "Notifications");

        var secretNodes = targets
            .Select(ToSecretTargetNode)
            .ToArray<JsonNode>();

        if (secretNodes.All(static node => node is JsonObject { Count: 0 }))
        {
            notifications.Remove("Webhooks");
            if (notifications.Count == 0)
                root.Remove("Notifications");

            return root;
        }

        notifications["Webhooks"] = new JsonArray(secretNodes);
        return root;
    }

    private static JsonNode ToBaseTargetNode(WebhookTarget target)
    {
        var node = new JsonObject();

        if (!string.IsNullOrWhiteSpace(target.Name))
            node["Name"] = target.Name;

        return node;
    }

    private static JsonNode ToSecretTargetNode(WebhookTarget target)
    {
        var node = new JsonObject();

        if (target.Url is not null && !string.IsNullOrWhiteSpace(target.Url.Value))
            node["Url"] = target.Url.Value;

        if (target.Headers is null || target.Headers.Count == 0)
            return node;

        var headers = new JsonObject();
        foreach (var (key, value) in target.Headers.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            headers[key] = value.Value;

        node["Headers"] = headers;
        return node;
    }

    private static List<WebhookTarget> ReadTargets(JsonArray? array, NetclawPaths paths, bool decryptValues)
    {
        var targets = new List<WebhookTarget>();
        if (array is null)
            return targets;

        foreach (var node in array)
        {
            if (node is not JsonObject targetObject)
            {
                targets.Add(new WebhookTarget());
                continue;
            }

            targets.Add(new WebhookTarget
            {
                Url = targetObject["Url"]?.GetValue<string>() is { } url ? new SensitiveString(url) : null,
                Name = targetObject["Name"]?.GetValue<string>(),
                Headers = ReadHeaders(targetObject["Headers"] as JsonObject, paths, decryptValues)
            });
        }

        return targets;
    }

    private static Dictionary<string, SensitiveString>? ReadHeaders(JsonObject? headersObject, NetclawPaths paths, bool decryptValues)
    {
        if (headersObject is null || headersObject.Count == 0)
            return null;

        var headers = new Dictionary<string, SensitiveString>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headersObject)
        {
            var raw = value?.GetValue<string>() ?? string.Empty;
            headers[key] = new SensitiveString(decryptValues ? ConfigFileHelper.DecryptIfEncrypted(paths, raw) : raw);
        }

        return headers;
    }

    private static Dictionary<string, SensitiveString> MergeHeaders(
        IReadOnlyDictionary<string, SensitiveString>? baseHeaders,
        IReadOnlyDictionary<string, SensitiveString>? secretHeaders)
    {
        var merged = new Dictionary<string, SensitiveString>(StringComparer.OrdinalIgnoreCase);
        if (baseHeaders is not null)
        {
            foreach (var (key, value) in baseHeaders)
                merged[key] = value;
        }

        if (secretHeaders is not null)
        {
            foreach (var (key, value) in secretHeaders)
                merged[key] = value;
        }

        return merged;
    }

    private static WebhookHeaderLocation GetHeaderLocation(
        IReadOnlyDictionary<string, SensitiveString>? baseHeaders,
        IReadOnlyDictionary<string, SensitiveString>? secretHeaders)
    {
        var hasBaseHeaders = baseHeaders is { Count: > 0 };
        var hasSecretHeaders = secretHeaders is { Count: > 0 };

        return (hasBaseHeaders, hasSecretHeaders) switch
        {
            (false, false) => WebhookHeaderLocation.None,
            (true, false) => WebhookHeaderLocation.BaseConfig,
            (false, true) => WebhookHeaderLocation.Secrets,
            _ => WebhookHeaderLocation.Mixed
        };
    }

    private static int ReadInt(JsonObject? notifications, string key, int defaultValue)
    {
        if (notifications?[key] is JsonValue value && value.TryGetValue<int>(out var parsed))
            return parsed;

        return defaultValue;
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }

    private static JsonObject ReadJsonObject(string path)
    {
        if (!File.Exists(path))
            return new JsonObject { ["configVersion"] = 1 };

        return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject { ["configVersion"] = 1 };
    }

    private static bool MoveLegacyWebhookSecrets(JsonObject configRoot, JsonObject secretsRoot)
    {
        if (configRoot["Notifications"] is not JsonObject configNotifications ||
            configNotifications["Webhooks"] is not JsonArray configWebhooks)
            return false;

        var secretsNotifications = GetOrCreateObject(secretsRoot, "Notifications");
        var secretsWebhooks = secretsNotifications["Webhooks"] as JsonArray ?? new JsonArray();

        while (secretsWebhooks.Count < configWebhooks.Count)
            secretsWebhooks.Add(new JsonObject());

        var movedAny = false;
        for (var i = 0; i < configWebhooks.Count; i++)
        {
            if (configWebhooks[i] is not JsonObject configWebhook)
                continue;

            var legacyUrl = configWebhook["Url"]?.GetValue<string>();
            var legacyHeaders = configWebhook["Headers"] as JsonObject;
            if (legacyUrl is null && legacyHeaders is null)
                continue;

            var secretsWebhook = secretsWebhooks[i] as JsonObject ?? new JsonObject();
            if (legacyUrl is not null)
                secretsWebhook["Url"] = legacyUrl;

            if (legacyHeaders is not null)
            {
                var secretHeaders = secretsWebhook["Headers"] as JsonObject ?? new JsonObject();
                foreach (var (key, value) in legacyHeaders)
                    secretHeaders[key] = value?.GetValue<string>();

                secretsWebhook["Headers"] = secretHeaders;
                configWebhook.Remove("Headers");
            }

            configWebhook.Remove("Url");
            secretsWebhooks[i] = (JsonObject)secretsWebhook.DeepClone();
            movedAny = true;
        }

        if (!movedAny)
            return false;

        secretsNotifications["Webhooks"] = secretsWebhooks;
        return true;
    }

    private static void WriteJsonObject(string path, JsonObject root, bool encryptSecrets, NetclawPaths paths)
    {
        if (encryptSecrets)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(root.ToJsonString()) ?? [];
            ConfigFileHelper.WriteSecretsFile(paths, dict);
            return;
        }

        File.WriteAllText(path, root.ToJsonString(ConfigFileHelper.JsonOptions));
    }
}

internal sealed record NotificationWebhookConfigState(
    JsonObject ConfigRoot,
    JsonObject SecretsRoot,
    NotificationsConfig Config,
    IReadOnlyList<LoadedWebhookTarget> Targets);

internal readonly record struct LoadedWebhookTarget(int Index, WebhookTarget Target, WebhookHeaderLocation HeaderLocation);

internal enum WebhookHeaderLocation
{
    None,
    BaseConfig,
    Secrets,
    Mixed
}
