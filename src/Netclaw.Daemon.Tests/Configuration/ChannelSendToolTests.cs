// -----------------------------------------------------------------------
// <copyright file="ChannelSendToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ChannelSendToolTests
{
    [Fact]
    public void Send_schema_enumerates_enabled_send_channels_only()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Discord, isEnabled: false, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination),
            BuildDescriptor(
                ChannelType.Tui,
                isEnabled: true,
                ChannelCapabilities.SendMessages,
                [ChannelAddressKind.LocalSession],
                includeSendIntent: false));
        var tool = new SendChannelMessageTool(registry);

        var keys = ReadChannelKeyEnum(tool.ParameterSchema);

        Assert.Equal(["slack"], keys);
    }

    [Fact]
    public async Task Send_rejects_mismatched_destination_channel_key()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Mattermost, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry);

        var result = await ExecuteAsync(tool, "mattermost", "slack", "destination", "C1234567890");

        Assert.Contains("destination.channel_key 'slack' does not match channel_key 'mattermost'", result);
    }

    [Fact]
    public async Task Send_rejects_unsupported_direct_message_capability()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry);

        var result = await ExecuteAsync(tool, "discord", "discord", "direct_message", "123456789012345678");

        Assert.Contains("Channel 'discord' does not support direct-message output", result);
    }

    [Fact]
    public async Task Send_rejects_bare_display_name_destination()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry);

        var result = await ExecuteAsync(tool, "slack", "slack", "destination", "#general");

        Assert.Contains("Bare display names", result);
    }

    [Fact]
    public async Task Send_rejects_user_kind_and_instructs_direct_message_workflow()
    {
        var registry = BuildRegistry(BuildDescriptor(
            ChannelType.Slack,
            isEnabled: true,
            ChannelCapabilities.SendMessages | ChannelCapabilities.DirectMessages,
            ChannelAddressKind.Destination,
            ChannelAddressKind.DirectMessage));
        var tool = new SendChannelMessageTool(registry);

        var result = await ExecuteAsync(tool, "slack", "slack", "user", "U1234567890");

        Assert.Contains("cannot send to destination.kind 'user'", result);
        Assert.Contains("destination.kind='direct_message'", result);
    }

    [Fact]
    public async Task Send_rejects_trigger_origin_without_requested_delivery_target()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry);
        var context = TriggerContext(requestedTarget: null);

        var result = await ExecuteAsync(tool, "slack", "slack", "destination", "C1234567890", context);

        Assert.Contains("trigger-originated channel send requires a configured channel delivery target", result);
        Assert.Contains("No default output channel will be selected", result);
    }

    [Fact]
    public async Task Send_rejects_trigger_origin_destination_that_differs_from_requested_target()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry);
        var context = TriggerContext(new ChannelDeliveryTargetInfo("slack", "destination", "C9999999999"));

        var result = await ExecuteAsync(tool, "slack", "slack", "destination", "C1234567890", context);

        Assert.Contains("must match the configured delivery target destination.id 'C9999999999'", result);
    }

    [Fact]
    public async Task Send_allows_trigger_origin_to_reach_normal_validation_when_requested_target_matches()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry);
        var context = TriggerContext(new ChannelDeliveryTargetInfo("slack", "destination", "not-a-stable-id"));

        var result = await ExecuteAsync(tool, "slack", "slack", "destination", "not-a-stable-id", context);

        Assert.Contains("does not look like a stable Slack ID", result);
    }

    [Fact]
    public async Task Send_dispatches_to_outbound_client_matching_channel_key()
    {
        var slackClient = new FakeOutboundClient(ChannelDescriptorKey.FromChannelType(ChannelType.Slack));
        var mattermostClient = new FakeOutboundClient(ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost));
        var registry = BuildRegistry(
            [
                BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination),
                BuildDescriptor(ChannelType.Mattermost, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination)
            ],
            [slackClient, mattermostClient]);
        var tool = new SendChannelMessageTool(registry);

        var result = await ExecuteAsync(tool, "slack", "slack", "destination", "C1234567890");

        Assert.Equal("sent via slack", result);
        Assert.NotNull(slackClient.Request);
        Assert.Equal(ChannelAddressKind.Destination, slackClient.Request!.AddressKind);
        Assert.Equal("C1234567890", slackClient.Request.TargetId);
        Assert.Equal("Test message", slackClient.Request.Text);
        Assert.Null(mattermostClient.Request);
    }

    [Fact]
    public async Task Send_fails_loudly_when_no_outbound_client_is_registered_for_channel()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry);

        var result = await ExecuteAsync(tool, "slack", "slack", "destination", "C1234567890");

        Assert.Contains("does not have a registered send adapter", result);
    }

    private static Task<string> ExecuteAsync(
        SendChannelMessageTool tool,
        string channelKey,
        string destinationChannelKey,
        string destinationKind,
        string destinationId,
        ToolExecutionContext? context = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["channel_key"] = channelKey,
            ["destination"] = new Dictionary<string, object?>
            {
                ["channel_key"] = destinationChannelKey,
                ["kind"] = destinationKind,
                ["id"] = destinationId
            },
            ["text"] = "Test message",
            ["_rationale"] = "test"
        };

        return context is null
            ? tool.ExecuteAsync(arguments, TestContext.Current.CancellationToken)
            : tool.ExecuteAsync(arguments, context, TestContext.Current.CancellationToken);
    }

    private static ToolExecutionContext TriggerContext(ChannelDeliveryTargetInfo? requestedTarget)
        => new("reminder/test", null)
        {
            Audience = TrustAudience.Team,
            ChannelType = ChannelType.Reminder.ToWireValue(),
            RequestedDeliveryTarget = requestedTarget
        };

    private static string[] ReadChannelKeyEnum(JsonElement schema)
    {
        return schema
            .GetProperty("properties")
            .GetProperty("channel_key")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    private static ChannelRegistry BuildRegistry(params ChannelDescriptor[] descriptors)
    {
        return BuildRegistry(descriptors, []);
    }

    private static ChannelRegistry BuildRegistry(
        IReadOnlyList<ChannelDescriptor> descriptors,
        IReadOnlyList<IChannelOutboundClient> outboundClients)
    {
        var providers = descriptors.Select(descriptor => new StaticChannelDescriptorProvider(descriptor)).ToArray();
        return new ChannelRegistry(providers, [], outboundClients: outboundClients);
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelType channelType,
        bool isEnabled,
        ChannelCapabilities capabilities,
        params ChannelAddressKind[] addressKinds)
    {
        return BuildDescriptor(channelType, isEnabled, capabilities, addressKinds, includeSendIntent: true);
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelType channelType,
        bool isEnabled,
        ChannelCapabilities capabilities,
        ChannelAddressKind[] addressKinds,
        bool includeSendIntent)
    {
        var toolIntents = includeSendIntent
            ? new HashSet<ChannelToolIntentKind> { ChannelToolIntentKind.SendMessage }
            : new HashSet<ChannelToolIntentKind>();

        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(channelType),
            channelType,
            channelType == ChannelType.Tui ? ChannelKind.LocalInteractiveClient : ChannelKind.RemoteChat,
            channelType.ToString(),
            isEnabled,
            capabilities,
            ToolIntents: toolIntents,
            AddressKinds: new HashSet<ChannelAddressKind>(addressKinds),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }

    private sealed class FakeOutboundClient(ChannelDescriptorKey key) : IChannelOutboundClient
    {
        public ChannelDescriptorKey Key { get; } = key;

        public ChannelSendRequest? Request { get; private set; }

        public Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult($"sent via {Key.Value}");
        }
    }
}
