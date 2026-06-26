// -----------------------------------------------------------------------
// <copyright file="ReminderChannelFailureNotifierTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels;
using Netclaw.Daemon.Reminders;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Reminders;

public class ReminderChannelFailureNotifierTests
{
    [Fact]
    public async Task NotifyFailure_posts_text_to_the_resolved_destination_channel()
    {
        var key = ChannelDescriptorKey.Create("slack");
        var client = new CapturingOutboundClient(key);
        var notifier = new ReminderChannelFailureNotifier(
            new StubChannelRegistry(client),
            NullLogger<ReminderChannelFailureNotifier>.Instance);

        var target = new ChannelDeliveryTargetInfo("slack", "destination", "C12345");
        notifier.NotifyFailure(target, "Reminder \"gotowebinar\" failed: stalled");

        // Fire-and-forget — await the capture (a real signal, not a sleep).
        var request = await client.Captured.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressKind.Destination, request.AddressKind);
        Assert.Equal("C12345", request.TargetId);
        Assert.Contains("failed", request.Text);
    }

    [Fact]
    public async Task NotifyFailure_maps_direct_message_kind()
    {
        var key = ChannelDescriptorKey.Create("slack");
        var client = new CapturingOutboundClient(key);
        var notifier = new ReminderChannelFailureNotifier(
            new StubChannelRegistry(client),
            NullLogger<ReminderChannelFailureNotifier>.Instance);

        var target = new ChannelDeliveryTargetInfo("slack", "direct_message", "U999");
        notifier.NotifyFailure(target, "Reminder failed");

        var request = await client.Captured.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressKind.DirectMessage, request.AddressKind);
        Assert.Equal("U999", request.TargetId);
    }

    private sealed class CapturingOutboundClient(ChannelDescriptorKey key) : IChannelOutboundClient
    {
        private readonly TaskCompletionSource<ChannelSendRequest> _captured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelDescriptorKey Key { get; } = key;

        public Task<ChannelSendRequest> Captured => _captured.Task;

        public Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
        {
            _captured.TrySetResult(request);
            return Task.FromResult("ok");
        }
    }

    /// <summary>Minimal registry that only resolves the outbound client under test.</summary>
    private sealed class StubChannelRegistry(IChannelOutboundClient client) : IChannelRegistry
    {
        public IChannelOutboundClient GetOutboundClient(ChannelDescriptorKey key) => client;

        public IReadOnlyCollection<ChannelDescriptor> ListChannels() => throw new NotImplementedException();
        public ChannelDescriptor GetChannel(ChannelDescriptorKey key) => throw new NotImplementedException();
        public ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(ChannelDescriptorKey key, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IChannelAddressResolver GetResolver(ChannelDescriptorKey key, ChannelAddressKind addressKind) => throw new NotImplementedException();
        public IChannelOutputRenderer GetOutputRenderer(ChannelDescriptorKey key) => throw new NotImplementedException();
        public ValueTask<ChannelAddressResolutionResult> ResolveAddressAsync(ChannelAddressResolutionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(ChannelDescriptorKey key, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ChannelOutputRenderResult> RenderOutputAsync(ChannelOutputRenderRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
