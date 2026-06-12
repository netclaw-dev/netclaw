// -----------------------------------------------------------------------
// <copyright file="ChannelSendTools.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Mattermost;
using Netclaw.Tools;

namespace Netclaw.Daemon.Configuration;

// Registered once per host by the remote chat channel builder
// (RemoteChatChannelRegistrationExtensions.AddSharedChannelTools) whenever at
// least one remote chat channel is enabled.
internal sealed class SendChannelMessageTool(IChannelRegistry registry) : IChannelTool
{
    private AITool? _aiTool;
    private JsonElement? _parameterSchema;
    private LlmFacingToolName? _llmFacingName;

    public string Name => "send_channel_message";

    public LlmFacingToolName LlmFacingName => _llmFacingName ??= LlmFacingToolName.FromCanonical(Name);

    public string Description =>
        "Send a message through an enabled chat channel using a resolved destination. " +
        "Examples: channel_key=slack with destination.kind=destination, channel_key=mattermost with destination.kind=direct_message, channel_key=discord with destination.kind=destination or direct_message.";

    public string GrantCategory => "builtin";

    public JsonElement ParameterSchema => _parameterSchema ??= BuildParameterSchema();

    public AITool ToAITool()
    {
        return _aiTool ??= AIFunctionFactory.CreateDeclaration(Name, Description, ParameterSchema);
    }

    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
        => await ExecuteCoreAsync(arguments, context: null, ct);

    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolExecutionContext context, CancellationToken ct = default)
        => ExecuteCoreAsync(arguments, context, ct);

    private async Task<string> ExecuteCoreAsync(
        IDictionary<string, object?>? arguments,
        ToolExecutionContext? context,
        CancellationToken ct)
    {
        var channelKeyValue = ToolArgumentHelper.GetString(arguments, "channel_key");
        if (string.IsNullOrWhiteSpace(channelKeyValue))
            return "Error: 'channel_key' parameter is required.";

        var text = ToolArgumentHelper.GetString(arguments, "text")
                   ?? ToolArgumentHelper.GetString(arguments, "Message");
        if (string.IsNullOrWhiteSpace(text))
            return "Error: 'text' parameter is required.";

        if (IsTriggerOrigin(context) && context!.RequestedDeliveryTarget is null)
        {
            return "Error: trigger-originated channel send requires a configured channel delivery target on the turn. " +
                   "No default output channel will be selected.";
        }

        if (!TryReadDestination(arguments, out var destination, out var destinationError))
            return destinationError;

        var key = ChannelDescriptorKey.Create(channelKeyValue.Trim());
        if (!destination.ChannelKey.Equals(key))
        {
            return $"Error: destination.channel_key '{destination.ChannelKey}' does not match channel_key '{key}'. " +
                   "Use the channel_key returned by lookup_channel_user or lookup_channel_destination.";
        }

        ChannelDescriptor descriptor;
        try
        {
            descriptor = registry.GetChannel(key);
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message} Supported channel_key values: {string.Join(", ", GetEnabledSendChannelKeys())}.";
        }

        if (!descriptor.IsEnabled)
            return $"Error: Channel '{key}' is disabled. Supported channel_key values: {string.Join(", ", GetEnabledSendChannelKeys())}.";

        if (!descriptor.Capabilities.HasFlag(ChannelCapabilities.SendMessages)
            || !descriptor.ToolIntents.Contains(ChannelToolIntentKind.SendMessage))
        {
            return $"Error: Channel '{key}' does not support message sending.";
        }

        if (IsTriggerOrigin(context) && context!.RequestedDeliveryTarget is { } requestedTarget)
        {
            var targetError = ValidateRequestedTriggerTarget(key, destination, requestedTarget);
            if (targetError is not null)
                return targetError;
        }

        var validationError = ValidateDestination(descriptor, destination);
        if (validationError is not null)
            return validationError;

        IChannelOutboundClient outboundClient;
        try
        {
            // Duplicate-key detection happens once at ChannelRegistry
            // construction, not per send.
            outboundClient = registry.GetOutboundClient(key);
        }
        catch (InvalidOperationException)
        {
            // The descriptor advertises SendMessage but no outbound client was
            // wired — surface as a tool error rather than an unhandled throw.
            return $"Error: Channel '{key}' does not have a registered send adapter.";
        }

        return await outboundClient.SendMessageAsync(
            new ChannelSendRequest(destination.AddressKind, destination.StableId, text.Trim()),
            ct);
    }

    private JsonElement BuildParameterSchema()
    {
        var channelKeys = GetEnabledSendChannelKeys();
        var channelEnum = JsonSerializer.Serialize(channelKeys);
        var destinationKindEnum = JsonSerializer.Serialize(new[]
        {
            ChannelAddressKindWire.ToWireValue(ChannelAddressKind.Destination),
            ChannelAddressKindWire.ToWireValue(ChannelAddressKind.DirectMessage)
        });
        var schemaJson = $$"""
        {
            "type": "object",
            "properties": {
                "channel_key": {
                    "type": "string",
                    "description": "Enabled channel key to send through, such as slack, discord, or mattermost.",
                    "enum": {{channelEnum}}
                },
                "destination": {
                    "type": "object",
                    "description": "Resolved destination object returned by lookup_channel_destination or assembled from lookup_channel_user for DMs.",
                    "properties": {
                        "channel_key": {
                            "type": "string",
                            "description": "Channel key from the lookup result. Must match the top-level channel_key.",
                            "enum": {{channelEnum}}
                        },
                        "kind": {
                            "type": "string",
                            "description": "Destination kind. Use destination for channel posts; use direct_message for DMs after lookup_channel_user.",
                            "enum": {{destinationKindEnum}}
                        },
                        "id": {
                            "type": "string",
                            "description": "Stable platform ID from lookup_channel_destination or lookup_channel_user. Do not pass display names like #general or @alice."
                        },
                        "display_name": {
                            "type": "string",
                            "description": "Optional display name copied from the lookup result for operator readability."
                        }
                    },
                    "required": ["channel_key", "kind", "id"]
                },
                "text": {
                    "type": "string",
                    "description": "Message text to send."
                },
                "_rationale": {
                    "type": "string",
                    "description": "State your intent for this tool call in one sentence - what are you trying to accomplish and why?"
                },
                "_timeout_seconds": {
                    "type": "integer",
                    "description": "Requested timeout in seconds. Only set when the default is insufficient."
                },
                "_background": {
                    "type": "boolean",
                    "description": "Set to true to run this tool in the background and receive results later."
                }
            },
            "required": ["channel_key", "destination", "text", "_rationale"]
        }
        """;

        return JsonDocument.Parse(schemaJson).RootElement.Clone();
    }

    private IReadOnlyList<string> GetEnabledSendChannelKeys()
    {
        return registry.ListChannels()
            .Where(descriptor => descriptor.IsEnabled && descriptor.ToolIntents.Contains(ChannelToolIntentKind.SendMessage))
            .Select(descriptor => descriptor.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryReadDestination(
        IDictionary<string, object?>? arguments,
        out SendChannelDestination destination,
        out string error)
    {
        destination = default;

        if (arguments is null || !TryGetFlexible(arguments, "destination", out var rawDestination) || rawDestination is null)
        {
            error = "Error: 'destination' parameter is required.";
            return false;
        }

        string? channelKey;
        string? kind;
        string? id;
        string? displayName;

        switch (rawDestination)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                channelKey = GetJsonString(element, "channel_key");
                kind = GetJsonString(element, "kind");
                id = GetJsonString(element, "id");
                displayName = GetJsonString(element, "display_name");
                break;

            case IDictionary<string, object?> dictionary:
                channelKey = ToolArgumentHelper.GetString(dictionary, "channel_key");
                kind = ToolArgumentHelper.GetString(dictionary, "kind");
                id = ToolArgumentHelper.GetString(dictionary, "id");
                displayName = ToolArgumentHelper.GetString(dictionary, "display_name");
                break;

            default:
                error = "Error: 'destination' must be an object with channel_key, kind, and id.";
                return false;
        }

        if (string.IsNullOrWhiteSpace(channelKey))
        {
            error = "Error: 'destination.channel_key' parameter is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            error = "Error: 'destination.kind' parameter is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Error: 'destination.id' parameter is required.";
            return false;
        }

        if (!ChannelAddressKindWire.TryParse(kind, out var addressKind))
        {
            error = $"Error: Unsupported destination.kind '{kind}'. Use 'destination' or 'direct_message'.";
            return false;
        }

        destination = new SendChannelDestination(
            ChannelDescriptorKey.Create(channelKey.Trim()),
            addressKind,
            id.Trim(),
            displayName?.Trim());
        error = string.Empty;
        return true;
    }

    private static string? ValidateDestination(ChannelDescriptor descriptor, SendChannelDestination destination)
    {
        if (destination.AddressKind == ChannelAddressKind.User)
        {
            return "Error: send_channel_message cannot send to destination.kind 'user'. " +
                   "For DMs, use destination.kind='direct_message' with the stable user ID returned by lookup_channel_user.";
        }

        if (destination.AddressKind == ChannelAddressKind.DirectMessage)
        {
            if (!descriptor.Capabilities.HasFlag(ChannelCapabilities.DirectMessages)
                || !descriptor.AddressKinds.Contains(ChannelAddressKind.DirectMessage))
            {
                return $"Error: Channel '{descriptor.Key}' does not support direct-message output.";
            }
        }
        else if (destination.AddressKind == ChannelAddressKind.Destination)
        {
            if (!descriptor.AddressKinds.Contains(ChannelAddressKind.Destination))
                return $"Error: Channel '{descriptor.Key}' does not support destination output.";
        }
        else
        {
            return $"Error: send_channel_message cannot send to destination.kind '{ChannelAddressKindWire.ToWireValue(destination.AddressKind)}'.";
        }

        if (LooksLikeDisplayName(destination.StableId))
        {
            return "Error: destination.id must be a stable platform ID from lookup_channel_destination or lookup_channel_user. " +
                   "Bare display names like '#general' or '@alice' are not accepted.";
        }

        return IsStablePlatformId(descriptor.ChannelType, destination.AddressKind, destination.StableId)
            ? null
            : $"Error: destination.id '{destination.StableId}' does not look like a stable {descriptor.DisplayName} ID. " +
              "Use lookup_channel_destination for channel posts or lookup_channel_user for direct messages.";
    }

    private static string? ValidateRequestedTriggerTarget(
        ChannelDescriptorKey channelKey,
        SendChannelDestination destination,
        ChannelDeliveryTargetInfo requestedTarget)
    {
        if (!string.Equals(requestedTarget.ChannelKey, channelKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: trigger-originated channel send must use configured channel_key '{requestedTarget.ChannelKey}', " +
                   $"not '{channelKey}'.";
        }

        if (!ChannelAddressKindWire.TryParse(requestedTarget.DestinationKind, out var expectedKind))
            return $"Error: configured trigger delivery target has unsupported destination kind '{requestedTarget.DestinationKind}'.";

        if (destination.AddressKind != expectedKind)
        {
            return "Error: trigger-originated channel send must match the configured delivery target " +
                   $"destination.kind '{requestedTarget.DestinationKind}'.";
        }

        if (!string.Equals(destination.StableId, requestedTarget.DestinationId, StringComparison.Ordinal))
        {
            return "Error: trigger-originated channel send must match the configured delivery target " +
                   $"destination.id '{requestedTarget.DestinationId}'.";
        }

        return null;
    }

    private static bool IsTriggerOrigin(ToolExecutionContext? context)
    {
        if (string.IsNullOrWhiteSpace(context?.ChannelType))
            return false;

        return string.Equals(context.ChannelType, ChannelType.Reminder.ToWireValue(), StringComparison.OrdinalIgnoreCase)
               || string.Equals(context.ChannelType, ChannelType.Webhook.ToWireValue(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStablePlatformId(ChannelType channelType, ChannelAddressKind addressKind, string stableId)
        => channelType switch
        {
            ChannelType.Slack => addressKind == ChannelAddressKind.DirectMessage
                ? stableId.StartsWith("U", StringComparison.Ordinal) || stableId.StartsWith("W", StringComparison.Ordinal)
                : stableId.StartsWith("C", StringComparison.Ordinal) || stableId.StartsWith("G", StringComparison.Ordinal) || stableId.StartsWith("D", StringComparison.Ordinal),
            ChannelType.Discord => (addressKind == ChannelAddressKind.Destination || addressKind == ChannelAddressKind.DirectMessage)
                                   && IsDiscordSnowflake(stableId),
            ChannelType.Mattermost => IsMattermostId(stableId),
            _ => false
        };

    private static bool LooksLikeDisplayName(string value)
        => value.StartsWith('#')
           || value.StartsWith('@')
           || value.Any(char.IsWhiteSpace);

    private static bool IsDiscordSnowflake(string value)
        => DiscordAddressResolver.IsDiscordSnowflake(value);

    private static bool IsMattermostId(string value)
        => MattermostIdentifierFormat.IsMattermostId(value);

    private static bool TryGetFlexible(IDictionary<string, object?> arguments, string key, out object? value)
    {
        if (arguments.TryGetValue(key, out value))
            return true;

        var normalizedKey = NormalizeKey(key);
        foreach (var pair in arguments)
        {
            if (string.Equals(NormalizeKey(pair.Key), normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(NormalizeKey(property.Name), NormalizeKey(propertyName), StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return null;
    }

    private static string NormalizeKey(string key)
        => ChannelAddressKindWire.Normalize(key);

    private readonly record struct SendChannelDestination(
        ChannelDescriptorKey ChannelKey,
        ChannelAddressKind AddressKind,
        string StableId,
        string? DisplayName);
}
