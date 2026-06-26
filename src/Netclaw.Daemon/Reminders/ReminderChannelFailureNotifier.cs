// -----------------------------------------------------------------------
// <copyright file="ReminderChannelFailureNotifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Tools;

namespace Netclaw.Daemon.Reminders;

/// <summary>
/// Daemon-side <see cref="IReminderChannelNotifier"/>: posts a reminder's failure
/// notice to its destination channel via the channel outbound registry, so the
/// operator sees the failure where they expect that reminder's output. Lives in
/// the daemon (not the actor) because <see cref="IChannelRegistry"/> is a channel
/// concern; the actor layer stays transport-agnostic. Fire-and-forget — never
/// blocks or throws into the reminder manager; delivery failures are logged.
/// </summary>
internal sealed class ReminderChannelFailureNotifier : IReminderChannelNotifier
{
    private readonly IChannelRegistry _registry;
    private readonly ILogger<ReminderChannelFailureNotifier> _logger;

    public ReminderChannelFailureNotifier(
        IChannelRegistry registry,
        ILogger<ReminderChannelFailureNotifier> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public void NotifyFailure(ChannelDeliveryTargetInfo target, string text)
        => _ = PostAsync(target, text);

    private async Task PostAsync(ChannelDeliveryTargetInfo target, string text)
    {
        try
        {
            if (!ChannelAddressKindWire.TryParse(target.DestinationKind, out var addressKind))
            {
                _logger.LogWarning(
                    "Reminder failure notice not posted: unrecognized destination kind '{DestinationKind}' for channel '{ChannelKey}'.",
                    target.DestinationKind, target.ChannelKey);
                return;
            }

            IChannelOutboundClient outbound;
            try
            {
                outbound = _registry.GetOutboundClient(ChannelDescriptorKey.Create(target.ChannelKey));
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning(
                    "Reminder failure notice not posted: channel '{ChannelKey}' has no registered outbound client.",
                    target.ChannelKey);
                return;
            }

            // Outbound clients return an "Error: ..." string for expected send
            // failures rather than throwing, so inspect the result.
            var result = await outbound.SendMessageAsync(
                new ChannelSendRequest(addressKind, target.DestinationId, text));

            if (result.StartsWith("Error:", StringComparison.Ordinal))
                _logger.LogWarning(
                    "Reminder failure notice to channel '{ChannelKey}' was rejected: {Result}",
                    target.ChannelKey, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to post reminder failure notice to channel '{ChannelKey}'.",
                target.ChannelKey);
        }
    }
}
