// -----------------------------------------------------------------------
// <copyright file="WebhookRouteActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Configuration;
using static Netclaw.Actors.Webhooks.WebhookRouteProtocol;

namespace Netclaw.Actors.Webhooks;

/// <summary>
/// The single webhook route mutation authority inside the daemon. The agent
/// tools <c>set_webhook</c> and <c>delete_webhook</c> and the
/// <c>/api/webhooks</c> resource ask this actor instead of touching
/// <see cref="WebhookRouteStore"/>, so concurrent read-modify-write requests
/// serialize by mailbox order rather than by lock contention.
/// <para>
/// The actor is a plain <see cref="ReceiveActor"/> with no journal and no
/// cache. Disk stays the canonical store: every message reads the route file
/// through the store, merges the message's fields, and writes the result back.
/// A route file that an external writer changed is therefore visible to the
/// next read with no reconciliation step, and a restart rebuilds nothing
/// because there is nothing to rebuild.
/// </para>
/// <para>
/// This actor is the only writer. An old CLI binary that writes a route file
/// directly during a version skew risks one lost update, and only when it
/// patches the same route at the same moment. Each write stays atomic on its
/// own, so no reader sees a partial file. At webhook mutation rates that risk
/// is accepted; it does not justify a cross-process lock.
/// </para>
/// </summary>
public sealed class WebhookRouteActor : ReceiveActor
{
    private readonly WebhookRouteStore _store;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    public WebhookRouteActor(WebhookRouteStore store)
    {
        _store = store;

        Receive<UpsertRoute>(HandleUpsert);
        Receive<DeleteRoute>(HandleDelete);
        Receive<GetRoute>(HandleGet);
        Receive<ListRoutes>(_ => HandleList());
    }

    private void HandleUpsert(UpsertRoute command)
    {
        var routeName = command.RouteName;
        try
        {
            var outcome = _store.Update(
                routeName.Value,
                existing => Merge(routeName, command, existing));
            Sender.Tell(outcome);
        }
        catch (Exception ex)
        {
            // A persistence failure is never swallowed: the ask faults with the
            // original exception so the tool reports it and the HTTP handler
            // returns a server error. The actor keeps serving — its state lives
            // on disk, so one failed write leaves nothing to repair in memory.
            _log.Warning(ex, "Webhook route {RouteName} could not be saved.", routeName.Value);
            Sender.Tell(new Status.Failure(ex));
        }
    }

    private void HandleDelete(DeleteRoute command)
    {
        var routeName = command.RouteName;
        try
        {
            Sender.Tell(new RouteDeleted(routeName, _store.Delete(routeName.Value)));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Webhook route {RouteName} could not be deleted.", routeName.Value);
            Sender.Tell(new Status.Failure(ex));
        }
    }

    private void HandleGet(GetRoute query)
    {
        var routeName = query.RouteName;
        try
        {
            var found = _store.TryGet(routeName.Value, out var result);
            Sender.Tell(new RouteResponse(routeName, found, found ? result.Definition : null));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Webhook route {RouteName} could not be read.", routeName.Value);
            Sender.Tell(new Status.Failure(ex));
        }
    }

    private void HandleList()
    {
        try
        {
            var entries = _store.ListRouteFiles()
                .Select(x => new RouteEntry(x.RouteName, x.Definition))
                .ToList();
            Sender.Tell(new RouteListResponse(entries));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Webhook routes could not be listed.");
            Sender.Tell(new Status.Failure(ex));
        }
    }

    /// <summary>
    /// Applies one field-level patch to the stored route and validates the
    /// result. Returns a null definition for every rejection, which tells
    /// <see cref="WebhookRouteStore.Update"/> to leave the file untouched.
    /// </summary>
    private (WebhookRouteConfig? Definition, RouteSaved Result) Merge(
        WebhookRouteName routeName,
        UpsertRoute command,
        WebhookRouteConfig? existing)
    {
        if (existing is not null && existing.Audience > command.CreatorAudience)
        {
            return Reject(
                routeName,
                command,
                existing,
                RouteSaveOutcome.AuthorityRejected,
                $"Existing route audience '{existing.Audience.ToWireValue()}' exceeds creator authority ({command.CreatorAudience.ToWireValue()}).");
        }

        TrustAudience audience;
        if (command.RequestedAudience is not { } requested)
        {
            audience = existing?.Audience ?? command.CreatorAudience;
        }
        else if (requested > command.CreatorAudience)
        {
            return Reject(
                routeName,
                command,
                existing,
                RouteSaveOutcome.AuthorityRejected,
                $"Requested audience '{requested.ToWireValue()}' exceeds creator authority ({command.CreatorAudience.ToWireValue()}).");
        }
        else
        {
            audience = requested;
        }

        var existingVerification = existing?.Verification;

        // The tool and the CLI reject timestamp settings on a non-timestamped
        // kind before they reach this actor, each with its own parameter
        // wording. The HTTP patch surface has no such front, so the authority
        // enforces the same rule here: a patch that carries timestamp settings
        // while the merged kind is not HmacTimestamped would persist inert
        // fields that silently activate when the kind is later flipped.
        var mergedKind = command.VerificationKind ?? existingVerification?.Kind ?? WebhookVerifierKind.Hmac;
        var patchHasTimestampSettings =
            command.TimestampField is not null
            || command.SignatureField is not null
            || command.SignedPayloadSeparator is not null
            || command.ToleranceSeconds is not null;
        if (mergedKind != WebhookVerifierKind.HmacTimestamped && patchHasTimestampSettings)
        {
            return Reject(
                routeName,
                command,
                existing,
                RouteSaveOutcome.ValidationRejected,
                "Timestamp verification settings require verification kind 'hmac-timestamped'.");
        }

        var definition = new WebhookRouteConfig
        {
            Enabled = command.Enabled ?? existing?.Enabled ?? true,
            Prompt = command.Prompt?.Trim() ?? existing?.Prompt ?? string.Empty,
            Events = command.Events is null ? [.. existing?.Events ?? []] : [.. command.Events],
            Audience = audience,
            NotifyInstructions = command.NotifyInstructions?.Trim() ?? existing?.NotifyInstructions ?? string.Empty,
            DeliveryRequired = command.DeliveryRequired ?? existing?.DeliveryRequired ?? true,
            MaxBodyBytes = command.MaxBodyBytes ?? existing?.MaxBodyBytes ?? 1024 * 1024,
            RateLimitPerMinute = command.RateLimitPerMinute ?? existing?.RateLimitPerMinute ?? 30,
            Verification = new WebhookVerificationConfig
            {
                Kind = command.VerificationKind ?? existingVerification?.Kind ?? WebhookVerifierKind.Hmac,
                HmacAlgorithm = existingVerification?.HmacAlgorithm ?? WebhookHmacAlgorithm.Sha256,
                Secret = command.Secret is null
                    ? existingVerification?.Secret
                    : new SensitiveString(command.Secret),
                SignatureHeaderName = command.SignatureHeaderName is null
                    ? existingVerification?.SignatureHeaderName
                    : NormalizeOptional(command.SignatureHeaderName),
                SignaturePrefix = command.SignaturePrefix is null
                    ? existingVerification?.SignaturePrefix
                    : NormalizeOptional(command.SignaturePrefix, trim: false),
                SecretHeaderName = command.SecretHeaderName is null
                    ? existingVerification?.SecretHeaderName
                    : NormalizeOptional(command.SecretHeaderName),
                EventHeaderName = command.EventHeaderName is null
                    ? existingVerification?.EventHeaderName
                    : NormalizeOptional(command.EventHeaderName),
                DeliveryIdHeaderName = command.DeliveryIdHeaderName is null
                    ? existingVerification?.DeliveryIdHeaderName
                    : NormalizeOptional(command.DeliveryIdHeaderName),
                TimestampField = command.TimestampField ?? existingVerification?.TimestampField,
                SignatureField = command.SignatureField ?? existingVerification?.SignatureField,
                SignedPayloadSeparator = command.SignedPayloadSeparator ?? existingVerification?.SignedPayloadSeparator,
                ToleranceSeconds = command.ToleranceSeconds ?? existingVerification?.ToleranceSeconds
            }
        };

        if (command.NotificationChannelId is null && existing?.NotificationTarget is { } existingTarget)
        {
            definition.NotificationTarget = new NotificationTargetConfig
            {
                Kind = existingTarget.Kind,
                ChannelId = existingTarget.ChannelId
            };
        }
        else if (!string.IsNullOrWhiteSpace(command.NotificationChannelId))
        {
            definition.NotificationTarget = new NotificationTargetConfig
            {
                Kind = NotificationTargetKind.Slack,
                ChannelId = command.NotificationChannelId.Trim()
            };
        }

        var validationErrors = WebhookRouteValidator.Validate(routeName.Value, definition);
        if (validationErrors.Count > 0)
        {
            return Reject(
                routeName,
                command,
                existing,
                RouteSaveOutcome.ValidationRejected,
                validationErrors[0]);
        }

        return (definition, new RouteSaved(
            routeName,
            existing is null ? RouteSaveOutcome.Created : RouteSaveOutcome.Updated,
            definition));
    }

    /// <summary>
    /// Builds a rejection reply and records it. A refused route mutation is a
    /// security event: it is the one signal that a caller tried to take over or
    /// to mint authority above its own. The record names the route, both
    /// audiences, and the reason. It never names the route secret.
    /// </summary>
    private (WebhookRouteConfig? Definition, RouteSaved Result) Reject(
        WebhookRouteName routeName,
        UpsertRoute command,
        WebhookRouteConfig? existing,
        RouteSaveOutcome outcome,
        string reason)
    {
        _log.Warning(
            "Webhook route {RouteName} rejected ({RejectionKind}). Creator audience {CreatorAudience}, "
            + "requested audience {RequestedAudience}, stored audience {StoredAudience}. Reason: {Reason}",
            routeName.Value,
            outcome,
            command.CreatorAudience.ToWireValue(),
            command.RequestedAudience?.ToWireValue() ?? "(inherited)",
            existing?.Audience.ToWireValue() ?? "(new route)",
            reason);

        return (null, new RouteSaved(routeName, outcome, Route: null, reason));
    }

    private static string? NormalizeOptional(string value, bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return trim ? value.Trim() : value;
    }

    public static Props CreateProps(WebhookRouteStore store)
        => Props.Create(() => new WebhookRouteActor(store));
}
