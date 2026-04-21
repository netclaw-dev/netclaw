using System.Text.Json;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Webhooks;

/// <summary>
/// Handles <c>netclaw webhooks &lt;subcommand&gt;</c> CLI subcommands.
/// All commands are offline — no daemon required.
/// </summary>
internal static class WebhooksCommand
{
    public static Task<int> RunAsync(string[] args, NetclawPaths paths)
    {
        var subcommand = args.Length > 1 ? args[1] : "list";

        if (subcommand is "help" or "-h" or "--help")
            return Task.FromResult(WriteHelp());

        var store = new WebhookRouteStore(paths);

        return Task.FromResult(subcommand switch
        {
            "list" => RunList(args, store, paths),
            "show" => RunShow(args, store, paths),
            "set" => RunSet(args, store, paths),
            "delete" => RunDelete(args, store),
            "validate" => RunValidate(args, paths),
            _ => WriteHelp()
        });
    }

    // ── list ──

    private static int RunList(string[] args, WebhookRouteStore store, NetclawPaths paths)
    {
        var json = HasFlag(args, "--json");
        var all = HasFlag(args, "--all");

        var routes = store.ListRouteFiles()
            .Select(route =>
            {
                var validationErrors = route.Definition is null
                    ? ["Webhook route file could not be parsed."]
                    : WebhookRouteValidator.Validate(route.RouteName, route.Definition);
                var isValid = validationErrors.Count == 0;

                return new
                {
                    route.RouteName,
                    route.Definition,
                    IsValid = isValid,
                    Status = !isValid ? "invalid" : (route.Definition!.Enabled ? "enabled" : "disabled"),
                };
            })
            .ToList();

        if (routes.Count == 0)
        {
            if (json)
            {
                Console.WriteLine("[]");
            }
            else
            {
                Console.WriteLine("No webhook routes configured.");
                Console.WriteLine($"Routes are stored in: {paths.WebhooksDirectory}");
            }
            return 0;
        }

        if (json)
        {
            var items = routes
                .Where(r => all || r.Status != "disabled")
                .Select(r => new
                {
                    name = r.RouteName,
                    status = r.Status,
                    audience = r.IsValid ? r.Definition!.Audience.ToString().ToLowerInvariant() : "unknown",
                    verification = r.IsValid ? r.Definition!.Verification.Kind.ToString().ToLowerInvariant() : "unknown",
                    deliveryRequired = r.IsValid && r.Definition!.DeliveryRequired
                })
                .ToList();
            Console.WriteLine(JsonSerializer.Serialize(items, JsonDefaults.IndentedCamelCase));
            return 0;
        }

        const int colName = 24;
        const int colStatus = 10;
        const int colAudience = 10;
        const int colVerification = 14;

        Console.WriteLine(
            $"{"NAME",-colName}  {"STATUS",-colStatus}  {"AUDIENCE",-colAudience}  {"VERIFICATION",-colVerification}  DELIVERY");
        Console.WriteLine(new string('-', colName + colStatus + colAudience + colVerification + 18));

        foreach (var route in routes)
        {
            var status = route.Status;

            if (!all && status == "disabled")
                continue;

            var audience = route.IsValid ? route.Definition!.Audience.ToString().ToLowerInvariant() : "-";
            var verification = route.IsValid ? route.Definition!.Verification.Kind.ToString().ToLowerInvariant() : "-";
            var delivery = route.IsValid && route.Definition!.DeliveryRequired ? "required" : "optional";

            Console.WriteLine(
                $"{route.RouteName,-colName}  {status,-colStatus}  {audience,-colAudience}  {verification,-colVerification}  {delivery}");
        }

        return 0;
    }

    // ── show ──

    private static int RunShow(string[] args, WebhookRouteStore store, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw webhooks show <route> [--json] [--show-secret]");
            return 1;
        }

        if (!TryParseRouteName(args[2], out var routeName))
            return 1;

        var json = HasFlag(args, "--json");
        var showSecret = HasFlag(args, "--show-secret");

        if (!store.TryGet(routeName, out var match))
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' not found.");
            Console.Error.WriteLine($"       Routes are stored in: {paths.WebhooksDirectory}");
            return 1;
        }

        if (match.Definition is null)
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' could not be parsed.");
            Console.Error.WriteLine($"       Run 'netclaw webhooks validate {routeName}' for details.");
            return 1;
        }

        var route = match.Definition;
        var validationErrors = WebhookRouteValidator.Validate(routeName, route);
        if (validationErrors.Count > 0)
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' has validation errors:");
            foreach (var error in validationErrors)
                Console.Error.WriteLine($"  - {error}");

            Console.Error.WriteLine($"       Run 'netclaw webhooks validate {routeName}' for details.");
            return 1;
        }

        if (json)
        {
            var output = new
            {
                name = routeName,
                file = match.FilePath,
                endpoint = $"/api/webhooks/{routeName}",
                enabled = route.Enabled,
                verification = new
                {
                    kind = route.Verification.Kind.ToString().ToLowerInvariant(),
                    secret = showSecret ? route.Verification.Secret?.Value : "********",
                    hmacAlgorithm = route.Verification.HmacAlgorithm.ToString().ToLowerInvariant(),
                    signatureHeader = route.Verification.SignatureHeaderName,
                    signaturePrefix = route.Verification.SignaturePrefix,
                    secretHeader = route.Verification.SecretHeaderName,
                    eventHeader = route.Verification.EventHeaderName,
                    deliveryIdHeader = route.Verification.DeliveryIdHeaderName
                },
                audience = route.Audience.ToString().ToLowerInvariant(),
                events = route.Events,
                prompt = route.Prompt,
                notifyInstructions = route.NotifyInstructions,
                deliveryRequired = route.DeliveryRequired,
                notificationTarget = route.NotificationTarget is null ? null : new
                {
                    kind = route.NotificationTarget.Kind.ToString().ToLowerInvariant(),
                    channelId = route.NotificationTarget.ChannelId
                },
                maxBodyBytes = route.MaxBodyBytes,
                rateLimitPerMinute = route.RateLimitPerMinute
            };
            Console.WriteLine(JsonSerializer.Serialize(output, JsonDefaults.IndentedCamelCase));
            return 0;
        }

        Console.WriteLine($"Route:              {routeName}");
        Console.WriteLine($"File:               {match.FilePath}");
        Console.WriteLine($"Status:             {(route.Enabled ? "enabled" : "disabled")}");
        Console.WriteLine($"Endpoint:           /api/webhooks/{routeName}");
        Console.WriteLine();
        Console.WriteLine("Verification:");
        Console.WriteLine($"  Kind:             {route.Verification.Kind.ToString().ToLowerInvariant()}");
        Console.WriteLine($"  Secret:           {(showSecret ? route.Verification.Secret?.Value ?? "(not set)" : "********** (use --show-secret to reveal)")}");
        if (route.Verification.Kind == WebhookVerifierKind.Hmac)
        {
            Console.WriteLine($"  Algorithm:        {route.Verification.HmacAlgorithm.ToString().ToLowerInvariant()}");
            Console.WriteLine($"  Signature Header: {route.Verification.SignatureHeaderName ?? "(default)"}");
            Console.WriteLine($"  Signature Prefix: {route.Verification.SignaturePrefix ?? "(none)"}");
        }
        else
        {
            Console.WriteLine($"  Secret Header:    {route.Verification.SecretHeaderName ?? "(default)"}");
        }
        Console.WriteLine($"  Event Header:     {route.Verification.EventHeaderName ?? "(default)"}");
        Console.WriteLine($"  Delivery Header:  {route.Verification.DeliveryIdHeaderName ?? "(default)"}");
        Console.WriteLine();
        Console.WriteLine("Behavior:");
        Console.WriteLine($"  Audience:         {route.Audience.ToString().ToLowerInvariant()}");
        Console.WriteLine($"  Events:           {(route.Events.Count > 0 ? string.Join(", ", route.Events) : "(all)")}");
        Console.WriteLine($"  Rate Limit:       {route.RateLimitPerMinute} req/min");
        Console.WriteLine($"  Max Body:         {route.MaxBodyBytes} bytes ({route.MaxBodyBytes / 1024 / 1024} MB)");
        Console.WriteLine();
        Console.WriteLine("Notification:");
        Console.WriteLine($"  Delivery:         {(route.DeliveryRequired ? "required" : "optional")}");
        if (route.NotificationTarget is not null)
        {
            Console.WriteLine($"  Target:           {route.NotificationTarget.Kind.ToString().ToLowerInvariant()} (channel: {route.NotificationTarget.ChannelId ?? "(not set)"})");
        }
        else
        {
            Console.WriteLine("  Target:           (not configured)");
        }
        if (!string.IsNullOrWhiteSpace(route.NotifyInstructions))
        {
            var truncated = route.NotifyInstructions.Length > 60
                ? route.NotifyInstructions[..60] + "..."
                : route.NotifyInstructions;
            Console.WriteLine($"  Instructions:     {truncated.Replace("\n", " ")}");
        }
        Console.WriteLine();
        Console.WriteLine("Prompt:");
        if (string.IsNullOrWhiteSpace(route.Prompt))
        {
            Console.WriteLine("  (empty)");
        }
        else if (route.Prompt.Length > 200)
        {
            Console.WriteLine($"  {route.Prompt[..200].Replace("\n", " ")}...");
            Console.WriteLine("  (truncated; run with --json for full prompt)");
        }
        else
        {
            foreach (var line in route.Prompt.Split('\n'))
            {
                Console.WriteLine($"  {line}");
            }
        }

        return 0;
    }

    // ── set ──

    private static int RunSet(string[] args, WebhookRouteStore store, NetclawPaths paths)
    {
        if (args.Length < 3 || HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            WriteSetHelp();
            return args.Length < 3 ? 1 : 0;
        }

        if (!TryParseRouteName(args[2], out var routeName))
            return 1;

        var dryRun = HasFlag(args, "--dry-run");
        var createOnly = HasFlag(args, "--create-only");
        var updateOnly = HasFlag(args, "--update-only");

        if (createOnly && updateOnly)
        {
            Console.Error.WriteLine("[FAIL] --create-only and --update-only cannot be used together.");
            return 1;
        }

        var exists = store.TryGet(routeName, out var existing);

        if (createOnly && exists)
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' already exists (--create-only specified).");
            return 1;
        }

        if (updateOnly && !exists)
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' does not exist (--update-only specified).");
            return 1;
        }

        // Start with existing config or defaults
        var route = existing.Definition ?? new WebhookRouteConfig();
        route.Verification ??= new WebhookVerificationConfig();
        route.Events ??= [];

        // Parse prompt
        if (!TryResolveTextInput(args, "--prompt", "--prompt-file", out var prompt, out var hasPrompt))
            return 1;

        if (hasPrompt)
            route.Prompt = prompt;

        // Parse secret
        if (!TryResolveSecret(args, out var secret, out var hasSecret))
            return 1;

        if (hasSecret)
            route.Verification.Secret = new SensitiveString(secret);

        // Parse verification kind
        if (!TryGetFlagValue(args, "--verification-kind", out var verificationKind, out var hasVerificationKind))
            return 1;

        if (hasVerificationKind)
        {
            if (!Enum.TryParse<WebhookVerifierKind>(verificationKind, ignoreCase: true, out var kind))
            {
                Console.Error.WriteLine($"[FAIL] Invalid verification kind: '{verificationKind}'. Use 'hmac' or 'header-secret'.");
                return 1;
            }
            route.Verification.Kind = kind;
        }

        // Parse verification headers
        if (!TryGetFlagValue(args, "--signature-header", out var signatureHeader, out var hasSignatureHeader))
            return 1;

        if (hasSignatureHeader)
            route.Verification.SignatureHeaderName = signatureHeader;

        if (!TryGetFlagValue(args, "--signature-prefix", out var signaturePrefix, out var hasSignaturePrefix))
            return 1;

        if (hasSignaturePrefix)
            route.Verification.SignaturePrefix = signaturePrefix;

        if (!TryGetFlagValue(args, "--secret-header", out var secretHeader, out var hasSecretHeader))
            return 1;

        if (hasSecretHeader)
            route.Verification.SecretHeaderName = secretHeader;

        if (!TryGetFlagValue(args, "--event-header", out var eventHeader, out var hasEventHeader))
            return 1;

        if (hasEventHeader)
            route.Verification.EventHeaderName = eventHeader;

        if (!TryGetFlagValue(args, "--delivery-header", out var deliveryHeader, out var hasDeliveryHeader))
            return 1;

        if (hasDeliveryHeader)
            route.Verification.DeliveryIdHeaderName = deliveryHeader;

        // Parse events
        if (!TryGetFlagValue(args, "--events", out var events, out var hasEvents))
            return 1;

        if (hasEvents)
        {
            route.Events = events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        // Parse audience
        if (!TryGetFlagValue(args, "--audience", out var audience, out var hasAudience))
            return 1;

        if (hasAudience)
        {
            if (!Enum.TryParse<TrustAudience>(audience, ignoreCase: true, out var aud))
            {
                Console.Error.WriteLine($"[FAIL] Invalid audience: '{audience}'. Use 'public', 'team', or 'personal'.");
                return 1;
            }
            route.Audience = aud;
        }

        // Parse notification settings
        if (!TryResolveTextInput(args, "--notify-instructions", "--notify-instructions-file", out var notifyInstructions, out var hasNotifyInstructions))
            return 1;

        if (hasNotifyInstructions)
            route.NotifyInstructions = notifyInstructions;

        var deliveryRequired = HasFlag(args, "--delivery-required");
        var noDeliveryRequired = HasFlag(args, "--no-delivery-required");
        if (deliveryRequired && noDeliveryRequired)
        {
            Console.Error.WriteLine("[FAIL] --delivery-required and --no-delivery-required cannot be used together.");
            return 1;
        }

        if (deliveryRequired)
            route.DeliveryRequired = true;
        if (noDeliveryRequired)
            route.DeliveryRequired = false;

        if (!TryGetFlagValue(args, "--notification-channel", out var notificationChannel, out var hasNotificationChannel))
            return 1;

        if (hasNotificationChannel)
        {
            route.NotificationTarget ??= new NotificationTargetConfig();
            route.NotificationTarget.ChannelId = notificationChannel;
        }

        // Parse limits
        if (!TryGetFlagValue(args, "--max-body", out var maxBody, out var hasMaxBody))
            return 1;

        if (hasMaxBody)
        {
            if (!int.TryParse(maxBody, out var bytes) || bytes < 1)
            {
                Console.Error.WriteLine($"[FAIL] Invalid max body size: '{maxBody}'. Must be a positive integer.");
                return 1;
            }
            route.MaxBodyBytes = bytes;
        }

        if (!TryGetFlagValue(args, "--rate-limit", out var rateLimit, out var hasRateLimit))
            return 1;

        if (hasRateLimit)
        {
            if (!int.TryParse(rateLimit, out var limit) || limit < 1)
            {
                Console.Error.WriteLine($"[FAIL] Invalid rate limit: '{rateLimit}'. Must be a positive integer.");
                return 1;
            }
            route.RateLimitPerMinute = limit;
        }

        // Parse enabled/disabled
        var enabled = HasFlag(args, "--enabled");
        var disabled = HasFlag(args, "--disabled");
        if (enabled && disabled)
        {
            Console.Error.WriteLine("[FAIL] --enabled and --disabled cannot be used together.");
            return 1;
        }

        if (enabled)
            route.Enabled = true;
        if (disabled)
            route.Enabled = false;

        // Validate
        var errors = WebhookRouteValidator.Validate(routeName, route);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' has validation errors:");
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }
            return 1;
        }

        if (dryRun)
        {
            Console.WriteLine($"[OK] Webhook route '{routeName}' is valid (dry run, not saved).");
            Console.WriteLine($"     Endpoint: /api/webhooks/{routeName}");
            return 0;
        }

        // Save
        store.Save(routeName, route);

        var action = exists ? "Updated" : "Created";
        Console.WriteLine($"[OK] {action} webhook route '{routeName}'.");
        Console.WriteLine($"     File: {Path.Combine(paths.WebhooksDirectory, $"{routeName}.json")}");
        Console.WriteLine($"     Endpoint: /api/webhooks/{routeName}");

        return 0;
    }

    // ── delete ──

    private static int RunDelete(string[] args, WebhookRouteStore store)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw webhooks delete <route> [--force]");
            return 1;
        }

        if (!TryParseRouteName(args[2], out var routeName))
            return 1;

        var force = HasFlag(args, "--force") || HasFlag(args, "-f");

        if (!force)
        {
            Console.Write($"Delete webhook route '{routeName}'? [y/N]: ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response is not "y" and not "yes")
            {
                Console.WriteLine("Cancelled.");
                return 0;
            }
        }

        if (!store.Delete(routeName))
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' not found.");
            return 1;
        }

        Console.WriteLine($"[OK] Deleted webhook route '{routeName}'.");
        return 0;
    }

    // ── validate ──

    private static int RunValidate(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw webhooks validate <route>");
            return 1;
        }

        if (!TryParseRouteName(args[2], out var routeName))
            return 1;

        var filePath = Path.GetFullPath(Path.Combine(paths.WebhooksDirectory, $"{routeName}.json"));

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[FAIL] Webhook route file not found: {filePath}");
            return 1;
        }

        WebhookRouteConfig route;
        try
        {
            var json = File.ReadAllText(filePath);
            route = JsonSerializer.Deserialize<WebhookRouteConfig>(json, JsonDefaults.ConfigRead)
                ?? throw new InvalidOperationException("Deserialization returned null.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] Could not parse webhook route file:");
            Console.Error.WriteLine($"       {ex.Message}");
            return 1;
        }

        var errors = WebhookRouteValidator.Validate(routeName, route);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' has validation errors:");
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }
            return 1;
        }

        Console.WriteLine($"[OK] Webhook route '{routeName}' is valid.");
        Console.WriteLine($"     Endpoint: /api/webhooks/{routeName}");
        Console.WriteLine($"     Verification: {route.Verification.Kind.ToString().ToLowerInvariant()}");
        Console.WriteLine($"     Audience: {route.Audience.ToString().ToLowerInvariant()}");
        return 0;
    }

    // ── Helpers ──

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetFlagValue(string[] args, string flag, out string value, out bool isSpecified)
    {
        value = string.Empty;
        isSpecified = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith($"{flag}=", StringComparison.OrdinalIgnoreCase))
            {
                value = args[i][(flag.Length + 1)..];
                isSpecified = true;
                return true;
            }

            if (i < args.Length - 1 && string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                if (LooksLikeFlagToken(args[i + 1]))
                {
                    Console.Error.WriteLine($"[FAIL] Missing value for {flag}.");
                    return false;
                }

                value = args[i + 1];
                isSpecified = true;
                return true;
            }

            if (i == args.Length - 1 && string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[FAIL] Missing value for {flag}.");
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveTextInput(
        string[] args,
        string inlineFlag,
        string fileFlag,
        out string value,
        out bool isSpecified)
    {
        value = string.Empty;
        isSpecified = false;

        if (!TryGetFlagValue(args, inlineFlag, out var inlineValue, out var hasInlineValue))
            return false;

        if (!TryGetFlagValue(args, fileFlag, out var fileValue, out var hasFileValue))
            return false;

        if (hasInlineValue && hasFileValue)
        {
            Console.Error.WriteLine($"[FAIL] Use either {inlineFlag} or {fileFlag}, not both.");
            return false;
        }

        if (hasFileValue)
        {
            if (!File.Exists(fileValue))
            {
                Console.Error.WriteLine($"[FAIL] File not found: {fileValue}");
                return false;
            }

            value = File.ReadAllText(fileValue).Trim();
            isSpecified = true;
            return true;
        }

        if (hasInlineValue)
        {
            value = inlineValue;
            isSpecified = true;
        }

        return true;
    }

    private static bool TryResolveSecret(string[] args, out string value, out bool isSpecified)
    {
        value = string.Empty;
        isSpecified = false;

        if (!TryGetFlagValue(args, "--secret-file", out var secretFile, out var hasSecretFile))
            return false;

        if (!TryGetFlagValue(args, "--secret-env", out var secretEnv, out var hasSecretEnv))
            return false;

        if (!TryGetFlagValue(args, "--secret", out var inlineSecret, out var hasInlineSecret))
            return false;

        var sourceCount = (hasSecretFile ? 1 : 0) + (hasSecretEnv ? 1 : 0) + (hasInlineSecret ? 1 : 0);
        if (sourceCount > 1)
        {
            Console.Error.WriteLine("[FAIL] Use only one secret source: --secret, --secret-file, or --secret-env.");
            return false;
        }

        if (hasSecretFile)
        {
            if (!File.Exists(secretFile))
            {
                Console.Error.WriteLine($"[FAIL] Secret file not found: {secretFile}");
                return false;
            }

            value = File.ReadAllText(secretFile).Trim();
            isSpecified = true;
            return true;
        }

        if (hasSecretEnv)
        {
            value = Environment.GetEnvironmentVariable(secretEnv) ?? string.Empty;
            if (string.IsNullOrEmpty(value))
            {
                Console.Error.WriteLine($"[FAIL] Environment variable '{secretEnv}' is not set or empty.");
                return false;
            }

            isSpecified = true;
            return true;
        }

        if (hasInlineSecret)
        {
            Console.Error.WriteLine("warning: --secret exposes the value in shell history; consider --secret-file or --secret-env");
            value = inlineSecret;
            isSpecified = true;
            return true;
        }

        return true;
    }

    private static bool TryParseRouteName(string rawRouteName, out string routeName)
    {
        routeName = string.Empty;

        if (!WebhookRouteStore.TryNormalizeRouteName(rawRouteName, out routeName, out var error))
        {
            Console.Error.WriteLine($"[FAIL] {error}");
            return false;
        }

        return true;
    }

    private static bool LooksLikeFlagToken(string token)
        => token.StartsWith("-", StringComparison.Ordinal);

    // ── Help ──

    private static int WriteHelp()
    {
        Console.WriteLine("Usage: netclaw webhooks <subcommand>");
        Console.WriteLine();
        Console.WriteLine("Manage inbound webhook routes. Routes define how external services");
        Console.WriteLine("(GitHub, Slack, etc.) can trigger agent actions via HTTP webhooks.");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  list                     List configured webhook routes");
        Console.WriteLine("  show <route>             Show route details");
        Console.WriteLine("  set <route> [options]    Create or update a route");
        Console.WriteLine("  delete <route>           Delete a route");
        Console.WriteLine("  validate <route>         Validate a route file");
        Console.WriteLine();
        Console.WriteLine("Options for list:");
        Console.WriteLine("  --json                   Output as JSON");
        Console.WriteLine("  --all                    Include disabled routes");
        Console.WriteLine();
        Console.WriteLine("Options for show:");
        Console.WriteLine("  --json                   Output full config as JSON");
        Console.WriteLine("  --show-secret            Reveal verification secret");
        Console.WriteLine();
        Console.WriteLine("Run 'netclaw webhooks set --help' for set command options.");
        Console.WriteLine();
        Console.WriteLine("Routes are stored in ~/.netclaw/config/webhooks/<route>.json");
        Console.WriteLine("and served at /api/webhooks/<route> by the daemon.");
        Console.WriteLine();
        Console.WriteLine("Note: This command manages INBOUND webhook routes (external services");
        Console.WriteLine("calling Netclaw). For OUTBOUND notifications (Netclaw posting to Slack),");
        Console.WriteLine("see `netclaw secrets set Slack.BotToken` and notification target config.");
        return 0;
    }

    private static void WriteSetHelp()
    {
        Console.WriteLine("Usage: netclaw webhooks set <route> [options]");
        Console.WriteLine();
        Console.WriteLine("Create or update an inbound webhook route.");
        Console.WriteLine();
        Console.WriteLine("Required (for new routes):");
        Console.WriteLine("  --prompt <text>              Prompt instructions for the agent");
        Console.WriteLine("  --prompt-file <path>         Read prompt from file");
        Console.WriteLine("  --secret <value>             Verification secret (visible in shell history!)");
        Console.WriteLine("  --secret-file <path>         Read secret from file");
        Console.WriteLine("  --secret-env <VAR>           Read secret from environment variable");
        Console.WriteLine();
        Console.WriteLine("Verification:");
        Console.WriteLine("  --verification-kind <kind>   'hmac' (default) or 'header-secret'");
        Console.WriteLine("  --signature-header <name>    HMAC signature header (e.g., X-Hub-Signature-256)");
        Console.WriteLine("  --signature-prefix <prefix>  HMAC signature prefix (e.g., sha256=)");
        Console.WriteLine("  --secret-header <name>       Header-secret header name");
        Console.WriteLine("  --event-header <name>        Event type header");
        Console.WriteLine("  --delivery-header <name>     Delivery ID header");
        Console.WriteLine();
        Console.WriteLine("Behavior:");
        Console.WriteLine("  --events <list>              Comma-separated event allowlist");
        Console.WriteLine("  --audience <level>           'public' (default), 'team', or 'personal'");
        Console.WriteLine("  --max-body <bytes>           Max request body size (default: 1048576)");
        Console.WriteLine("  --rate-limit <req/min>       Rate limit per minute (default: 30)");
        Console.WriteLine("  --enabled / --disabled       Enable or disable the route");
        Console.WriteLine();
        Console.WriteLine("Notification:");
        Console.WriteLine("  --notify-instructions <text>     Notification instructions");
        Console.WriteLine("  --notify-instructions-file <path>");
        Console.WriteLine("  --delivery-required              Require notification delivery");
        Console.WriteLine("  --no-delivery-required           Make notification optional");
        Console.WriteLine("  --notification-channel <id>      Slack channel ID for notifications");
        Console.WriteLine();
        Console.WriteLine("Modifiers:");
        Console.WriteLine("  --dry-run                    Validate without saving");
        Console.WriteLine("  --create-only                Fail if route already exists");
        Console.WriteLine("  --update-only                Fail if route doesn't exist");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  netclaw webhooks set github-issues \\");
        Console.WriteLine("    --prompt \"Triage incoming GitHub issues\" \\");
        Console.WriteLine("    --secret-env GITHUB_WEBHOOK_SECRET \\");
        Console.WriteLine("    --signature-header X-Hub-Signature-256 \\");
        Console.WriteLine("    --signature-prefix \"sha256=\" \\");
        Console.WriteLine("    --events issues.opened,issues.closed");
    }
}
