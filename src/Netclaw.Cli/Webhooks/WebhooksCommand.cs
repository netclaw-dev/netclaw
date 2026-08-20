// -----------------------------------------------------------------------
// <copyright file="WebhooksCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Webhooks;

/// <summary>
/// Handles <c>netclaw webhooks &lt;subcommand&gt;</c> CLI subcommands.
/// <para>
/// Reads (<c>list</c>, <c>show</c>, <c>validate</c>) are always local: disk is
/// the canonical route store and the daemon actor holds no cache, so a file read
/// is always current, and <c>show</c> needs the secret that the API never
/// returns. Writes (<c>set</c>, <c>delete</c>) require the daemon — see
/// <see cref="WebhookRouteDaemonClient"/>. There is no local write path.
/// </para>
/// </summary>
internal static class WebhooksCommand
{
    /// <summary>
    /// Runs one <c>netclaw webhooks</c> invocation.
    /// </summary>
    /// <param name="daemonApi">
    /// The daemon client for route mutations. The read subcommands never use it,
    /// so they accept null. A null on <c>set</c> or <c>delete</c> fails the
    /// command exactly as an unreachable daemon does.
    /// </param>
    public static async Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        TextWriter? output = null,
        DaemonApi? daemonApi = null)
    {
        output ??= Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "list";

        if (subcommand is "help" or "-h" or "--help")
            return WriteHelp(output);

        var store = new WebhookRouteStore(paths);
        var daemon = new WebhookRouteDaemonClient(daemonApi);

        return subcommand switch
        {
            "list" => RunList(args, store, paths, output),
            "show" => RunShow(args, store, paths, output),
            "set" => await RunSetAsync(args, store, paths, output, daemon),
            "delete" => await RunDeleteAsync(args, output, daemon),
            "validate" => RunValidate(args, paths, output),
            _ => WriteHelp(output)
        };
    }

    // ── list ──

    private static int RunList(string[] args, WebhookRouteStore store, NetclawPaths paths, TextWriter output)
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
                output.WriteLine("[]");
            }
            else
            {
                output.WriteLine("No webhook routes configured.");
                output.WriteLine($"Routes are stored in: {paths.WebhooksDirectory}");
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
                    verification = r.IsValid ? ToCliVerifierKind(r.Definition!.Verification.Kind) : "unknown",
                    deliveryRequired = r.IsValid && r.Definition!.DeliveryRequired
                })
                .ToList();
            output.WriteLine(JsonSerializer.Serialize(items, JsonDefaults.IndentedCamelCase));
            return 0;
        }

        const int colName = 24;
        const int colStatus = 10;
        const int colAudience = 10;
        const int colVerification = 18;

        output.WriteLine(
            $"{"NAME",-colName}  {"STATUS",-colStatus}  {"AUDIENCE",-colAudience}  {"VERIFICATION",-colVerification}  DELIVERY");
        output.WriteLine(new string('-', colName + colStatus + colAudience + colVerification + 18));

        foreach (var route in routes)
        {
            var status = route.Status;

            if (!all && status == "disabled")
                continue;

            var audience = route.IsValid ? route.Definition!.Audience.ToString().ToLowerInvariant() : "-";
            var verification = route.IsValid ? ToCliVerifierKind(route.Definition!.Verification.Kind) : "-";
            var delivery = route.IsValid && route.Definition!.DeliveryRequired ? "required" : "optional";

            output.WriteLine(
                $"{route.RouteName,-colName}  {status,-colStatus}  {audience,-colAudience}  {verification,-colVerification}  {delivery}");
        }

        return 0;
    }

    // ── show ──

    private static int RunShow(string[] args, WebhookRouteStore store, NetclawPaths paths, TextWriter output)
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
            var jsonOutput = new
            {
                name = routeName,
                file = match.FilePath,
                endpoint = $"/api/webhooks/{routeName}",
                enabled = route.Enabled,
                verification = BuildVerificationOutput(route.Verification, showSecret),
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
            output.WriteLine(JsonSerializer.Serialize(jsonOutput, JsonDefaults.IndentedCamelCase));
            return 0;
        }

        output.WriteLine($"Route:              {routeName}");
        output.WriteLine($"File:               {match.FilePath}");
        output.WriteLine($"Status:             {(route.Enabled ? "enabled" : "disabled")}");
        output.WriteLine($"Endpoint:           /api/webhooks/{routeName}");
        output.WriteLine();
        output.WriteLine("Verification:");
        output.WriteLine($"  Kind:             {ToCliVerifierKind(route.Verification.Kind)}");
        output.WriteLine($"  Secret:           {(showSecret ? route.Verification.Secret?.Value ?? "(not set)" : "********** (use --show-secret to reveal)")}");
        if (route.Verification.Kind is WebhookVerifierKind.Hmac or WebhookVerifierKind.HmacTimestamped)
        {
            output.WriteLine($"  Algorithm:        {route.Verification.HmacAlgorithm.ToString().ToLowerInvariant()}");
            output.WriteLine($"  Signature Header: {route.Verification.SignatureHeaderName ?? "(default)"}");
            if (route.Verification.Kind == WebhookVerifierKind.Hmac)
            {
                output.WriteLine($"  Signature Prefix: {route.Verification.SignaturePrefix ?? "(none)"}");
            }
            else
            {
                output.WriteLine($"  Timestamp Field:  {route.Verification.TimestampField ?? "t (default)"}");
                output.WriteLine($"  Signature Field:  {route.Verification.SignatureField ?? "v1 (default)"}");
                output.WriteLine($"  Payload Separator: {route.Verification.SignedPayloadSeparator ?? ". (default)"}");
                output.WriteLine($"  Tolerance:        {route.Verification.ToleranceSeconds?.ToString() ?? "300 (default)"} seconds");
            }
        }
        else
        {
            output.WriteLine($"  Secret Header:    {route.Verification.SecretHeaderName ?? "(default)"}");
        }
        output.WriteLine($"  Event Header:     {route.Verification.EventHeaderName ?? "(default)"}");
        output.WriteLine($"  Delivery Header:  {route.Verification.DeliveryIdHeaderName ?? "(default)"}");
        output.WriteLine();
        output.WriteLine("Behavior:");
        output.WriteLine($"  Audience:         {route.Audience.ToString().ToLowerInvariant()}");
        output.WriteLine($"  Events:           {(route.Events.Count > 0 ? string.Join(", ", route.Events) : "(all)")}");
        output.WriteLine($"  Rate Limit:       {route.RateLimitPerMinute} req/min");
        output.WriteLine($"  Max Body:         {route.MaxBodyBytes} bytes ({route.MaxBodyBytes / 1024 / 1024} MB)");
        output.WriteLine();
        output.WriteLine("Notification:");
        output.WriteLine($"  Delivery:         {(route.DeliveryRequired ? "required" : "optional")}");
        if (route.NotificationTarget is not null)
        {
            output.WriteLine($"  Target:           {route.NotificationTarget.Kind.ToString().ToLowerInvariant()} (channel: {route.NotificationTarget.ChannelId ?? "(not set)"})");
        }
        else
        {
            output.WriteLine("  Target:           (not configured)");
        }
        if (!string.IsNullOrWhiteSpace(route.NotifyInstructions))
        {
            var truncated = route.NotifyInstructions.Length > 60
                ? route.NotifyInstructions[..60] + "..."
                : route.NotifyInstructions;
            output.WriteLine($"  Instructions:     {truncated.Replace("\n", " ")}");
        }
        output.WriteLine();
        output.WriteLine("Prompt:");
        if (string.IsNullOrWhiteSpace(route.Prompt))
        {
            output.WriteLine("  (empty)");
        }
        else if (route.Prompt.Length > 200)
        {
            output.WriteLine($"  {route.Prompt[..200].Replace("\n", " ")}...");
            output.WriteLine("  (truncated; run with --json for full prompt)");
        }
        else
        {
            foreach (var line in route.Prompt.Split('\n'))
            {
                output.WriteLine($"  {line}");
            }
        }

        return 0;
    }

    // ── set ──

    private static async Task<int> RunSetAsync(
        string[] args,
        WebhookRouteStore store,
        NetclawPaths paths,
        TextWriter output,
        WebhookRouteDaemonClient daemon)
    {
        if (args.Length < 3 || HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            WriteSetHelp(output);
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

        if (!TryResolveTextInput(args, "--prompt", "--prompt-file", out var prompt, out var hasPrompt))
            return 1;

        if (!TryResolveSecret(args, out var secret, out var hasSecret))
            return 1;

        if (!TryResolveTextInput(args, "--notify-instructions", "--notify-instructions-file", out var notifyInstructions, out var hasNotifyInstructions))
            return 1;

        // Argument grammar stays local: these checks read only the command line,
        // so they answer the same way with or without a daemon.
        if (!TryGetFlagValue(args, "--verification-kind", out var verificationKindText, out var hasVerificationKind))
            return 1;

        var verificationKind = WebhookVerifierKind.Hmac;
        if (hasVerificationKind && !WebhookRouteValidator.TryParseVerifierKind(verificationKindText, out verificationKind))
        {
            Console.Error.WriteLine($"[FAIL] Invalid verification kind: '{verificationKindText}'. Use 'hmac', 'hmac-timestamped', or 'header-secret'.");
            return 1;
        }

        if (!TryGetFlagValue(args, "--signature-header", out var signatureHeader, out var hasSignatureHeader))
            return 1;

        if (!TryGetFlagValue(args, "--signature-prefix", out var signaturePrefix, out var hasSignaturePrefix))
            return 1;

        if (!TryGetFlagValue(args, "--secret-header", out var secretHeader, out var hasSecretHeader))
            return 1;

        if (!TryGetFlagValue(args, "--event-header", out var eventHeader, out var hasEventHeader))
            return 1;

        if (!TryGetFlagValue(args, "--delivery-header", out var deliveryHeader, out var hasDeliveryHeader))
            return 1;

        if (!TryGetFlagValue(args, "--timestamp-field", out var timestampField, out var hasTimestampField))
            return 1;

        if (!TryGetFlagValue(args, "--signature-field", out var signatureField, out var hasSignatureField))
            return 1;

        if (!TryGetFlagValue(args, "--signed-payload-separator", out var payloadSeparator, out var hasPayloadSeparator))
            return 1;

        if (!TryGetFlagValue(args, "--signature-tolerance-seconds", out var toleranceText, out var hasTolerance))
            return 1;

        var toleranceSeconds = 0;
        if (hasTolerance && !int.TryParse(toleranceText, out toleranceSeconds))
        {
            Console.Error.WriteLine($"[FAIL] Invalid signature tolerance: '{toleranceText}'. Must be a whole number from 1 to 3600.");
            return 1;
        }

        if (!TryGetFlagValue(args, "--events", out var eventsText, out var hasEvents))
            return 1;

        string[] events = hasEvents
            ? [.. eventsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

        if (!TryGetFlagValue(args, "--audience", out var audienceText, out var hasAudience))
            return 1;

        var audience = TrustAudience.Public;
        if (hasAudience && !Enum.TryParse(audienceText, ignoreCase: true, out audience))
        {
            Console.Error.WriteLine($"[FAIL] Invalid audience: '{audienceText}'. Use 'public', 'team', or 'personal'.");
            return 1;
        }

        var deliveryRequired = HasFlag(args, "--delivery-required");
        var noDeliveryRequired = HasFlag(args, "--no-delivery-required");
        if (deliveryRequired && noDeliveryRequired)
        {
            Console.Error.WriteLine("[FAIL] --delivery-required and --no-delivery-required cannot be used together.");
            return 1;
        }

        if (!TryGetFlagValue(args, "--notification-channel", out var notificationChannel, out var hasNotificationChannel))
            return 1;

        if (!TryGetFlagValue(args, "--max-body", out var maxBodyText, out var hasMaxBody))
            return 1;

        var maxBodyBytes = 0;
        if (hasMaxBody && (!int.TryParse(maxBodyText, out maxBodyBytes) || maxBodyBytes < 1))
        {
            Console.Error.WriteLine($"[FAIL] Invalid max body size: '{maxBodyText}'. Must be a positive integer.");
            return 1;
        }

        if (!TryGetFlagValue(args, "--rate-limit", out var rateLimitText, out var hasRateLimit))
            return 1;

        var rateLimit = 0;
        if (hasRateLimit && (!int.TryParse(rateLimitText, out rateLimit) || rateLimit < 1))
        {
            Console.Error.WriteLine($"[FAIL] Invalid rate limit: '{rateLimitText}'. Must be a positive integer.");
            return 1;
        }

        var enabled = HasFlag(args, "--enabled");
        var disabled = HasFlag(args, "--disabled");
        if (enabled && disabled)
        {
            Console.Error.WriteLine("[FAIL] --enabled and --disabled cannot be used together.");
            return 1;
        }

        var updatedExistingRoute = false;

        // Merges the parsed flags onto the stored route and validates the result.
        // It is a local preview: it answers --create-only / --update-only, the
        // Created-or-Updated wording, and --dry-run before the command contacts
        // the daemon. A null definition means the command sends nothing. The
        // daemon re-reads and re-validates the patch, so it stays the one
        // enforcement point.
        (WebhookRouteConfig? Definition, int Result) Merge(WebhookRouteConfig? existing)
        {
            var exists = existing is not null;

            if (createOnly && exists)
            {
                Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' already exists (--create-only specified).");
                return (null, 1);
            }

            if (updateOnly && !exists)
            {
                Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' does not exist (--update-only specified).");
                return (null, 1);
            }

            var route = existing ?? new WebhookRouteConfig();
            route.Verification ??= new WebhookVerificationConfig();
            route.Events ??= [];

            if (hasPrompt)
                route.Prompt = prompt;

            if (hasSecret)
                route.Verification.Secret = new SensitiveString(secret);

            if (hasVerificationKind)
                route.Verification.Kind = verificationKind;

            if (hasSignatureHeader)
                route.Verification.SignatureHeaderName = signatureHeader;

            if (hasSignaturePrefix)
                route.Verification.SignaturePrefix = signaturePrefix;

            if (hasSecretHeader)
                route.Verification.SecretHeaderName = secretHeader;

            if (hasEventHeader)
                route.Verification.EventHeaderName = eventHeader;

            if (hasDeliveryHeader)
                route.Verification.DeliveryIdHeaderName = deliveryHeader;

            if (hasTimestampField)
                route.Verification.TimestampField = timestampField;

            if (hasSignatureField)
                route.Verification.SignatureField = signatureField;

            if (hasPayloadSeparator)
                route.Verification.SignedPayloadSeparator = payloadSeparator;

            if (hasTolerance)
                route.Verification.ToleranceSeconds = toleranceSeconds;

            // The merged kind decides this, because an omitted --verification-kind
            // keeps the stored kind.
            if ((hasTimestampField || hasSignatureField || hasPayloadSeparator || hasTolerance)
                && route.Verification.Kind != WebhookVerifierKind.HmacTimestamped)
            {
                Console.Error.WriteLine("[FAIL] Timestamp signature options require '--verification-kind hmac-timestamped'.");
                return (null, 1);
            }

            if (hasEvents)
                route.Events = [.. events];

            if (hasAudience)
                route.Audience = audience;

            if (hasNotifyInstructions)
                route.NotifyInstructions = notifyInstructions;

            if (deliveryRequired)
                route.DeliveryRequired = true;
            if (noDeliveryRequired)
                route.DeliveryRequired = false;

            if (hasNotificationChannel)
            {
                route.NotificationTarget ??= new NotificationTargetConfig();
                route.NotificationTarget.ChannelId = notificationChannel;
            }

            if (hasMaxBody)
                route.MaxBodyBytes = maxBodyBytes;

            if (hasRateLimit)
                route.RateLimitPerMinute = rateLimit;

            if (enabled)
                route.Enabled = true;
            if (disabled)
                route.Enabled = false;

            var errors = WebhookRouteValidator.Validate(routeName, route);
            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' has validation errors:");
                foreach (var error in errors)
                {
                    Console.Error.WriteLine($"  - {error}");
                }
                return (null, 1);
            }

            if (dryRun)
            {
                output.WriteLine($"[OK] Webhook route '{routeName}' is valid (dry run, not saved).");
                output.WriteLine($"     Endpoint: /api/webhooks/{routeName}");
                return (null, 0);
            }

            updatedExistingRoute = exists;
            return (route, 0);
        }

        WebhookRouteConfig? existing;
        WebhookRouteConfig? merged;
        int result;
        try
        {
            existing = ReadExistingRoute(store, routeName);
            (merged, result) = Merge(existing);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"[FAIL] {ex.Message}");
            return 1;
        }

        // A dry run and a rejected merge both send nothing, so neither needs the
        // daemon. Merge already reported the reason.
        if (merged is null)
            return result;

        var available = await daemon.EnsureAvailableAsync(CancellationToken.None);
        if (!available.Success)
        {
            Console.Error.WriteLine($"[FAIL] {available.Error}");
            return 1;
        }

        var saved = await daemon.UpsertAsync(routeName, BuildPatch(existing is null), CancellationToken.None);
        if (!saved.Success)
        {
            Console.Error.WriteLine($"[FAIL] {saved.Error}");
            return 1;
        }

        var action = updatedExistingRoute ? "Updated" : "Created";
        output.WriteLine($"[OK] {action} webhook route '{routeName}'.");
        output.WriteLine($"     File: {Path.Combine(paths.WebhooksDirectory, $"{routeName}.json")}");
        output.WriteLine($"     Endpoint: /api/webhooks/{routeName}");

        return result;

        // Projects the parsed flags into the daemon's field-level patch. An
        // unspecified flag stays null so the daemon keeps the stored value.
        WebhookRoutePatch BuildPatch(bool isNewRoute) => new()
        {
            Prompt = hasPrompt ? prompt : null,
            Secret = hasSecret ? secret : null,
            VerificationKind = hasVerificationKind ? verificationKind.ToString() : null,
            // A new route keeps the CLI's documented 'public' default. Left null,
            // the daemon would mint the route at the caller's own authority, which
            // is higher than the flag default and would raise the route's audience.
            Audience = hasAudience
                ? audience.ToWireValue()
                : isNewRoute ? TrustAudience.Public.ToWireValue() : null,
            Events = hasEvents ? events : null,
            NotifyInstructions = hasNotifyInstructions ? notifyInstructions : null,
            DeliveryRequired = ResolveToggle(deliveryRequired, noDeliveryRequired),
            NotificationChannelId = hasNotificationChannel ? notificationChannel : null,
            MaxBodyBytes = hasMaxBody ? maxBodyBytes : null,
            RateLimitPerMinute = hasRateLimit ? rateLimit : null,
            Enabled = ResolveToggle(enabled, disabled),
            SignatureHeaderName = hasSignatureHeader ? signatureHeader : null,
            SignaturePrefix = hasSignaturePrefix ? signaturePrefix : null,
            SecretHeaderName = hasSecretHeader ? secretHeader : null,
            EventHeaderName = hasEventHeader ? eventHeader : null,
            DeliveryIdHeaderName = hasDeliveryHeader ? deliveryHeader : null,
            TimestampField = hasTimestampField ? timestampField : null,
            SignatureField = hasSignatureField ? signatureField : null,
            SignedPayloadSeparator = hasPayloadSeparator ? payloadSeparator : null,
            ToleranceSeconds = hasTolerance ? toleranceSeconds : null
        };
    }

    private static bool? ResolveToggle(bool onFlag, bool offFlag)
        => onFlag ? true : offFlag ? false : null;

    /// <summary>
    /// Reads the stored route for the merge preview. An unparseable file stops
    /// the command: the CLI must not send a patch built on a route it could not
    /// read.
    /// </summary>
    private static WebhookRouteConfig? ReadExistingRoute(WebhookRouteStore store, string routeName)
    {
        if (!store.TryGet(routeName, out var match))
            return null;

        return match.Definition
            ?? throw new InvalidDataException($"Existing webhook route '{routeName}' could not be parsed.");
    }

    // ── delete ──

    private static async Task<int> RunDeleteAsync(
        string[] args,
        TextWriter output,
        WebhookRouteDaemonClient daemon)
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
                output.WriteLine("Cancelled.");
                return 0;
            }
        }

        var available = await daemon.EnsureAvailableAsync(CancellationToken.None);
        if (!available.Success)
        {
            Console.Error.WriteLine($"[FAIL] {available.Error}");
            return 1;
        }

        // The probe already ran, so a 404 here is a missing route, not an old
        // daemon without the resource.
        var removed = await daemon.DeleteAsync(routeName, CancellationToken.None);
        if (!removed.Success && !removed.NotFound)
        {
            Console.Error.WriteLine($"[FAIL] {removed.Error}");
            return 1;
        }

        if (!removed.Success)
        {
            Console.Error.WriteLine($"[FAIL] Webhook route '{routeName}' not found.");
            return 1;
        }

        output.WriteLine($"[OK] Deleted webhook route '{routeName}'.");
        return 0;
    }

    // ── validate ──

    private static int RunValidate(string[] args, NetclawPaths paths, TextWriter output)
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

        output.WriteLine($"[OK] Webhook route '{routeName}' is valid.");
        output.WriteLine($"     Endpoint: /api/webhooks/{routeName}");
        output.WriteLine($"     Verification: {ToCliVerifierKind(route.Verification.Kind)}");
        output.WriteLine($"     Audience: {route.Audience.ToString().ToLowerInvariant()}");
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

    private static string ToCliVerifierKind(WebhookVerifierKind kind)
        => kind == WebhookVerifierKind.HmacTimestamped
            ? "hmac-timestamped"
            : kind.ToString().ToLowerInvariant();

    private static Dictionary<string, object?> BuildVerificationOutput(
        WebhookVerificationConfig verification,
        bool showSecret)
    {
        var output = new Dictionary<string, object?>
        {
            ["kind"] = ToCliVerifierKind(verification.Kind),
            ["secret"] = showSecret ? verification.Secret?.Value : "********",
            ["hmacAlgorithm"] = verification.HmacAlgorithm.ToString().ToLowerInvariant(),
            ["signatureHeader"] = verification.SignatureHeaderName,
            ["signaturePrefix"] = verification.SignaturePrefix,
            ["secretHeader"] = verification.SecretHeaderName,
            ["eventHeader"] = verification.EventHeaderName,
            ["deliveryIdHeader"] = verification.DeliveryIdHeaderName
        };

        if (verification.Kind == WebhookVerifierKind.HmacTimestamped)
        {
            output["timestampField"] = verification.TimestampField ?? "t";
            output["signatureField"] = verification.SignatureField ?? "v1";
            output["signedPayloadSeparator"] = verification.SignedPayloadSeparator ?? ".";
            output["toleranceSeconds"] = verification.ToleranceSeconds ?? 300;
        }

        return output;
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

        // The CLI keeps the name as a string: it travels over HTTP as a path
        // segment. The parse still runs here so an operator typo gets its
        // message before any daemon call.
        if (!WebhookRouteName.TryCreate(rawRouteName, out var parsed, out var error))
        {
            Console.Error.WriteLine($"[FAIL] {error}");
            return false;
        }

        routeName = parsed.Value;
        return true;
    }

    private static bool LooksLikeFlagToken(string token)
        => token.StartsWith("-", StringComparison.Ordinal);

    // ── Help ──

    private static int WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw webhooks <subcommand>");
        output.WriteLine();
        output.WriteLine("Manage inbound webhook routes. Routes define how external services");
        output.WriteLine("(GitHub, Slack, etc.) can trigger agent actions via HTTP webhooks.");
        output.WriteLine();
        output.WriteLine("Subcommands:");
        output.WriteLine("  list                     List configured webhook routes");
        output.WriteLine("  show <route>             Show route details");
        output.WriteLine("  set <route> [options]    Create or update a route");
        output.WriteLine("  delete <route>           Delete a route");
        output.WriteLine("  validate <route>         Validate a route file");
        output.WriteLine();
        output.WriteLine("Options for list:");
        output.WriteLine("  --json                   Output as JSON");
        output.WriteLine("  --all                    Include disabled routes");
        output.WriteLine();
        output.WriteLine("Options for show:");
        output.WriteLine("  --json                   Output full config as JSON");
        output.WriteLine("  --show-secret            Reveal verification secret");
        output.WriteLine();
        output.WriteLine("Run 'netclaw webhooks set --help' for set command options.");
        output.WriteLine();
        output.WriteLine("Routes are stored in ~/.netclaw/config/webhooks/<route>.json");
        output.WriteLine("and served at /api/webhooks/<route> by the daemon.");
        output.WriteLine();
        output.WriteLine("'set' and 'delete' need a running daemon: the daemon owns route");
        output.WriteLine("changes. 'list', 'show', and 'validate' read the files directly.");
        output.WriteLine();
        output.WriteLine("Note: This command manages INBOUND webhook routes (external services");
        output.WriteLine("calling Netclaw). For OUTBOUND notifications (Netclaw posting to Slack),");
        output.WriteLine("see `netclaw secrets set Slack.BotToken` and notification target config.");
        return 0;
    }

    private static void WriteSetHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw webhooks set <route> [options]");
        output.WriteLine();
        output.WriteLine("Create or update an inbound webhook route. The daemon must be");
        output.WriteLine("running: it owns every route change. --dry-run needs no daemon.");
        output.WriteLine();
        output.WriteLine("Required (for new routes):");
        output.WriteLine("  --prompt <text>              Prompt instructions for the agent");
        output.WriteLine("  --prompt-file <path>         Read prompt from file");
        output.WriteLine("  --secret <value>             Verification secret (visible in shell history!)");
        output.WriteLine("  --secret-file <path>         Read secret from file");
        output.WriteLine("  --secret-env <VAR>           Read secret from environment variable");
        output.WriteLine();
        output.WriteLine("Verification:");
        output.WriteLine("  --verification-kind <kind>   'hmac' (default), 'hmac-timestamped', or 'header-secret'");
        output.WriteLine("  --signature-header <name>    HMAC signature header (e.g., X-Hub-Signature-256)");
        output.WriteLine("  --signature-prefix <prefix>  HMAC signature prefix (e.g., sha256=)");
        output.WriteLine("  --secret-header <name>       Header-secret header name");
        output.WriteLine("  --event-header <name>        Event type header");
        output.WriteLine("  --delivery-header <name>     Delivery ID header");
        output.WriteLine("  --timestamp-field <name>     Timestamped HMAC field (default: t)");
        output.WriteLine("  --signature-field <name>     Timestamped HMAC signature field (default: v1)");
        output.WriteLine("  --signed-payload-separator <value>");
        output.WriteLine("                               Timestamp/body separator (default: .)");
        output.WriteLine("  --signature-tolerance-seconds <seconds>");
        output.WriteLine("                               Replay tolerance, 1-3600 (default: 300)");
        output.WriteLine();
        output.WriteLine("Behavior:");
        output.WriteLine("  --events <list>              Comma-separated event allowlist");
        output.WriteLine("  --audience <level>           'public' (default), 'team', or 'personal'");
        output.WriteLine("  --max-body <bytes>           Max request body size (default: 1048576)");
        output.WriteLine("  --rate-limit <req/min>       Rate limit per minute (default: 30)");
        output.WriteLine("  --enabled / --disabled       Enable or disable the route");
        output.WriteLine();
        output.WriteLine("Notification:");
        output.WriteLine("  --notify-instructions <text>     Notification instructions");
        output.WriteLine("  --notify-instructions-file <path>");
        output.WriteLine("  --delivery-required              Require notification delivery");
        output.WriteLine("  --no-delivery-required           Make notification optional");
        output.WriteLine("  --notification-channel <id>      Slack channel ID for notifications");
        output.WriteLine();
        output.WriteLine("Modifiers:");
        output.WriteLine("  --dry-run                    Validate without saving");
        output.WriteLine("  --create-only                Fail if route already exists");
        output.WriteLine("  --update-only                Fail if route doesn't exist");
        output.WriteLine();
        output.WriteLine("Examples:");
        output.WriteLine("  netclaw webhooks set github-issues \\");
        output.WriteLine("    --prompt \"Triage incoming GitHub issues\" \\");
        output.WriteLine("    --secret-env GITHUB_WEBHOOK_SECRET \\");
        output.WriteLine("    --signature-header X-Hub-Signature-256 \\");
        output.WriteLine("    --signature-prefix \"sha256=\" \\");
        output.WriteLine("    --events issues.opened,issues.closed");
        output.WriteLine();
        output.WriteLine("  netclaw webhooks set stripe-events \\");
        output.WriteLine("    --prompt \"Process this Stripe event\" \\");
        output.WriteLine("    --secret-env STRIPE_WEBHOOK_SECRET \\");
        output.WriteLine("    --verification-kind hmac-timestamped \\");
        output.WriteLine("    --signature-header Stripe-Signature");
    }
}
