// -----------------------------------------------------------------------
// <copyright file="DaemonStatsServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class DaemonStatsServiceTests
{
    [Fact]
    public void BuildChannelActivityList_UsesEnabledChannelDescriptors()
    {
        ChannelTelemetry.For(ChannelType.Slack).RecordEventReceived("test");
        ChannelTelemetry.For(ChannelType.Mattermost).RecordEventReceived("test");

        var registry = CreateRegistry(
        [
            Descriptor(ChannelType.Slack, enabled: false),
            Descriptor(ChannelType.Mattermost, enabled: true)
        ]);

        var activity = DaemonStatsService.BuildChannelActivityList(registry);

        Assert.Contains(activity, item => item.ChannelType == "mattermost");
        Assert.DoesNotContain(activity, item => item.ChannelType == "slack");
    }

    private static IChannelRegistry CreateRegistry(IReadOnlyCollection<ChannelDescriptor> descriptors)
    {
        return new ChannelRegistry(
            descriptors.Select(descriptor => new StaticChannelDescriptorProvider(descriptor)),
            descriptors.Select(descriptor => new DescriptorChannelRuntimeSnapshotProvider(descriptor, [])));
    }

    private static ChannelDescriptor Descriptor(ChannelType channelType, bool enabled)
    {
        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(channelType),
            channelType,
            ChannelKind.RemoteChat,
            channelType.ToString(),
            enabled,
            ChannelCapabilities.RuntimeHealth,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }
}
